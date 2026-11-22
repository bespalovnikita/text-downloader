#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$CommitSha
)
$ErrorActionPreference = 'Stop'
if ($Repository -ne 'bespalovnikita/text-downloader' -or $Tag -notmatch '^v\d+\.\d+\.\d+$' -or
    $CommitSha -notmatch '^[a-f0-9]{40}$' -or (git rev-parse HEAD) -cne $CommitSha) {
    throw 'Недопустимый тег или коммит выпуска.'
}

$uri = "repos/$Repository/git/ref/tags/$Tag"
$existing = & gh api $uri --jq '.object.type + " " + .object.sha' 2>&1
if ($LASTEXITCODE -eq 0) {
    if ($existing -ceq "commit $CommitSha") { return }
    throw 'Тег выпуска уже занят другим указателем.'
}
if (($existing -join ' ') -notmatch 'HTTP 404') {
    throw "Не удалось проверить тег: $existing"
}
& gh api --method POST "repos/$Repository/git/refs" -f "ref=refs/tags/$Tag" -f "sha=$CommitSha" --silent
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать тег выпуска.' }
