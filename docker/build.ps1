param(
    [string]$ComposeService = "jellyfin",
    [string]$ComposeFile = "docker\docker-compose.yaml",
    [string]$VersionFile = "docker\dev-version.txt",
    [switch]$DryRun,
    [switch]$NoLogs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-VersionFromDockerfile {
    param([string]$DockerfilePath)

    if (-not (Test-Path $DockerfilePath)) {
        throw "Dockerfile not found: $DockerfilePath"
    }

    $line = Get-Content -Path $DockerfilePath | Where-Object { $_ -match '^ARG\s+VERSION=' } | Select-Object -First 1
    if (-not $line) {
        throw "Could not find 'ARG VERSION=...' in Dockerfile."
    }

    return ($line -replace '^ARG\s+VERSION=', '').Trim()
}

function Increment-Version {
    param([string]$VersionText)

    $parts = $VersionText.Split('.')
    if ($parts.Count -ne 4) {
        throw "Version must be in format major.minor.patch.revision, got '$VersionText'."
    }

    $major = [int]$parts[0]
    $minor = [int]$parts[1]
    $patch = [int]$parts[2]
    $rev = [int]$parts[3] + 1

    return "$major.$minor.$patch.$rev"
}

$repoRoot = (Split-Path -Parent $PSScriptRoot)
Push-Location $repoRoot
try {
    $dockerfilePath = Join-Path (Join-Path $repoRoot "docker") "jellyfin\Dockerfile"
    $composeFilePath = Join-Path $repoRoot $ComposeFile
    $versionFilePath = Join-Path $repoRoot $VersionFile

    $currentVersion = $null
    if (Test-Path $versionFilePath) {
        $currentVersion = (Get-Content -Path $versionFilePath -TotalCount 1).Trim()
        if (-not $currentVersion) {
            throw "Version file is empty: $versionFilePath"
        }
        Write-Step "Loaded current dev version from ${VersionFile}: $currentVersion"
    }
    else {
        $currentVersion = Get-VersionFromDockerfile -DockerfilePath $dockerfilePath
        Write-Step "Version file not found. Using Dockerfile ARG VERSION as base: $currentVersion"
    }

    $nextVersion = Increment-Version -VersionText $currentVersion
    Write-Step "Next dev version: $nextVersion"

    $testProjectPath = Join-Path $repoRoot "Jellyfin.Plugin.TvHeadendApi.Tests\Jellyfin.Plugin.TvHeadendApi.Tests.csproj"

    if ($DryRun) {
        Write-Host "[DRY-RUN] Would run: dotnet test $testProjectPath -c Release --filter `"Category!=LiveIntegration`""
        Write-Host "[DRY-RUN] Would run: docker compose -f $ComposeFile build --build-arg VERSION=$nextVersion $ComposeService"
        Write-Host "[DRY-RUN] Would run: docker compose -f $ComposeFile up -d --force-recreate $ComposeService"
        if (-not $NoLogs) {
            Write-Host "[DRY-RUN] Would run: docker compose -f $ComposeFile logs --no-color --tail=120 $ComposeService"
        }
        Write-Host "[DRY-RUN] Would write version file: $VersionFile -> $nextVersion"
        exit 0
    }

    if (-not (Test-Path $testProjectPath)) {
        throw "Test project not found: $testProjectPath"
    }

    Write-Step "Running unit tests"
    dotnet test $testProjectPath -c Release --filter "Category!=LiveIntegration"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }

    Write-Step "Building image with VERSION=$nextVersion"
    docker compose -f $ComposeFile build --build-arg "VERSION=$nextVersion" $ComposeService
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose build failed with exit code $LASTEXITCODE"
    }

    Write-Step "Recreating service '$ComposeService'"
    docker compose -f $ComposeFile up -d --force-recreate $ComposeService
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose up failed with exit code $LASTEXITCODE"
    }

    if (-not $NoLogs) {
        Write-Step "Recent logs"
        docker compose -f $ComposeFile logs --no-color --tail=120 $ComposeService
    }

    New-Item -ItemType Directory -Path (Split-Path -Parent $versionFilePath) -Force | Out-Null
    Set-Content -Path $versionFilePath -Value $nextVersion -NoNewline

    Write-Host ""
    Write-Host "Done. Dev version bumped to: $nextVersion" -ForegroundColor Green
    Write-Host "Next run starts from this value in: ${VersionFile}"
}
finally {
    Pop-Location
}
