# Build SlideDock.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No Visual Studio or .NET SDK needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1
param([switch]$NoSdk)   # -NoSdk: test the fallback that works on any Windows 10/11 PC

$ErrorActionPreference = 'Stop'
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf  = "$fw\WPF"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path "$fw\csc.exe")) { throw ".NET Framework 4.8 compiler not found at $fw" }

$refs = @(
  "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
  "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
  "$fw\System.Core.dll", "$fw\System.Data.dll", "$fw\System.Management.dll", "$fw\System.dll",
  "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Runtime.dll"
)

# Windows Runtime metadata (notifications API): use the Windows SDK if installed,
# otherwise the metadata files every Windows 10/11 already has.
$sdk = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata\*\Windows.winmd' -ErrorAction SilentlyContinue |
       Where-Object { $_.Directory.Name -match '^\d+(\.\d+)+$' } |          # version folders only (skip "Facade")
       Sort-Object { [version]$_.Directory.Name } -Descending | Select-Object -First 1
if ($sdk -and -not $NoSdk) {
  $refs += $sdk.FullName
} else {
  $refs += (Get-ChildItem "$env:windir\System32\WinMetadata\*.winmd").FullName
}

$cscArgs = $refs | ForEach-Object { "/r:$_" }
& "$fw\csc.exe" /nologo /codepage:65001 /target:winexe "/win32manifest:$here\app.manifest" /optimize+ "/out:$here\SlideDock.exe" @cscArgs "$here\SlideDock.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Build OK: $here\SlideDock.exe"
