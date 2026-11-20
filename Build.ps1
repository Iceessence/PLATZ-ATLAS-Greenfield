[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $Test,
    [switch] $Bundle,
    [switch] $Run,
    [switch] $Offline,
    [switch] $Cpu,
    [switch] $OpenGl,
    [switch] $Vulkan,
    [string] $Project
)

$ErrorActionPreference = 'Stop'
if (($Cpu -or $OpenGl -or $Vulkan -or $Project) -and -not $Run) {
    throw 'Use -Run with -Cpu, -OpenGl, -Vulkan, or -Project.'
}
$modeCount = @($Cpu, $OpenGl, $Vulkan | Where-Object { $_ }).Count
if ($modeCount -gt 1) {
    throw 'Choose at most one of -Cpu, -OpenGl, or -Vulkan.'
}

$dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw 'Add the Windows x64 .NET SDK 10.0.401 to PATH before building. See README.md.'
}

function Invoke-AtlasDotnet {
    & $dotnet.Source @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($args[0]) exited with code $LASTEXITCODE."
    }
}

$studioProject = 'src/PlatzAtlas.Studio/PlatzAtlas.Studio.csproj'
$playerProject = 'src/PlatzAtlas.Player/PlatzAtlas.Player.csproj'
$testProject = 'tests/PlatzAtlas.Core.Tests/PlatzAtlas.Core.Tests.csproj'
$studioExecutable = Join-Path $PSScriptRoot (
    'artifacts/bin/PlatzAtlas.Studio/{0}/PlatzAtlas.Studio.exe' -f $Configuration.ToLowerInvariant()
)
$playerExecutable = Join-Path $PSScriptRoot (
    'artifacts/bin/PlatzAtlas.Player/{0}/PlatzAtlas.Player.exe' -f $Configuration.ToLowerInvariant()
)
$restoreOptions = @('--locked-mode', '--source', 'https://api.nuget.org/v3/index.json')
if ($Offline) {
    $packageCache = $env:NUGET_PACKAGES
    if (-not $packageCache) {
        $packageCache = Join-Path $env:USERPROFILE '.nuget/packages'
    }
    if (-not (Test-Path -LiteralPath $packageCache -PathType Container)) {
        throw 'The local NuGet package cache is missing. Prepare it using the README.md offline build instructions.'
    }
    $packageCache = (Resolve-Path -LiteralPath $packageCache).ProviderPath
    $restoreOptions = @('--locked-mode', '--source', $packageCache, '-p:NuGetAudit=false')
}

$buildOptions = @('-m:1', '--disable-build-servers')
$previousTelemetry = $env:DOTNET_CLI_TELEMETRY_OPTOUT
$previousWorkloadNotify = $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE
$previousGenerateCertificate = $env:DOTNET_GENERATE_ASPNET_CERTIFICATE
Push-Location -LiteralPath $PSScriptRoot
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    Invoke-AtlasDotnet restore $studioProject @restoreOptions @buildOptions
    Invoke-AtlasDotnet build $studioProject --configuration $Configuration --no-restore @buildOptions
    Invoke-AtlasDotnet restore $playerProject @restoreOptions @buildOptions
    Invoke-AtlasDotnet build $playerProject --configuration $Configuration --no-restore @buildOptions
    if ($Test) {
        Invoke-AtlasDotnet restore $testProject @restoreOptions @buildOptions
        Invoke-AtlasDotnet build $testProject --configuration $Configuration --no-restore @buildOptions
        Invoke-AtlasDotnet run --project $testProject --configuration $Configuration --no-build
    }
    if ($Bundle) {
        & (Join-Path $PSScriptRoot 'tools/BundlePlayer.ps1') -Dotnet $dotnet.Source -Configuration $Configuration
    }
}
finally {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = $previousTelemetry
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = $previousWorkloadNotify
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = $previousGenerateCertificate
    Pop-Location
}

Write-Output "Studio executable: $studioExecutable"
Write-Output "Player executable: $playerExecutable"
if ($Run) {
    $studioOptions = @()
    if ($Cpu) {
        $studioOptions += '--cpu'
    }
    elseif ($OpenGl) {
        $studioOptions += '--opengl'
    }
    elseif ($Vulkan) {
        $studioOptions += '--vulkan'
    }
    if ($Project) {
        $studioOptions += @('--project', $Project)
    }
    & $studioExecutable @studioOptions
}
