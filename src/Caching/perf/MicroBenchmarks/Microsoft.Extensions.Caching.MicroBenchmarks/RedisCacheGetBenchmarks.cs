// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Microsoft.Extensions.Caching.Benchmarks;

[MemoryDiagnoser, ShortRunJob]
public class RedisCacheGetBenchmarks : IDisposable
{
    private const string RedisConfigurationString = "127.0.0.1,AllowAdmin=true";
    private const int OperationsPerInvoke = 256;

    private readonly string _key = Guid.NewGuid().ToString();
    private ConnectionMultiplexer _multiplexer = null!;
    private ServiceProvider _services = null!;
    private IDistributedCache _cache = null!;

    [Params(128, 1024, 10 * 1024)]
    public int PayloadSize { get; set; }

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

    public void Dispose()
    {
        _services.Dispose();
        _multiplexer.Dispose();
    }
}
