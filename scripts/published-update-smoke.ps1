#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Проверка опубликованного установщика требует Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$Directory = [IO.Path]::GetFullPath($Directory)
$current = Get-Content -LiteralPath (Join-Path $Directory 'windows-package.json') -Raw | ConvertFrom-Json
$version = [version]$current.version
$nextVersion = '{0}.{1}.{2}' -f $version.Major, $version.Minor, ($version.Build + 1)
$fixture = Join-Path $root ('dist/update-smoke-' + [guid]::NewGuid().ToString('N'))
$nextDirectory = Join-Path $fixture 'next'
try {
    # This is a local, real PE with another version. It is never a GitHub release
    # and does not recursively invoke build.ps1 -Task Publish.
    & (Join-Path $PSScriptRoot 'publish-directory.ps1') -Directory $nextDirectory -Version $nextVersion `
        -Configuration $Configuration -AddUpdateSmokeMarker
    & dotnet run --project (Join-Path $root 'tests/TextDownloader.UpdateProcessTests/TextDownloader.UpdateProcessTests.csproj') `
        -c $Configuration -- $Directory $nextDirectory
    if ($LASTEXITCODE -ne 0) { throw "Опубликованный установщик не прошёл process smoke: $LASTEXITCODE" }
}
finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
}
