# Reset Guard

A small, portable Windows tray app that uses your **existing banked Codex reset credits**, according to rules you explicitly enable. It can also watch your Claude plan usage and alert you when a Claude limit reset is worth using. Version 1.2.0. Independent software; not an OpenAI or Anthropic product.

## Start

1. Keep `ResetGuard.exe` and `ResetGuard.exe.config` together. Double-click the executable.
2. Wait for your account, weekly usage, and reset credits to load.
3. On **Credits**, check the credits this app may use. **Allow all shown** selects the credits currently displayed. Future credits remain unchecked.
4. On **Automation**, choose the usage window, threshold, timing, enabled period, maximum resets, reserve count, and credit selection.
5. Select **Enable automatic resets**. This is the opt-in that permits real redemptions under the displayed rules. No additional confirmation is required when the threshold is reached. If usage already meets the rules when you enable, the app asks first, because a credit would be used within seconds. Enabling is refused when the rules could never fire, for example when the reserve is at least the number of available credits.

Defaults: **off**, weekly usage, **95% used**, timing **at the threshold**, a **15-minute** natural-reset margin, 12 hours, maximum **one reset**, keep **one credit** in reserve, and earliest expiry among checked credits. No credits are initially selected. You can lower the reserve to zero if you want the selected final credit to be usable.

Left-click the tray icon to open settings; right-click for Enable/Pause, threshold presets, credits, refresh, and Quit. Closing settings keeps the tray app running. A gray icon means automatic resets are off, green means enabled, amber means a reset is awaiting confirmation, and red means automatic resets are enabled but cannot work right now (connection failing, or the rules are blocked). The tooltip shows Codex weekly and 5-hour usage, plus Claude usage when monitoring is on.

Pause before editing rules. **Save rules** does not enable automatic resets. Starting a new enabled period creates a new reset allowance. The saved deadline and allowance survive an app restart. Quit stops monitoring; it does not erase an unexpired opt-in. **Settings → Launch in the tray at Windows sign-in** is a separate, initially disabled setting.

## Appearance and navigation

The interface has a compact usage summary, flat navigation, and matching tray menus. In **Settings → Appearance**, choose **Use Windows setting**, **Light**, or **Dark**. Appearance is applied immediately and stored separately in `%LOCALAPPDATA%\CodexResetGuard\appearance.json`; it never enables resets or changes your saved rules. With the Windows setting selected, the app follows changes to the system's app theme.

The main action becomes **Pause automatic resets** while enabled, including when a request is pending. After pausing, **Review pending reset** opens Activity. That page offers **Reconcile pending reset** for the original request, or **Close or recheck reset** once Codex has accepted it. Disabled controls remain readable in both themes.

Use **F5** to refresh, **Ctrl+Tab** / **Ctrl+Shift+Tab** to change pages, and **Escape** to hide the window in the tray (or close an open dropdown). The 95% and 99% buttons are editing shortcuts; selecting one does not save or enable it. Reset budgets and reserves must be whole numbers.

## Choosing a threshold

The app-server reports usage as whole percentages. A threshold of **99.99% cannot target the last 0.01%**: it will wait for a reported 100%, which may already be too late for an active request. The UI allows 1–100% with two decimal places, and offers 95%, 97%, 98%, 99%, and 99.99% tray presets.

Use **95%** when uninterrupted unattended work matters most. 98–99% trades away some headroom to consume more of the current allowance. No polling tool can promise zero interruption: a large request or concurrent work may cross the remaining quota between checks. This app does not automatically retry failed coding tasks or resume interrupted sessions.

### Timing

**Automation → Timing** decides when, past the threshold, the reset is used:

| Timing | Resets when | Best for |
| --- | --- | --- |
| **At the threshold (never stop)** | the watched window reaches the threshold | long autonomous runs; the default |
| **Use the last few percent** | usage is at or above the threshold **and** Codex is projected to reach 100% within about 75 seconds, or reports 99% | getting more out of each window without hitting the wall |
| **At the limit (let running turns finish)** | Codex reports 100% **and** no Codex session log has been written for 20 seconds, or the grace period (default 3 minutes, 0–30) has passed | interactive use |

