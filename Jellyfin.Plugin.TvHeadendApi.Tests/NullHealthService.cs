// No-op health service for unit tests that don't need health tracking behavior.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Statistics;
using Jellyfin.Plugin.TvHeadendApi.Service.Helper;

namespace Jellyfin.Plugin.TvHeadendApi.Tests;

/// <summary>
/// Stub <see cref="ITvHeadendHealthService"/> that never blocks requests and records nothing.
/// Use this in tests where health tracking is not the subject under test.
/// </summary>
internal sealed class NullHealthService : ITvHeadendHealthService
{
    /// <summary>Gets a shared instance for reuse across tests.</summary>
    internal static readonly NullHealthService Instance = new();

    /// <inheritdoc />
    public TvHeadendHealthSnapshot GetSnapshot() => new();

    /// <inheritdoc />
    public void RecordSuccess(int? responseTimeMs = null)
    {
    }

    /// <inheritdoc />
    public void RecordFailure(FailureReason reason)
    {
    }

    /// <inheritdoc />
    public bool ShouldBlockRequest() => false;

    /// <inheritdoc />
    public Task<TvHeadendHealthSnapshot> CheckHealthAsync(CancellationToken cancellationToken)
        => Task.FromResult(new TvHeadendHealthSnapshot());

    /// <inheritdoc />
    public IReadOnlyList<HealthTransition> GetHealthHistory(int count = 100)
        => Array.Empty<HealthTransition>();
}

