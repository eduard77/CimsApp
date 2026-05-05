# PAFM scope reconciliation — 2026-05-05

First time the PAFM-SD `.docx` Appendix F was read end-to-end since the
S15 retro flagged the spec-paste request. This doc captures the
reconciliation for the three sprints that shipped under the
"scope-spec caveat" pattern (S15 retro / S16 kickoff / S17 kickoff)
plus the labelling-drift correction.

## Headline finding — F-section to sprint-number offset

PAFM Appendix F maps **`F.N = S(N-1)`** end-to-end (every entry is
`F.X SY — name` where `Y = X - 1`). Our S16 / S17 kickoffs assumed
`F.N = SN` and labelled the sprints F.16 / F.17 in their docs.
Both kickoffs predicted the right *content* (Admin Console;
Mobile Views) but used the wrong F-section number.

The drift originated at our S8 — "CI Hardening / branch-protection
groundwork" — which is **not in the PAFM Appendix F roadmap** at
all. PAFM expected S8 = F.9 ISO 19650 / MIDP. We inserted CI
Hardening as an extra sprint, pushing every subsequent sprint by
one. Then at S15 we inserted **another** extra sprint —
"Search & Discovery" — which is also not in PAFM. So our 18
shipped sprints (S0..S17) cover roughly 16 PAFM-listed F-sections
plus two off-roadmap insertions.

**Action:** future kickoffs use `F.N = S(N-1)`. For the immediate
horizon: our S18 = PAFM F.17 (Audit & Compliance Support), our
S19 = PAFM F.18, our S20 = PAFM F.19, our S21 = PAFM F.20 (UAT &
Release — the v1.0 ship gate).

---

## F.15 reconciliation — Admin Console (shipped as our S16)

**PAFM bullets (verbatim):**

> F.15 S14 — Admin Console
> - User management within organisation.
> - Role assignment.
> - Template library (letters, reports, NCRs).
> - Subscription/billing view (read-only for v1.0).

**Our S16 shipped:**

- ✅ **User management within organisation** — `/admin/users`
  (paged list with email/name search; deactivate; revoke tokens;
  Global role inline-set with privilege guards).
- ✅ **Role assignment** — `PUT /admin/users/{id}/global-role`
  with three privilege guards (no self-target, only SuperAdmin
  may grant SuperAdmin, OrgAdmin scoped via standard query
  filter). Audit-twin event `auth.user_global_role_set` captures
  every change.
- ❌ **Template library** — NOT shipped. Letters / reports / NCRs
  per-tenant template library was not in our S16 scope. Closest
  existing shape: per-project PMBOK template provisioning at
  `/projects/{id}/templates` (S0 era) — that's per-project, not
  per-tenant, and not letter/report/NCR-shaped.
- ❌ **Subscription / billing view (read-only)** — NOT shipped.
  The v1.0 product has no billing model and no subscription
  surface; we have no entity for it. This was always going to
  be a placeholder for the eventual SaaS-mode build.

**Bonus we shipped beyond F.15:**

- Organisation administration (`/admin/organisations`) — list,
  edit Name+Country, SuperAdmin-only deactivate. Useful but not
  in the F.15 bullets.
- Invitation administration (`/admin/invitations`) — list active,
  revoke pending. Reuses the existing `InvitationService.CreateAsync`
  for mint.
- Tenant-wide audit viewer (`/admin/audit`) — paged tenant-scoped
  audit query with date / action / entity / userId filters.
  Cross-project complement to the existing project-scoped
  `/audit` page. Note: this is *audit log viewing*, distinct from
  F.17 *Audit & Compliance Support* (Evidence library / External
  Auditor role / Audit export).

**Reconciliation outcome:** **2 of 4 F.15 bullets shipped at v1.0**;
two deferred:

- B-105 (NEW) — Template library (letters / reports / NCRs).
  Per-tenant template library distinct from the per-project PMBOK
  templates already shipped. Unblock: pilot operator requests
  organisational standard-letters / NCR-form templates beyond what
  per-project provisioning provides.
- B-106 (NEW) — Subscription / billing view (read-only). Premature
  without a billing model; defer until SaaS-mode planning starts.
  Unblock: v1.1 product manager decision to add subscription/billing
  to the product.

The "bonus" admin surface we shipped (orgs + invitations + audit
viewer) is reasonable v1.0 admin scope and adds real value the
operator would otherwise reach via SQL / PowerShell — keep, no
backlog action needed.

---

## F.16 reconciliation — Mobile Views (shipped as our S17)

**PAFM bullets (verbatim):**