Codex lets a turn that is already running continue past the limit, subject to fair use; only the next message is blocked. A reset absorbs everything used before it, so the later the reset, the more each credit is worth. The cost is risk: with **At the limit**, a message sent while the app waits can be blocked until the reset lands, which can stop an autonomous loop. Idle detection reads only file times under `CODEX_HOME\sessions` for today and yesterday, never their contents. It cannot see Codex use on other machines or in the cloud, so the grace period always caps the wait.

**Margins → reset margin** (default 15 minutes, 0–240): a credit is not used when the watched window resets on its own within this many minutes. Set it to 0 to restore the old behaviour.

### Polling

Polling uses 60 seconds while disabled, 30 seconds while enabled, and 15 seconds when a *watched* window is within five percentage points of the threshold. It drops to 10 seconds once a watched window reaches the threshold or 97%, whichever is higher, in the last-few-percent and at-the-limit modes. A pending request is checked every 30 seconds. Errors back off from 30 seconds, doubling to at most five minutes. Scheduling uses a monotonic clock, so wall-clock corrections do not delay checks. Every redemption decision uses a read no older than 40 seconds. A natural reset within 30 seconds always prevents redemption; windows with unknown reset times cannot trigger it. The main Codex bucket is used; separate Spark buckets are excluded. Weekly and 5-hour windows are identified by their reported duration, not by their primary/secondary position.

## Credit selection and recovery

- Earliest expiry applies only to checked, currently available `codexRateLimits` credits shown by the server. Credits without a reported expiry are placed last. Credits expiring within 30 seconds are excluded.
- Selecting a specific credit never falls back to a different credit when that selection becomes unavailable.
- A reserve is applied to the server-reported available count. A count-only response never permits an arbitrary redemption.
- A reset request is saved before sending and always includes a specific credit ID and an idempotency key. A timeout preserves that exact request. If the next read shows that credit is already gone, the app resends the same key automatically: that cannot spend anything new, and it recovers a reply lost after a successful reset. Otherwise automatic retries reuse it only while fresh usage, reserve, expiry, and authorization rules still permit spending, at least 60 seconds apart, at most three sends before pausing. Changed or unknown conditions pause automatic recovery and preserve the journal. Pause blocks retries too, including a reconciliation that was started but not yet sent.
- **Activity → Reconcile pending reset** explicitly reconciles the original pending attempt. If it was never applied, this can use that selected credit now even if usage dropped, the reserve changed, or the enabled period ended; the app asks you to confirm this before sending. It never authorizes a different credit or starts another enabled period. A lost response after a successful reset may require this manual reconciliation.
- Acknowledged resets count against the allowance once. Another credit cannot be used until the selected credit is no longer available and either fresh usage decreased or the available count dropped. If that is never observed, **Close or recheck reset** can close it; it stays counted and nothing is spent. A spent credit is removed from the allowed list and from Credit order.
- If a resend of a credit that is already gone gets `noCredit` or `nothingToReset`, the reset was most likely applied by the earlier attempt. It is recorded as **Reset counted**, charged to the allowance, and another credit waits for refreshed usage.
- Cooldowns: ten minutes after an acknowledged reset, and two minutes after a new attempt (pending retries use 60 seconds). When Codex declines with `noCredit` or `nothingToReset`, the wait grows to 5, 15, then 30 minutes. Repeated declines are recorded as one Activity entry, and automation pauses after five in a row.
- A corrupt or unwritable journal stops automatic operation. Do not delete a pending journal to work around an ambiguous request: doing so loses its idempotency record.
- An account change pauses automatic resets. A pending reset must be reconciled while signed in to its original account.

The server decides eligibility. A full banked reset changes the weekly reset date. Credits are promotional, may expire, and are not purchased API credits. This version only redeems at the selected usage threshold; it has no separate automatic “use before expiration” trigger.

## Claude usage alerts

The **Claude** page can watch your Claude plan usage. Turn on **Watch my Claude plan usage (read-only)**, choose the alert threshold and window, and select **Save settings**. It starts off.

