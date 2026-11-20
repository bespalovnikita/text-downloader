#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$Directory = [IO.Path]::GetFullPath($Directory)
$manifestPath = Join-Path $Directory 'windows-package.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.application -ne 'TextDownloader' -or $manifest.version -ne $Version) {
    throw 'Неверные метаданные Windows-пакета.'
}
$paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $name = [string]$entry.path
    if (-not $name -or [IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
        $name.Contains('\') -or $name.Split('/') -contains '..' -or $name.Split('/') -contains '.' -or
        -not $paths.Add($name)) {
        throw "Небезопасный или повторный путь в пакете: $name"
    }
    $file = Get-Item -LiteralPath (Join-Path $Directory $name)
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $file.Length -ne $entry.size -or
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {
        throw "Файл не соответствует манифесту: $name"
    }
}
if (-not $paths.Contains('TextDownloader.exe')) { throw 'В пакете нет TextDownloader.exe.' }
foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -Force) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'В пакете найдена ссылка.' }
    if (-not $file.PSIsContainer) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
        if ($relative -ne 'windows-package.json' -and -not $paths.Contains($relative)) {
            throw "Незаявленный файл в пакете: $relative"
        }
    }
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory 'TextDownloader.exe'))
$actual = [version]$info.FileVersion
$expected = [version]$Version
if ($actual.Major -ne $expected.Major -or $actual.Minor -ne $expected.Minor -or $actual.Build -ne $expected.Build) {
    throw "Версия EXE $actual не соответствует выпуску $Version."
}
Write-Host "Пакет проверен: $($paths.Count) файлов, версия $Version."
