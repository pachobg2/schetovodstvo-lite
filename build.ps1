# Builds the single-file app:  Счетоводство Lite.exe  (self-contained .NET 8 + WebView2, web app embedded)
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1   (close the running app first - it locks the exe)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out  = Join-Path $root 'dist'
dotnet publish (Join-Path $root 'host') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Copy-Item (Join-Path $out 'SchetovodstvoLite.exe') (Join-Path $root 'Счетоводство Lite.exe') -Force
Remove-Item (Join-Path $root 'host\bin'), (Join-Path $root 'host\obj'), $out -Recurse -Force
'{0}: {1:n1} MB' -f 'Счетоводство Lite.exe', ((Get-Item (Join-Path $root 'Счетоводство Lite.exe')).Length / 1MB)
