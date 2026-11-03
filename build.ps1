# Builds the single-file app:  Счетоводство Lite.exe  (.NET Framework 4.8 host - already part of Windows 10/11 - with the WebView2 DLLs and the web app embedded)
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1   (close the running app first - it locks the exe)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
dotnet build (Join-Path $root 'host') -c Release -o (Join-Path $root 'dist')
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Copy-Item (Join-Path $root 'dist\SchetovodstvoLite.exe') (Join-Path $root 'Счетоводство Lite.exe') -Force
Remove-Item (Join-Path $root 'host\bin'), (Join-Path $root 'host\obj'), (Join-Path $root 'dist') -Recurse -Force -ErrorAction SilentlyContinue
'{0}: {1:n2} MB' -f 'Счетоводство Lite.exe', ((Get-Item (Join-Path $root 'Счетоводство Lite.exe')).Length / 1MB)
