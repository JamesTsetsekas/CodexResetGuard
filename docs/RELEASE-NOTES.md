**Codex Reset Guard 1.2.0** adds reset timing modes, Claude usage alerts, and a round of reliability fixes for the reset engine, transport, and tray.

### New

- **Reset timing** (Automation → Timing):
  - **At the threshold (never stop)**: the existing behaviour and the default.
  - **Use the last few percent**: from your threshold, waits until Codex is projected to hit its limit in about a minute (or reports 99%), then resets. It uses most of the remaining headroom without letting the next message hit the wall.
  - **At the limit (let running turns finish)**: waits for a reported 100%, then until Codex has written no session log for 20 seconds, or until the grace period ends (3 minutes by default). Codex lets a running turn continue past the limit, so that overrun is absorbed by the reset. A new message sent during the wait may be blocked, so this mode suits interactive use more than long autonomous loops.
- **Natural-reset margin**: a credit is no longer spent when the watched window resets on its own within 15 minutes (configurable 0–240).
- **Claude tab**: optional, read-only monitoring of Claude plan usage (5-hour, weekly, and per-model weekly windows) through Claude Code's existing sign-in. It alerts once per window at your threshold and again at 100%. **Use a reset in Claude Code** opens Claude Code's own `/limit-reset` confirmation, and **Open claude.ai usage** opens Settings → Usage. Reset Guard never claims a Claude reset itself, and never stores or refreshes Claude tokens.
- The tray tooltip shows the Codex weekly and 5-hour windows and, when enabled, Claude usage. Alert balloons open the relevant page.
- The tray icon turns red when automatic resets are enabled but cannot work (for example, the reserve leaves no usable credit, or the connection is failing).
- **Close or recheck reset**: closes a reset Codex accepted but whose refreshed usage was never observed. The reset stays counted against your allowance, and closing it spends nothing.

### Fixed

- A reset whose reply was lost (a timeout after the server applied it) is now recovered automatically with its original key when the credit is already gone. Previously this turned automation off.
- An accepted reset is also closed by a lower credit inventory, so it no longer waits forever when usage did not visibly drop. No other credit is used until Codex reports refreshed usage (or you enable a new period).
- A resend of an already-gone credit that Codex declines is counted as used instead of being forgotten.
- Pausing now cancels a reconciliation that had been started but not yet sent.
- After a `noCredit`/`nothingToReset` reply the next attempt waits 5, then 15, then 30 minutes (instead of every 2 minutes); repeated declines collapse into one Activity entry, and automation pauses after five in a row.
- A failed journal write no longer leaves a phantom pending reset that was never sent.
- Enabling now refuses rules that could never fire (reserve ≥ available credits, or a usage window Codex doesn't report), and asks before spending immediately when usage already meets the rules.
- The transport now delivers a reply that the helper flushes just before exiting. Pipe writes no longer run on the UI thread, so a stalled helper cannot freeze the app.
- Clearing the Codex executable setting restores automatic detection. An empty or relative `CODEX_HOME` falls back to `~\.codex`.
- Polling uses a monotonic clock, so a backward clock correction no longer pauses checks. Connection back-off now reaches the documented five-minute cap.
- Journals with an enabled timed period but no deadline, or with negative counters, now fail closed.
- Tray: the menu opens on the first right-click. Changing the threshold from the tray no longer destroys an open window. Tray enable uses the same confirmation path as the window.
- Window:
  - It fits shorter and scaled displays.
  - Long status text is no longer clipped.
  - Saving settings before credits load no longer clears your allowed credits.
  - Credits stay readable while locked, including in dark mode.
  - Lists no longer rebuild, losing scroll position, on every poll.
  - Times respect cultures without AM/PM.
  - Escape closes an open dropdown before hiding the window.
  - Fonts are shared instead of leaked.
- A spent specific credit is cleared from Credit order, so the next Enable doesn't fail. The Codex handshake now reports the real app version.
- Release script: test failures print their details, the UI smoke runs before packaging, the version is checked in `Program.cs` (both assembly attributes and `Program.Version`), `app.manifest` and these notes, and successful runs clean up their staging folder.

**Download:** extract `CodexResetGuard-1.2.0-windows-x64.zip` and run `CodexResetGuard.exe`. Keep its `.config` file beside it. Automatic resets start **off** on a fresh install. To upgrade, quit the existing tray instance and replace the files in its portable folder. Version 1.1 settings, allowed credits, and any pending request survive the upgrade; new settings start at their defaults (threshold timing, 15-minute margin, Claude monitoring off).

Requires Windows 10/11 x64, .NET Framework 4.8+, and the official Codex CLI 0.147.0+ signed in to ChatGPT with file-backed authentication. Claude monitoring additionally requires Claude Code signed in with a Claude subscription. The executable is unsigned. `SHA256SUMS.txt` provides the ZIP checksum; the ZIP includes individual file checksums and source.

This app does not purchase credits, switch accounts, or resume interrupted tasks. It is independent software, unaffiliated with OpenAI or Anthropic.

Validation: offline policy, timing, recovery, persistence, Claude parsing/alert, and real stdio-transport checks, plus UI smoke checks for both themes and all five pages. Screenshots use synthetic data. No real reset credit was used to test this update.
