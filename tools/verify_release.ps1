<#
.SYNOPSIS
    Verifies an archived HexTactics release: checksum, build info, Git tag and ZIP contents.

.DESCRIPTION
    Read-only: never modifies the archive. With -Run the ZIP is extracted into a temporary
    folder and the game is started in smoke-test mode (it renders a few frames and exits).

.EXAMPLE
    pwsh tools/verify_release.ps1 -Version 0.1.0
    pwsh tools/verify_release.ps1 -Version 0.1.0 -Run
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$ArchiveRoot,
    [switch]$Run,
    # Command used to launch the Windows executable on non-Windows hosts (e.g. wine64).
    [string]$Launcher
)

. "$PSScriptRoot/common.ps1"

if (-not $Version) { $Version = Get-PropsVersion }
Assert-Version $Version
if (-not $ArchiveRoot) { $ArchiveRoot = $script:DefaultArchiveRoot }
$archive = Get-ArchiveInfo -Version $Version -ArchiveRoot $ArchiveRoot
$tag = "v$Version"
$errors = New-Object System.Collections.Generic.List[string]

Write-Step "Verifying $tag in $($archive.Directory)"
foreach ($f in @($archive.Zip, $archive.Sha256, $archive.BuildInfo)) {
    if (-not (Test-Path $f)) { Fail "Missing file: $f" }
}

# Checksum
$expected = ((Get-Content $archive.Sha256 -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = Get-Sha256 $archive.Zip
if ($expected -ne $actual) { $errors.Add("SHA-256 mismatch: file $actual, recorded $expected") }
else { Write-Host "SHA-256 OK  $actual" }

# Build info vs Git
$info = Read-BuildInfo $archive.BuildInfo
if ($info['version'] -ne $Version) { $errors.Add("BUILD_INFO version '$($info['version'])' != $Version") }
if ($info['sha256'] -and $info['sha256'] -ne $actual) { $errors.Add('BUILD_INFO sha256 differs from the ZIP') }
$tagCommit = Get-GitOutput @('rev-list', '-n', '1', "refs/tags/$tag")
if (-not $tagCommit) {
    & git -C $script:RepoRoot cat-file -e "$($info['commit'])^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { $errors.Add("Neither tag $tag nor commit $($info['commit']) exist in this clone (git fetch --tags)") }
    else { Write-Warning "Tag $tag is missing; the build commit $($info['commit']) exists. Recreate tags: pwsh tools/create_release_tags.ps1" }
}
elseif ($tagCommit -ne $info['commit']) { $errors.Add("Tag $tag -> $tagCommit, but archive was built from $($info['commit'])") }
else { Write-Host "Commit OK   $tagCommit ($tag)" }

# ZIP contents
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive.Zip)
try {
    $names = $zip.Entries | ForEach-Object { $_.FullName -replace '\\', '/' }
    $root = $archive.BaseName
    foreach ($required in @("$root/HexTactics.exe", "$root/HexTactics.pck", "$root/BUILD_INFO.txt", "$root/data_HexTactics_windows_x86_64/HexTactics.dll", "$root/data_HexTactics_windows_x86_64/HexTactics.Core.dll")) {
        if ($names -notcontains $required) { $errors.Add("ZIP is missing $required") }
    }
    Write-Host "ZIP entries $($names.Count)"
}
finally { $zip.Dispose() }

# Optional smoke run
if ($Run -and $errors.Count -eq 0) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("hextactics_verify_" + [guid]::NewGuid().ToString('N'))
    try {
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archive.Zip, $tmp)
        # The console wrapper forwards the game log to stdout (the main exe is a GUI application).
        $exe = Join-Path $tmp "$($archive.BaseName)/HexTactics.console.exe"
        if (-not (Test-Path $exe)) { $exe = Join-Path $tmp "$($archive.BaseName)/HexTactics.exe" }
        $log = Join-Path $tmp 'smoke.log'
        $smokeArgs = @('--', '--hx-frames=30', '--hx-quit')
        Write-Step "Smoke run: $exe"
        if ($Launcher) { & $Launcher $exe @smokeArgs *> $log }
        else { & $exe @smokeArgs *> $log }
        $text = Get-Content -Raw $log
        if ($text -notmatch [regex]::Escape("HexTactics v$Version")) { $errors.Add("Smoke run did not report HexTactics v$Version. Log: $log") }
        elseif ($text -notmatch '\[automation\] done') { $errors.Add("Smoke run did not finish. Log: $log") }
        else { Write-Host 'Smoke run OK' }
    }
    finally {
        if ($errors.Count -eq 0) { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "Release $tag verified." -ForegroundColor Green
