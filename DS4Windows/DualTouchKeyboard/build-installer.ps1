# Builds the DS4 DualTouch distributable zip from a checkout.
# Run on a build machine with the .NET SDK. Does NOT publish a GitHub
# Release. Does NOT pip-install anything. Output: a zip containing the
# DS4 publish output plus the DualTouchKeyboard folder and installer.
# NOTE: the self-contained publish is ~275 MB uncompressed (measured
# 2026-11-03), so this zip will NOT be ~20 MB. See INSTALLER.md.
param(
    [string]$RepoRoot = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$OutZip = "$env:TEMP\DS4Windows-DualTouch.zip"
)
$ErrorActionPreference = 'Stop'
Set-Location $RepoRoot
dotnet publish DS4WindowsWPF.sln -c Release -r win-x64 --self-contained true
$pub = Join-Path $RepoRoot 'DS4Windows\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish'
if (-not (Test-Path (Join-Path $pub 'DualTouchKeyboard\ds4_keyboard.py'))) {
    throw 'Publish output is missing DualTouchKeyboard\ds4_keyboard.py; check the .csproj Content rule.'
}
$stage = Join-Path $env:TEMP 'DS4DualTouch-stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
Copy-Item $pub $stage -Recurse
Copy-Item (Join-Path $PSScriptRoot 'Install-DS4DualTouch.ps1') (Join-Path $stage 'Install-DS4DualTouch.ps1')
Copy-Item (Join-Path $PSScriptRoot 'INSTALLER.md') (Join-Path $stage 'INSTALLER.md')
if (Test-Path $OutZip) { Remove-Item $OutZip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $OutZip
$exe = Get-Item (Join-Path $stage 'DS4Windows.exe')
$zip = Get-Item $OutZip
Write-Output "DS4 exe: $($exe.LastWriteTime) $($exe.Length) bytes"
Write-Output "Zip: $($zip.FullName) $($zip.Length) bytes"
Write-Output 'NOT published to GitHub Releases. Upload manually only when approved.'
