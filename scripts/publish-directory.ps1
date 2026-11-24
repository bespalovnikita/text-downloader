#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$AddUpdateSmokeMarker
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Публикация Windows-пакета выполняется в Windows.' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Неверная версия: $Version" }
$root = Split-Path -Parent $PSScriptRoot
$Directory = [IO.Path]::GetFullPath($Directory)
$dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
$relative = [IO.Path]::GetRelativePath($dist, $Directory)
if ($relative -eq '.' -or $relative -eq '..' -or
    $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -or [IO.Path]::IsPathRooted($relative)) {
    throw 'Каталог публикации должен находиться внутри dist этого проекта.'
}
if (Test-Path -LiteralPath $Directory) {
    if ((Get-Item -LiteralPath $Directory).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Каталог публикации не должен быть ссылкой.'
    }
    Remove-Item -LiteralPath $Directory -Recurse -Force
}
& dotnet publish (Join-Path $root 'src/TextDownloader/TextDownloader.csproj') -c $Configuration -r win-x64 --self-contained true `
    "-p:Version=$Version" "-p:FileVersion=$Version.0" "-p:AssemblyVersion=$Version.0" `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishReferencesDocumentationFiles=false -p:DebugType=None -p:DebugSymbols=false -o $Directory
if ($LASTEXITCODE -ne 0) { throw "dotnet publish: код завершения $LASTEXITCODE" }
foreach ($name in @('README.md', 'THIRD_PARTY_NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $Directory $name)
}
foreach ($name in @('docs', 'third-party')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $Directory $name) -Recurse
}
if ($AddUpdateSmokeMarker) {
    "Text Downloader synthetic next-version update fixture $Version" |
        Set-Content -LiteralPath (Join-Path $Directory 'docs/update-smoke.md') -Encoding utf8NoBOM
}
$packageFiles = @(
    Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
[ordered]@{ schema = 1; application = 'TextDownloader'; version = $Version; files = $packageFiles } |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $Directory 'windows-package.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $Directory -Version $Version
