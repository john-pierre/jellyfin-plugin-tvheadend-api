using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.TvHeadendApi.Api.Endpoints;
using Jellyfin.Plugin.TvHeadendApi.Api.Models;
using Jellyfin.Plugin.TvHeadendApi.Service.Comet;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Api;

/// <summary>
/// Tests for <see cref="LogsController"/>.
/// </summary>
public class LogsControllerTests
{
    [Fact]
    public void Constructor_NullSnapshotReader_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new LogsController(null!));
    }

    [Fact]
    public void GetLogs_ClampsCountAndMapsPayload()
    {
        var reader = new StubCometSnapshotReader();
        var sut = new LogsController(reader);

        var result = sut.GetLogs(999);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<LogsResponse>(ok.Value);
        Assert.Equal(500, reader.LastRequestedCount);
        Assert.Equal(2, payload.LogEntries.Count);
        Assert.Equal("first", payload.LogEntries[0].Message);
        Assert.NotNull(payload.DiskSpace);
        Assert.Equal(50, payload.DiskSpace!.PercentUsed);
    }

    [Fact]
    public void GetLogEntries_ClampsLowCountToOne()
    {
        var reader = new StubCometSnapshotReader();
        var sut = new LogsController(reader);

        var result = sut.GetLogEntries(0);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<List<LogEntryDto>>(ok.Value);
        Assert.Equal(1, reader.LastRequestedCount);
        Assert.Single(payload);
    }

    [Fact]
    public void GetDiskSpace_WhenNoSnapshotExists_ReturnsNotFound()
    {
        var reader = new StubCometSnapshotReader { DiskSpaceUpdate = null };
        var sut = new LogsController(reader);

        var result = sut.GetDiskSpace();

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public void LogsController_HasRequiresElevationPolicy()
    {
        var attribute = (AuthorizeAttribute)Attribute.GetCustomAttributes(typeof(LogsController), typeof(AuthorizeAttribute)).Single();

        Assert.Equal(Policies.RequiresElevation, attribute.Policy);
    }

    private sealed class StubCometSnapshotReader : ICometSnapshotReader
    {
        public StubCometSnapshotReader()
        {
            LogMessages =
            [
                new LogMessage { Timestamp = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc), Text = "first" },
                new LogMessage { Timestamp = new DateTime(2026, 4, 20, 10, 1, 0, DateTimeKind.Utc), Text = "second" },
            ];

            DiskSpaceUpdate = new DiskSpaceUpdate
            {
                Timestamp = new DateTime(2026, 4, 20, 11, 0, 0, DateTimeKind.Utc),
                TotalDiskSpace = 100,
                UsedDiskSpace = 50,
                FreeDiskSpace = 50,
            };
        }

        public int LastRequestedCount { get; private set; }

        public IReadOnlyList<LogMessage> LogMessages { get; init; }

        public DiskSpaceUpdate? DiskSpaceUpdate { get; init; }

        public DiskSpaceUpdate? GetLastDiskSpaceUpdate()
        {
            return DiskSpaceUpdate;
        }

        public IReadOnlyList<LogMessage> GetRecentLogs(int count = 100)
        {
            LastRequestedCount = count;
            return LogMessages.Take(count).ToList();
        }

        public IReadOnlyList<Model.Statistics.TvhLogEntry> GetLogHistory(int count = 500, DateTime? sinceUtc = null)
        {
            return Array.Empty<Model.Statistics.TvhLogEntry>();
        }
    }
}

