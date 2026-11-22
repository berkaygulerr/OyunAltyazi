# Yerel yedek yayın: CI ile aynı adımlar. Gerekli: gh oturumu açık, dotnet 10, `dotnet tool install -g vpk --version 1.2.161`.
# Kullanım: powershell -File tools\release.ps1 [-Version 1.0.99]
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root
$repo = 'https://github.com/berkaygulerr/OyunAltyazi-releases'
$token = (gh auth token)
if (-not $Version) {
  $last = (gh release list -R berkaygulerr/OyunAltyazi-releases -L 1 --json tagName --jq '.[0].tagName' 2>$null)
  $n = if ($last -match '^v1\.0\.(\d+)$') { [int]$Matches[1] + 1 } else { 1 }
  $Version = "1.0.$n"
}
dotnet test tests/OyunAltyazi.Core.Tests -c Release --nologo -v q
dotnet test tests/OyunAltyazi.Translation.Tests -c Release --nologo -v q
Remove-Item publish, Releases -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/OyunAltyazi.App -c Release -r win-x64 --self-contained "-p:Version=$Version" -o publish
vpk download github --repoUrl $repo --token $token
vpk pack -u OyunAltyazi -v $Version -p publish -e OyunAltyazi.App.exe --packTitle OyunAltyazi --icon src/OyunAltyazi.App/app.ico
vpk upload github --repoUrl $repo --publish --releaseName "OyunAltyazi $Version" --tag "v$Version" --token $token
Write-Host "Yayınlandı: v$Version"
