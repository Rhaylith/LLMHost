<#
  Builds the release files for Windows x64 into artifacts\ :
    LLMHost-<version>-win-x64.zip   portable package (what the in-app updater installs)
    SHA256SUMS                      checksums of every file in artifacts\
  Usage:  pwsh build\publish.ps1 -Version 1.2.0
  Signing: set SIGN_CERT_PFX and SIGN_CERT_PASSWORD to sign llmhost.exe (and the updater) with signtool before they are zipped.
#>
param([Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'artifacts'
$pub = Join-Path $root 'bin\publish\win-x64'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $out -ItemType Directory | Out-Null

dotnet publish (Join-Path $root 'LLMHost.csproj') -p:PublishProfile=win-x64-single "-p:Version=$Version" -nologo
if ($LASTEXITCODE) { throw 'publish failed' }

if ($env:SIGN_CERT_PFX) {
  foreach ($f in @('llmhost.exe', 'updater\LLMHost.Updater.exe')) {
    signtool sign /f $env:SIGN_CERT_PFX /p $env:SIGN_CERT_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 (Join-Path $pub $f)
    if ($LASTEXITCODE) { throw "signing $f failed" }
  }
}

$zip = Join-Path $out "LLMHost-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $pub '*') -DestinationPath $zip
Get-ChildItem $out -File | Where-Object Name -ne 'SHA256SUMS' | ForEach-Object {
  "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS') -Encoding ascii
Get-Content (Join-Path $out 'SHA256SUMS')
