# Builds the release files into dist\:
#   SlideDock.exe, SlideDock.exe.sha256, SlideDock-<version>-win-x64.zip  (GitHub release)
#   SlideDock-<version>-store.msix                                       (Microsoft Store upload, unsigned)
#   powershell -ExecutionPolicy Bypass -File packaging\pack.ps1
param(
  [string]$IdentityName = $env:STORE_IDENTITY_NAME,
  [string]$Publisher = $env:STORE_PUBLISHER,
  [string]$PublisherDisplayName = $env:STORE_PUBLISHER_DISPLAY_NAME
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null
$version = (Get-Content "$root\VERSION" -Raw).Trim()

# 1. app
& powershell -ExecutionPolicy Bypass -File "$root\build.ps1" -Out "$dist\SlideDock.exe"
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = "$dist\SlideDock.exe"

# 2. checksum (the in-app updater checks it before replacing itself)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
"$hash  SlideDock.exe" | Set-Content -Path "$dist\SlideDock.exe.sha256" -Encoding ASCII -NoNewline

# 3. zip for manual download
$zipDir = Join-Path $root 'obj\zip'
if (Test-Path $zipDir) { Remove-Item -Recurse -Force $zipDir }
New-Item -ItemType Directory -Force $zipDir | Out-Null
Copy-Item $exe, "$root\README.md" $zipDir
Compress-Archive -Path "$zipDir\*" -DestinationPath "$dist\SlideDock-$version-win-x64.zip" -Force

# 4. MSIX for the Microsoft Store (Store signs it; it can't be installed by double-click without a signature)
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' -ErrorAction SilentlyContinue |
            Sort-Object { [version]$_.Directory.Parent.Name } -Descending -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $makeappx) { Write-Warning "makeappx.exe not found (Windows SDK): skipping the MSIX"; return }
if (-not $IdentityName) { $IdentityName = 'SlideDock' }
if (-not $Publisher) { $Publisher = 'CN=SlideDock' }
if (-not $PublisherDisplayName) { $PublisherDisplayName = 'SlideDock' }
$stage = Join-Path $root 'obj\msix'
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force "$stage\Assets" | Out-Null
Copy-Item $exe $stage
Copy-Item "$root\assets\store\*.png" "$stage\Assets"
(Get-Content "$root\packaging\AppxManifest.xml" -Raw).
  Replace('$IdentityName$', $IdentityName).
  Replace('$Publisher$', [System.Security.SecurityElement]::Escape($Publisher)).
  Replace('$PublisherDisplayName$', [System.Security.SecurityElement]::Escape($PublisherDisplayName)).
  Replace('$Version$', "$version.0") | Set-Content -Path "$stage\AppxManifest.xml" -Encoding UTF8
& $makeappx.FullName pack /o /d $stage /p "$dist\SlideDock-$version-store.msix" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Get-ChildItem $dist | Format-Table Name, Length
