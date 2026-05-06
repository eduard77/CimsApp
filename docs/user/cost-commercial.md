# Cost & Commercial

The S1 module — NEC4-aligned cost management spanning Cost
Breakdown Structure (CBS), commitments, cost periods, payment
certificates, variations, EVM, and cashflow.

## Cost Breakdown Structure

Per-project hierarchy of `CostBreakdownItem` rows with budget
amounts (decimal, project currency). Per-line schedule + progress
+ EVM/valuation/cashflow wire-ups landed in B-017 (post-S1
hardening). Import via CSV upload at the API
(`POST /projects/{id}/cost/import`); see admin docs for format.

## Payment certificates

NEC4 cumulative semantics per ADR-0013. Two states: **Draft →
Issued**. Draft updates allowed (audit-twin
`payment_certificate.draft_updated`); issuing a certificate
freezes the snapshot (`payment_certificate.issued`). Variation
omissions (negative `EstimatedCostImpact`) net against approved
additions in `IncludedVariationsAmount` (PR #32 pinned this
contract).

## Variations

3-state workflow: **Raised → Approved | Rejected**.
Approved variations are Included on subsequent payment certs.
Atomic transaction wrap on document-state transitions (PR #34)
ensures revision publish + state flip are committed together.

## Earnings Value Management & Cashflow

`Core/Evm.cs` implements PV / EV / AC, SPI / CPI, EAC. Cashflow
forecast computed from CBS planned amounts × period schedule
(`CostPeriodPlannedCashflow` migration).

## Common gotchas

- **CBS import is empty-only at v1.0** — re-import / merge into
  an existing CBS is deferred to v1.1.
- **Construction Act notices and final-account schedule**
  (B-014 / B-015) are CR-003 deferrals; not in v1.0.
- **6-state Variations workflow** is v1.1 / B-016; v1.0 is the
  reduced 3-state.
