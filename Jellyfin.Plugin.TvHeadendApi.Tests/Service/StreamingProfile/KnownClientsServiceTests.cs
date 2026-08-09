// Tests for the known-clients aggregation service feeding the rule editor UI.

using System;
using System.Collections.Generic;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Devices;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.StreamingProfile;

/// <summary>
/// Tests for <see cref="KnownClientsService"/>.
/// </summary>
[Trait("Category", "Unit")]
public class KnownClientsServiceTests
{
    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        var userManager = Mock.Of<IUserManager>();
        var sessionManager = Mock.Of<ISessionManager>();
        var deviceManager = Mock.Of<IDeviceManager>();

        Assert.Throws<ArgumentNullException>(() => new KnownClientsService(null!, userManager, sessionManager, deviceManager));
        Assert.Throws<ArgumentNullException>(() => new KnownClientsService(NullLogger<KnownClientsService>.Instance, null!, sessionManager, deviceManager));
        Assert.Throws<ArgumentNullException>(() => new KnownClientsService(NullLogger<KnownClientsService>.Instance, userManager, null!, deviceManager));
        Assert.Throws<ArgumentNullException>(() => new KnownClientsService(NullLogger<KnownClientsService>.Instance, userManager, sessionManager, null!));
    }

    [Fact]
    public void GetKnownClients_ReturnsUsersSortedByNameWithDashedGuidIds()
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var sut = CreateService(
            users: new[]
            {
                CreateUser("zoe", idA),
                CreateUser("Adam", idB),
            });

        var result = sut.GetKnownClients();

        Assert.Equal(2, result.Users.Count);
        Assert.Equal("Adam", result.Users[0].Name);
        Assert.Equal(idB.ToString("D"), result.Users[0].Id);
        Assert.Equal("zoe", result.Users[1].Name);
        Assert.Equal(idA.ToString("D"), result.Users[1].Id);
    }

    [Fact]
    public void GetKnownClients_MergesSessionAndDeviceSources_DeduplicatedAndSorted()
    {
        var sut = CreateService(
            sessions: new[]
            {
                CreateSession("Jellyfin Web", "Chrome Desktop"),
                CreateSession("jellyfin web", "Living Room TV"),
            },
            devices: new[]
            {
                new DeviceInfo { AppName = "Jellyfin Android", Name = "Pixel 8" },
                new DeviceInfo { AppName = "Jellyfin Web", Name = "Chrome Desktop" },
            });

        var result = sut.GetKnownClients();

        // "Jellyfin Web"/"jellyfin web" collapse into one case-insensitive entry.
        Assert.Equal(new[] { "Jellyfin Android", "Jellyfin Web" }, result.Clients);
        Assert.Equal(new[] { "Chrome Desktop", "Living Room TV", "Pixel 8" }, result.Devices);
    }

    [Fact]
    public void GetKnownClients_PrefersCustomDeviceNameOverReportedName()
    {
        var sut = CreateService(
            devices: new[]
            {
                new DeviceInfo { AppName = "Jellyfin Android", Name = "SM-G991B", CustomName = "Kitchen Tablet" },
                new DeviceInfo { AppName = "Jellyfin Web", Name = "Firefox", CustomName = "   " },
            });

        var result = sut.GetKnownClients();

        Assert.Contains("Kitchen Tablet", result.Devices);
        Assert.DoesNotContain("SM-G991B", result.Devices);
        Assert.Contains("Firefox", result.Devices);
    }

    [Fact]
    public void GetKnownClients_SkipsBlankNamesAndBlankUsernames()
    {
        var sut = CreateService(
            users: new[] { CreateUser("  ", Guid.NewGuid()), CreateUser("alice", Guid.NewGuid()) },
            sessions: new[] { CreateSession(string.Empty, "  ") },
            devices: new[] { new DeviceInfo { AppName = null, Name = null } });

        var result = sut.GetKnownClients();

        Assert.Single(result.Users);
        Assert.Equal("alice", result.Users[0].Name);
        Assert.Empty(result.Clients);
        Assert.Empty(result.Devices);
    }

    [Fact]
    public void GetKnownClients_TrimsNames()
    {
        var sut = CreateService(sessions: new[] { CreateSession("  Jellyfin Web  ", "  iPhone 15  ") });

        var result = sut.GetKnownClients();

        Assert.Equal(new[] { "Jellyfin Web" }, result.Clients);
        Assert.Equal(new[] { "iPhone 15" }, result.Devices);
    }

    [Fact]
    public void GetKnownClients_DeviceManagerThrows_StillReturnsUsersAndSessions()
    {
        var userManager = new Mock<IUserManager>();
        userManager.SetupGet(m => m.Users).Returns(new[] { CreateUser("alice", Guid.NewGuid()) });

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.SetupGet(m => m.Sessions).Returns(new[] { CreateSession("Jellyfin Web", "Chrome") });

        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(m => m.GetDeviceInfos(It.IsAny<DeviceQuery>())).Throws(new InvalidOperationException("boom"));

        var sut = new KnownClientsService(
            NullLogger<KnownClientsService>.Instance,
            userManager.Object,
            sessionManager.Object,
            deviceManager.Object);

        var result = sut.GetKnownClients();

        Assert.Single(result.Users);
        Assert.Equal(new[] { "Jellyfin Web" }, result.Clients);
        Assert.Equal(new[] { "Chrome" }, result.Devices);
    }

    [Fact]
    public void GetKnownClients_AllSourcesEmpty_ReturnsEmptyResult()
    {
        var sut = CreateService();

        var result = sut.GetKnownClients();

        Assert.Empty(result.Users);
        Assert.Empty(result.Clients);
        Assert.Empty(result.Devices);
    }

    private static KnownClientsService CreateService(
        IEnumerable<User>? users = null,
        IEnumerable<SessionInfo>? sessions = null,
        IReadOnlyList<DeviceInfo>? devices = null)
    {
        var userManager = new Mock<IUserManager>();
        userManager.SetupGet(m => m.Users).Returns(users ?? Array.Empty<User>());

        var sessionManager = new Mock<ISessionManager>();
        sessionManager.SetupGet(m => m.Sessions).Returns(sessions ?? Array.Empty<SessionInfo>());

        var deviceManager = new Mock<IDeviceManager>();
        deviceManager
            .Setup(m => m.GetDeviceInfos(It.IsAny<DeviceQuery>()))
            .Returns(new QueryResult<DeviceInfo>(devices ?? Array.Empty<DeviceInfo>()));

        return new KnownClientsService(
            NullLogger<KnownClientsService>.Instance,
            userManager.Object,
            sessionManager.Object,
            deviceManager.Object);
    }

    private static User CreateUser(string username, Guid id)
    {
        return new User(username, "auth-provider", "password-reset-provider") { Id = id };
    }

    private static SessionInfo CreateSession(string client, string deviceName)
    {
        return new SessionInfo(Mock.Of<ISessionManager>(), NullLogger<SessionInfo>.Instance)
        {
            Client = client,
            DeviceName = deviceName,
        };
    }
}
