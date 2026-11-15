# Builds the Windows installer: dist\ClaudeQuota-Setup-<version>.exe
#   powershell -File win\build-installer.ps1 [-Iscc path\to\ISCC.exe]
param([string]$Iscc = "")
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "ClaudeQuota.Win\ClaudeQuota.Win.csproj"
$out  = Join-Path $root "ClaudeQuota.Win\bin\publish"
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "G:\Tools\InnoSetup\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { $Iscc = (Get-Command ISCC.exe -ErrorAction Stop).Source }
}

dotnet test (Join-Path $root "ClaudeQuota.Core.Tests") -c Release
if ($LASTEXITCODE) { throw "tests failed" }

# Self-contained (the .NET runtime is bundled): nothing to install besides the app. WebView2 ships with Windows 11.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true -o $out -p:DebugType=none
if ($LASTEXITCODE) { throw "publish failed" }

& $Iscc "/DAppVersion=$version" "/DPublishDir=$out" (Join-Path $root "installer\ClaudeQuota.iss")
if ($LASTEXITCODE) { throw "Inno Setup failed" }
