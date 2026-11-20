#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Build', 'Test', 'Publish')][string]$Task = 'Build',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$OutputDirectory = 'dist/windows',
    [string]$ArchivePath = 'dist/TextDownloader-Windows-x64.zip',
    [string]$ChecksumPath = 'dist/SHA256SUMS.txt'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/TextDownloader/TextDownloader.csproj'
$coreTests = Join-Path $root 'tests/TextDownloader.Core.Tests/TextDownloader.Core.Tests.csproj'
$uiTests = Join-Path $root 'tests/TextDownloader.UiTests/TextDownloader.UiTests.csproj'

function Invoke-DotNet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]): код завершения $LASTEXITCODE" }
}

function Resolve-DistPath([string]$Path) {
    $full = if ([IO.Path]::IsPathRooted($Path)) {
        [IO.Path]::GetFullPath($Path)
    } else {
        [IO.Path]::GetFullPath((Join-Path $root $Path))
    }
    $dist = [IO.Path]::GetFullPath((Join-Path $root 'dist'))
    $relative = [IO.Path]::GetRelativePath($dist, $full)
    if ($relative -eq '.' -or $relative -eq '..' -or
        $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -or
        [IO.Path]::IsPathRooted($relative)) {
        throw 'Каталоги публикации и архивы должны находиться внутри dist этого проекта.'
    }
    return $full
}

if (-not $IsWindows) { throw 'Сборка и проверка WPF выполняются в Windows с .NET 8 SDK и PowerShell 7.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Установите .NET 8 SDK.' }
if (-not $Version) {
    [xml]$definition = Get-Content -LiteralPath (Join-Path $root 'Version.props') -Raw
    $Version = [string]$definition.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Неверная версия: $Version" }

Push-Location $root
try {
    switch ($Task) {
        'Build' {
            Invoke-DotNet -Arguments @('build', $project, '-c', $Configuration, "-p:Version=$Version")
        }
        'Test' {
            # Parse every development script; failures here must not become a green CI.
            foreach ($script in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1') {
                $tokens = $null
                $errors = $null
                [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw "Ошибка синтаксиса $($script.Name): $($errors -join '; ')" }
            }
            & (Join-Path $PSScriptRoot 'release-tests.ps1')
            Invoke-DotNet -Arguments @('build', $project, '-c', $Configuration, "-p:Version=$Version")
            Invoke-DotNet -Arguments @('run', '--project', $coreTests, '-c', $Configuration)
            Invoke-DotNet -Arguments @('run', '--project', $uiTests, '-c', $Configuration)
        }
        'Publish' {
            $output = Resolve-DistPath $OutputDirectory
            $archive = Resolve-DistPath $ArchivePath
            $checksums = Resolve-DistPath $ChecksumPath
            if (Test-Path -LiteralPath $output) {
                if ((Get-Item -LiteralPath $output).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw 'Каталог публикации не должен быть ссылкой.'
                }
                Remove-Item -LiteralPath $output -Recurse -Force
            }
            Invoke-DotNet -Arguments @('publish', $project, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
                "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:AssemblyVersion=$Version.0",
                '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true',
                '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false',
                '-o', $output)
            foreach ($name in @('README.md', 'THIRD_PARTY_NOTICES.md')) {
                Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $output $name)
            }
            foreach ($name in @('docs', 'third-party')) {
                Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $output $name) -Recurse
            }
            $packageFiles = @(
                Get-ChildItem -LiteralPath $output -Recurse -File | Sort-Object FullName | ForEach-Object {
                    [ordered]@{
                        path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/')
                        size = $_.Length
                        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                    }
                }
            )
            [ordered]@{ schema = 1; application = 'TextDownloader'; version = $Version; files = $packageFiles } |
                ConvertTo-Json -Depth 5 |
                Set-Content -LiteralPath (Join-Path $output 'windows-package.json') -Encoding utf8NoBOM
            & (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $output -Version $Version
            & (Join-Path $PSScriptRoot 'published-smoke.ps1') -Directory $output
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $archive))
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $checksums))
            Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
            $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $([IO.Path]::GetFileName($archive))" |
                Set-Content -LiteralPath $checksums -Encoding utf8NoBOM
            Write-Host "Text Downloader $Version`: $archive"
        }
    }
}
finally { Pop-Location }
