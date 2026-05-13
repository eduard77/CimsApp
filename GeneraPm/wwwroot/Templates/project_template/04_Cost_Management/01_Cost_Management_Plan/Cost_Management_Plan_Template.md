# COST MANAGEMENT PLAN

**Document Reference:** [PRJ-CODE]-CST-001
**Revision:** P01
**CDE Status:** Shared · Suitability: S3

---

## 1. PURPOSE

Define how project costs are planned, estimated, budgeted, funded, managed, and controlled — using PMBOK Cost domain principles and Earned Value Management (EVM).

---

## 2. UNITS & CURRENCIES

- **Primary currency:** GBP (GBP)
- **Level of precision:** whole £
- **VAT treatment:** Exclusive / Inclusive
- **Exchange rate source & cadence:** [e.g. Bank of England, monthly]

---

## 3. COST STRUCTURE & CODING

Costs are coded to the WBS (`/02_Scope_Management/04_WBS/`) and an additional **cost element structure**:

| Code | Cost Element |
|---|---|
| 1 | Preliminaries |
| 2 | Substructure |
| 3 | Superstructure |
| 4 | Internal finishes |
| 5 | MEP services |
| 6 | External works |
| 7 | Professional fees |
| 8 | Plant & equipment |
| 9 | Labour |
| 10 | Materials |
| 11 | Subcontracts |
| 12 | Contingency |

---

## 4. BUDGET BASELINE (PMB — Performance Measurement Baseline)

| Control Account | Budget (£) | % |
|---|---|---|
| 1.1 Project Management | | |
| 1.2 Design | | |
| 1.3 Procurement | | |
| 1.4 Enabling Works | | |
| 1.5 Substructure | | |
| 1.6 Superstructure | | |
| 1.7 Envelope | | |
| 1.8 Fit-out | | |
| 1.9 MEP | | |
| 1.10 Externals | | |
| 1.11 Commissioning | | |
| 1.12 Handover | | |
| **TOTAL** | | 100% |

---

## 5. EARNED VALUE MANAGEMENT (EVM)

### 5.1 Key Metrics
| Metric | Formula | Target |
|---|---|---|
| PV (Planned Value) | Baseline budget for planned work | — |
| EV (Earned Value) | Budget for work actually completed | — |
| AC (Actual Cost) | Actual cost of work performed | — |
| SV (Schedule Variance) | EV − PV | ≥ 0 |
| CV (Cost Variance) | EV − AC | ≥ 0 |
| SPI (Schedule Perf. Index) | EV / PV | ≥ 1.00 |
| CPI (Cost Perf. Index) | EV / AC | ≥ 1.00 |
| EAC | BAC / CPI | ≤ BAC |
| ETC | EAC − AC | — |
| TCPI | (BAC − EV) / (BAC − AC) | ≤ 1.00 |

### 5.2 EVM Cadence
- Measurement period: monthly (last day)
- Report published: by working day 5 of following month
- Location: `/04_Cost_Management/05_Earned_Value_Reports/`

### 5.3 Progress Measurement Rules
| Work Package Type | Rule |
|---|---|
| Short duration (< 1 reporting period) | 0/100 (binary) |
| Medium duration | 50/50 or % complete |
| Long duration | Physical % complete (milestones) |
| Level of effort | Apportioned to time elapsed |

---

## 6. COST CONTROL THRESHOLDS

| Variance | Action |
|---|---|
| CPI drops below 0.95 | Cost recovery plan within 2 weeks |
| CPI drops below 0.90 | Escalate to Sponsor |
| EAC exceeds BAC by > 5% | Change Request required |
| Contingency < 2% remaining | Sponsor notification |

---

## 7. CHANGE & CONTINGENCY

- **Contingency reserve:** [X]% of baseline — held by PM, drawn down by approved CRs
- **Management reserve:** [Y]% — held by Sponsor, outside baseline

---

## 8. CASH FLOW

Monthly cash flow forecast maintained in `/04_Cost_Management/04_Cash_Flow/`.
Retention: 5% until Practical Completion, then 2.5% until end of defects period.

---

## 9. APPROVALS

| Authority | Cumulative Threshold | Approver |
|---|---|---|
| Variance approval | ≤ 2% of budget | PM |
| Variance approval | 2–5% of budget | Sponsor |
| Variance approval | > 5% of budget | Steering Group |
| Change Order | ≤ £[X]k | PM |
| Change Order | > £[X]k | CCB |

---

## 10. APPROVAL OF THIS PLAN

| Role | Name | Signature | Date |
|---|---|---|---|
| Cost Manager | | | |
| Project Manager | | | |
| Sponsor | | | |