- Usage is read from the same endpoint Claude Code uses for `/usage`, with the sign-in Claude Code already stores in `~\.claude\.credentials.json` (or `CLAUDE_CONFIG_DIR`). The account email shown is read from `~\.claude.json` (or `CLAUDE_CONFIG_DIR\.claude.json`). The token is read into memory for each request. It is never stored, logged, copied, or refreshed. If it has expired, open Claude Code once to refresh it. Requests identify themselves as Reset Guard.
- Each watched 5-hour or weekly window alerts once at your threshold and again at 100% (optional). At most one alert is shown per check; a window that jumps straight to 100% gets only the limit alert. Per-model windows (such as Fable) are shown but do not alert. Alerts are skipped when the window resets on its own within two minutes. Clicking an alert opens the Claude page. Usage from a failed check is never shown as current.
- Claude limit resets are claimed in Claude's own apps, which ask you to confirm each one. **Use a reset in Claude Code** opens Claude Code with `/limit-reset`. **Open claude.ai usage** opens Settings → Usage, where Claude offers *Reset for free* when you have one. Reset Guard never claims a Claude reset itself. Claude currently reports banked-reset details only to its own apps, so the page may say so instead of showing a count.
- Polling is every three minutes, or every minute once a watched window is within ten points of the threshold. Errors back off to 2, 4, 8, then 15 minutes.

## Requirements and privacy

- Windows 10/11 x64 with .NET Framework 4.8 or newer. No separate UI runtime, Node, or Python is needed for this app.
- Installed official Codex CLI **0.147.0 or newer**, signed in to ChatGPT. Common CLI installation paths are detected. An executable can be selected in Settings.
- File-backed ChatGPT identity in Codex's existing `auth.json`. Keyring-only, API-key-only, and unidentified accounts can’t be armed in this version. The app respects `CODEX_HOME`.
- The PC must be awake and the tray app running. It does not wake the computer or prevent sleep.
- Optional Claude monitoring needs Claude Code signed in with a Claude subscription (Pro, Max, or Team); API-key sign-ins have no plan limits.

The app launches its own hidden official `codex app-server --listen stdio://` child process, reads account usage, and requests a reset only when authorized by your rules. It never starts a model turn, switches accounts, or restarts the desktop app. On completion it closes only its own app-server process and child job.

The existing Codex auth file is read in memory to derive a hashed account identity and guard against account switches; this app never writes it, copies tokens, or keeps tokens in its own journal. The official Codex process owns any normal credential refresh. No account information is sent to third-party servers; Claude monitoring talks only to Anthropic, with Claude Code's own sign-in. Server stderr and raw error text are not logged.

Settings, checked credit IDs, a hashed account identity, pending request identity, and the last 100 action records live in `%LOCALAPPDATA%\CodexResetGuard\state.json`. The optional sign-in setting creates this app's named entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. There is no installer or administrator requirement. The executable is unsigned.

To remove the app, turn off its sign-in setting, pause automatic resets, quit from the tray, and remove its portable folder. Preserve any pending journal until its original request has been reconciled.

## Source and validation

The complete C# source and build script are in `source/`. From PowerShell:

```powershell
.\source\Build.ps1 -OutDir .\build
.\build\ResetGuard.Tests.exe
```

The compiler supplied with .NET Framework is used; no package downloads are required. `--ui-smoke <directory>` renders all five pages in both themes using explicitly synthetic data and cannot redeem credits. `--read-only-check <report.json>` checks the real account connection without sending a reset or model request. Use `Start-Process -Wait` when scripting those GUI-executable modes.

Validation for this build: the offline policy, timing, persistence, reset-recovery, Claude parsing/alert and actual stdio-transport checks in `Tests.cs`, plus rendered inspection of all five pages. Credit redemption was tested only against an isolated fake service; **no real reset credit was consumed in development**.

Protocol references:

- [Official Codex app-server reset API](https://learn.chatgpt.com/docs/app-server#8-earned-rate-limit-resets-chatgpt)
- [Rate-limit protocol types and percentage rounding](https://github.com/openai/codex/blob/main/codex-rs/app-server-protocol/src/protocol/v2/account.rs)
- [How banked Codex resets work](https://help.openai.com/en/articles/20001498-how-banked-codex-resets-work)

The app-server interface can change with Codex releases. This build verifies the installed CLI version and stops spending on missing account identity, unsupported data, failed checks, or unrecognized reset outcomes; it does not patch or replace Codex.
