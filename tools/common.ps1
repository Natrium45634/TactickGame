# Shared helpers for the HexTactics release tools. Dot-source it: . "$PSScriptRoot/common.ps1"
# Works with Windows PowerShell 5.1 and PowerShell 7+ (Windows and Linux).

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:ClientDir = Join-Path $script:RepoRoot 'src/GodotClient'
$script:DefaultArchiveRoot = Join-Path $script:RepoRoot 'release_archives'

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Fail([string]$Message) {
    throw "HexTactics tools: $Message"
}

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = $script:RepoRoot)
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) { Fail "'$FilePath $($Arguments -join ' ')' exited with code $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

function Get-GitOutput([string[]]$Arguments) {
    $out = & git -C $script:RepoRoot @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($out -join "`n").Trim()
}

# Version from Directory.Build.props (single source of truth for .NET assemblies).
function Get-PropsVersion {
    [xml]$props = Get-Content -Raw (Join-Path $script:RepoRoot 'Directory.Build.props')
    $v = $props.Project.PropertyGroup.HexTacticsVersion | Where-Object { $_ } | Select-Object -First 1
    if (-not $v) { Fail 'HexTacticsVersion not found in Directory.Build.props' }
    return $v.Trim()
}

# Version from project.godot (application/config/version).
function Get-GodotProjectVersion {
    $line = Select-String -Path (Join-Path $script:ClientDir 'project.godot') -Pattern '^config/version="([^"]+)"' | Select-Object -First 1
    if (-not $line) { Fail 'config/version not found in project.godot' }
    return $line.Matches[0].Groups[1].Value
}

function Assert-Version([string]$Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version '$Version' is not X.Y.Z" }
}

function Get-ArchiveInfo([string]$Version, [string]$ArchiveRoot) {
    $dir = Join-Path $ArchiveRoot "v$Version"
    $name = "HexTactics_v$($Version)_windows_x64"
    return [pscustomobject]@{
        Directory = $dir
        BaseName  = $name
        Zip       = Join-Path $dir "$name.zip"
        Sha256    = Join-Path $dir "$name.zip.sha256"
        BuildInfo = Join-Path $dir 'BUILD_INFO.txt'
    }
}

function Find-Godot([string]$GodotPath) {
    if ($GodotPath) {
        if (-not (Test-Path $GodotPath)) { Fail "Godot not found at '$GodotPath'" }
        return (Resolve-Path $GodotPath).Path
    }
    if ($env:GODOT) { return $env:GODOT }
    foreach ($name in @('godot', 'godot4', 'Godot_v4.7.2-stable_mono_win64.exe', 'Godot_v4.7.2-stable_mono_win64_console.exe')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }
    }
    Fail 'Godot 4.7.2 .NET not found. Pass -GodotPath or set the GODOT environment variable.'
}

function Read-BuildInfo([string]$Path) {
    $map = @{}
    foreach ($line in Get-Content $Path) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { $map[$line.Substring(0, $i).Trim()] = $line.Substring($i + 1).Trim() }
    }
    return $map
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToLowerInvariant()
}
