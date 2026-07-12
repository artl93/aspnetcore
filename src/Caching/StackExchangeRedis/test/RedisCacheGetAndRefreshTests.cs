// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Caching.Distributed;
using Moq;
using StackExchange.Redis;
using StackExchange.Redis.Profiling;
using Xunit;

namespace Microsoft.Extensions.Caching.StackExchangeRedis;

public class RedisCacheGetAndRefreshTests
{
    private const string EnabledEnvironmentVariable = "REDISCACHETESTS_ENABLED";
    private const string Key = "key";
    private static readonly byte[] _value = [1, 2, 3];

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetMissingKeyReturnsNull(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out _);

        var value = await GetAsync(cache, useAsync);

        Assert.Null(value);
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetWithSlidingExpirationRefreshesTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(3)));
        await database.KeyExpireAsync(redisKey, TimeSpan.FromMilliseconds(500));

        var value = await GetAsync(cache, useAsync);
        var timeToLive = await database.KeyTimeToLiveAsync(redisKey);

        Assert.Equal(_value, value);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshWithSlidingExpirationRefreshesTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(3)));
        await database.KeyExpireAsync(redisKey, TimeSpan.FromMilliseconds(500));

        if (useAsync)
        {
            await cache.RefreshAsync(Key);
        }
        else
        {
            cache.Refresh(Key);
        }
        var timeToLive = await database.KeyTimeToLiveAsync(redisKey);

        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetWithAbsoluteExpirationDoesNotRefreshTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(5)));
        await database.KeyExpireAsync(redisKey, TimeSpan.FromSeconds(1));
        var timeToLiveBeforeGet = await database.KeyTimeToLiveAsync(redisKey);

        var value = await GetAsync(cache, useAsync);
        var timeToLiveAfterGet = await database.KeyTimeToLiveAsync(redisKey);

        Assert.Equal(_value, value);
        Assert.True(timeToLiveAfterGet <= timeToLiveBeforeGet);
        Assert.True(timeToLiveAfterGet > TimeSpan.Zero);
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetWithSlidingAndAbsoluteExpirationCapsTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var absoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(2);
        await SetRawEntryAsync(connection.GetDatabase(), redisKey, absoluteExpiration.Ticks, TimeSpan.FromSeconds(5).Ticks);

        var value = await GetAsync(cache, useAsync);
        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(redisKey);

        Assert.Equal(_value, value);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetNearAbsoluteExpirationReturnsValueAndCapsTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var absoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(1);
        await SetRawEntryAsync(connection.GetDatabase(), redisKey, absoluteExpiration.Ticks, TimeSpan.FromSeconds(5).Ticks);

        var value = await GetAsync(cache, useAsync);
        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(redisKey);

        Assert.Equal(_value, value);
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false, 0)]
    [InlineData(false, -TimeSpan.TicksPerSecond)]
    [InlineData(true, 0)]
    [InlineData(true, -TimeSpan.TicksPerSecond)]
    public async Task GetWithNonPositiveSlidingExpirationReturnsValueThenDeletesKey(bool useAsync, long slidingExpirationTicks)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        await SetRawEntryAsync(connection.GetDatabase(), redisKey, -1, slidingExpirationTicks);

        var value = await GetAsync(cache, useAsync);
        var exists = await connection.GetDatabase().KeyExistsAsync(redisKey);

        Assert.Equal(_value, value);
        Assert.False(exists);
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetMissingKeyReturnsFalse(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out _);
        var destination = new TestBufferWriter();
        var bufferCache = (IBufferDistributedCache)cache;

        var found = useAsync
            ? await bufferCache.TryGetAsync(Key, destination, default)
            : bufferCache.TryGet(Key, destination);

        Assert.False(found);
        Assert.Empty(destination.WrittenMemory.ToArray());
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetWithSlidingExpirationReturnsValueAndRefreshesTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(3)));
        await database.KeyExpireAsync(redisKey, TimeSpan.FromMilliseconds(500));
        var destination = new TestBufferWriter();
        var bufferCache = (IBufferDistributedCache)cache;

        var found = useAsync
            ? await bufferCache.TryGetAsync(Key, destination, default)
            : bufferCache.TryGet(Key, destination);
        var timeToLive = await database.KeyTimeToLiveAsync(redisKey);

        Assert.True(found);
        Assert.Equal(_value, destination.WrittenMemory.ToArray());
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetWithoutDataDoesNotRefreshTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        await database.HashSetAsync(redisKey,
        [
            new HashEntry("absexp", -1),
            new HashEntry("sldexp", TimeSpan.FromSeconds(3).Ticks),
        ]);
        await database.KeyExpireAsync(redisKey, TimeSpan.FromMilliseconds(500));
        var timeToLiveBeforeGet = await database.KeyTimeToLiveAsync(redisKey);
        var destination = new TestBufferWriter();
        var bufferCache = (IBufferDistributedCache)cache;

        var found = useAsync
            ? await bufferCache.TryGetAsync(Key, destination, default)
            : bufferCache.TryGet(Key, destination);
        var timeToLiveAfterGet = await database.KeyTimeToLiveAsync(redisKey);

        Assert.False(found);
        Assert.Empty(destination.WrittenMemory.ToArray());
        Assert.True(timeToLiveAfterGet <= timeToLiveBeforeGet);
        Assert.True(timeToLiveAfterGet > TimeSpan.Zero);
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetWithAbsoluteExpirationDoesNotRefreshTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var database = connection.GetDatabase();
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(5)));
        await database.KeyExpireAsync(redisKey, TimeSpan.FromSeconds(1));
        var timeToLiveBeforeGet = await database.KeyTimeToLiveAsync(redisKey);
        var destination = new TestBufferWriter();
        var bufferCache = (IBufferDistributedCache)cache;

        var found = useAsync
            ? await bufferCache.TryGetAsync(Key, destination, default)
            : bufferCache.TryGet(Key, destination);
        var timeToLiveAfterGet = await database.KeyTimeToLiveAsync(redisKey);

        Assert.True(found);
        Assert.Equal(_value, destination.WrittenMemory.ToArray());
        Assert.True(timeToLiveAfterGet <= timeToLiveBeforeGet);
        Assert.True(timeToLiveAfterGet > TimeSpan.Zero);
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TryGetWithSlidingAndAbsoluteExpirationCapsTimeToLive(bool useAsync)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out var redisKey);
        var absoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(2);
        await SetRawEntryAsync(connection.GetDatabase(), redisKey, absoluteExpiration.Ticks, TimeSpan.FromSeconds(5).Ticks);
        var destination = new TestBufferWriter();
        var bufferCache = (IBufferDistributedCache)cache;

        var found = useAsync
            ? await bufferCache.TryGetAsync(Key, destination, default)
            : bufferCache.TryGet(Key, destination);
        var timeToLive = await connection.GetDatabase().KeyTimeToLiveAsync(redisKey);

        Assert.True(found);
        Assert.Equal(_value, destination.WrittenMemory.ToArray());
        Assert.NotNull(timeToLive);
        Assert.InRange(timeToLive.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [ConditionalTheory]
    [EnvironmentVariableSkipCondition(EnabledEnvironmentVariable, "1")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetWithSlidingExpirationExecutesOneRedisCommand(bool useAsync)
    {
        var session = new ProfilingSession();
        using var connection = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
        using var cache = CreateCache(connection, out _, session);
        cache.Set(Key, _value, new DistributedCacheEntryOptions().SetSlidingExpiration(TimeSpan.FromSeconds(3)));
        _ = session.FinishProfiling().ToArray();

        var value = await GetAsync(cache, useAsync);
        var commands = session.FinishProfiling().ToArray();

        Assert.Equal(_value, value);
        Assert.Single(commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetPropagatesRedisFailure(bool useAsync)
    {
        var exception = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Failure");
        var database = new Mock<IDatabase>();
        var connection = new Mock<IConnectionMultiplexer>();
        connection
            .Setup(c => c.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(database.Object);
        database
            .SetupGet(d => d.Multiplexer)
            .Returns(connection.Object);
        database
            .Setup(d => d.HashGet(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Throws(exception);
        database
            .Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Throws(exception);
        database
            .Setup(d => d.ScriptEvaluate(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Throws(exception);
        database
            .Setup(d => d.ScriptEvaluateAsync(It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Throws(exception);
        using var cache = new RedisCache(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult(connection.Object),
        });

        var actual = useAsync
            ? await Assert.ThrowsAsync<RedisConnectionException>(() => cache.GetAsync(Key))
            : Assert.Throws<RedisConnectionException>(() => cache.Get(Key));

        Assert.Same(exception, actual);
    }

    [Fact]
    public async Task GetAsyncWithCanceledTokenDoesNotConnect()
    {
        var connection = new Mock<IConnectionMultiplexer>(MockBehavior.Strict);
        using var cache = new RedisCache(new RedisCacheOptions
        {
            ConnectionMultiplexerFactory = () => Task.FromResult(connection.Object),
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.GetAsync(Key, cancellation.Token));
    }

    private static RedisCache CreateCache(
        ConnectionMultiplexer connection,
        out RedisKey redisKey,
        ProfilingSession profilingSession = null)
    {
        var instanceName = $"{nameof(RedisCacheGetAndRefreshTests)}:{Guid.NewGuid()}:";
        redisKey = instanceName + Key;

        return new RedisCache(new RedisCacheOptions
        {
            InstanceName = instanceName,
            ConnectionMultiplexerFactory = () => Task.FromResult<IConnectionMultiplexer>(connection),
            ProfilingSession = profilingSession is null ? null : () => profilingSession,
        });
    }

    private static Task<byte[]> GetAsync(RedisCache cache, bool useAsync)
        => useAsync ? cache.GetAsync(Key) : Task.FromResult(cache.Get(Key));

    private static Task SetRawEntryAsync(IDatabase database, RedisKey key, long absoluteExpirationTicks, long slidingExpirationTicks)
        => database.HashSetAsync(key,
        [
            new HashEntry("absexp", absoluteExpirationTicks),
            new HashEntry("sldexp", slidingExpirationTicks),
            new HashEntry("data", _value),
        ]);

    private sealed class TestBufferWriter : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[16];
        private int _written;

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count)
            => _written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);

            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            var requiredLength = _written + sizeHint;
            if (requiredLength > _buffer.Length)
            {
                Array.Resize(ref _buffer, requiredLength);
            }
        }
    }
}
