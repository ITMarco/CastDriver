<#
.SYNOPSIS
  One-command release: bump version, build both single-file exes, commit/push,
  and publish a GitHub release with both builds attached.

.EXAMPLE
  ./release.ps1            # bump minor (1.2.0 -> 1.3.0), tag v1.3
  ./release.ps1 -Major     # bump major (1.3.0 -> 2.0.0), tag v2.0
  ./release.ps1 -Patch     # bump patch for a bugfix (1.13.0 -> 1.13.1), tag v1.13.1
  ./release.ps1 1.5        # set an explicit version
#>
param(
    [string]$Version = "",   # explicit "major.minor[.patch]"; empty = bump minor
    [switch]$Major,          # bump major instead of minor
    [switch]$Patch,          # bump patch (third number) for a bugfix release
    [string]$Feature = ""    # headline feature shown in the in-app update banner
)

$ErrorActionPreference = 'Stop'
$repo   = "ITMarco/CastDriver"
$csproj = "CastDriver.UI/CastDriver.UI.csproj"

# ── 1. Work out the new version ──────────────────────────────────────────────
[xml]$xml = Get-Content $csproj
$verNode  = $xml.SelectSingleNode('//Version')
if (-not $verNode) { throw "No <Version> element in $csproj" }
$cur = [version]$verNode.InnerText

if     ($Version) { $nv = [version]($(if ($Version -match '\.') { $Version } else { "$Version.0" })) }
elseif ($Major)   { $nv = [version]"$($cur.Major + 1).0.0" }
elseif ($Patch)   { $nv = [version]"$($cur.Major).$($cur.Minor).$([math]::Max($cur.Build,0) + 1)" }
else              { $nv = [version]"$($cur.Major).$($cur.Minor + 1).0" }

$build  = [math]::Max($nv.Build, 0)
$newVer = "$($nv.Major).$($nv.Minor).$build"
# Patch releases tag as vMAJOR.MINOR.PATCH so the update check sees them as newer; minor/major
# releases keep the clean vMAJOR.MINOR tag.
$tag    = if ($build -gt 0) { "v$($nv.Major).$($nv.Minor).$build" }
          else              { "v$($nv.Major).$($nv.Minor)" }
Write-Host "Releasing $tag (version $newVer, was $cur)" -ForegroundColor Cyan

# ── 2. Update csproj + commit + push ─────────────────────────────────────────
$verNode.InnerText = $newVer
$xml.Save((Resolve-Path $csproj))

git add -A
if (git status --porcelain) {
    git commit -m "Release $tag"
}
git push origin main
if ($LASTEXITCODE -ne 0) {
    throw "git push failed — the remote has commits you don't have locally. Run 'git pull --rebase origin main', then re-run release.ps1. (Nothing was published.)"
}

# ── 3. Build both single-file builds ─────────────────────────────────────────
$dist = "dist"
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue

$common = @(
    "CastDriver.UI", "-c", "Release", "-r", "win-x64",
    "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
    # NAudio.Lame ships libmp3lame.*.dll as content (not native runtime libs), so we must
    # bundle ALL content into the single file or MP3 fails with "LAME DLL not found".
    "-p:IncludeAllContentForSelfExtract=true",
    "-p:DebugType=none"
)

Write-Host "Building self-contained build..." -ForegroundColor Cyan
# SelfContainedBuild=true defines SELF_CONTAINED so this build updates itself with the
# standalone asset (the framework build below updates itself with CastDriver.exe).
dotnet publish @common --self-contained true -p:EnableCompressionInSingleFile=true -p:SelfContainedBuild=true -o "$dist/standalone"

Write-Host "Building framework-dependent build..." -ForegroundColor Cyan
dotnet publish @common --self-contained false -o "$dist/framework"

Copy-Item "$dist/framework/CastDriver.UI.exe"  "$dist/CastDriver.exe" -Force
Copy-Item "$dist/standalone/CastDriver.UI.exe" "$dist/CastDriver-standalone.exe" -Force

