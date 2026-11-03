# Builds and publishes a GitHub release (tag vX.Y.Z from host\Schetovodstvo-Lite.csproj) with schetovodstvo-lite.exe and SHA256SUMS.txt.
# Usage:  powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "- what changed"   (commit first; close the running app)
param([Parameter(Mandatory)][string]$Notes)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root
$ver = ([xml](Get-Content (Join-Path $root 'host\Schetovodstvo-Lite.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "bad version '$ver'" }
$tag = "v$ver"
if (git status --porcelain) { throw 'uncommitted changes - commit first' }
if (git tag -l $tag) { throw "tag $tag already exists - bump the version" }
& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$stage = Join-Path $env:TEMP ("schetovodstvo-lite-release-" + $ver)
New-Item -ItemType Directory -Force $stage | Out-Null
$exe = Join-Path $stage 'schetovodstvo-lite.exe'
Copy-Item (Join-Path $root 'Счетоводство Lite.exe') $exe -Force
$sum = '{0} *schetovodstvo-lite.exe' -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText((Join-Path $stage 'SHA256SUMS.txt'), $sum + "`n", (New-Object Text.UTF8Encoding($false)))
$notesFile = Join-Path $stage 'notes.md'
[IO.File]::WriteAllText($notesFile, $Notes, (New-Object Text.UTF8Encoding($false)))
git tag $tag
git push origin HEAD
git push origin $tag
gh release create $tag $exe (Join-Path $stage 'SHA256SUMS.txt') --repo pachobg2/schetovodstvo-lite --title $tag --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release failed' }
"released $tag"
