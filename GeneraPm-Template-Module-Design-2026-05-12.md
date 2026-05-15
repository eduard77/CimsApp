# Genera PM Template Module — Design Document

**Session:** 2026-05-12 (consolidation of decisions made across the session)
**Status:** Draft v1.0 — Layer 1 complete, Layers 2–7 deferred to future sessions
**Author:** Eduard Szigeti, with Claude
**Branch state at writing:** `master @ 9ca9d63` (post-merge, all 5 fixes verified, 982/982 tests passing)

---

## 1. Executive Summary

This document captures the design decisions made in the 2026-05-12 session for what is now established as **the most important module in Genera PM: the Template Module**.

The Template Module is the structured-data spine of Genera PM. Every project artefact a UK PM Consultancy produces (Business Case, Charter, Risk Register, Stakeholder Register, Communications Plan, all subsidiary management plans, NEC4 contract artefacts, IPA Gateway reviews, RIBA stage outputs, CDM artefacts, ISO 19650 information delivery plans) is expressed as a Template instance within this module. The module is what makes Genera PM "complete PM software" rather than a document register.

This session designed Layer 1 (Domain Model) in full and made the load-bearing philosophical and architectural decisions on which Layers 2–7 depend. The build itself is deferred to future sessions.

### What is locked in

- **Strategic goal:** PMBOK 8 Predictive worked exemplar, UK overlay (full), PM Consultancy perspective, fictional NHS healthcare fit-out as the scenario.
- **Architecture:** Path B (region-pluggable from module 1).
- **Philosophy:** Template-only document creation. No file upload path. Structured data first, documents are an output. "Do it once, do it right."
- **Domain model:** Single unified `Document` entity with internal `DocumentRevision` history (Option A1).
- **Integration:** Template Module extends Documents/CDE rather than parallels it. Reuses Stakeholder, Actions, and Audit modules.
- **Lifecycle:** Templates are sequenced — only the next valid template in the project lifecycle is selectable at any moment.

### What is deferred

- Layer 2: Field Model (types, validation, structured-data design)
- Layer 3: Pre-fill Engine
- Layer 4: State Machine and Governance (per-revision-checkpoint save, reviewer assignment, narrative comments)
- Layer 5: Render and Export
- Layer 6: Regional Pluggability (UK pack as first regional skin)
- Layer 7: Lifecycle / Dependency Engine (newly identified during Step 2 walkthrough)

### What this changes about Genera PM

The Template Module redefines Genera PM. Today's Documents/CDE module supports arbitrary file upload with metadata. **In the new model, this path is removed.** New documents can only be created from templates. Existing uploaded documents (from today's test session) remain as legacy data but no new uploads are accepted.

This is a breaking change. It is deliberate and aligned with the "do it once, do it right" philosophy.

---

## 2. Strategic Framing

### 2.1 Goal hierarchy

The exercise has three goals, ranked by priority:

1. **Build Genera PM into a complete PM software platform aligned with UK construction PM practice.** This is the primary commercial deliverable. The Template Module is the architectural enabler.
2. **Produce a worked PMBOK 8 exemplar for the Acacia Ward fictional project.** This validates the platform by exercising every artefact.
3. **Generate Project Experience Record evidence for public sector procurement.** A side-effect of the first two.

These goals are mutually reinforcing but they are not equivalent. The platform is the primary goal; the exemplar is the validation method; the PER is a beneficial output.

### 2.2 The fictional project: Acacia Ward Upgrade

Locked at session start. Summary:

- **Client:** North Surrey NHS Foundation Trust (fictional)
- **Project:** Refurbishment of Acacia Ward, Frimley Park Hospital, Level 2 South Wing. 26 beds → 22 beds with reconfigured single-room ratio. Replace ageing M&E plant.
- **Budget:** £4.8M (Trust capital £3.2M + STP/ICS bid £1.6M)
- **Programme:** 18 months
- **Procurement:** Two-stage NEC4 Option C via ProCure23
- **Our role:** Independent PM Consultancy appointed under NEC4 Professional Services Contract by the Trust. RIBA Stages 1–7 scope. We are not the Principal Designer.
- **Drivers:** Clinical (single-room ratio, HBN 04-01), Estates (M&E end-of-life), Regulatory (HTM 03-01 ventilation, HTM 04-01 water)

