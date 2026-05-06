# CIMS User Documentation

Construction Information Management System — user-facing reference
covering every module the v1.0 pilot ships. Each entry below links
to a module-specific doc with: what the page is for, who uses it,
primary actions, and common gotchas.

## Getting started

- [Logging in and selecting a project](getting-started.md)
- [Projects — create, configure, members](projects.md)

## Project work

- [Documents (CDE)](documents-cde.md) — document register,
  CDE state transitions
- [RFIs and Actions](rfis-actions.md) — request-for-information
  flow + general action tracker
- [Cost & Commercial](cost-commercial.md) — CBS, EVM, cashflow,
  variations, payment certificates
- [Risk Management](risk.md)
- [Stakeholders & Communications](stakeholders-communications.md)
- [Schedule & Programme](schedule.md) — activities, dependencies,
  baselines, Last Planner System
- [Change Control](change-control.md) — change requests, BSA
  HRB tagging
- [Procurement](procurement.md) — strategy, tender packages,
  evaluation, contracts, NEC4 early warnings + compensation events
- [Reporting & Dashboards](reporting.md)
- [Quality / Inspection Activities](inspections.md)
- [Daily Diary and NCRs](daily-diary-ncrs.md) — site-user workflows
- [Notifications & Alerts](notifications-alerts.md)
- [Search](search.md) — cross-entity project search
- [Audit Trail and Audit & Compliance Support](audit-and-compliance.md)
  — project audit log, evidence library, external auditor role,
  audit export

## Standards-driven modules

- [ISO 19650 / MIDP / TIDP](iso-19650.md)
- [Golden Thread / BSA 2022](bsa-2022-golden-thread.md) — Gateway
  packages, MOR, Safety Case, HRB metadata
- [UK GDPR](uk-gdpr.md) — ROPA, DPIA, SAR, data-breach log,
  retention schedules
- [Kaizen / Lessons Learned](kaizen-lessons.md) — Improvement
  Register, Lessons Library, Opportunities to Improve

## Administration

- [Admin Console](admin-console.md) — user / organisation /
  invitation management; tenant audit

## Conventions used in these docs

- **Roles** referenced by their CIMS enum names (`SuperAdmin`,
  `OrgAdmin`, `ProjectManager`, `InformationManager`,
  `TaskTeamMember`, `ClientRep`, `Viewer`, `ExternalAuditor`).
  See `docs/security/role-matrix.md` for the full per-endpoint
  matrix.
- **State machine references** point to the relevant
  `Core/<X>Workflow.cs` source — workflows are pure functions
  documented inline.
- **PAFM Appendix** references map sprint scope to the Project
  Assurance Framework Manual; `docs/scope-reconciliation-2026-05-05.md`
  explains the F.N → S(N-1) numbering offset.
