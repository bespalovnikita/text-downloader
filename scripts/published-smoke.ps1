#requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Проверка окна опубликованного приложения требует Windows.' }
$Directory = [IO.Path]::GetFullPath($Directory)
$profile = Join-Path ([IO.Path]::GetTempPath()) ('TextDownloader-smoke-' + [guid]::NewGuid().ToString('N'))
$previousProfile = $env:TD_DATA_DIRECTORY
$process = $null
try {
    [void][IO.Directory]::CreateDirectory($profile)
    $env:TD_DATA_DIRECTORY = $profile
    $process = Start-Process -FilePath (Join-Path $Directory 'TextDownloader.exe') -WorkingDirectory $Directory -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "Опубликованное приложение завершилось до открытия окна: $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
            $process.MainWindowTitle -eq 'Text Downloader' -and $process.Responding) {
            $ready = $true
            break
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Опубликованный EXE не открыл отвечающее главное окно за 45 секунд.' }
    [void]$process.CloseMainWindow()
    if (-not $process.WaitForExit(10000)) { throw 'Опубликованное приложение не завершилось после закрытия окна.' }
    if ($process.ExitCode -ne 0) { throw "После закрытия окна приложение вернуло $($process.ExitCode)." }
    Write-Host 'Опубликованный автономный EXE открыл и закрыл настоящее окно WPF.'
}
finally {
    if ($null -eq $previousProfile) { Remove-Item Env:TD_DATA_DIRECTORY -ErrorAction SilentlyContinue }
    else { $env:TD_DATA_DIRECTORY = $previousProfile }
    if ($process) {
        try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
        finally { $process.Dispose() }
    }
    if (Test-Path -LiteralPath $profile) { Remove-Item -LiteralPath $profile -Recurse -Force }
}