This is the stable backdrop for every artefact produced through the module.

### 2.3 The philosophical breakthrough

Mid-session, Eduard articulated the core principle:

> "Do it once, do it right. We will gather data from there that we use in different analysis tools and if data is not there the analysis will be compromised. So template only."

This reframes Genera PM. It is not a document management system that happens to have structured templates. **It is a structured data platform that happens to produce documents.** Every Genera PM feature downstream (risk analysis, schedule optimisation, change control, earned value, dashboards, AI insights) is a data consumer of the templates. The fidelity of every analysis depends on the discipline enforced at template creation.

Three consequences:

1. **No escape hatches for unstructured data.** No "upload anything" path. No free-text catch-all fields.
2. **The platform is opinionated by design.** Users who don't want to work in this discipline are not the audience.
3. **Differentiation from competitors.** Procore, Aconex, Asite all accept arbitrary uploads. Genera PM does not. This is a deliberate product positioning.

---

## 3. Decisions — Stage A through Stage D

### 3.1 Development approach: PMBOK 8 Predictive

Chosen over Adaptive or Hybrid because:
- Public-sector procurement panels and APM ChPP assessors expect predictive artefacts.
- Forces breadth across PMBOK and UK frameworks (which is what Genera PM needs).
- Project Experience Record evidence is stronger.

PMBOK 8 explicitly permits adaptive sub-flows within a predictive project. Reserved for later when relevant artefacts come up.

### 3.2 Regional overlay: Full UK

Green Book Five Case Model, IPA Gateway 0–5, NEC4 (Option C + sub-contracts), ProCure23, CDM 2015, Building Safety Act 2022 (BSA-aware, HRB not triggered), ISO 19650-2 (with UK National Annex), Uniclass 2015, RIBA Plan of Work 2020, Government Soft Landings Framework.

### 3.3 Perspective: Independent PM Consultancy

Selected over Client-side or Main Contractor-side. Most artefact-heavy of the three positions. Closest to Eduard's stated career trajectory (consultancy build, public sector framework targets). Closest to Genera PM's eventual buyer audience.

### 3.4 Architecture: Path B (region-pluggable from module 1)

Confirmed after explicit discussion of Paths A, B, B-lite, C. Path B is the most expensive upfront but commits properly to the international roadmap.

Architectural implications:
- Code organised as `GeneraPm.Core` (region-agnostic) + `GeneraPm.Regions.UK` (UK pack) + future `Regions.US`, `Regions.AU` packs.
- Templates are tagged by region.
- Lookup data (Uniclass codes, framework definitions, role taxonomies) tagged by region.
- Configuration determines which pack(s) load at startup. Per-tenant or per-project pack selection deferred to Layer 6.

### 3.5 Document handling: Option A with internal revisions (A1)

Single unified `Document` entity. Template Instance and Document are the same thing.

Rationale: Lean management — no two-step "draft template then render to document." Metadata (ISO 19650 number, Uniclass codes, project, template) is assigned at creation. Revisions are children of the Document, capturing field values and state. The Document carries identity; revisions carry content.

This rejects an earlier proposed Option B (separate TemplateInstance and Document entities). Eduard's reasoning was correct: lean management, no double work, no rework.

### 3.6 The six workflow decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Document creation entry point | **Template-only.** No file upload path. Existing upload capability removed from Documents/CDE in the new model. |
| 2 | Template picker UX | **Sequenced availability.** Only the next valid template in the project lifecycle is selectable. Lifecycle/Dependency Engine implied (Layer 7). |
| 3 | ISO 19650 number sequencing | **Per-Type sequential**, scoped to Project + Originator + Type. Confirms existing Genera PM behaviour. |
| 4 | Edit save strategy | **Per-revision-checkpoint.** User explicitly saves at logical milestones. Each save is a new revision creation event in the audit trail. "Unsaved changes" indicator required in UI. |
| 5 | Reviewer / approver selection | **User picks from project participants** at submission time. Reviewer pool is the project's Stakeholder Register. Not template-prescribed. |
| 6 | Comment placement | **Per-revision narrative comment**, single comment per state transition, attached to the revision. No file attachments. No external references. |

### 3.7 Implications surfaced

