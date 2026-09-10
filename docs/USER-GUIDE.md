# Codex Reset Guard

A small, portable Windows tray app that uses your **existing banked Codex reset credits**, according to rules you explicitly enable. Version 1.0.0. Independent software; not an OpenAI product.

## Start

1. Keep `CodexResetGuard.exe` and `CodexResetGuard.exe.config` together. Double-click the executable.
2. Wait for your account, weekly usage, and reset credits to load.
3. On **Reset credits**, check the credits this app may use. **Allow all shown** selects the credits currently displayed. Future credits remain unchecked.
4. On **Automatic resets**, choose the usage window, threshold, enabled period, maximum resets, reserve count, and credit selection.
5. Select **Enable automatic resets**. This is the opt-in that permits real redemptions under the displayed rules. No additional confirmation is required when the threshold is reached.

Defaults: **off**, weekly usage, **95% used**, 12 hours, maximum **one reset**, keep **one credit** in reserve, and earliest expiry among checked credits. No credits are initially selected. You can lower the reserve to zero if you want the selected final credit to be usable.

Left-click the tray icon to open settings; right-click for Enable/Pause, threshold presets, credits, refresh, and Quit. Closing settings keeps the tray app running. A gray icon means automatic resets are off, green means enabled, and amber means a reset is awaiting confirmation.

Pause before editing rules. **Save rules** does not enable automatic resets. Starting a new enabled period creates a new reset allowance. The saved deadline and allowance survive an app restart. Quit stops monitoring; it does not erase an unexpired opt-in. **Connection → Launch in the tray when I sign in to Windows** is a separate, initially disabled setting.

## Choosing a threshold

The app-server reports usage as whole percentages. A threshold of **99.99% cannot target the last 0.01%**: it will wait for a reported 100%, which may already be too late for an active request. The UI allows 1–100% with two decimal places, and offers 95%, 97%, 98%, 99%, and 99.99% tray presets.

Use **95%** when uninterrupted unattended work matters most. 98–99% trades away some headroom to consume more of the current allowance. No polling tool can promise zero interruption: a large request or concurrent work may cross the remaining quota between checks. This app does not automatically retry failed coding tasks or resume interrupted sessions.

Polling uses 60 seconds while disabled, 30 seconds while enabled, and 15 seconds when reported usage is within five percentage points of the threshold. Errors back off to at most five minutes. Every redemption decision uses a fresh read. A natural reset within 30 seconds prevents redemption; windows with unknown reset times cannot trigger it. The main Codex bucket is used; separate Spark buckets are excluded. Weekly and 5-hour windows are identified by their reported duration, not by their primary/secondary position.

## Credit selection and recovery

- Earliest expiry applies only to checked, currently available `codexRateLimits` credits shown by the server. Credits without a reported expiry are placed last. Credits expiring within 30 seconds are excluded.
- Selecting a specific credit never falls back to a different credit when that selection becomes unavailable.
- A reserve is applied to the server-reported available count. A count-only response never permits an arbitrary redemption.
- A reset request is saved before sending and always includes a specific credit ID and an idempotency key. A timeout preserves that exact request. Automatic retries reuse it only while fresh usage, reserve, expiry, and authorization rules still permit spending, at least 60 seconds apart, at most three sends before pausing. Changed or unknown conditions pause automatic recovery and preserve the journal. Pause blocks retries too.
- **Activity → Retry pending request** explicitly reconciles the original pending attempt. If it was never applied, this can use that selected credit now even if usage dropped, the reserve changed, or the enabled period ended; the app asks you to confirm this before sending. It never authorizes a different credit or starts another enabled period. A lost response after a successful reset may require this manual reconciliation.
- Acknowledged resets count against the allowance once. Another credit cannot be used until fresh usage decreases and the selected credit is no longer available. There is also a ten-minute cooldown after an acknowledged reset and a two-minute cooldown after any attempted reset.
- A corrupt or unwritable journal stops automatic operation. Do not delete a pending journal to work around an ambiguous request: doing so loses its idempotency record.
- An account change pauses automatic resets. A pending reset must be reconciled while signed in to its original account.

The server decides eligibility. A full banked reset changes the weekly reset date. Credits are promotional, may expire, and are not purchased API credits. This version only redeems at the selected usage threshold; it has no separate automatic “use before expiration” trigger.

## Requirements and privacy

- Windows 10/11 x64 with .NET Framework 4.8 or newer. No separate UI runtime, Node, or Python is needed for this app.
- Installed official Codex CLI **0.147.0 or newer**, signed in to ChatGPT. Common CLI installation paths are detected. An executable can be selected in Connection settings.
- File-backed ChatGPT identity in Codex's existing `auth.json`. Keyring-only, API-key-only, and unidentified accounts can’t be armed in this version. The app respects `CODEX_HOME`.
- The PC must be awake and the tray app running. It does not wake the computer or prevent sleep.

The app launches its own hidden official `codex app-server --listen stdio://` child process, reads account usage, and requests a reset only when authorized by your rules. It never starts a model turn, switches accounts, or restarts the desktop app. On completion it closes only its own app-server process and child job.

The existing Codex auth file is read in memory to derive a hashed account identity and guard against account switches; this app never writes it, copies tokens, or keeps tokens in its own journal. The official Codex process owns any normal credential refresh. No account information is sent to third-party servers. Server stderr and raw error text are not logged.

Settings, checked credit IDs, a hashed account identity, pending request identity, and the last 100 action records live in `%LOCALAPPDATA%\CodexResetGuard\state.json`. The optional sign-in setting creates this app's named entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. There is no installer or administrator requirement. The executable is unsigned.

To remove the app, turn off its sign-in setting, pause automatic resets, quit from the tray, and remove its portable folder. Preserve any pending journal until its original request has been reconciled.

## Source and validation

The complete C# source and build script are in `source/`. From PowerShell:

```powershell
.\source\Build.ps1 -OutDir .\build
.\build\CodexResetGuard.Tests.exe
```

The compiler supplied with .NET Framework is used; no package downloads are required. `--ui-smoke <directory>` renders all four tabs using explicitly synthetic data and cannot redeem credits. `--read-only-check <report.json>` checks the real account connection without sending a reset or model request. Use `Start-Process -Wait` when scripting those GUI-executable modes.

Validation for this build: 58 policy, persistence, reset-recovery and actual stdio-transport checks, plus live read-only account verification and rendered inspection of all four UI tabs. Credit redemption was tested only against an isolated fake service; **no real reset credit was consumed in development**.

Protocol references:

- [Official Codex app-server reset API](https://learn.chatgpt.com/docs/app-server#8-earned-rate-limit-resets-chatgpt)
- [Rate-limit protocol types and percentage rounding](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs)
- [How banked Codex resets work](https://help.openai.com/en/articles/20001498-how-banked-codex-resets-work)

The app-server interface can change with Codex releases. This build verifies the installed CLI version and stops spending on missing account identity, unsupported data, failed checks, or unrecognized reset outcomes; it does not patch or replace Codex.
