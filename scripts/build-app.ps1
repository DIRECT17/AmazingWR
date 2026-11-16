$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$versionFile = Join-Path $root 'app-version.txt'
$appVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
if ($appVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid app version in ${versionFile}: $appVersion" }
$assetBuild = Join-Path $PSScriptRoot 'build-assets.ps1'
& $assetBuild
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Branding asset generation failed.' }
$buildName = 'build-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$output = Join-Path (Join-Path $root 'dist') $buildName
dotnet publish (Join-Path $root 'app\AmazingWR.App\AmazingWR.App.csproj') -r win-x64 -c Release --self-contained true -p:Version=$appVersion -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
Write-Output "Built $(Join-Path $output 'AmazingWR.exe')"
