using Xunit;

namespace Jellyfin.Plugin.TvHeadendApi.Tests.Integration;

/// <summary>
/// xUnit collection definition that ensures all Jellyfin API integration tests
/// share a single <see cref="JellyfinApiFixture"/> instance.
/// This prevents concurrent fixture initialization and test-ordering issues
/// where one test class resets configuration that another depends on.
/// </summary>
[CollectionDefinition("JellyfinApi")]
public sealed class JellyfinApiCollection : ICollectionFixture<JellyfinApiFixture>
{
}
