# Golden Thread / BSA 2022

S10 module — Building Safety Act 2022 statutory information
artefacts for Higher-Risk Buildings (HRBs). Three workflow
entities (Gateway packages, MOR, Safety Case) plus per-project
HRB metadata.

## HRB metadata

Set on the project detail page (PM-and-up). Fields:

- `IsHrb` — boolean
- `HrbCategory` — A / B / C per Building Safety Regulator
  guidance, or NotApplicable for non-HRB projects.

When `IsHrb = true`, the Golden Thread surfaces become available
on the project. Per-tenant inference rules (e.g. "residential ≥
18m and ≥ 7 storeys → Cat A") are v1.1 / B-072.

## Gateway packages

`GatewayPackage` workflow: **Drafting → Submitted → Decided**.
Per-project per-Type-per-Number unique-when-active.

- `GatewayType`: Gateway1 (planning), Gateway2 (pre-construction),
  Gateway3 (pre-occupation) per Part 3 of the BSA 2022.
- `GatewayDecision` populated on Decided: Approved /
  ApprovedWithConditions / Refused.

## MOR — Mandatory Occurrence Report

`MandatoryOccurrenceReport` per project: severity (Low / Medium /
High / Critical), narrative, occurrence date, reporter, current
state. BSA 2022 s.87 reference. v1.1 / B-070 adds direct BSR API
push.

## Safety Case

`SafetyCase` summary entity per HRB project, linking to the
underlying documents and the BSR submission status. v1.1 / B-071
extends with Schedule 5 canonical structure + PDF export.

## Common gotchas

- **HRB-only surfaces are gated by `Project.IsHrb`** — the
  MIDP/TIDP / Gateway / MOR / SafetyCase pages don't appear in
  the nav for non-HRB projects.
- **Audit-log query layer** for Golden Thread compliance reporting
  is v1.1 / B-074.
- **Soft-immutability un-mark workflow** for accidental Golden
  Thread artefact lock is v1.1 / B-075.
