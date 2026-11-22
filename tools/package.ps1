<#
.SYNOPSIS
    Builds RouteViz and StrippedApiShim in Release and assembles the release zips in dist\.

.DESCRIPTION
    Produces, in dist\ (git-ignored):
      RouteViz-<v>-mod-only.zip                     plugin + patcher, for people who already have BepInEx set up
      RouteViz-<v>-all-in-one.zip                   BepInEx + config + corlibs + patcher + plugin: extract into the game folder
      RouteViz-<v>-thunderstore.zip                 Thunderstore/r2modman package for the mod
      BepInExPack_MiniMotorways-<p>-thunderstore.zip  Thunderstore loader pack for the game (what the mod depends on)
      SHA256SUMS.txt
    The last three/two need -CorlibDir; without it only the mod-only and the mod's Thunderstore zip are made.

    The game folder is only READ: its Managed DLLs are copied into dist\build\refgame and the projects are
    compiled with GameDir pointing there, so their DeployToGame targets copy into that scratch folder, never
    into the real game. Sources are copied to dist\build\src first, so this doesn't touch the projects' own
    bin\obj (safe to run while a dev build/hot-reload loop is going on).

    Windows PowerShell 5.1 compatible. Needs the .NET SDK (dotnet) on PATH.

.PARAMETER GameDir
    Mini Motorways install, used read-only for the reference DLLs and to check that no game file is packaged.

.PARAMETER BepInEx
    Where to get BepInEx 5.4.23.2 x86 from: a URL to the release zip (default: the official GitHub release),
    a local copy of that zip, or a folder laid out like a game folder with BepInEx installed (only winhttp.dll,
    .doorstop_version, changelog.txt and BepInEx\core are taken from it).

.PARAMETER CorlibDir
    Folder with the Windows "unityjit" Mono class libraries of Unity 2022.3.62f2 (mscorlib.dll, System*.dll,
    netstandard.dll, Mono.Security.dll). E.g. <Unity 2022.3.62f2>\Editor\Data\MonoBleedingEdge\lib\mono\unityjit-win32,
    or an existing BepInEx\unstripped_corlib.

.PARAMETER PackVersion
    version_number of the BepInExPack_MiniMotorways Thunderstore package (convention: BepInEx 5.4.23.2 -> 5.4.2302,
    bump the last digits for pack-only changes).

.PARAMETER ThunderstoreNamespace
    Thunderstore team name that will own the pack; used in the mod's dependency string.

