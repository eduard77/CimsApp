# Risk Management

PMBOK 5/7 negative-risk module from S2. Per-project risk register
with RBS (Risk Breakdown Structure) taxonomy, qualitative + 3-point
quantitative assessments, response strategies, and contingency
drawdown tracking.

## Risk register

`Risk` entity per project. Lifecycle: **Identified → Assessed →
Active → Mitigated → Closed**. Each risk carries:

- Title, Description, RBS category (`RiskCategory` tree —
  Technical / External / Organisational / Project Management is
  the conventional starting set; per-project taxonomy).
- Probability + Impact (1–5 scale) — qualitative scoring drives
  the heatmap.
- 3-point quantitative estimate (Best / Most-Likely / Worst) with
  distribution shape (Triangular / PERT / Beta).
- Response strategy (Avoid / Transfer / Mitigate / Accept; positive
  strategies are v1.1 / B-029 — opportunity register).

## Contingency drawdown

Per-risk `RiskDrawdown` rows track when contingency is consumed.
Cross-module contingency-to-cost link is v1.1 / B-030.

## Monte Carlo (schedule-side)

Quantitative aggregation across risks → schedule-side Monte Carlo
is v1.1 / B-028 (also blocked on S4 CPM data).

## Common gotchas

- **The probability × impact heatmap is computed at render time**
  — service stores the inputs, not the score.
- **Closed risks remain in the register** — no archive flag in
  v1.0; filter by Status to hide.
