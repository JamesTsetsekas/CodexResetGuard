# Security reports

For a possible vulnerability involving unintended credit redemption or credential exposure, use GitHub's private vulnerability reporting under **Security → Report a vulnerability**. Do not post tokens, real account IDs, reset-credit IDs, or a live recovery journal in a public issue.

Include the app version, Codex CLI version, expected behavior, and reproduction steps using a fake account where possible. Pause automatic resets while investigating unexpected behavior. Preserve a pending journal so the original request can be reconciled safely.

The app is designed to redeem only existing credits explicitly allowed by the user. It cannot revoke a request already sent to Codex. It is independent software and the Codex server remains responsible for credit eligibility.
