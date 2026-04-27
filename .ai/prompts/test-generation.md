# Test Generation

Generate tests for changed code in jellyfin-plugin-tvheadend-api.

## Steps

### 1. Identify changed code

- Determine which files were modified (from diff, commit, or user description).
- Map each changed file to its service domain under `Service/<Domain>/`.
- List every public method that was added or modified.

### 2. Analyze existing coverage

- Check for existing test files in `Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/`.
- Identify which methods already have tests and which do not.
- Note the test patterns used in neighboring test files for consistency.

### 3. Write unit tests

**Framework and conventions:**
- xUnit with `[Fact]` and `[Theory]` attributes.
- Moq for mocking interfaces. Use `Mock<IService>` with `.Setup()` / `.Verify()`.
- `NullLogger<T>.Instance` for logger dependencies (from `Microsoft.Extensions.Logging.Abstractions`).
- AutoFixture where useful for generating test data.
- FluentAssertions for complex assertions (optional — xUnit asserts are also acceptable).

**Test structure:**
```csharp
// File: Jellyfin.Plugin.TvHeadendApi.Tests/Service/<Domain>/<ClassName>Tests.cs
namespace Jellyfin.Plugin.TvHeadendApi.Tests.Service.<Domain>;

public class <ClassName>Tests
{
    [Fact]
    public async Task MethodName_Scenario_ExpectedResult()
    {
        // Arrange
        var dep = new Mock<IDependency>();
        var sut = new TargetClass(NullLogger<TargetClass>.Instance, dep.Object);

        // Act
        var result = await sut.MethodAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        dep.Verify(d => d.SomeCall(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

**What to cover:**
- Happy path with valid inputs.
- Null/empty inputs (nullable types are enabled).
- `CancellationToken` cancellation behavior.
- Exception paths (use `Assert.ThrowsAsync<T>`).
- Edge cases: empty collections, boundary values, concurrent access patterns.
- For database services: mock `DatabaseWriteCoordinator` and repositories.
- For relay services: verify zero-copy streaming behavior, token validation.
- For TVHeadend API services: mock `IApiClient`, verify request construction.

### 4. Verify

- Build:
  ```
  dotnet build Jellyfin.Plugin.TvHeadendApi.sln -c Release
  ```
- Run tests:
  ```
  dotnet test Jellyfin.Plugin.TvHeadendApi.Tests -c Release --no-build --filter "Category!=LiveIntegration"
  ```
- Confirm all new tests pass and no existing tests broke.
