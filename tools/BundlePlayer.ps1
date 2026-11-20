param(
    [Parameter(Mandatory = $true)] [string] $Dotnet,
    [Parameter(Mandatory = $true)] [ValidateSet('Debug','Release')] [string] $Configuration
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$repo = Split-Path -Parent $PSScriptRoot
$distribution = [IO.Directory]::CreateDirectory((Join-Path $repo 'artifacts/distribution')).FullName
$stage = Join-Path $distribution ('player-stage-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $distribution ("PlatzAtlas.Player-{0}.zip" -f $Configuration.ToLowerInvariant())
$pending = $stage + '.zip'
[IO.Directory]::CreateDirectory($stage) | Out-Null
try {
    $publishOptions = @('-c', $Configuration, '--no-restore', '--no-self-contained', '--output', $stage,
        '-m:1', '--disable-build-servers')
    & $Dotnet publish (Join-Path $repo 'src/PlatzAtlas.Player/PlatzAtlas.Player.csproj') @publishOptions
    if ($LASTEXITCODE -ne 0) { throw "Player publish failed with exit code $LASTEXITCODE." }

    $notices = [IO.Directory]::CreateDirectory((Join-Path $stage 'licenses')).FullName
    [IO.File]::Copy((Join-Path $PSScriptRoot 'PlayerNotices/Silk.NET.txt'), (Join-Path $notices 'Silk.NET.txt'))
    $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $locks = (Get-Content -Raw -LiteralPath (Join-Path $repo 'src/PlatzAtlas.Player/packages.lock.json') |
        ConvertFrom-Json).dependencies.'net10.0-windows7.0'
    foreach ($name in @('Microsoft.DotNet.PlatformAbstractions', 'Microsoft.Extensions.DependencyModel')) {
        $source = Join-Path $cache ($name.ToLowerInvariant() + '/' + $locks.$name.resolved)
        foreach ($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
            [IO.File]::Copy((Join-Path $source $notice), (Join-Path $notices "$name-$notice"))
        }
    }

    $requirements = @(
        'PLATZ Player'
        'Use Windows x64 with the .NET 10 Windows Desktop Runtime installed.'
        'Open PlatzAtlas.Player.exe and select a .platzroom package exported by Studio.'
        'Move with the arrow keys or WASD. Use E when in range of a switch.'
        'The executable also accepts --room <path> and --cpu.'
    )
    [IO.File]::WriteAllLines((Join-Path $stage 'START-HERE.txt'), $requirements)
    $names = [string[]]@(
        Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
            $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        }
    )
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $manifest = foreach ($name in $names) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $stage $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS.txt'), [string[]]$manifest)
    $names += 'SHA256SUMS.txt'

    $stream = [IO.File]::Open($pending, [IO.FileMode]::CreateNew)
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($name in $names) {
                $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $entryStream = $entry.Open()
                try {
                    $inputFile = [IO.File]::OpenRead((Join-Path $stage $name))
                    try { $inputFile.CopyTo($entryStream) } finally { $inputFile.Dispose() }
                } finally { $entryStream.Dispose() }
            }
        } finally { $zip.Dispose() }
    } finally { $stream.Dispose() }
    if ([IO.File]::Exists($archive)) {
        [IO.File]::Replace($pending, $archive, [NullString]::Value)
    } else {
        [IO.File]::Move($pending, $archive)
    }
    Write-Output "Player bundle: $archive"
} finally {
    [IO.File]::Delete($pending)
    $resolvedStage = (Get-Item -LiteralPath $stage).FullName
    $validParent = [IO.Path]::GetDirectoryName($resolvedStage).Equals($distribution, [StringComparison]::OrdinalIgnoreCase)
    if ($validParent -and [IO.Path]::GetFileName($resolvedStage).StartsWith('player-stage-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
