# Notifications & Alerts

S14 module — in-app SignalR notifications, SMTP email pipeline,
and threshold-based alerts on cost / schedule / risk metrics.

## In-app notifications

SignalR hub at `/hubs/notifications`. Authenticated via JWT —
the bearer is passed as `?access_token=` query param at
hub-connect time per the standard SignalR-over-WebSocket
pattern (see `Program.cs` `OnMessageReceived`).

Each `Notification` row: UserId (recipient), Type, Title, Body,
optional Link, Read flag, ReadAt. Pushed in real-time via the
`INotificationPusher` service when business mutations fire.

## Email

`IEmailSender` + `EmailQueue` (in-memory channel) +
`EmailDispatcherHostedService` background drain. Configure SMTP
via `Email:Smtp:*` env vars per ADR-0016. `Email:Enabled = false`
(default) routes every send through `NoopEmailSender` —
production deployments flip the flag.

Email loss on app restart is acceptable for v1.0 (notifications
are advisory, the underlying record is the source of truth).
Persistent queue with retry / audit trail is v1.1 / B-091.

## Threshold-based alerts

`AlertRule` per project: Title, Metric, Comparison operator,
Threshold, Recipient, CooldownMinutes. The `ThresholdEvaluator`
hosted service iterates active rules every tick; when the
metric crosses the threshold per the comparison, a Notification
+ email fire to the recipient.

Three v1.0 metrics:
- `CostUtilizationPercent` — (committed + actuals) / budget × 100
- `OpenEarlyWarnings` — count of EarlyWarning where State != Closed
- `OpenRisks` — count of Risk where Status != Closed

Rich rule DSL (boolean combinations, time-of-day, multi-recipient)
is v1.1 / B-092 / B-093.

## Common gotchas

- **SMS notifications** (Twilio) are v1.1 / B-090 — v1.0 ships
  in-app + email only.
- **Mobile push notifications** (FCM / APNs) are v1.1 / B-094.
- **Cooldown prevents alert storms** — default 60 min between
  re-fires of the same rule. Adjust per rule if needed.
