# UK GDPR

S11 module — data-protection record-keeping for UK GDPR
compliance. Five entities cover the regulator-required artefacts.

## ROPA — Record of Processing Activities

`RopaEntry` per organisation (NOT per project — ROPA is a tenant
artefact). Each row: data category, lawful basis (Consent /
Contract / LegalObligation / VitalInterests / PublicTask /
LegitimateInterest per Art. 6(1)), purpose, retention period,
recipients. Required by Art. 30.

## DPIA — Data Protection Impact Assessment

`DataProtectionImpactAssessment` workflow:
**Drafting → UnderReview → (Approved | RequiresChanges)**.
RequiresChanges loops back to Drafting for re-work. Approved is
terminal. State-machine in `Core/DpiaWorkflow.cs`. Required by
Art. 35 for high-risk processing.

## SAR — Subject Access Request

`SubjectAccessRequest` per tenant: **Received → InProgress →
(Fulfilled | Refused)**. 30-day clock per Art. 12(3) starts at
`RequestedAt`; service computes `DueAt = RequestedAt + 30 days`.

## Data breach log

`DataBreachLog` per tenant. Severity (Low / Medium / High /
Critical) drives the 72-hour ICO-notification clock per Art. 33
(starts at `DiscoveredAt` for breaches likely to result in risk
to data subjects).

## Retention schedules

Per-tenant `RetentionSchedule` entries: data category, retention
period (years), basis. Auto-enforcement across domain entities is
v1.1 / B-078; v1.0 records the schedule but doesn't auto-purge.

## Common gotchas

- **ROPA + Lessons + Retention are TENANT-scoped**, not
  project-scoped — they're cross-project compliance artefacts.
- **ICO breach reporting API integration** is v1.1 / B-076 —
  v1.0 records the breach; submission to the regulator is
  manual.
- **SAR auto-extraction across domain entities** is v1.1 /
  B-077 — v1.0 lets the operator track the request, manually
  collate the response.