- **Stakeholder Register is now a hard prerequisite.** Step 5 requires the Register to exist and be populated before any document can be submitted for review. The Stakeholder Register is currently Partial (backend exists, UI does not). It must be built before or alongside the Template Module.
- **Documents/CDE module is structurally extended, not replaced.** The Template Module makes documents *structured* (with fields and revisions). Documents/CDE already handles registers, state, ISO 19650 numbers, audit. They share infrastructure.
- **Existing modules become integration points.** Stakeholder (role resolution), Actions (reviewer notifications), Audit (state and field-level history), Documents/CDE (lifecycle). The Template Module is a spine, not a silo.

---

## 4. Layer 1 — Domain Model (complete)

### 4.1 Core entities

```
Template
├── Id (Guid)
├── TemplateCode (e.g. "SOC-GreenBook-UK")
├── TemplateVersion (e.g. "1.0")
├── RegionalPackId (FK → RegionalPack)
├── Framework (enum: GreenBook, IPA_Gateway, NEC4, ISO19650, RIBA, PMBOK, CDM, BSA, SoftLandings)
├── PerformanceDomain (enum: per PMBOK 8 — Stakeholder, Team, Development Approach, Planning, Project Work, Delivery, Measurement, Uncertainty)
├── ArtefactClass (enum: BusinessCase, Charter, ManagementPlan, Register, ReviewReport, ContractArtefact, GovernanceArtefact, etc.)
├── UniclassPMCode (e.g. "PM_40_10_15" — applied to instances)
├── UniclassFICode (e.g. "FI_60_10_75" — applied to instances)
├── Iso19650TypeCode (the "Type" segment of the document number, e.g. "BC" for Business Case)
├── Title
├── Description
├── Sections (collection of TemplateSection)
├── StateMachineId (FK → StateMachine — Layer 4)
├── RenderProfileId (FK → RenderProfile — Layer 5)
├── LifecycleDependencyId (FK → LifecycleNode — Layer 7)
├── IsActive (supersedes prior versions)
└── PublishedAt, PublishedBy
```

```
TemplateSection
├── Id, TemplateId (FK), SectionNumber, Title, GuidanceText, Order
└── Fields (collection of TemplateField)

TemplateField
├── Id, SectionId (FK), FieldKey, FieldLabel, FieldType
├── IsRequired, PreFillRule (Layer 3), GuidanceText, ValidationRule, Order
```

```
Document
(Unified entity — Template Instance and Document are one thing)
├── Id
├── TemplateId (FK)
├── TemplateVersionAtCreation (snapshot — never changes for this Document)
├── ProjectId (FK)
├── Iso19650Number (full: PROJ-ORG-VOL-LVL-TYPE-ROLE-NNNN)
├── UniclassPMCode (copied from template at creation)
├── UniclassFICode (copied from template at creation)
├── Title (defaults to template; user can override at creation)
├── CurrentState (FK → StateMachineNode)
├── CurrentRevisionId (FK → DocumentRevision, latest)
├── CreatedAt, CreatedBy
└── Revisions (collection of DocumentRevision)
```

```
DocumentRevision
├── Id
├── DocumentId (FK)
├── RevisionCode (P01, P02, ..., C01, C02, ... per ISO 19650-2 UK convention — P-series pre-construction, C-series construction)
├── FieldValues (collection — all field values at this revision)
├── State (the state this revision is in: WIP / Shared / Published / Archived)
├── CreatedAt, CreatedBy
├── PublishedAt (nullable — set when state reaches Published)
├── NarrativeComment (nullable — populated on state transitions)
└── IsLatest (bool)
```

```
FieldValue
├── Id
├── DocumentRevisionId (FK)
├── FieldId (FK → TemplateField at snapshot version)
├── Value (text — serialised per field type)
├── LastEditedAt, LastEditedBy
```

### 4.2 Key relationships

**Template ↔ Document:** Many-to-one. Many SOCs across projects, all derived from one SOC Template version. New template versions do not retroactively affect existing Documents (snapshot at creation).

**Document ↔ DocumentRevision:** One-to-many. A Document is identity (ISO 19650 number, codes, parent template). Revisions are state and content. ISO 19650 number stays constant across revisions; revision code increments.

**DocumentRevision ↔ State:** Each revision has its own state. When a new revision is created on a Published document, the old revision is moved to Archived state, the new revision starts in WIP. This satisfies ISO 19650-2 immutability of Published documents.