> F.16 S15 — Mobile Views
> - Responsive site-user workflows (NCR raising, RFI response, daily diary).
> - Works on iPhone and Android browsers.
> - Offline-tolerant reads (no offline writes in v1.0).

**Our S17 shipped:**

- ✅ **Works on iPhone and Android browsers** — `MainLayout`
  responsive sidebar drawer (`Variant.Responsive` at `Sm`
  breakpoint); per-page MudTable column gating via `d-none
  d-{breakpoint}-table-cell` utility classes; `AdminOrganisations`
  Edit dialog FullWidth fix. Visual verification at 375px
  confirmed clean (T-S17-05). MudBlazor's mobile browser support
  is the underlying guarantee — we exercised it, didn't add it.
- ⚠️ **Responsive site-user workflows (NCR raising, RFI response,
  daily diary)** — PARTIALLY satisfied. We made the existing pages
  responsive, but the *specific* workflows the bullet calls out are:
  - **NCR raising:** there is no NCR entity / page in v1.0 (NCRs
    appear as a `DocumentType` enum value but no dedicated raise
    flow; per-project PMBOK templates have `NCRs` folder
    placeholders). The mobile responsiveness work didn't add an
    NCR-raising flow because none existed.
  - **RFI response:** `/rfis` page is responsive; respond dialog
    works on mobile. ✅
  - **Daily diary:** there is no Daily Diary entity / page in
    v1.0. Like NCR, it doesn't exist to be made responsive.
- ❌ **Offline-tolerant reads (no offline writes in v1.0)** — NOT
  shipped. S17 explicitly deferred this to v1.1 / B-102 in the
  kickoff (Decision 1 chose "no PWA shell, no offline cache").
  PWA / service worker / offline cache is a meaningful chunk of
  work that doesn't fit the responsive-sweep sprint shape.

**Reconciliation outcome:** **1.5 of 3 F.16 bullets shipped at
v1.0**; 1.5 gaps:

- The two missing workflow pages (NCR raising, Daily diary) are
  **product gaps**, not S17-Mobile gaps. They were never in the
  v1.0 product scope explicitly. PAFM seems to assume they would
  be — flag for product owner. Adding them is multi-sprint work,
  not a fast-follower.
- B-107 (NEW) — Daily Diary entity + page + tenant + audit. Not
  in v1.0 scope. Unblock: pilot site supervisor explicitly asks
  for a daily diary feature OR product owner promotes it into a
  future sprint.
- B-108 (NEW) — NCR raising flow. Distinct from the existing
  `DocumentType.Ncr` enum value (which is just a typing tag, not
  a flow). Unblock: similar — pilot demand or product-owner
  promotion.
- B-102 (existing, raised in S17) — Mobile-first UX redesign /
  PWA / offline reads. The "offline-tolerant reads" bullet maps
  directly here. Already in the backlog with explicit unblock
  conditions.

---

## F.17 reconciliation — Audit & Compliance Support (= our S18 scope)

**PAFM bullets (verbatim):**

> F.17 S16 — Audit & Compliance Support
> - Evidence library — organised artefact collection per compliance area.
> - External Auditor role — time-limited, scope-limited read access.
> - Audit export (PDF or ZIP bundle).

**This is NEXT.** Our S18 default-lean is now decisively pinned —
no scope-spec caveat needed at S18 kickoff because the .docx is
read.

**Per-bullet S18 sprint-shape preview:**

- **Evidence library** — organised artefact collection per
  compliance area. Likely shape: a new `ComplianceArea` enum
  (UK GDPR / BSA 2022 / ISO 19650 / NEC4 / Quality / HSE
  reasonable starting set) with a join entity
  `EvidenceArtefact (ProjectId, ComplianceAreaId,
  ArtefactRef, AddedById, AddedAt)` referencing existing
  Document/Audit/Inspection rows. Service + controller + UI
  page — moderate effort, ~5h. Most existing entities can be
  surfaced as evidence; the artefact reference is a discriminated
  link (DocumentId / RfiId / AuditLogId / InspectionActivityId
  nullable) rather than a duplicated body.