.PARAMETER WebsiteUrl
    website_url for the Thunderstore manifests (e.g. the GitHub repo). May be empty.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package.ps1 -CorlibDir "D:\SteamLibrary\steamapps\common\Mini Motorways\BepInEx\unstripped_corlib"

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\package.ps1 -BepInEx "D:\SteamLibrary\steamapps\common\Mini Motorways" -CorlibDir "...\unstripped_corlib"
#>
[CmdletBinding()]
param(
    [string]$GameDir = 'D:\SteamLibrary\steamapps\common\Mini Motorways',
    [string]$BepInEx = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x86_5.4.23.2.zip',
    [string]$CorlibDir = '',
    [string]$OutDir = '',
    [string]$PackVersion = '5.4.2302',
    [string]$ThunderstoreNamespace = 'TEAM_TBD',
    [string]$WebsiteUrl = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$Files    = Join-Path $PSScriptRoot 'package-files'
if (-not $OutDir) { $OutDir = Join-Path $RepoRoot 'dist' }
$OutDir   = [IO.Path]::GetFullPath($OutDir)
$Build    = Join-Path $OutDir 'build'
$Stage    = Join-Path $OutDir 'stage'
$Utf8     = New-Object System.Text.UTF8Encoding $false   # no BOM: Thunderstore rejects manifest.json with a BOM

# The 13 class libraries that BepInEx needs unstripped (same set as the working install).
$CorlibNames = @('mscorlib.dll', 'netstandard.dll', 'Mono.Security.dll', 'System.dll', 'System.Core.dll',
    'System.Configuration.dll', 'System.Data.dll', 'System.Net.Http.dll', 'System.Numerics.dll',
    'System.Runtime.Serialization.dll', 'System.Security.dll', 'System.Xml.dll', 'System.Xml.Linq.dll')

# ---------------------------------------------------------------- helpers

function Write-Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Assert-NotInGame([string]$path) {
    $full = [IO.Path]::GetFullPath($path).TrimEnd('\') + '\'
    $game = [IO.Path]::GetFullPath($GameDir).TrimEnd('\') + '\'
    if ($full.StartsWith($game, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write inside the game folder: $path"
    }
}

function New-Dir([string]$path) {
    Assert-NotInGame $path
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

function Copy-File([string]$src, [string]$dstDir, [string]$name = '') {
    New-Dir $dstDir | Out-Null
    if (-not $name) { $name = Split-Path $src -Leaf }
    Copy-Item -LiteralPath $src -Destination (Join-Path $dstDir $name) -Force
}

# Copies the contents of $src into $dst (created if needed), skipping the named top-level entries.
function Copy-DirContents([string]$src, [string]$dst, [string[]]$exclude = @()) {
    New-Dir $dst | Out-Null
    Get-ChildItem -LiteralPath $src -Force | Where-Object { $exclude -notcontains $_.Name } |
        Copy-Item -Destination $dst -Recurse -Force
}

function Write-Text([string]$path, [string]$text) {
    Assert-NotInGame $path
    New-Dir (Split-Path $path -Parent) | Out-Null
    [IO.File]::WriteAllText($path, $text, $Utf8)
}

function Expand-Template([string]$path, [hashtable]$values) {
    $text = [IO.File]::ReadAllText($path)
    foreach ($k in $values.Keys) { $text = $text.Replace('{{' + $k + '}}', $values[$k]) }
    return $text
}

# Zips a folder with forward-slash entry names (Compress-Archive in PS 5.1 writes backslashes, which
# Thunderstore/r2modman and non-Windows unzip tools mishandle, and it skips dotfiles like .doorstop_version).
function New-Zip([string]$srcDir, [string]$zipPath) {
    Assert-NotInGame $zipPath
    $srcDir = (Resolve-Path -LiteralPath $srcDir).ProviderPath.TrimEnd('\')
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $srcDir -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
            $rel = $_.FullName.Substring($srcDir.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $rel,
                [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
}

function Get-PEMachine([string]$path) {
    $b = [IO.File]::ReadAllBytes($path)
    $pe = [BitConverter]::ToInt32($b, 0x3c)
    return [BitConverter]::ToUInt16($b, $pe + 4)
}

function Write-Manifest([string]$template, [string]$dst, [hashtable]$values) {
    $text = Expand-Template $template $values
    $json = $text | ConvertFrom-Json   # throws if the result isn't valid JSON
    if ($json.name -notmatch '^[A-Za-z0-9_]{1,128}$') { throw "Thunderstore name '$($json.name)' is invalid" }
    if ($json.version_number -notmatch '^\d+\.\d+\.\d+$') { throw "Thunderstore version '$($json.version_number)' must be Major.Minor.Patch" }
    if ($json.description.Length -gt 250) { throw "Thunderstore description is $($json.description.Length) chars (max 250)" }
    Write-Text $dst $text
}

# ---------------------------------------------------------------- inputs

Write-Step 'Checking inputs'
$pluginSrc = Join-Path $RepoRoot 'RouteViz\RouteVizPlugin.cs'
$m = [regex]::Match([IO.File]::ReadAllText($pluginSrc), 'const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $m.Success) { throw "Couldn't find the Version constant in $pluginSrc" }
$Version = $m.Groups[1].Value
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' must be Major.Minor.Patch (Thunderstore requires it)" }
Write-Host "RouteViz version: $Version"

$Managed = Join-Path $GameDir 'Mini Motorways_Data\Managed'
if (-not (Test-Path (Join-Path $Managed 'App.dll'))) {
    throw "Game not found at '$GameDir' (needed read-only, to compile against). Pass -GameDir."
}
Assert-NotInGame $OutDir
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet (the .NET SDK) is not on PATH' }

$OurLicense = Join-Path $RepoRoot 'LICENSE'
if (-not (Test-Path $OurLicense)) {
    Write-Warning 'No LICENSE file in the repo root: the packages will not contain a licence for our own code. Add one before publishing (see PACKAGING.md).'
    $OurLicense = $null
}

foreach ($d in @($Build, $Stage)) { if (Test-Path $d) { Assert-NotInGame $d; Remove-Item -LiteralPath $d -Recurse -Force } }
New-Dir $OutDir | Out-Null

# ---------------------------------------------------------------- BepInEx

Write-Step "Getting BepInEx from $BepInEx"
$bepSrc = $BepInEx
if ($BepInEx -match '^https?://') {
    $dl = New-Dir (Join-Path $Build 'download')
    $bepSrc = Join-Path $dl 'BepInEx.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $oldPref = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
    try { Invoke-WebRequest -Uri $BepInEx -OutFile $bepSrc -UseBasicParsing } finally { $ProgressPreference = $oldPref }
}
if ((Test-Path -LiteralPath $bepSrc -PathType Leaf) -and $bepSrc -like '*.zip') {
    $ex = Join-Path $Build 'bepinex-extracted'
    New-Dir $ex | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $bepSrc).ProviderPath, $ex)
    $bepSrc = $ex
} elseif (-not (Test-Path -LiteralPath $bepSrc -PathType Container)) {
    throw "-BepInEx must be a URL, a .zip file or a folder: $BepInEx"
}
# Take only BepInEx's own files, never config/plugins/logs/caches from an existing install.
$Bep = New-Dir (Join-Path $Build 'bepinex')
foreach ($f in @('winhttp.dll', '.doorstop_version', 'changelog.txt')) {
    $p = Join-Path $bepSrc $f
    if (Test-Path -LiteralPath $p) { Copy-File $p $Bep } elseif ($f -eq 'winhttp.dll') { throw "winhttp.dll not found in $bepSrc" }
}
$coreSrc = Join-Path $bepSrc 'BepInEx\core'
if (-not (Test-Path (Join-Path $coreSrc 'BepInEx.Preloader.dll'))) { throw "BepInEx\core\BepInEx.Preloader.dll not found in $bepSrc" }
Copy-DirContents $coreSrc (Join-Path $Bep 'BepInEx\core')
if ((Get-PEMachine (Join-Path $Bep 'winhttp.dll')) -ne 0x14c) { throw 'winhttp.dll is not the x86 build; this game is 32-bit' }
$bepVer = (Get-Item (Join-Path $Bep 'BepInEx\core\BepInEx.dll')).VersionInfo.FileVersion
Write-Host "BepInEx.dll version: $bepVer"
if ($bepVer -notlike '5.4.23*') { Write-Warning "Expected BepInEx 5.4.23.x, got $bepVer. THIRD-PARTY-NOTICES.txt and the pack README name 5.4.23.2." }

# ---------------------------------------------------------------- corlibs

$Corlib = $null
if ($CorlibDir) {
    Write-Step "Checking corlibs in $CorlibDir"
    $Corlib = New-Dir (Join-Path $Build 'corlib')
    foreach ($n in $CorlibNames) {
        $p = Join-Path $CorlibDir $n
        if (-not (Test-Path $p)) { throw "Missing $n in $CorlibDir" }
        $gameCopy = Join-Path $Managed $n
        if ((Test-Path $gameCopy) -and ((Get-FileHash $p).Hash -eq (Get-FileHash $gameCopy).Hash)) {
            throw "$n in $CorlibDir is the game's own stripped copy; use the unstripped Unity 2022.3.62f2 unityjit-win32 libraries"
        }
        Copy-File $p $Corlib
    }
    $mv = (Get-Item (Join-Path $Corlib 'mscorlib.dll')).VersionInfo.FileVersion
    Write-Host "mscorlib.dll version: $mv"
    if ((Get-PEMachine (Join-Path $Corlib 'mscorlib.dll')) -ne 0x14c) { Write-Warning 'mscorlib.dll PE header is not x86 (managed DLLs normally are; check the source)' }
} else {
    Write-Warning 'No -CorlibDir given: skipping the all-in-one zip and the BepInExPack Thunderstore zip.'
}

# ---------------------------------------------------------------- build

Write-Step 'Building (Release) against a scratch copy of the game references'
$RefGame = New-Dir (Join-Path $Build 'refgame')
Copy-DirContents (Join-Path $Bep 'BepInEx\core') (Join-Path $RefGame 'BepInEx\core')
New-Dir (Join-Path $RefGame 'Mini Motorways_Data\Managed') | Out-Null
Get-ChildItem -LiteralPath $Managed -Filter *.dll | Copy-Item -Destination (Join-Path $RefGame 'Mini Motorways_Data\Managed') -Force

$Bin = @{}
foreach ($proj in @('StrippedApiShim', 'RouteViz')) {
    $src = New-Dir (Join-Path $Build "src\$proj")
    Copy-DirContents (Join-Path $RepoRoot $proj) $src @('bin', 'obj')
    $out = Join-Path $Build "out\$proj"
    # GameDir -> scratch folder: the projects' DeployToGame targets then copy into dist\build\refgame, not the game.
    & dotnet build (Join-Path $src "$proj.csproj") -c Release -nologo -v:minimal `
        "-p:GameDir=$RefGame" '-p:DeployTo=plugins' "-p:OutDir=$out"
    if ($LASTEXITCODE -ne 0) { throw "dotnet build $proj failed ($LASTEXITCODE)" }
    $Bin[$proj] = Join-Path $out "$proj.dll"
    if (-not (Test-Path $Bin[$proj])) { throw "Build output missing: $($Bin[$proj])" }
}

# ---------------------------------------------------------------- stage

Write-Step 'Staging packages'
$tv = @{ VERSION = $Version; PACK_VERSION = $PackVersion; WEBSITE_URL = $WebsiteUrl;
         PACK_DEPENDENCY = "$ThunderstoreNamespace-BepInExPack_MiniMotorways-$PackVersion" }
$Lic = Join-Path $Files 'licenses'
$Zips = @()

function Add-OurLicense([string]$dir) { if ($OurLicense) { Copy-File $OurLicense $dir 'LICENSE' } }
function Add-ThirdParty([string]$dir) {
    Copy-File (Join-Path $Files 'THIRD-PARTY-NOTICES.txt') $dir
    Copy-DirContents $Lic (Join-Path $dir 'licenses')
}
# BepInEx + game-specific setup, in game-folder layout (shared by the all-in-one zip and the Thunderstore pack).
# $corlibRel: where the corlibs go, relative to the game folder / r2modman profile root. r2modman's own
# convention (its @bepInExCorlibs launch variable) is "unstripped_corlib" at the profile root.
function Add-LoaderSetup([string]$dir, [string]$corlibRel) {
    Copy-DirContents $Bep $dir
    $ini = [IO.File]::ReadAllText((Join-Path $Files 'doorstop_config.ini'))
    $ini = [regex]::Replace($ini, '(?m)^dll_search_path_override\s*=.*$', "dll_search_path_override = $corlibRel")
    Write-Text (Join-Path $dir 'doorstop_config.ini') $ini
    Copy-File (Join-Path $Files 'BepInEx.cfg') (Join-Path $dir 'BepInEx\config')
    Copy-DirContents $Corlib (Join-Path $dir $corlibRel)
    Copy-File $Bin['StrippedApiShim'] (Join-Path $dir 'BepInEx\patchers')
    Add-ThirdParty $dir
}

# 1. mod-only
$d = New-Dir (Join-Path $Stage 'mod-only')
Copy-File $Bin['RouteViz'] (Join-Path $d 'BepInEx\plugins\RouteViz')
Copy-File $Bin['StrippedApiShim'] (Join-Path $d 'BepInEx\patchers')
Write-Text (Join-Path $d 'INSTALL.txt') (Expand-Template (Join-Path $Files 'INSTALL-mod-only.txt') $tv)
Add-OurLicense $d
$Zips += ,@($d, (Join-Path $OutDir "RouteViz-$Version-mod-only.zip"))

# 2. all-in-one
if ($Corlib) {
    $d = New-Dir (Join-Path $Stage 'all-in-one')
    Add-LoaderSetup $d 'BepInEx\unstripped_corlib'
    Copy-File $Bin['RouteViz'] (Join-Path $d 'BepInEx\plugins\RouteViz')
    Write-Text (Join-Path $d 'INSTALL.txt') (Expand-Template (Join-Path $Files 'INSTALL-all-in-one.txt') $tv)
    Add-OurLicense $d
    $Zips += ,@($d, (Join-Path $OutDir "RouteViz-$Version-all-in-one.zip"))
}

# 3. Thunderstore: the mod
$ts = Join-Path $Files 'thunderstore\RouteViz'
$d = New-Dir (Join-Path $Stage 'thunderstore-RouteViz')
Write-Manifest (Join-Path $ts 'manifest.template.json') (Join-Path $d 'manifest.json') $tv
Write-Text (Join-Path $d 'README.md') (Expand-Template (Join-Path $ts 'README.md') $tv)
Copy-File (Join-Path $ts 'icon.png') $d
Copy-File $Bin['RouteViz'] (Join-Path $d 'plugins')
Add-OurLicense $d
$Zips += ,@($d, (Join-Path $OutDir "RouteViz-$Version-thunderstore.zip"))

# 4. Thunderstore: the loader pack for the game (BepInExPack convention: everything under a root folder
#    that r2modman copies into the profile; must be registered in ecosystem-schema misc/modloader-packages.yml).
if ($Corlib) {
    $ts = Join-Path $Files 'thunderstore\BepInExPack_MiniMotorways'
    $d = New-Dir (Join-Path $Stage 'thunderstore-BepInExPack')
    Write-Manifest (Join-Path $ts 'manifest.template.json') (Join-Path $d 'manifest.json') $tv
    Write-Text (Join-Path $d 'README.md') (Expand-Template (Join-Path $ts 'README.md') $tv)
    Copy-File (Join-Path $ts 'icon.png') $d
    Add-LoaderSetup (Join-Path $d 'BepInExPack_MiniMotorways') 'unstripped_corlib'
    $Zips += ,@($d, (Join-Path $OutDir "BepInExPack_MiniMotorways-$PackVersion-thunderstore.zip"))
}

# ---------------------------------------------------------------- safety check: no game files

Write-Step 'Checking that no game file is packaged'
$gameHashes = @{}
# Game-owned binaries: the top-level exe/dlls except BepInEx's winhttp.dll, plus everything under the
# data and Mono runtime folders.
$gameFiles = @(Get-ChildItem -LiteralPath $GameDir -File | Where-Object { $_.Extension -in '.exe', '.dll' -and $_.Name -ne 'winhttp.dll' })
foreach ($sub in @('Mini Motorways_Data', 'MonoBleedingEdge')) {
    $p = Join-Path $GameDir $sub
    if (Test-Path $p) { $gameFiles += @(Get-ChildItem -LiteralPath $p -Recurse -File -Include *.dll, *.exe) }
}
foreach ($g in $gameFiles) { $gameHashes[(Get-FileHash $g.FullName).Hash] = $g.FullName }
# BepInEx's own files may legitimately be identical to the ones installed in the game folder.
$allowed = @{}
Get-ChildItem -LiteralPath $Bep -Recurse -File -Force | ForEach-Object { $allowed[(Get-FileHash $_.FullName).Hash] = $true }
foreach ($f in (Get-ChildItem -LiteralPath $Stage -Recurse -File -Force)) {
    $h = (Get-FileHash $f.FullName).Hash
    if ($gameHashes.ContainsKey($h) -and -not $allowed.ContainsKey($h)) {
        throw "Staged file $($f.FullName) is identical to game file $($gameHashes[$h]). Not packaging game files."
    }
}

# ---------------------------------------------------------------- zip

Write-Step 'Writing zips'
$sums = @()
foreach ($z in $Zips) {
    New-Zip $z[0] $z[1]
    $sums += '{0}  {1}' -f (Get-FileHash $z[1] -Algorithm SHA256).Hash.ToLower(), (Split-Path $z[1] -Leaf)
}
Write-Text (Join-Path $OutDir 'SHA256SUMS.txt') (($sums -join "`n") + "`n")

foreach ($z in $Zips) {
    $zip = [IO.Compression.ZipFile]::OpenRead($z[1])
    try {
        Write-Host ''
        Write-Host ('{0}  ({1:N0} KB, {2} files)' -f (Split-Path $z[1] -Leaf), ((Get-Item $z[1]).Length / 1KB), $zip.Entries.Count) -ForegroundColor Green
        $zip.Entries | Sort-Object FullName | ForEach-Object { '    {0,10:N0}  {1}' -f $_.Length, $_.FullName }
    } finally { $zip.Dispose() }
}
Write-Host ''
Write-Host "Done. Output: $OutDir"
