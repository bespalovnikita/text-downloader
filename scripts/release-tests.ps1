#requires -Version 7.0
# Offline tests: the gh command below models asset storage. No GitHub requests occur.
$ErrorActionPreference = 'Stop'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('TextDownloader-release-tests-' + [guid]::NewGuid().ToString('N'))
$local = Join-Path $temporary 'local'
$remote = Join-Path $temporary 'remote'
$tag = 'v0.1.0'
$commit = '0123456789abcdef0123456789abcdef01234567'
$repository = 'bespalovnikita/text-downloader'
$scriptFile = Join-Path $PSScriptRoot 'publish-release.ps1'
$oldGh = Get-Item Function:gh -ErrorAction SilentlyContinue

function Assert-Release([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Set-LocalAssets([string]$Contents) {
    [IO.File]::WriteAllText((Join-Path $local 'TextDownloader-Windows-x64.zip'), $Contents)
    $hash = (Get-FileHash -LiteralPath (Join-Path $local 'TextDownloader-Windows-x64.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  TextDownloader-Windows-x64.zip" | Set-Content (Join-Path $local 'SHA256SUMS.txt') -Encoding utf8NoBOM
}
function Invoke-TestRelease {
    & $scriptFile -Repository $repository -Tag $tag -CommitSha $commit -AssetDirectory $local 6>$null
}
function global:gh {
    $arguments = @($args)
    $state = $global:TextDownloaderReleaseTestState
    $global:LASTEXITCODE = 0
    if ($arguments[0] -eq 'api') {
        $route = @($arguments | Where-Object { $_ -like 'repos/*' })[0]
        if ($route.EndsWith('/git/ref/tags/v0.1.0')) {
            $state.tagReads++
            return (@{ object = @{ type = 'tag'; sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' } } | ConvertTo-Json -Compress)
        }
        if ($route.EndsWith('/git/tags/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')) {
            $state.tagReads++
            $targetCommit = if ($state.mismatchedTag) { 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' } else { '0123456789abcdef0123456789abcdef01234567' }
            return (@{ object = @{ type = 'commit'; sha = $targetCommit } } | ConvertTo-Json -Compress)
        }
        if ($route.EndsWith('/releases') -and $arguments -contains 'POST') {
            if ($state.tagReads -lt 2) { throw 'Creation did not verify and peel the remote annotated tag.' }
            $payloadFile = $arguments[[Array]::IndexOf($arguments, '--input') + 1]
            $payload = Get-Content -LiteralPath $payloadFile -Raw | ConvertFrom-Json
            $state.release = @{ id = 1234; tag_name = $payload.tag_name; body = $payload.body; draft = $true; assets = @() }
            $state.mutations.Add('create')
            $receipt = $state.release.Clone()
            if ($state.badReceipt) { $receipt.id = 0 }
            return ($receipt | ConvertTo-Json -Depth 8 -Compress)
        }
        $releasePageMatch = [regex]::Match($route, '/releases\?per_page=100&page=(\d+)$')
        if ($releasePageMatch.Success) {
            $state.listReads++
            [object[]]$items = @()
            if ($releasePageMatch.Groups[1].Value -eq '1') {
                # A full first page requires requesting the later matching page.
                $items = @(for ($index = 0; $index -lt 100; $index++) {
                    @{ tag_name = "v98.7.$index"; body = 'Unrelated draft'; draft = $true; assets = @() }
                })
            } elseif ($releasePageMatch.Groups[1].Value -eq '2' -and $state.release -and -not $state.hideDraft) {
                $items = @($state.release)
            }
            return (ConvertTo-Json -InputObject $items -Depth 8 -Compress)
        }
        if ($route.EndsWith('/latest') -and $state.latestTag) {
            return (@{ tag_name = $state.latestTag } | ConvertTo-Json -Compress)
        }
        # Match real GitHub behavior: the tag endpoint does not reveal a draft.
        if ($route.EndsWith('/latest') -or -not $state.release -or $state.release.draft) {
            $global:LASTEXITCODE = 1
            return 'gh: Not Found (HTTP 404)'
        }
        return ($state.release | ConvertTo-Json -Depth 8 -Compress)
    }
    $operation = $arguments[1]
    switch ($operation) {
        'upload' {
            $path = $arguments[3]
            $name = [IO.Path]::GetFileName($path)
            Copy-Item -LiteralPath $path -Destination (Join-Path $state.remote $name)
            $state.release.assets += @{ name = $name; size = (Get-Item -LiteralPath $path).Length }
            $state.mutations.Add("upload:$name")
        }
        'download' {
            $destination = $arguments[[Array]::IndexOf($arguments, '--dir') + 1]
            [void][IO.Directory]::CreateDirectory($destination)
            Get-ChildItem -LiteralPath $state.remote -File | Copy-Item -Destination $destination
        }
        'edit' {
            $state.release.draft = $false
            $state.latestFlag = if ($arguments -contains '--latest=true') { 'true' } else { 'false' }
            $state.mutations.Add('publish')
        }
        default { throw "Unexpected fake gh invocation: $($arguments -join ' ')" }
    }
}

try {
    [void][IO.Directory]::CreateDirectory($local)
    [void][IO.Directory]::CreateDirectory($remote)
    $global:TextDownloaderReleaseTestState = @{
        release = $null
        remote = $remote
        listReads = 0
        tagReads = 0
        hideDraft = $true
        mutations = [Collections.Generic.List[string]]::new()
    }
    $state = $global:TextDownloaderReleaseTestState
    Set-LocalAssets 'verified fixture archive'
    Invoke-TestRelease
    Assert-Release (-not $state.release.draft -and $state.release.assets.Count -eq 2) 'A new release was not fully published.'
    Assert-Release ($state.listReads -eq 2) 'Creation incorrectly rediscovered its draft through the stale tag/list index.'
    Assert-Release ($state.tagReads -eq 2) 'The remote annotated tag was not verified and peeled before creation.'
    Assert-Release ($state.mutations.Count -eq 4) 'The release did not perform exactly create/upload/upload/publish.'
    Assert-Release ($state.latestFlag -eq 'true') 'The first release was not made latest.'
    Invoke-TestRelease
    Assert-Release ($state.mutations.Count -eq 4) 'A published release was modified on retry.'

    $originalHash = (Get-FileHash (Join-Path $remote 'TextDownloader-Windows-x64.zip')).Hash
    Set-LocalAssets 'changed fixture archive!'
    $rejected = $false
    try { Invoke-TestRelease } catch { $rejected = $true }
    Assert-Release $rejected 'Different published bytes were accepted.'
    Assert-Release ($state.mutations.Count -eq 4 -and (Get-FileHash (Join-Path $remote 'TextDownloader-Windows-x64.zip')).Hash -eq $originalHash) 'A rejected build changed published assets.'

    Set-LocalAssets 'verified fixture archive'
    $state.hideDraft = $false
    $state.release.body = 'Someone else owns this release.'
    $state.release.draft = $true
    $rejected = $false
    try { Invoke-TestRelease } catch { $rejected = $true }
    Assert-Release ($rejected -and $state.mutations.Count -eq 4) 'A release without the ownership marker was changed.'

    $state.release.body = "<!-- TextDownloader release repository=$repository tag=$tag commit=$commit -->"
    $state.release.draft = $true
    $state.latestTag = 'v9.0.0'
    $state.release.assets = @($state.release.assets | Where-Object name -eq 'TextDownloader-Windows-x64.zip')
    Remove-Item -LiteralPath (Join-Path $remote 'SHA256SUMS.txt')
    Invoke-TestRelease
    Assert-Release ($state.release.assets.Count -eq 2 -and -not $state.release.draft -and $state.mutations.Count -eq 6) 'An owned partial draft did not resume safely.'
    Assert-Release ($state.latestFlag -eq 'false') 'An older late release rolled latest backwards.'
    Assert-Release ($state.listReads -ge 3) 'Resuming a draft did not use the authenticated release list.'

    # Invalid creation receipts must never proceed to upload or publication.
    $state.release = $null
    $state.hideDraft = $true
    $state.badReceipt = $true
    $mutationsBefore = $state.mutations.Count
    $rejected = $false
    try { Invoke-TestRelease } catch { $rejected = $true }
    Assert-Release ($rejected -and $state.mutations.Count -eq $mutationsBefore + 1 -and $state.release.draft) 'An invalid creation receipt was uploaded or published.'
    $state.release = $null
    $state.badReceipt = $false
    $state.mismatchedTag = $true
    $mutationsBefore = $state.mutations.Count
    $rejected = $false
    try { Invoke-TestRelease } catch { $rejected = $true }
    Assert-Release ($rejected -and $state.mutations.Count -eq $mutationsBefore) 'A mismatched remote tag allowed release creation.'
    Write-Host 'Release tests: stale-index creation receipt, annotated tag verification, paginated draft resume, immutable assets, ownership and latest ordering passed.'
}
finally {
    Remove-Item Function:gh -ErrorAction SilentlyContinue
    if ($oldGh) { Set-Item Function:global:gh $oldGh.ScriptBlock }
    Remove-Variable TextDownloaderReleaseTestState -Scope Global -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
    $global:LASTEXITCODE = 0
}
