using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.TvHeadendApi.Model.Input;
using Jellyfin.Plugin.TvHeadendApi.Service.Backend;
using Jellyfin.Plugin.TvHeadendApi.Service.Common;
using Jellyfin.Plugin.TvHeadendApi.Service.Health;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TvHeadendApi.Service.Input;

/// <summary>
/// Retrieves TVHeadend TV input/tuner status via the HTTP API.
/// </summary>
internal sealed class InputMonitorService : StatusGridService<InputGridResponse, InputStatusEntry>, IInputMonitorService
{
    /// <summary>
    /// Initializes a new instance of the <see cref="InputMonitorService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="apiClient">TVHeadend API client.</param>
    /// <param name="urlBuilder">TVHeadend URL builder.</param>
    /// <param name="healthService">TVHeadend health tracking service.</param>
    public InputMonitorService(ILogger<InputMonitorService> logger, IApiClient apiClient, IUrlBuilder urlBuilder, IHealthService healthService)
        : base(logger, apiClient, urlBuilder, healthService)
    {
    }

    /// <inheritdoc />
    protected override string ServiceName => nameof(InputMonitorService);

    /// <inheritdoc />
    protected override string EndpointPath => "api/status/inputs";

    /// <inheritdoc />
    public Task<IReadOnlyList<InputStatusEntry>> GetInputStatusAsync(CancellationToken cancellationToken)
        => FetchEntriesAsync(cancellationToken);

    /// <inheritdoc />
    protected override IReadOnlyList<InputStatusEntry>? SelectEntries(InputGridResponse grid) => grid.Entries;
}
