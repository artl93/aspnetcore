// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Microsoft.Extensions.Caching.Benchmarks;

[Config(typeof(RedisCacheBenchmarkConfig))]
[MemoryDiagnoser]
public class RedisCacheGetBenchmarks : IDisposable
{
    private const string RedisConfigurationString = "127.0.0.1,AllowAdmin=true";
    private const int OperationsPerInvoke = 256;

    private readonly string _key = Guid.NewGuid().ToString();
    private ConnectionMultiplexer _multiplexer = null!;
    private ServiceProvider _services = null!;
    private IDistributedCache _cache = null!;
    private IBufferDistributedCache _bufferCache = null!;
    private ReusableBufferWriter _destination = null!;

    [ParamsSource(nameof(PayloadSizes))]
    public int PayloadSize { get; set; }

    public IEnumerable<int> PayloadSizes()
    {
        var configuredPayloadSize = Environment.GetEnvironmentVariable("REDIS_BENCHMARK_PAYLOAD_SIZE");
        if (configuredPayloadSize is not null)
        {
            if (!int.TryParse(configuredPayloadSize, out var payloadSize) || payloadSize <= 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(RedisCacheGetBenchmarks)} requires REDIS_BENCHMARK_PAYLOAD_SIZE to be a positive integer.");
            }

            yield return payloadSize;
        }
        else
        {
            yield return 128;
            yield return 1024;
            yield return 10 * 1024;
        }
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _multiplexer = ConnectionMultiplexer.Connect(RedisConfigurationString);

        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options =>
        {
            options.ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(_multiplexer);
        });
        _services = services.BuildServiceProvider();
        _cache = _services.GetRequiredService<IDistributedCache>();
        _bufferCache = (IBufferDistributedCache)_cache;
        _destination = new ReusableBufferWriter(PayloadSize);

        _cache.Set(_key, new byte[PayloadSize], new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
            SlidingExpiration = TimeSpan.FromMinutes(5),
        });
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int Get()
    {
        var total = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            total += _cache.Get(_key)?.Length ?? 0;
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task<int> GetAsync()
    {
        var total = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            total += (await _cache.GetAsync(_key))?.Length ?? 0;
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int TryGet()
    {
        var total = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            _destination.Reset();
            if (_bufferCache.TryGet(_key, _destination))
            {
                total += _destination.WrittenCount;
            }
        }

        return total;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task<int> TryGetAsync()
    {
        var total = 0;
        for (var i = 0; i < OperationsPerInvoke; i++)
        {
            _destination.Reset();
            if (await _bufferCache.TryGetAsync(_key, _destination, default))
            {
                total += _destination.WrittenCount;
            }
        }

        return total;
    }

    public void Dispose()
    {
        _services.Dispose();
        _multiplexer.Dispose();
    }

    private sealed class ReusableBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer;

        public ReusableBufferWriter(int capacity)
        {
            _buffer = new byte[capacity];
        }

        public int WrittenCount { get; private set; }

        public void Advance(int count)
            => WrittenCount += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _buffer.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _buffer.AsSpan(WrittenCount);
        }

        public void Reset()
            => WrittenCount = 0;

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            var requiredLength = WrittenCount + sizeHint;
            if (requiredLength > _buffer.Length)
            {
                Array.Resize(ref _buffer, requiredLength);
            }
        }
    }
}
