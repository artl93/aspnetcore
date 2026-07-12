// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;

namespace Microsoft.Extensions.Caching.Benchmarks;

public class RedisCacheBenchmarkConfig : ManualConfig
{
    public RedisCacheBenchmarkConfig()
    {
        AddColumn(
            StatisticColumn.OperationsPerSecond,
            StatisticColumn.P50,
            StatisticColumn.P95,
            StatisticColumn.P100);
    }
}
