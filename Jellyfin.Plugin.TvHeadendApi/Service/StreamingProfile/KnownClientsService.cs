// Default IKnownClientsService backed by Jellyfin's user, session, and device managers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.TvHeadendApi.Model.Profile;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.StreamingProfile;

/// <summary>
/// Default <see cref="IKnownClientsService"/> that reads registered users from
/// <see cref="IUserManager"/>, live client/device names from <see cref="ISessionManager"/>,
/// and historical device registrations from <see cref="IDeviceManager"/>.
/// Each source is queried independently — a failing source is logged and skipped so the
/// configuration UI always receives whatever information is available.
/// </summary>
internal sealed class KnownClientsService : IKnownClientsService
{
    private readonly ILogger<KnownClientsService> _logger;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IDeviceManager _deviceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="KnownClientsService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="userManager">Jellyfin user manager providing registered users.</param>
    /// <param name="sessionManager">Jellyfin session manager providing active client sessions.</param>
    /// <param name="deviceManager">Jellyfin device manager providing registered devices.</param>
    public KnownClientsService(
        ILogger<KnownClientsService> logger,
        IUserManager userManager,
        ISessionManager sessionManager,
        IDeviceManager deviceManager)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
    }

    /// <inheritdoc />
    public KnownClientsResult GetKnownClients()
    {
        var clients = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        CollectFromSessions(clients, devices);
        CollectFromDeviceRegistry(clients, devices);

        return new KnownClientsResult
        {
            Users = CollectUsers(),
            Clients = clients.ToArray(),
            Devices = devices.ToArray(),
        };
    }

    private static void AddIfPresent(SortedSet<string> target, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target.Add(value.Trim());
        }
    }

    private KnownUser[] CollectUsers()
    {
        try
        {
            return _userManager.Users
                .Where(u => !string.IsNullOrWhiteSpace(u.Username))
                .Select(u => new KnownUser
                {
                    Id = u.Id.ToString("D", CultureInfo.InvariantCulture),
                    Name = u.Username,
                })
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate Jellyfin users for the rule editor.");
            return Array.Empty<KnownUser>();
        }
    }

    private void CollectFromSessions(SortedSet<string> clients, SortedSet<string> devices)
    {
        try
        {
            foreach (var session in _sessionManager.Sessions)
            {
                AddIfPresent(clients, session.Client);
                AddIfPresent(devices, session.DeviceName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate active Jellyfin sessions for the rule editor.");
        }
    }

    private void CollectFromDeviceRegistry(SortedSet<string> clients, SortedSet<string> devices)
    {
        try
        {
            var deviceInfos = _deviceManager.GetDeviceInfos(new DeviceQuery());
            foreach (var device in deviceInfos.Items)
            {
                AddIfPresent(clients, device.AppName);

                // Prefer the admin-assigned custom name; fall back to the reported device name.
                AddIfPresent(devices, string.IsNullOrWhiteSpace(device.CustomName) ? device.Name : device.CustomName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate registered Jellyfin devices for the rule editor.");
        }
    }
}
