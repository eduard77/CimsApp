# Procurement

S6 module — strategy capture, tender packages, evaluation matrix,
contracts, and NEC4 early warnings + compensation events.

## Procurement strategy

Per-project `ProcurementStrategy` row: approach (Traditional /
Design and Build / Construction Management / Management Contracting
/ Partnering Framework / Other), contract form (NEC4 Options A–F,
JCT Standard / Design and Build, Other), notes.

## Tender packages

`TenderPackage` workflow: **Draft → Issued → Closed**. Draft is
editable; Issued freezes the package (bidders are working on it);
Closed is reached via Award (T-S6-06) or explicit
abandon-without-award. State-machine in
`Core/TenderPackageWorkflow.cs`.

Per-package: `EvaluationCriterion` rows (Price / Quality buckets
for v1.0; richer typology v1.1) with weights summing to 1.0.

## Tenders + evaluation

Each tender package collects `Tender` submissions: Submitted →
Evaluated → (Awarded / Rejected / Withdrawn). `EvaluationScore`
per criterion per tender. The evaluation matrix surface
(`Core/EvaluationMatrix.cs`) computes per-tender weighted overall
scores; partial scoring leaves OverallScore null until complete.

## Contracts

`Contract` records the awarded engagement. v1.0 ships Active →
Closed only; richer states (Suspended, Terminated, Disputed) are
v1.1 driven by NEC4 / JCT contract clauses.

## NEC4 Early Warnings

`EarlyWarning` per project: Raised → UnderReview → Closed (NEC
clause 15 reference). Inline state guard in the service.

## NEC4 Compensation Events

`CompensationEvent` per project: **Notified → Quoted → (Accepted
| Rejected) → Implemented**. NEC4 clause 60.1 reference. v1.0
ships the bare workflow; deadline-driven automatic transitions
(B-048 PM 4-week notification, B-049 contractor 3-week quotation,
B-050 risk-allowance pricing) are v1.1.

## Common gotchas

- **Evaluation criterion weights MUST sum to 1.0** — the matrix
  flags invalid weight totals.
- **Tender Withdrawn is bidder-initiated** — Award after
  Withdrawn is rejected.
