<#
.SYNOPSIS
    Restores an older HexTactics version without touching the current one.

.DESCRIPTION
    -Mode Build  (default): verifies the archived ZIP of the version and extracts it into
                  -Destination (default: <user home>/HexTactics_versions/vX.Y.Z). Run HexTactics.exe there.
    -Mode Source: creates a separate Git worktree with the source code of tag vX.Y.Z
                  (default: <repo>/../HexTactics_src_vX.Y.Z). The current checkout is not changed.

    Existing non-empty destinations are never overwritten silently: the script asks for
    confirmation (or requires -Force in non-interactive use).

.EXAMPLE
    pwsh tools/restore_version.ps1 -Version 0.1.0
    pwsh tools/restore_version.ps1 -Version 0.1.0 -Mode Source
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('Build', 'Source')][string]$Mode = 'Build',
    [string]$Destination,
    [string]$ArchiveRoot,
    [switch]$Force
)

. "$PSScriptRoot/common.ps1"

Assert-Version $Version
$tag = "v$Version"
if (-not $ArchiveRoot) { $ArchiveRoot = $script:DefaultArchiveRoot }

function Confirm-Destination([string]$Path) {
    if ((Test-Path $Path) -and (Get-ChildItem -Force $Path | Select-Object -First 1)) {
        if ($Force) { return }
        if (-not $PSCmdlet.ShouldContinue("'$Path' is not empty. Files with the same names will be replaced. Continue?", 'Destination exists')) {
            Fail 'Cancelled by user.'
        }
    }
}

if ($Mode -eq 'Build') {
    $archive = Get-ArchiveInfo -Version $Version -ArchiveRoot $ArchiveRoot
    if (-not (Test-Path $archive.Zip)) {
        Fail "No archive for $tag at $($archive.Zip). If the repository uses Git LFS, run: git lfs pull"
    }
    $expected = ((Get-Content $archive.Sha256 -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = Get-Sha256 $archive.Zip
    if ($expected -ne $actual) { Fail "Checksum mismatch for $($archive.Zip): $actual != $expected" }

    if (-not $Destination) { $Destination = Join-Path ([Environment]::GetFolderPath('UserProfile')) "HexTactics_versions/$tag" }
    Confirm-Destination $Destination
    if ($PSCmdlet.ShouldProcess($Destination, "Extract $tag")) {
        New-Item -ItemType Directory -Force -Path $Destination | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive.Zip)
        try {
            foreach ($entry in $zip.Entries) {
                $target = Join-Path $Destination $entry.FullName
                if ($entry.FullName.EndsWith('/')) { New-Item -ItemType Directory -Force -Path $target | Out-Null; continue }
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            }
        }
        finally { $zip.Dispose() }
        $exe = Join-Path $Destination "$($archive.BaseName)/HexTactics.exe"
        Write-Host "Restored $tag -> $exe" -ForegroundColor Green
    }
}
else {
    if (-not (Get-GitOutput @('rev-parse', '-q', '--verify', "refs/tags/$tag"))) { Fail "Tag $tag not found. Run: git fetch --tags" }
    if (-not $Destination) { $Destination = Join-Path (Split-Path -Parent $script:RepoRoot) "HexTactics_src_$tag" }
    if (Test-Path $Destination) { Fail "'$Destination' already exists; choose another -Destination." }
    if ($PSCmdlet.ShouldProcess($Destination, "git worktree add $tag")) {
        Invoke-Checked 'git' @('-C', $script:RepoRoot, 'worktree', 'add', '--detach', $Destination, $tag)
        Write-Host "Source of $tag checked out at $Destination (current checkout unchanged)." -ForegroundColor Green
        Write-Host "Remove later with: git worktree remove `"$Destination`""
    }
}
