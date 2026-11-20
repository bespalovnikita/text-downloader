#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$CommitSha,
    [Parameter(Mandatory)][string]$AssetDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Repository -ne 'bespalovnikita/text-downloader' -or $Tag -notmatch '^v\d+\.\d+\.\d+$' -or
    $CommitSha -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Неверные параметры публикации Text Downloader.'
}
$AssetDirectory = [IO.Path]::GetFullPath($AssetDirectory)
$archiveName = 'TextDownloader-Windows-x64.zip'
$names = @($archiveName, 'SHA256SUMS.txt')
foreach ($name in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $AssetDirectory $name) -PathType Leaf)) {
        throw "Отсутствует файл выпуска: $name"
    }
}
$localHash = (Get-FileHash -LiteralPath (Join-Path $AssetDirectory $archiveName) -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = (Get-Content -LiteralPath (Join-Path $AssetDirectory 'SHA256SUMS.txt') -Raw).Trim()
if ($checksumText -ne "$localHash  $archiveName") { throw 'Контрольная сумма подготовленного ZIP не совпадает.' }
$marker = "<!-- TextDownloader release repository=$Repository tag=$Tag commit=$CommitSha -->"

function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI: $($result -join [Environment]::NewLine)" }
    return $result -join [Environment]::NewLine
}

function Get-Release([string]$Reference) {
    $result = & gh api "repos/$Repository/releases/$Reference" 2>&1
    if ($LASTEXITCODE -ne 0) {
        if (($result -join ' ') -match 'HTTP 404') {
            if (-not $Reference.StartsWith('tags/', [StringComparison]::Ordinal)) { return $null }
            # The tag endpoint can hide drafts, even from the token that just
            # created them. The authenticated release list includes owned drafts.
            # Enumerate all pages and match the exact tag; never create a second
            # draft merely because /releases/tags/{tag} returned 404.
            $wantedTag = $Reference.Substring(5)
            $candidates = @()
            $pageNumber = 1
            do {
                $listed = Invoke-Gh -Arguments @('api', "repos/$Repository/releases?per_page=100&page=$pageNumber")
                $releasePage = $listed | ConvertFrom-Json -NoEnumerate
                if ($releasePage -isnot [array]) { throw 'GitHub вернул неверный список выпусков.' }
                foreach ($candidate in $releasePage) {
                    if ([string]::Equals([string]$candidate.tag_name, $wantedTag, [StringComparison]::Ordinal)) {
                        $candidates += $candidate
                    }
                }
                $pageNumber++
            } while ($releasePage.Count -eq 100)
            if ($candidates.Count -gt 1) { throw "Найдено несколько выпусков с тегом $wantedTag. Они оставлены без изменений." }
            if ($candidates.Count -eq 1) { return $candidates[0] }
            return $null
        }
        throw "Не удалось проверить существующий выпуск: $($result -join [Environment]::NewLine)"
    }
    return ($result -join [Environment]::NewLine | ConvertFrom-Json)
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('TextDownloader-release-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $release = Get-Release "tags/$Tag"
    if ($release -and -not ([string]$release.body).Contains($marker)) {
        throw 'Существующий выпуск не принадлежит этой сборке; он оставлен без изменений.'
    }
    if (-not $release) {
        $template = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.github/RELEASE_TEMPLATE.md') -Raw
        $notes = Join-Path $temporary 'notes.md'
        ($template.TrimEnd() + "`n`n" + $marker + "`n") | Set-Content -LiteralPath $notes -Encoding utf8NoBOM
        [void](Invoke-Gh -Arguments @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--target', $CommitSha,
            '--draft', '--title', "Text Downloader $Tag", '--notes-file', $notes))
        $release = Get-Release "tags/$Tag"
        if (-not $release -or -not $release.draft -or -not ([string]$release.body).Contains($marker)) {
            throw 'Не удалось подтвердить созданный собственный черновик. Загрузка файлов и публикация остановлены.'
        }
    }
    foreach ($asset in $release.assets) {
        if ($asset.name -notin $names) { throw "Неожиданный файл в выпуске: $($asset.name). Выпуск не изменён." }
    }
    foreach ($name in $names) {
        $asset = @($release.assets | Where-Object name -eq $name)
        if ($asset.Count -gt 1) { throw "Повторный файл выпуска: $name" }
        if ($asset.Count -eq 0) {
            if (-not $release.draft) { throw "Опубликованный выпуск неполон: $name. Он оставлен без изменений." }
            [void](Invoke-Gh -Arguments @('release', 'upload', $Tag, (Join-Path $AssetDirectory $name), '--repo', $Repository))
        } elseif ($asset[0].size -ne (Get-Item -LiteralPath (Join-Path $AssetDirectory $name)).Length) {
            throw "Существующий файл $name не соответствует сборке. Перезапись запрещена."
        }
    }
    # Verify the bytes GitHub actually serves, not just the names/sizes of assets.
    $download = Join-Path $temporary 'download'
    [void](Invoke-Gh -Arguments @('release', 'download', $Tag, '--repo', $Repository, '--dir', $download))
    foreach ($name in $names) {
        $expected = (Get-FileHash -LiteralPath (Join-Path $AssetDirectory $name) -Algorithm SHA256).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $download $name) -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Загруженный из GitHub файл $name не прошёл проверку SHA-256." }
    }
    if ($release.draft) {
        $latest = Get-Release 'latest'
        $makeLatest = -not $latest
        if ($latest -and $latest.tag_name -match '^v(\d+\.\d+\.\d+)$') {
            $makeLatest = [version]$Tag.Substring(1) -gt [version]$Matches[1]
        }
        $latestFlag = if ($makeLatest) { '--latest=true' } else { '--latest=false' }
        [void](Invoke-Gh -Arguments @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false', $latestFlag))
    }
    Write-Host "Выпуск проверен и опубликован: https://github.com/$Repository/releases/tag/$Tag"
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