### 4.3 Integration with existing Documents/CDE module

The Template Module is structurally an extension of Documents/CDE. They share:
- The same ISO 19650 numbering register.
- The same state machine (WIP → Shared → Published → Archived).
- The same Uniclass classification scheme.
- The same audit log.
- The same module entry point (the Documents (CDE) page that we tested today).

The difference: today's documents have a file attached. Future template-backed documents have structured field values + a rendered representation. Both are visible in the same register. Both transition through the same states.

The retirement of the file-upload path is a clean breaking change: future versions of Genera PM only create template-backed documents.

### 4.4 What Layer 1 deliberately doesn't address

- **Field types and structure** (Layer 2)
- **Pre-fill logic from project metadata** (Layer 3)
- **State transitions, reviewer assignment, comment capture, audit detail** (Layer 4)
- **Render to PDF / Word for distribution** (Layer 5)
- **Regional pack mechanics** (Layer 6)
- **Which template is selectable when** (Layer 7)

These are dependent on Layer 1 being right. Layer 1 is now considered settled.

---

## 5. Open Work — Layers 2 to 7

### 5.1 Layer 2 — Field Model

What kinds of fields can a TemplateField be?

Confirmed types so far: ShortText, LongText, RichText, Currency, Date, Choice (single), MultiChoice, Table, FileAttachment, ProjectMetadataRef, RegisterRef.

Open questions for Layer 2:
- How are typed fields designed for analysis as well as display? (Currency = value + currency code + confidence band?)
- Are there field *clusters* (Stakeholder reference = role + name + organisation)?
- How does validation work — at field level, at section level, at document level?
- How are tables structured (fixed columns? user-defined rows?)?
- File attachments — are they allowed at all under the "no upload escape hatch" rule? (Open question: maybe yes for genuinely external evidence like a signed agreement scan, with the meta data captured structurally and only the binary attached.)

### 5.2 Layer 3 — Pre-fill Engine

How does project metadata flow into a new Document?

Examples of pre-fill rules:
- Trust name → from `Project.AppointingParty.Name`
- Project budget → from `Project.BudgetValue`
- SRO name → from Stakeholder Register, role = "SRO"
- Programme dates → from Schedule module (when it exists)
- Risk count → from Risk Register (when surfaced)