# SHA-256 of each asset — written into the release notes so the in-app updater can verify
# its download before installing it.
$shaFramework  = (Get-FileHash "$dist/CastDriver.exe"            -Algorithm SHA256).Hash
$shaStandalone = (Get-FileHash "$dist/CastDriver-standalone.exe" -Algorithm SHA256).Hash

# Build the Windows installer (Inno Setup) — optional, so a release still works on a machine
# without Inno Setup. Packages the standalone build as a per-user (no-admin) install.
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$haveInstaller = $false
if (Test-Path $iscc) {
    Write-Host "Building installer (Inno Setup)..." -ForegroundColor Cyan
    & $iscc /Qp "/DAppVersion=$newVer" `
        ("/DSourceExe=" + (Resolve-Path "$dist/CastDriver-standalone.exe").Path) `
        ("/DIconFile="  + (Resolve-Path "CastDriver.UI/icon.ico").Path) `
        ("/DOutputDir=" + (Resolve-Path "$dist").Path) `
        "installer\CastDriver.iss"
    if ($LASTEXITCODE -eq 0 -and (Test-Path "$dist/CastDriver-Setup.exe")) {
        $shaSetup = (Get-FileHash "$dist/CastDriver-Setup.exe" -Algorithm SHA256).Hash
        $haveInstaller = $true
    } else {
        Write-Warning "Installer build failed — releasing without it."
    }
} else {
    Write-Warning "Inno Setup (ISCC.exe) not found — releasing without the installer."
}

# Optional installer lines, folded into the notes only when the installer was built.
$installerDownload = if ($haveInstaller) {
    "- **CastDriver-Setup.exe** (~69 MB) — one-click installer, no admin needed; adds Start Menu + optional desktop shortcut, with an uninstaller."
} else { "" }
$installerChecksum = if ($haveInstaller) { "- CastDriver-Setup.exe: $shaSetup" } else { "" }

# ── 4. Get a GitHub token from the git credential store ──────────────────────
$cred  = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
$token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $token) { throw "Could not get a GitHub token from the credential store." }

$headers = @{
    Authorization = "token $token"
    "User-Agent"  = "CastDriver-release"
    Accept        = "application/vnd.github+json"
}

# ── 5. Create the release ────────────────────────────────────────────────────
# The app parses a "Feature:" line out of these notes and shows it in the update banner.
$featureLine = if ($Feature) { "**Feature:** $Feature`n`n" } else { "" }

$notes = @"
CastDriver $tag — cast Windows PC audio to Chromecast and DLNA devices.

$featureLine
Downloads:
- **CastDriver.exe** (~4 MB) — needs the free .NET 10 Desktop Runtime (Windows x64): https://dotnet.microsoft.com/download/dotnet/10.0
- **CastDriver-standalone.exe** (~73 MB) — runs anywhere, no .NET install required.
$installerDownload

On first run, allow CastDriver through Windows Firewall so devices can reach the stream.

### Checksums (SHA-256)
- CastDriver.exe: $shaFramework
- CastDriver-standalone.exe: $shaStandalone
$installerChecksum
"@

$body = @{
    tag_name         = $tag
    target_commitish = "main"
    name             = "CastDriver $tag"
    body             = $notes
    draft            = $false
    prerelease       = $false
} | ConvertTo-Json

$rel        = Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/json" `
                -Uri "https://api.github.com/repos/$repo/releases" -Body $body
# Build the upload URL from the release id (the upload_url template is fiddly to parse).
$uploadBase = "https://uploads.github.com/repos/$repo/releases/$($rel.id)/assets"

# ── 6. Upload assets ─────────────────────────────────────────────────────────
$assets = @("CastDriver.exe", "CastDriver-standalone.exe")
if ($haveInstaller) { $assets += "CastDriver-Setup.exe" }
foreach ($asset in $assets) {
    Write-Host "Uploading $asset..." -ForegroundColor Cyan
    Invoke-RestMethod -Method Post -Headers $headers -ContentType "application/octet-stream" `
        -Uri ("{0}?name={1}" -f $uploadBase, $asset) -InFile "$dist/$asset" | Out-Null
}

Write-Host "`nReleased: $($rel.html_url)" -ForegroundColor Green