- **External Auditor role** — time-limited, scope-limited read
  access. New `UserRole.ExternalAuditor` enum value;
  `AuditorAssignment (UserId, ProjectId, ScopeAreas[],
  ExpiresAt)` table; query filter override that grants read on
  scoped entities under the assignment expiry; aggressive
  TokenInvalidationCutoff bump on assignment-end. Probably the
  most subtle bullet — needs careful tenant-isolation thinking
  since an external auditor is by definition cross-tenant
  (their User row lives in their own tenant; their *visibility*
  is scoped to the auditee's tenant). Sprint-shape risk: this
  alone could be 8-10h. Possibly v1.0-Option-A simplifies to
  "external auditor invitation with a project-scoped read role"
  rather than full cross-tenant scoping.
- **Audit export (PDF or ZIP bundle)** — package an
  audit-log + linked artefacts + signature manifest into a
  downloadable bundle. PDF rendering or ZIP-of-PDFs. Existing
  S7 reporting / dashboard service has rendering precedent;
  reuse if the shape fits. ~4h.

**Triple Option A defaults to surface at S18 kickoff:**

1. Evidence library shape: discriminated-link vs duplicated body
   (default A: discriminated link).
2. External Auditor role: project-scoped invitation+role-tag vs
   full cross-tenant scoping (default A: project-scoped
   invitation, simpler).
3. Audit export format: ZIP-of-PDFs vs single multi-section PDF
   (default A: ZIP-of-PDFs, easier to reason about).

Total sprint-shape estimate: **15-20h** depending on Decision 2.
Headroom against 72h capacity: comfortable.

---

## F.18 — Security & Performance (preview only, S19 scope)

**PAFM bullets (verbatim):**

> F.18 S17 — Security & Performance
> - Penetration test passed with zero open criticals.
> - Load test: 100 concurrent users without degradation.
> - OWASP Top 10 review complete and signed off.
> - Secrets management in production.

This is a **non-development sprint** — it's a quality gate before
v1.0 ship rather than a feature-build sprint. Penetration test
needs an external tester; load test needs an environment that can
take traffic; OWASP review is a structured walk through the OWASP
Top 10 checklist (parts of which are already covered by post-S1
audits + the structured-audit-twin work + the smoke-walk-bug-class
fixes from PRs #41-#44). Secrets management is a deployment-config
sprint.

**Out-of-scope for the agent's per-session sprint shape.** This
sprint will need:
- A real secrets-management decision (Azure Key Vault vs
  environment variables vs sealed-secret pattern).
- An external pen-test commission (not a code change).
- A load-test rig provisioned somewhere not-the-laptop.

Worth flagging to the user in advance so the S19 kickoff can
schedule the external dependencies. **No backlog change.**

---

## Action items (this session)

1. ✅ Capture this reconciliation as `docs/scope-reconciliation-2026-05-05.md`.
2. Update `docs/current-sprint.md` (already on PR #63) to:
   - Use the correct PAFM mapping (F.17 not F.18 for S18).
   - Pin S18 default-lean to **Audit & Compliance Support**, no
     longer "speculative without spec".
3. Add B-105..B-108 to v1.1 backlog:
   - B-105 Admin Console template library (per-tenant letters /
     reports / NCR templates).
   - B-106 Subscription / billing view (read-only).
   - B-107 Daily Diary entity + page.
   - B-108 NCR raising flow (distinct from `DocumentType.Ncr`
     enum value).
4. Update memory with the F-section offset finding so future
   kickoffs don't repeat it.

---

## Open questions for the user

- ~~**B-107 / B-108 priority:**~~ **RESOLVED 2026-05-05.** Product
  owner confirmed both as v1.0 product gaps to schedule, not
  v1.1 aspirations. Both promoted from v1.1 backlog to **v1.0 /
  S20 — Site-User Mobile Workflows** (paired sprint). Slot is
  between S19 (F.18 Security & Performance) and S21 (F.19
  Documentation & Training). Backlog entries B-107 / B-108
  updated with `Status (2026-05-05): PROMOTED` markers; the
  v1.0 sprint roadmap in `docs/current-sprint.md` reflects the
  insertion. Trade-off accepted: F.17 Audit & Compliance
  Support's Evidence library will not have Daily Diary / NCR
  rows to draw from at v1.0 ship; the Evidence library shape
  generalises so adding those evidence types in S20 is an
  additive change, not a rework of S18.
- **F.13 reconciliation (Genera Systems QA / HSE Integration):**
  not part of this session's "do all three" scope, but worth
  flagging — our S13 shipped Inspection & Activities under the
  label "F.13 Option A scope cut". The PAFM F.13 bullets are
  REST API / Webhook / SSO / bidirectional sync — a partner
  integration sprint. Our S13 shipped a chunk that touches the
  inspection-activity entity but not the integration plumbing.
  B-086..B-089 carry the deferred Genera-integration work
  (REST endpoints / webhooks / SSO / bidirectional sync) and
  remain outstanding. PAFM Ch 47 paste is the canonical reference
  for these and is still on the standing-request list.
