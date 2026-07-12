// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Microsoft.Extensions.Caching.Benchmarks;

[Config(typeof(RedisCacheBenchmarkConfig))]
[MemoryDiagnoser]
public class RedisCacheConcurrencyBenchmarks : IDisposable
{
    private const string RedisConfigurationString = "127.0.0.1,AllowAdmin=true";
    private const int OperationsPerInvoke = 256;

    private readonly string _key = Guid.NewGuid().ToString();
    private ConnectionMultiplexer _multiplexer = null!;
    private ServiceProvider _services = null!;
    private IDistributedCache _cache = null!;
    private ParallelOptions _parallelOptions = null!;
    private Task<int>[] _pending = null!;

    [ParamsSource(nameof(ConcurrencyValues))]
    public int Concurrency { get; set; }

    public IEnumerable<int> ConcurrencyValues()
    {
        var configuredConcurrency = Environment.GetEnvironmentVariable("REDIS_BENCHMARK_CONCURRENCY");
        if (configuredConcurrency is not null)
        {
            if (!int.TryParse(configuredConcurrency, out var concurrency)
                || concurrency <= 0
                || concurrency > OperationsPerInvoke
                || OperationsPerInvoke % concurrency != 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(RedisCacheConcurrencyBenchmarks)} requires REDIS_BENCHMARK_CONCURRENCY to be a positive divisor of {OperationsPerInvoke}.");
            }

            yield return concurrency;
        }
        else
        {
            yield return 1;
            yield return 16;
            yield return 64;
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
        _cache.Set(_key, new byte[1024], new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30),
            SlidingExpiration = TimeSpan.FromMinutes(5),
        });

        _parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Concurrency };
        _pending = new Task<int>[Concurrency];
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public int Get()
    {
        if (Concurrency == 1)
        {
            return GetMany(OperationsPerInvoke);
        }

        var total = 0;
        Parallel.For(0, Concurrency, _parallelOptions, _ =>
        {
            Interlocked.Add(ref total, GetMany(OperationsPerInvoke / Concurrency));
        });

        return total;
    }

    [Benchmark(OperationsPerInvoke = OperationsPerInvoke)]
    public async Task<int> GetAsync()
    {
        var operations = OperationsPerInvoke / Concurrency;
        for (var i = 0; i < Concurrency; i++)
        {
            _pending[i] = GetManyAsync(operations);
        }

        var total = 0;
        for (var i = 0; i < Concurrency; i++)
        {
            total += await _pending[i];
        }

        return total;
    }

    private int GetMany(int operations)
    {
        var total = 0;
        for (var i = 0; i < operations; i++)
        {
            total += _cache.Get(_key)?.Length ?? 0;
        }

        return total;
    }

    private async Task<int> GetManyAsync(int operations)
    {
        var total = 0;
        for (var i = 0; i < operations; i++)
        {
            total += (await _cache.GetAsync(_key))?.Length ?? 0;
        }

        return total;
    }

    public void Dispose()
    {
        _services.Dispose();
        _multiplexer.Dispose();
    }
}
