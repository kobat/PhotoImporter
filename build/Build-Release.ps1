[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'PhotoImporter.sln'
$projectPath = Join-Path $repositoryRoot 'src\PhotoImporter.App\PhotoImporter.App.csproj'
$testProjectPath = Join-Path $repositoryRoot 'tests\PhotoImporter.Core.Tests\PhotoImporter.Core.Tests.csproj'
$nugetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
$buildOutput = Join-Path $repositoryRoot 'src\PhotoImporter.App\bin\Release\net481'
$artifactBase = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $artifactBase 'release'))
$packageName = "PhotoImporter-v$Version"
$stagingDirectory = Join-Path $releaseRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName-win.zip"
$checksumPath = "$zipPath.sha256"

$artifactPrefix = $artifactBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $releaseRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release output is outside the artifacts directory: $releaseRoot"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
if ($projectVersion -ne $Version) {
    throw "Requested version $Version does not match project version $projectVersion."
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    if (-not $SkipRestore) {
        Invoke-DotNet -Arguments @('restore', $solutionPath, '--configfile', $nugetConfigPath)
    }

    Invoke-DotNet -Arguments @('build', $solutionPath, '-c', 'Release', '--no-restore', '-p:ContinuousIntegrationBuild=true')
    Invoke-DotNet -Arguments @('test', $testProjectPath, '-c', 'Release', '--no-build')

    $executablePath = Join-Path $buildOutput 'PhotoImporter.exe'
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Release executable was not produced: $executablePath"
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
    if ($versionInfo.ProductVersion -ne $Version -or $versionInfo.FileVersion -ne "$Version.0") {
        throw "Executable version does not match $Version. Product=$($versionInfo.ProductVersion), File=$($versionInfo.FileVersion)"
    }

    if (Test-Path -LiteralPath $releaseRoot) {
        Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

    Copy-Item -LiteralPath $executablePath -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'DISTRIBUTION-README.txt') -Destination (Join-Path $stagingDirectory 'README.txt')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $stagingDirectory 'LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.txt') -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'src\PhotoImporter.App\Legal\Licenses') -Destination $stagingDirectory -Recurse

    Compress-Archive -LiteralPath $stagingDirectory -DestinationPath $zipPath -CompressionLevel Optimal

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entryNames = @($archive.Entries | ForEach-Object FullName)
        $forbiddenEntries = @($entryNames | Where-Object { $_ -match '\.(dll|pdb|config)$' })
        if ($forbiddenEntries.Count -ne 0) {
            throw "Release archive contains files that should be embedded or omitted: $($forbiddenEntries -join ', ')"
        }
        if (-not ($entryNames | Where-Object { $_ -match '(^|/)PhotoImporter\.exe$' })) {
            throw 'Release archive does not contain PhotoImporter.exe.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumLine = "$hash *$([IO.Path]::GetFileName($zipPath))`n"
    [IO.File]::WriteAllText($checksumPath, $checksumLine, [Text.UTF8Encoding]::new($false))

    Write-Host "Release archive: $zipPath"
    Write-Host "SHA-256:        $hash"
}
finally {
    Pop-Location
}