Open questions:
- Pre-fill at creation only, or live-bound to source? (Recommendation: at creation only — once filled, the value is an editable revision-bound value, not a live reference.)
- What if the source is empty? (e.g. Stakeholder Register has no SRO populated yet.) — depends on Layer 7 sequencing.
- How are pre-fill rules expressed declaratively? (JSON? C# expressions?)

### 5.3 Layer 4 — State Machine and Governance

The flow: WIP → Shared → Published → Archived. Already in Genera PM for unstructured documents.

For template-backed documents, additional:
- Per-revision-checkpoint save (each save = revision update; major saves = new revision)
- Submit-for-review (state transition with reviewer selection from Stakeholder Register)
- Review outcome (approve → Published, return → back to WIP)
- Per-revision narrative comment captured at each state transition
- Audit: every state transition, every reviewer selection, every comment, every revision creation

### 5.4 Layer 5 — Render and Export

Templates produce documents that human readers want as PDF or Word. The render layer handles this.

Open questions:
- Renderer per template or generic? (Recommendation: generic, driven by template structure metadata + theme.)
- How is structured data formatted in narrative form? (Tables for register-style templates; narrative paragraphs for plan-style templates.)
- Trust branding / project branding in headers and footers?
- Renderer triggered automatically on Publish or on user demand?

### 5.5 Layer 6 — Regional Pluggability

The UK pack is the first concrete regional implementation. Future US and AU packs.

Open questions:
- Pack as separate .NET project, or as data-only?
- How is the loaded pack determined at runtime? (Configuration? Per-tenant? Per-project?)
- Can a tenant have multiple packs loaded? (Likely yes, for consultancies with international subsidiaries.)
- Pack-level configuration vs template-level configuration boundary.

### 5.6 Layer 7 — Lifecycle / Dependency Engine (newly identified)

Step 2 of the workflow walkthrough surfaced this requirement: only the next valid template in the project lifecycle should be selectable. This is a project lifecycle enforcement engine.

Open questions:
- How are lifecycle dependencies declared? (Template A unlocks Template B when A reaches state X.)
- What are the unlock conditions? (Predecessor Published? Gateway X passed? Project at RIBA stage Y?)
- Can dependencies be overridden? (e.g. genuine exception case — recommend: no, with optional admin override flag for documented exceptions.)
- How is the project's "current position" in the lifecycle represented?

This is significantly more complex than the picker UX implied. It needs its own design layer.

---

## 6. Prerequisites and Sequencing

The Template Module cannot stand alone. Build dependencies:

| Build before Template Module | Reason |
|---|---|
| Stakeholder Register UI | Reviewer selection at submission depends on it. Currently Partial (backend only). |
| Regional Pack architecture (Layer 6) | Templates must be loadable from a pack. |
| Documents/CDE refactor: remove file upload path | The new model is template-only. Existing path retired. |

| Build alongside Template Module | Reason |
|---|---|
| Lifecycle / Dependency Engine (Layer 7) | Template availability depends on it. Sub-module of Template work. |
| First template (SOC under Green Book) | Validates the module by being its first user. |

| Build after Template Module | |
|---|---|
| Charter template | Validates that the module is generic, not SOC-specific. |
| OBC template, FBC template, all subsequent artefacts | Each is a new template, no new module work. |

Recommended sequence for build sprints:

1. **Sprint T1** — Stakeholder Register UI (prerequisite). 1–2 weeks.
2. **Sprint T2** — Documents/CDE refactor: remove upload path, retain register and state machine. 1 week.
3. **Sprint T3** — Regional pack architecture foundation. 1–2 weeks.
4. **Sprint T4** — Template Module core (data model, CRUD, basic editor). 3–4 weeks.
5. **Sprint T5** — Lifecycle Engine (Layer 7). 1–2 weeks.
6. **Sprint T6** — Governance flow (Layer 4 — review, approve, narrative comments). 1–2 weeks.
7. **Sprint T7** — Renderer (Layer 5). 2 weeks.
8. **Sprint T8** — SOC template (first artefact in UK pack). 1 week.
9. **Sprint T9** — Charter template + validation that templates are generic. 1 week.

Approximate total: **12–17 weeks of focused build**. This is consistent with the "months" framing from earlier in the session.

This sequencing assumes 1.0 FTE focus. Realistic given memory's note that Schedule Optimisation Platform is the *lead* commercial product, Eduard is solo, and contracting work continues — actual elapsed calendar time may be longer.

---

## 7. Strategic Risk Register

Items flagged during the session that warrant explicit acknowledgement.

| Risk | Description |
|---|---|
| Schedule Optimisation deprioritised | Building the Template Module makes Genera PM the lead product by consumed-time, even if Schedule Optimisation remains the *stated* lead. Strategic decision should be made consciously, not by drift. |
| Existing Genera PM data invalidated by upload-path removal | Today's tested document on Smoke Test 5 is created via the upload path. New model retires this path. Decision needed: migration, deletion, or accept-as-legacy. Recommend accept-as-legacy. |
| Template Module size underestimated | The 12–17 week estimate assumes the design holds. Real-world template authoring (each artefact's fields, validation, pre-fill rules) is likely an additional cost not captured here. |
| Lifecycle Engine complexity | Step 2's "only the next template is selectable" is a much bigger design space than initially recognised. Layer 7 deserves its own design session, not a sub-bullet of Layer 4. |
| Regional pack discipline | Path B's value is only realised if every new module respects regional separation from day one. Discipline drift across multi-month build is real risk. |

---

## 8. Loose Ends from Prior Sessions

Carried forward, not addressed in this session:

| Ref | Description | Severity | Status |
|---|---|---|---|
| BUG-009 | AntiforgeryValidationException not handled gracefully on auth state change | Medium | Open |
| BUG-010 | Circuit termination has no user-facing recovery | Low | Open |
| BUG-011 (revised) | Audit trail double-write inconsistent across entities | Low | Open |
| BUG-013 | CSP blocks Google Fonts stylesheet load | Low | Open |
| BUG-014 | Browser Link blocked by dev CSP | Low | Open |
| BUG-016 | SuperAdmin cross-tenant project invisible in own list | Medium | Open |
| BUG-020 | EmptyLayout.razor has same MudPopoverProvider gap MainLayout had | Medium | Open — proactive fix recommended |
| OBS-003 | Dashboard "Active Projects" count vs list mismatch | Observation | Open |
| OBS-004 | Document number preview shows pre-sanitised project code | Observation | Open (largely moot post-BUG-017) |
| OBS-005 | Post-create navigation goes straight to Register Document, not back to /projects | Observation | Open |
| TKT-008 | Decision needed: dev/test seeding infrastructure | Medium | Open |
| TKT-009 | Dev DB cleanup: leftover smoke/test rows | Low | Open |
| TKT-010 | Decision needed: Schedule module ownership (Genera PM vs Schedule Optimisation Platform vs shared core) | Medium | Open — strategic |

Resolved this session:
- BUG-006 (closed Friday)
- OBS-002 (closed Friday)
- BUG-012, BUG-017, BUG-018, BUG-019 (verified end-to-end, merged to master @ 9ca9d63)

---

## 9. Closed Decisions Reference

A flat list, for quick lookup. All locked unless explicitly reopened.

1. PMBOK 8 Predictive
2. Full UK overlay (Green Book, IPA Gateway, NEC4, ProCure23, CDM, BSA, ISO 19650, RIBA, Soft Landings)
3. PM Consultancy perspective
4. Acacia Ward Upgrade as the fictional scenario
5. Path B region-pluggable architecture
6. Single unified `Document` entity (Option A1 — Template Instance and Document are one thing)
7. Internal `DocumentRevision` history per Document
8. Template-only document creation (no upload escape hatch)
9. Sequenced template availability (Lifecycle/Dependency Engine, Layer 7)
10. Per-Type sequential ISO 19650 numbering
11. Per-revision-checkpoint save strategy
12. User picks reviewer from project Stakeholder Register at submission
13. Per-revision narrative comment, no file attachments to comments
14. Template Module extends Documents/CDE, not parallels it
15. Stakeholder Register UI is a hard prerequisite
16. Regional packs: UK first, US and AU later
17. Templates carry Uniclass 2015 PM-table and FI-table codes applied to instances at creation

---

## 10. Next Session Brief

When the next session opens, the starting state is:

- This document has been read.
- Layer 1 is considered settled.
- Layer 2 (Field Model) is the immediate next design work.

The next session should:

1. Confirm Layer 1 holds (5 minutes — re-read, challenge if needed).
2. Design Layer 2 (Field Model) — the question is "what is a TemplateField in detail," including how field types support both display and downstream analysis.
3. Time permitting: Layer 3 (Pre-fill Engine).

What the next session should NOT do:

- Start building. The full Layer 1–7 design must be complete before Claude Code is briefed for sprint T4.
- Reopen settled decisions unless a clear new piece of information justifies it.
- Drift into smoke testing or unrelated bug-fixing. Bug log is carried forward; address in dedicated sprints.

Out-of-band, in parallel with design work:

- Build Stakeholder Register UI (Sprint T1). Independent of Template Module design progression. Can start immediately if desired.
- Consider scheduling a strategic review of Schedule Optimisation Platform vs Genera PM priority (TKT-010-adjacent). Not a design question; a portfolio question.

---

## Appendix A — Session timeline

| Time | Event |
|---|---|
| Friday 2026-05-08 (prior session) | Smoke test of project creation. BUG-006 found and fixed via SQL UPDATE. Five other findings logged. |
| 2026-05-12 evening — opening | Continue smoke test. Find BUG-012 (MudPopoverProvider regression from MudBlazor 6→9 upgrade). |
| 2026-05-12 evening — Step 19 | Document Register form encountered. Pre-submit bugs identified (BUG-017, 018, 019). Fix branch created. |
| 2026-05-12 evening — verification loop | DetailedErrors enabled. Multiple merge cycles. Five fixes verified end-to-end on laptop. |
| 2026-05-12 evening — merge to master | `master @ 9ca9d63`, 982/982 tests passing. Feature branches deleted. |
| 2026-05-12 evening — reframe | "I want to create a whole project according to PMBOK." Shift from smoke-testing to platform building. |
| 2026-05-12 evening — five planning decisions | Predictive / Full UK / PM Consultancy / Path B / Template-only document creation. |
| 2026-05-12 evening — Layer 1 design | Domain model designed, walkthrough validates it, six workflow decisions made. |
| 2026-05-12 night — consolidation | This document. |

---

*End of design document v1.0.*
