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

# End-to-end scenarios on the prototype map (see docs/ARCHITECTURE.md, "Автоматизация"). The input scheme changed
# in v0.7.0 (spec part 2: continuous drafts + mandatory final facing), so each archive is checked with the scenario of
# its own generation.
$script:RegressionScenarioV05 = @(
    'spawn:base', 'expect:units:1',
    'drag:0,4>3,2', 'expect:waypoints:1:1',
    'key:Space', 'expect:paused:true', 'mark', 'wait:20', 'expect:same-tick',
    'drag:3,2>6,1', 'expect:waypoints:1:2',
    'key:Space', 'expect:paused:false',
    'waitidle:1:3000', 'expect:cell:1:6,1', 'expect:waypoints:1:0', 'expect:status:1:Idle',
    'drag:6,1>6,1@SouthWest:0.6', 'expect:facing:1:SouthWest',
    'place', 'click:9,8', 'expect:units:2',
    'drag:9,8>8,3', 'expect:waypoints:2:0',
    'key:Escape'
) -join ';'

$script:RegressionScenarioV07 = @(
    'spawn:base', 'expect:units:1', 'key:Space', 'expect:paused:true',
    'drag:0,4>3,2', 'expect:draft:1', 'click:3,2@North:0.6', 'expect:draft:none', 'expect:waypoints:1:1',
    'mark', 'wait:20', 'expect:same-tick',
    'drag:3,2>6,1', 'expect:draft:2', 'click:6,1@SouthWest:0.6', 'expect:waypoints:1:2',
    'key:Space', 'expect:paused:false',
    'waitidle:1:3000', 'expect:cell:1:6,1', 'expect:waypoints:1:0', 'expect:status:1:Idle', 'expect:facing:1:SouthWest',
    'click:6,1', 'click:6,1@North:0.6', 'wait:3', 'expect:facing:1:North', 'expect:cell:1:6,1',
    'place', 'click:9,8', 'expect:units:2',
    'drag:9,8>8,3', 'expect:draft:none', 'expect:waypoints:2:0',
    'drag:9,8>9,6', 'expect:draft:1', 'rclick:0,0', 'expect:draft:none', 'expect:waypoints:2:0',
    'key:Escape'
) -join ';'

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
        # From v0.5.0 the build understands expect:/waitidle: steps: run the end-to-end regression scenario
        # (spawn, drag orders, pause freezes time, chain built during pause, movement, rotate gesture,
        # unreachable target). Older builds get a plain smoke run.
        $regression = [version]$Version -ge [version]'0.5.0'
        $smokeArgs = @('--windowed', '--resolution', '1600x900', '--', '--hx-frames=10', '--hx-quit')
        if ($regression) {
            $scenario = if ([version]$Version -ge [version]'0.7.0') { $script:RegressionScenarioV07 } else { $script:RegressionScenarioV05 }
            $smokeArgs += "--hx-script=$scenario"
        }
        Write-Step ("{0}: {1}" -f ($(if ($regression) { 'Regression scenario' } else { 'Smoke run' }), $exe))
        if ($Launcher) { & $Launcher $exe @smokeArgs *> $log }
        else { & $exe @smokeArgs *> $log }
        $exitCode = $LASTEXITCODE
        $text = Get-Content -Raw $log
        if ($text -notmatch [regex]::Escape("HexTactics v$Version")) { $errors.Add("Run did not report HexTactics v$Version. Log: $log") }
        elseif ($text -notmatch '\[automation\] done') { $errors.Add("Run did not finish. Log: $log") }
        elseif ($regression -and ($exitCode -ne 0 -or $text -notmatch '\[automation\] checks passed: \d+' -or $text -match 'CHECK FAILED')) {
            $errors.Add("Regression scenario failed (exit $exitCode). Log: $log")
        }
        else {
            if ($regression) { ($text -split "`n" | Where-Object { $_ -match '\[automation\] checks passed' }) | ForEach-Object { Write-Host $_.Trim() } }
            Write-Host 'Run OK'
        }
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
