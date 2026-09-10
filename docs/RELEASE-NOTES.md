**Codex Reset Guard 1.1.0** refreshes the Windows interface while retaining the existing reset engine, authorization rules, and recovery journal.

- Compact layout with a usage summary, threshold marker, and allowed-credit count.
- Light, dark, and Windows appearance settings, including matching tray menus.
- Flat navigation and controls, with 95% / 99% editing shortcuts.
- Clear pending, paused, and completed reset states; Pause stays available during pending requests.
- Keyboard shortcuts: F5 to refresh, Ctrl+Tab to change pages, Escape to return to the tray.
- Existing settings, allowed credits, enabled period, budget, and pending request identity are preserved.

**Download:** extract `CodexResetGuard-1.1.0-windows-x64.zip` and run `CodexResetGuard.exe`. Keep its `.config` file beside it. Automatic resets start **off** on a fresh install. To upgrade, quit the existing tray instance and replace the files in its portable folder. Saved authorization and any pending request survive the upgrade.

Requires Windows 10/11 x64, .NET Framework 4.8+, and the official Codex CLI 0.147.0+ signed in to ChatGPT with file-backed authentication. The executable is unsigned. `SHA256SUMS.txt` provides the ZIP checksum; the ZIP includes individual file checksums and source.

Codex reports whole percentages, so 99.99% waits for a reported 100%. Polling cannot guarantee uninterrupted sessions. This app does not purchase credits, switch accounts, or resume interrupted tasks. It is independent software, unaffiliated with OpenAI.

Validation: 58 existing policy, persistence, recovery, and stdio checks, plus interactive UI checks for themes, inputs, opt-in, pending recovery, and empty inventory. Screenshots use synthetic account data. No real reset credit was used to test this update.

The original [v1.0 demo videos](https://github.com/JamesTsetsekas/CodexResetGuard/releases/tag/v1.0.0) still demonstrate the reset workflow. The README screenshots show the new interface.
