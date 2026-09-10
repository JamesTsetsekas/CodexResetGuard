# Contributing

Use Windows x64 with .NET Framework 4.8. Build and run the offline tests:

```powershell
.\source\Build.ps1 -OutDir .\build
.\build\CodexResetGuard.Tests.exe
```

For UI changes, render all tabs with synthetic account data:

```powershell
Start-Process .\build\CodexResetGuard.exe -ArgumentList '--ui-smoke', 'build\ui' -Wait
```

For connection changes, you may run `--read-only-check build\live-check.json` locally. This reads your signed-in account, so do not attach the raw report or live screenshots to a public issue. Automated tests must always use fake accounts and must never redeem real credits.

Keep changes focused. Add meaningful tests for policy, credit selection, account binding, and request recovery changes. Preserve the opt-in, credit allowlist, request journal, and account isolation. Include the reason for any change to redemption behavior in your pull request.

Do not commit auth files, account identifiers, reset-credit IDs, logs from real accounts, or tokens. Use `example.invalid` and clearly synthetic IDs in fixtures and screenshots. The portable ZIP must include its license and source. Release executables currently have no code-signing certificate.

Release maintainers can run `scripts/Release.ps1` or push a matching `v1.0.0` tag to build, test, package, and publish through GitHub Actions. Update both assembly version attributes and release documentation before changing the release version.
