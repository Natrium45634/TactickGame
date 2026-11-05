<#
.SYNOPSIS
    Builds the runnable Windows x64 release archive of HexTactics for the current tagged commit.

.DESCRIPTION
    1. Checks that the working tree is clean and HEAD carries the tag vX.Y.Z.
    2. Checks that Directory.Build.props and project.godot carry the same version.
    3. Runs all automated tests (dotnet test).
    4. Imports the Godot project and exports the "Windows Desktop" preset.
    5. Writes BUILD_INFO.txt, packs a ZIP, writes the SHA-256 file.
    Archives are written to <ArchiveRoot>/vX.Y.Z/ and are NEVER overwritten: an existing
    archive for the version stops the script. Old versions are never touched.

.EXAMPLE
    pwsh tools/build_release.ps1 -GodotPath "C:\Godot\Godot_v4.7.2-stable_mono_win64.exe"
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$GodotPath,
    [string]$ArchiveRoot,
    [switch]$SkipTests,
    # Allows building from a commit without the release tag (for local test builds only).
    [switch]$AllowUntagged,
    [switch]$AllowDirty
)

. "$PSScriptRoot/common.ps1"

if (-not $Version) { $Version = Get-PropsVersion }
Assert-Version $Version
if (-not $ArchiveRoot) { $ArchiveRoot = $script:DefaultArchiveRoot }
$tag = "v$Version"
$archive = Get-ArchiveInfo -Version $Version -ArchiveRoot $ArchiveRoot

Write-Step "HexTactics release $tag"

# --- Preconditions ---------------------------------------------------------------------------
$propsVersion = Get-PropsVersion
$godotVersion = Get-GodotProjectVersion
if ($propsVersion -ne $Version -or $godotVersion -ne $Version) {
    Fail "Version mismatch: requested $Version, Directory.Build.props $propsVersion, project.godot $godotVersion"
}

$status = Get-GitOutput @('status', '--porcelain', '--untracked-files=no')
if ($status -and -not $AllowDirty) { Fail "Working tree has uncommitted changes:`n$status" }

$commit = Get-GitOutput @('rev-parse', 'HEAD')
if (-not $commit) { Fail 'Not a Git repository or no commits.' }
$tagCommit = Get-GitOutput @('rev-list', '-n', '1', $tag)
if ($tagCommit -ne $commit -and -not $AllowUntagged) {
    Fail "HEAD ($commit) is not tagged $tag. Create the tag first: git tag -a $tag -m 'HexTactics $tag'"
}

if (Test-Path $archive.Zip) {
    Fail "Archive already exists and will not be replaced: $($archive.Zip)"
}

$godot = Find-Godot $GodotPath
Write-Host "Godot:   $godot"
Write-Host "Commit:  $commit"
Write-Host "Archive: $($archive.Zip)"

# --- Tests -----------------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Step 'Running automated tests'
    Invoke-Checked 'dotnet' @('test', (Join-Path $script:RepoRoot 'HexTactics.sln'), '-c', 'Debug', '--nologo')
}
else {
    Write-Warning 'Tests skipped (-SkipTests). Do not publish such a build as a release.'
}

# --- Export ----------------------------------------------------------------------------------
$staging = Join-Path $script:RepoRoot "staging/$tag"
$exportDir = Join-Path $staging $archive.BaseName
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }   # staging is our own scratch folder
New-Item -ItemType Directory -Force -Path $exportDir | Out-Null

Write-Step 'Building C# solution for the editor import'
Invoke-Checked 'dotnet' @('build', (Join-Path $script:ClientDir 'HexTactics.csproj'), '-c', 'Debug', '--nologo')

Write-Step 'Importing Godot project'
Invoke-Checked $godot @('--headless', '--path', $script:ClientDir, '--import') $script:ClientDir

Write-Step 'Exporting "Windows Desktop"'
$exe = Join-Path $exportDir 'HexTactics.exe'
Invoke-Checked $godot @('--headless', '--path', $script:ClientDir, '--export-release', 'Windows Desktop', $exe) $script:ClientDir
foreach ($required in @('HexTactics.exe', 'HexTactics.console.exe', 'HexTactics.pck', 'data_HexTactics_windows_x86_64')) {
    if (-not (Test-Path (Join-Path $exportDir $required))) { Fail "Export is missing '$required'" }
}

# --- Build info ------------------------------------------------------------------------------
$engine = (& $godot --version | Select-Object -Last 1).Trim()
$dotnetSdk = (& dotnet --version).Trim()
$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$info = @(
    "product=HexTactics",
    "version=$Version",
    "tag=$tag",
    "commit=$commit",
    "build_date_utc=$buildDate",
    "godot=$engine",
    "dotnet_sdk=$dotnetSdk",
    "target_framework=net8.0",
    "platform=windows-x86_64",
    "renderer=gl_compatibility",
    "run=HexTactics.exe"
)
$info | Set-Content -Encoding utf8 (Join-Path $exportDir 'BUILD_INFO.txt')

$readme = @(
    "HexTactics $tag - Windows x64",
    "",
    "Run: HexTactics.exe (keep the data_HexTactics_windows_x86_64 folder and HexTactics.pck next to it).",
    "HexTactics.console.exe starts the same game with a console window that shows the log.",
    "No installation and no .NET installation required.",
    "Release notes: docs/releases/$tag/README.md in the source repository.",
    "Build: see BUILD_INFO.txt"
)
$readme | Set-Content -Encoding utf8 (Join-Path $exportDir 'README.txt')

# --- Archive ---------------------------------------------------------------------------------
Write-Step 'Packing ZIP'
New-Item -ItemType Directory -Force -Path $archive.Directory | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($exportDir, $archive.Zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

$hash = Get-Sha256 $archive.Zip
"$hash  $(Split-Path -Leaf $archive.Zip)" | Set-Content -Encoding ascii $archive.Sha256
($info + "zip=$(Split-Path -Leaf $archive.Zip)" + "sha256=$hash" + "size_bytes=$((Get-Item $archive.Zip).Length)") | Set-Content -Encoding utf8 $archive.BuildInfo

Write-Step 'Done'
Write-Host "ZIP:     $($archive.Zip)"
Write-Host "SHA-256: $hash"
Write-Host "Verify:  pwsh tools/verify_release.ps1 -Version $Version"
