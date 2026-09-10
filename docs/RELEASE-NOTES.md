The first public release of **Codex Reset Guard**, a portable Windows tray app for opt-in redemption of your existing banked Codex reset credits.

- Choose allowed credits and use the earliest-expiring one, or select one specific credit.
- Watch weekly, 5-hour, or either usage window. Default threshold: 95% used.
- Set an enabled period, maximum number of resets, and reserve count.
- Persist requests before sending and recover with the same idempotency key.
- Pause automation when the account or retry conditions change.
- Native Windows UI, optional launch at sign-in, MIT-licensed source included.

**Download:** extract `CodexResetGuard-1.0.0-windows-x64.zip` and run `CodexResetGuard.exe`. Keep its `.config` file beside it. Automatic resets start **off**; select allowed credits and enable your rules to begin.

Requires Windows 10/11 x64, .NET Framework 4.8+, and the official Codex CLI 0.147.0+ signed in to ChatGPT with file-backed authentication. The executable is unsigned. `SHA256SUMS.txt` provides the ZIP checksum; the ZIP includes individual file checksums and source.

Codex reports whole percentages, so 99.99% waits for a reported 100%. Polling cannot guarantee uninterrupted sessions. This app does not purchase credits, switch accounts, or resume interrupted tasks. It is independent software, unaffiliated with OpenAI.

Validation uses an isolated fake reset service and real stdio transport. No real reset credits were consumed during development.

**Demo videos:** `codex-reset-guard-x-square.mp4` is formatted for social feeds; `codex-reset-guard-landscape.mp4` is 16:9. Both demonstrate the real app UI with synthetic data and original instrumental music. See `DEMO-LICENSE.txt` for reuse terms. Video files are separate from the app ZIP.
