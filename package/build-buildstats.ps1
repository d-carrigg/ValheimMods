# Builds BuildStats in Release and makes the Thunderstore upload zip.
#
#   pwsh package/build-collectall.ps1                       # uses the paths in BuildStats.csproj
#   pwsh package/build-collectall.ps1 -ManagedDir ... -BepInExDir ...
#
# Output: dist/dcarrigg-BuildStats-<version>.zip with manifest.json, icon.png, README.md
# and BuildStats.dll at the top level, ready to upload to thunderstore.io.
param(
    [string]$ManagedDir,
    [string]$BepInExDir
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$pkg  = Join-Path $root 'package/BuildStats'
$dist = Join-Path $root 'dist'

# The manifest version and the plugin version must match.
$manifest = Get-Content (Join-Path $pkg 'manifest.json') -Raw | ConvertFrom-Json
$version  = $manifest.version_number
$plugin   = Get-Content (Join-Path $root 'BuildStats/Plugin.cs') -Raw
if ($plugin -notmatch 'Version\s*=\s*"([^"]+)"') { throw 'Version not found in BuildStats/Plugin.cs' }
if ($Matches[1] -ne $version) {
    throw "manifest.json version_number ($version) differs from Plugin.cs Version ($($Matches[1])). Make them match."
}

$buildArgs = @('build', (Join-Path $root 'BuildStats/BuildStats.csproj'), '-c', 'Release', '-nologo')
if ($ManagedDir) { $buildArgs += "-p:ManagedDir=$ManagedDir" }
if ($BepInExDir) { $buildArgs += "-p:BepInExDir=$BepInExDir" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

$dll = Join-Path $root 'BuildStats/bin/Release/BuildStats.dll'
Copy-Item $dll $pkg -Force

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist "dcarrigg-BuildStats-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
$files = 'manifest.json', 'icon.png', 'README.md', 'BuildStats.dll' | ForEach-Object { Join-Path $pkg $_ }
Compress-Archive -Path $files -DestinationPath $zip

Write-Host "Built $zip"
