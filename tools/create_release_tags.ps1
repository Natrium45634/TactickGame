<#
.SYNOPSIS
    (Re)creates the annotated release tags vX.Y.Z from release_archives/*/BUILD_INFO.txt.

.DESCRIPTION
    Every archived release records the exact commit it was built from. If the tags were not
    pushed together with the branch (for example, the build environment could only push the
    branch), this script recreates them locally at exactly those commits. It never moves or
    deletes an existing tag: a tag that already points to a different commit is reported as an
    error and left untouched. Afterwards publish the tags with: git push origin --tags

.EXAMPLE
    pwsh tools/create_release_tags.ps1
    git push origin --tags
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([string]$ArchiveRoot)

. "$PSScriptRoot/common.ps1"

if (-not $ArchiveRoot) { $ArchiveRoot = $script:DefaultArchiveRoot }
$failed = $false

foreach ($dir in Get-ChildItem -Directory $ArchiveRoot | Sort-Object Name) {
    $infoPath = Join-Path $dir.FullName 'BUILD_INFO.txt'
    if (-not (Test-Path $infoPath)) { continue }
    $info = Read-BuildInfo $infoPath
    $tag = $info['tag']
    $commit = $info['commit']
    if (-not $tag -or -not $commit) { Write-Warning "$infoPath has no tag/commit"; continue }

    & git -C $script:RepoRoot cat-file -e "$commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "SKIP  ${tag}: commit $commit is not in this clone (git fetch?)" -ForegroundColor Yellow
        continue
    }

    $existing = Get-GitOutput @('rev-list', '-n', '1', "refs/tags/$tag")
    if ($existing) {
        if ($existing -eq $commit) { Write-Host "OK    $tag -> $commit (already exists)" }
        else { Write-Host "ERROR $tag exists but points to $existing, archive says $commit. Not changed." -ForegroundColor Red; $failed = $true }
        continue
    }

    if ($PSCmdlet.ShouldProcess($tag, "git tag -a at $commit")) {
        Invoke-Checked 'git' @('-C', $script:RepoRoot, 'tag', '-a', $tag, $commit, '-m', "HexTactics $tag")
        Write-Host "NEW   $tag -> $commit" -ForegroundColor Green
    }
}

if ($failed) { exit 1 }
Write-Host 'Publish tags with: git push origin --tags'
