# Regenerates the brand assets in assets\ (icon, Store logos, banner) from tools\make-assets.cs.
#   powershell -ExecutionPolicy Bypass -File tools\make-assets.ps1
$ErrorActionPreference = 'Stop'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf  = "$fw\WPF"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$tmp  = Join-Path $root 'obj\make-assets.exe'
New-Item -ItemType Directory -Force (Split-Path -Parent $tmp) | Out-Null
& "$fw\csc.exe" /nologo /target:exe "/out:$tmp" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" "/r:$wpf\PresentationFramework.dll" "/r:$fw\System.Xaml.dll" "$root\tools\make-assets.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
& $tmp $root
if ($LASTEXITCODE -ne 0) { throw "Asset generation failed" }
