// End-to-end (service level) tests for the zapping telemetry flow:
// token issue → telemetry attach → validated token carries the values that the
// relay controller copies into the persisted request metric.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Database;
using Jellyfin.Plugin.TvHeadendApi.Service.Relay;
using Jellyfin.Plugin.TvHeadendApi.Service.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.Relay;

/// <summary>
/// Verifies that stream-setup telemetry (resolution source, mediainfo cache status,
/// setup duration) attached after token issuance is retrievable with the token record,
/// and that the timing-context stamping produces a fully tagged metric row.
/// </summary>
public sealed class RelayTokenTelemetryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RelayTokenRepository _repo;
    private readonly RelayTokenService _service;
    private readonly RelayTokenHasher _hasher;
    private readonly DatabaseWriteCoordinator _writeCoordinator = new();

    public RelayTokenTelemetryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "tvh_tok_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var dbPath = Path.Combine(_tempDir, "tokens.db");
        var options = new DbContextOptionsBuilder<RelayTokenDbContext>()
            .UseSqlite($"DataSource={dbPath}")
            .Options;

        var pathProvider = new DataFolderPathProvider(() => _tempDir);
        var provider = new DatabaseProvider(pathProvider);
        var factory = new DatabaseConnectionFactory(provider);
        var migration = new DatabaseMigrationService(factory, NullLogger<DatabaseMigrationService>.Instance);
        var recovery = new DatabaseRecoveryService(provider, migration, factory, NullLogger<DatabaseRecoveryService>.Instance);
        var dbHealth = new DatabaseHealthService(provider, factory, migration, recovery, NullLogger<DatabaseHealthService>.Instance);
        dbHealth.Initialize();

        using (var ctx = new RelayTokenDbContext(options))
        {
            ctx.Database.EnsureCreated();
        }

        _repo = new RelayTokenRepository(NullLogger<RelayTokenRepository>.Instance, dbHealth, _writeCoordinator, options);
        _hasher = new RelayTokenHasher(new byte[32]);
        var configProvider = new Jellyfin.Plugin.TvHeadendApi.Service.Configuration.ConfigurationProvider(
            () => new Jellyfin.Plugin.TvHeadendApi.Configuration.PluginConfiguration());
        _service = new RelayTokenService(
            NullLogger<RelayTokenService>.Instance,
            _hasher,
            _repo,
            new RelayTokenOptions(configProvider));
    }

    public void Dispose()
    {
        _repo.Dispose();
        _writeCoordinator.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task IssueThenAttachTelemetry_TokenRecordCarriesZappingData()
    {
        var rawToken = await _service.IssueStreamTokenAsync("ch-1", "user-a", "device-1", "jellyfin", "DirectPlay", CancellationToken.None);

        await _service.AttachStreamTelemetryAsync(rawToken, "ClientRule", "hit", 37.5, CancellationToken.None);

        var record = await _repo.FindByHashAsync(_hasher.HashToken(rawToken), CancellationToken.None);
        Assert.NotNull(record);
        Assert.Equal("jellyfin", record!.SelectedProfile);
        Assert.Equal("ClientRule", record.ResolutionSource);
        Assert.Equal("hit", record.MediaInfoCacheStatus);
        Assert.Equal(37.5, record.StreamSetupMs);
    }

    [Fact]
    public async Task TokenRecordTelemetry_FlowsIntoMetricRow_ViaTimingContext()
    {
        // Full tagging path at service level: issue token, attach telemetry, read the
        // record back (what the validator hands the controller), stamp the timing
        // context (what StampStreamTelemetry does), convert to the persisted metric.
        var rawToken = await _service.IssueStreamTokenAsync("ch-9", null, null, "pass", "DirectPlay", CancellationToken.None);
        await _service.AttachStreamTelemetryAsync(rawToken, "GlobalDefault", "restored", 12.0, CancellationToken.None);
        var record = await _repo.FindByHashAsync(_hasher.HashToken(rawToken), CancellationToken.None);
        Assert.NotNull(record);

        var timing = new RelayTimingContext
        {
            RelayType = RelayType.Stream,
            ChannelId = "ch-9",
            SessionId = "sess-1",
            ClientStatusCode = 200,
            EndedBy = StreamEndedBy.UpstreamEof,
            EffectiveProfile = record!.SelectedProfile,
            ResolutionSource = record.ResolutionSource,
            MediaInfoCacheStatus = record.MediaInfoCacheStatus ?? "unknown",
            StreamSetupMs = record.StreamSetupMs,
        };

        var metric = timing.ToMetric();

        Assert.Equal("pass", metric.EffectiveProfile);
        Assert.Equal("GlobalDefault", metric.ResolutionSource);
        Assert.Equal("restored", metric.MediaInfoCacheStatus);
        Assert.Equal(12.0, metric.StreamSetupMs);
        Assert.Equal("sess-1", metric.SessionId);
    }

    [Fact]
    public async Task AttachTelemetry_UnknownToken_DoesNotThrow()
    {
        await _service.AttachStreamTelemetryAsync("unknownRawToken123", "GlobalDefault", "miss", 5, CancellationToken.None);
    }
}
