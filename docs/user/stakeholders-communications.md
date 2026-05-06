# Stakeholders & Communications

S3 module — stakeholder register with Mendelow's Power/Interest
matrix, engagement log, and a project-level communications matrix.

## Stakeholder register

Per-project stakeholders: name, organisation, role, contact
details, Power (1–5) + Interest (1–5). Service auto-computes
the engagement approach at write time:

- Power ≥ 4 + Interest ≥ 4 → **Manage Closely**
- Power ≥ 4 + Interest < 4 → **Keep Satisfied**
- Power < 4 + Interest ≥ 4 → **Keep Informed**
- Otherwise → **Monitor**

Per-tenant configurable thresholds is v1.1.

## Engagement log

Recorded interactions with stakeholders (`EngagementLog`) —
type (Meeting / Call / Email / Letter / Workshop / Other),
date, summary, follow-ups. Drives the activity feed for the
stakeholder management view.

## Communications matrix

Project-level `CommunicationItem` rows: who communicates with
whom, on what topic, at what frequency (Daily / Weekly / Monthly
/ Quarterly / AdHoc), via which channel (Email / Meeting /
Portal / Letter / Phone / Other). Used for project-handbook
communications planning.

## Common gotchas

- **Power and Interest are independent** — a contractor's
  director might score Power=5 / Interest=2 (ManageSatisfied)
  while their site supervisor is Power=2 / Interest=5
  (KeepInformed). The auto-classification reflects this.
- **The matrix doesn't generate communications automatically** —
  it's a planning surface; actual sends happen via Notifications
  (S14).
