# Contributing

Use Windows x64 with .NET Framework 4.8. The build uses the C# compiler that ships with .NET Framework (`Framework64\v4.0.30319\csc.exe`), which only understands **C# 5**: no string interpolation (`$""`), `?.`, `nameof`, expression-bodied members, or auto-property initializers. Build and run the offline tests:

```powershell
.\source\Build.ps1 -OutDir .\build
.\build\CodexResetGuard.Tests.exe
```

For UI changes, render every page in both themes and exercise the controls with synthetic account data:

```powershell
Start-Process .\build\CodexResetGuard.exe -ArgumentList '--ui-smoke', 'build\ui' -Wait
```

Inspect `build/ui/ui-checks.json`, the light/dark screenshots, and the enabled, pending, completed, and empty states. UI smoke mode never reads appearance preferences, changes startup registration, or sends a live reset request. Its isolated engine can only arm the fake account. Production reset logic remains covered by the separate tests above.

For connection changes, you may run `--read-only-check build\live-check.json` locally. This reads your signed-in account, so do not attach the raw report or live screenshots to a public issue. Automated tests must always use fake accounts and must never redeem real credits.

Keep changes focused. Add meaningful tests for policy, credit selection, account binding, and request recovery changes. Preserve the opt-in, credit allowlist, request journal, and account isolation. Include the reason for any change to redemption behavior in your pull request.

Do not commit auth files, account identifiers, reset-credit IDs, logs from real accounts, or tokens. Use `example.invalid` and clearly synthetic IDs in fixtures and screenshots. The portable ZIP must include its license and source. Release executables currently have no code-signing certificate.

Release maintainers can run `scripts/Release.ps1` or push a matching `v1.2.0` tag to build, test, smoke-test, package, and publish through GitHub Actions. Before changing the release version, update `Program.Version` and both assembly version attributes in `source/Program.cs`, the `assemblyIdentity` in `source/app.manifest`, and `docs/RELEASE-NOTES.md` (published as the release body). `Release.ps1` refuses to package when any of these disagree. Also update the `$Version` default in `scripts/Release.ps1` and the version line in `docs/USER-GUIDE.md`; those are not checked.

Claude support is monitor-and-assist only. Keep it read-only: do not add code that claims Claude resets, refreshes or persists Claude tokens, or presents itself as Claude Code.
