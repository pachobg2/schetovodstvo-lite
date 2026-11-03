# Счетоводство Lite

Small Windows app (WebView2) that calculates a monthly consultant salary from work hours (or a manually entered EUR amount) and issues Bulgarian + English invoices as PDF. Data stays on your PC in `Schetovodstvo-Lite-data\` next to the exe.

- Build: `powershell -ExecutionPolicy Bypass -File build.ps1`
- Release (maintainer): bump `<Version>` in `host\Schetovodstvo-Lite.csproj`, commit, run `release.ps1`.
- The app checks the latest GitHub release, verifies SHA-256 of `schetovodstvo-lite.exe` against `SHA256SUMS.txt` and replaces itself. Your data is not touched.
