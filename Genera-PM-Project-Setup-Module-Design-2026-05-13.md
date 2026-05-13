# Genera PM — Project Setup Module — Design Document

**Session:** 2026-05-13 (consolidation of today's session)
**Status:** Draft v1.0 — Layer 1 complete, Layer 2 partial (Stakeholder + GovernanceConfig + RiskProfile + AuthorityStatement specified; TailoringDecisions, LifecycleDefinition, EnvironmentalFactors, InformationRequirements deferred)
**Author:** Eduard Szigeti, with Claude
**Repo state at writing:** master @ 96f9061; rename PR #85 in draft pending laptop verification
**Successor to:** Genera-PM-Template-Module-Design-2026-05-12.md (Template Module deferred — Project Setup is the foundation it depends on)

---

## 1. Executive Summary

This document captures the design decisions made in the 2026-05-13 session for the **Project Setup module** — established this session as the foundational module of Genera PM. Project Setup must be built and locked down before any document can be created, because every document template pre-fills from project-level configuration data.

The session reframed the build sequence settled the day before. Yesterday's Template Module design doc placed templates at the centre. Today's session recognised that the Project entity, as it currently exists in the codebase (Name, Code, Location, Sector, Budget, Client Org), is *too shallow* to drive meaningful template pre-fill. Project Setup is the prerequisite that makes the Template Module useful.

This is also the session where the product was renamed from **Genera CIMS** to **Genera PM**, and the codebase from `CimsApp` to `GeneraPm`. PR #85 captures the codebase rebrand.

### What is locked in

- **Methodology:** Build one module, test it, lock it down. Project Setup first; Template Module after.
- **Module scope:** Governance, Environmental Factors, Risk Appetite, Tailoring, Lifecycle, Information Requirements — plus Stakeholder Register and Authority Statements as cross-cutting capabilities that emerged during design.
- **Architecture:** Model B — `Project` stays lean, `ProjectSetup` is a new aggregate root with six sub-entities, four-state status machine (NotStarted / InProgress / Complete / Locked).
- **Stakeholder Register:** Pattern 3 unified entity (organisation + role + person triple per row). UI lives inside Project Setup for now, graduates to standalone module later. Data model is already standalone-shaped.
- **GovernanceConfig:** Full RACI for decision authority. Singular Accountable, plural Responsible, plural Consulted, plural Informed.
- **RiskProfile:** Strategic context only at Setup stage (not detailed Risk Register). IAPC discipline — Identify, Analyse, Plan, Check. Scored on the same matrix the future Risk Register will use.
- **AuthorityStatement:** Structured authority data per Stakeholder, enforced at three layers (assignment, approval click, audit time). System refuses to route documents to people without sufficient authority. Becomes the first template the UK pack ships.
- **Cross-cutting patterns:** Regional pack base set + project-specific extensions; RACI as universal assignment mechanism; AuthorityStatement gates every responsibility assignment; shared RiskScoring infrastructure used by both RiskProfile and future Risk Register.

### What is deferred

- Layer 2 sub-entities not yet specified: TailoringDecisions, LifecycleDefinition, EnvironmentalFactors, InformationRequirements
- Layer 3: UX flow (wizard vs panel; revisiting Setup; partial-completion behaviour)
- Layer 4: Setup-complete validation rules and Setup-Lock transition gating
- Layer 5: Regional pluggability mechanics (UK pack ships first; mechanics designed when second region is built)
- Setup Change Request workflow (post-Lock changes — designed conceptually, not specified)
- AuthorityStatement template render (PDF generation)
- The standalone Stakeholder Register extraction (Sprint where it graduates out of Project Setup)

### What this commits the platform to

Genera PM is no longer "a CDE with templates." It is a **structured-data governance platform** with:
- Project-level configuration that gates all subsequent work (Setup-Lock)
- Stakeholder records that aren't just contacts — they are bearers of formally enforced authority
- Documents created only from templates, with reviewer routing gated by authority
- An audit trail at every state transition

This is meaningfully more sophisticated than Genera CIMS was positioned as. The rename from CIMS to PM reflects this — the product is project management with governance discipline, not information management with project metadata.

---

## 2. Strategic Framing

### 2.1 Why Project Setup is the first module

The Template Module design doc from 2026-05-12 placed the Template Module at the architectural centre. Today's session re-examined that and found a sequencing problem: **templates pre-fill from project data, and the Project entity is currently too thin to support meaningful pre-fill.**

The shift: Project Setup is built first, locked down, then the Template Module is built on top of the now-rich Project entity.

This matches the user's stated methodology: *"Build one module, test it, lock it down. The first code we will write will be the project setup properly. The governance, environmental factors, risk appetite, and all the other bits that are prerequisite to any other document."*

### 2.2 The Acacia Ward scenario

Carried forward unchanged from the 2026-05-12 design doc. £4.8M NHS healthcare fit-out, North Surrey NHS Foundation Trust, two-stage NEC4 Option C via ProCure23, PM Consultancy perspective, 18-month programme, RIBA Stages 1–7. BSA-aware, HRB not triggered for this scope.

Used in this session to walk through Stakeholder, Governance, and RiskProfile entities and surface design holes.

### 2.3 The product rebrand

Genera CIMS → Genera PM (public name). CimsApp → GeneraPm (codebase). Carried out today via PR #85 (draft state pending laptop verification).

Brand architecture stays: Genera Systems parent, three products (Genera QA & HS&E, Genera PM, Genera Schedule). Public website rebrand deferred to Friday when laptop access permits (website source not in the GitHub repo).

The rename reflects what the product actually does. "CIMS / Construction Information Management System" centred on documents. "PM / Project Management" centres on the work, with documents as one output.

### 2.4 The philosophical breakthrough — refined

Yesterday's "do it once, do it right" principle still holds. Today added a corollary: **structured data enforced by software, not policed by spreadsheet**. This phrasing comes from genera-systems.com — the website framing aligns with the product philosophy.

The Authority Statement design embodies this. A traditional PM tool documents authority in a Word file no-one consults. Genera PM enforces it: the system refuses to route a document to a person whose Authority Statement doesn't cover the document type at the threshold. Documentation becomes operational, not ceremonial.

---

## 3. Decisions Locked This Session

### 3.1 Methodology

**"Build one module, test it, lock it down. Move to the next."**

Three implications:
- Sprint scope deliberately narrow — Stakeholder Register alone for Sprint 1, not the whole Project Setup module
- Locked modules stay locked — no retrofitting "while we're in there" changes
- Each sprint produces a complete, tested, demoable unit of value

### 3.2 Module architecture — Model B (separate ProjectSetup aggregate)

Confirmed over Model A (fat Project entity). Project stays lean — Id, Code, Name, AppointingPartyId, IsActive, CreatedAt, plus a new nullable `SetupId` FK pointing at the ProjectSetup aggregate.

ProjectSetup is the new aggregate root with one-to-one relationship to Project. Internally owns six sub-entities, each independently auditable.

Reasoning: lifecycle separation (Project exists from creation; Setup is a process that happens to it), audit and versioning, optional installation if a tenant wants Project Setup as a feature.

### 3.3 Setup status machine — four states with formal Lock

Confirmed over simpler 2-state and 3-state models.

States: **NotStarted → InProgress → Complete → Locked**

- NotStarted: Setup hasn't begun. No documents can be created.
- InProgress: Setup is being filled. Some sub-entities complete, others not. Still no documents.
- Complete: All required sub-entity sections filled. "Setup complete" review can be triggered.
- Locked: User has explicitly approved completion. Setup data is immutable subject to formal change control. Documents can now be created.

The Locked state unblocks the rest of the platform. Setup Change Request workflow handles post-Lock changes (data model accommodates it; workflow itself is later sprint scope).

### 3.4 Stakeholder Register — Pattern 3 unified entity, option 4 sequencing

Pattern 3: each Stakeholder row is an organisation + role + (optionally) person triple. Confirmed over Pattern 1 (person only) and Pattern 2 (polymorphic with separate Role entity).

Real-world examples:
- "Jane Smith, SRO at North Surrey NHS FT" = one row: org=Trust, role=SRO, person=Jane
- "The Trust as organisational client" = handled at Project.AppointingPartyId, not in Stakeholder (per Hole #1 resolution from walk-through)
- "Surrey Heath Borough Council as Building Control" = one row: org=SHBC, role=BuildingControl, person=null (or named officer when known)
- "Jane leaves, Bob takes over SRO mid-project" = Jane's row deactivated with EffectiveTo set, new row created for Bob; history preserved

Option 4 sequencing: Stakeholder is designed as a clean standalone entity from the start. UI placement is inside Project Setup for the first build. Future extraction to standalone module is pure URL routing change — data model is already standalone-shaped.

Same person holding two roles = two rows. UI mitigates duplication via Person autocomplete.

### 3.5 GovernanceConfig — RACI for decision authority

Confirmed over simpler matrix (Approach A) and hybrid (Approach C).

RACI structure: Responsible (plural), Accountable (singular — RACI orthodoxy), Consulted (plural), Informed (plural). Each project decision type gets a RACI row. The schema enforces singular Accountable.

GovernanceConfig top-level structure: key role FKs to Stakeholders (SRO, Sponsor, ProjectManager, PMConsultancy — all nullable, populated during InProgress, required for Lock), Project Board membership collection with voting flags, Decision Authority RACI collection, Meeting Cadence collection, Reporting Obligation collection.

### 3.6 RiskProfile — strategic context only at Setup stage

Confirmed: this is not the detailed Risk Register. At Setup stage we are *before* the project has artefacts, designs, contracts. We declare the strategic risk landscape — economic environment, legislative environment, client relationship, market conditions, organisational maturity, etc. Detailed line-item risks live in the future Risk Register module.

Critical correction made mid-session: my first draft over-engineered RiskProfile with 5×5 likelihood/impact matrices and quantitative tolerance bands. User correctly pulled it back to high-level only.

Structure now:
- **Overall appetite** (enum: Averse / Cautious / Open / Hungry) with narrative rationale
- **Strategic context factors** — each with IAPC discipline:
  - **Identify:** Category (regional-pack-extensible), Posture (Favourable/Neutral/Challenging/Hostile), Statement, Implications
  - **Analyse:** Likelihood × Impact on the shared 5×5 RiskScoring matrix → ComputedRating
  - **Plan:** ResponseStrategy (Accept/Monitor/Mitigate/Transfer/Avoid), MitigationActions with owner and TargetDate OR TargetTrigger
  - **Check:** ReviewFrequency, ReviewOwner, NextReviewDate OR NextReviewTrigger
- **Tolerance statements** — qualitative narrative per dimension (PatientSafety, RegulatoryCompliance, Cost, Schedule, etc.). Regional-pack-extensible.

### 3.7 AuthorityStatement — structured, enforced, gating

The most consequential design decision of the session. Originated when user said: *"When a document is assigned to a person for approval, that person has to have attached a roles and responsibilities document that shows what he or she can and cannot do."*

User confirmation when challenged: *"It has to be enforced. If they don't have the credentials, they can't approve."*

Structure: AuthorityStatement is a structured-fields entity attached 1:1 to Stakeholder (per project), capturing:
- **Financial authority** — limit value, currency, scope
- **Document approval scope** — collection of ApprovalScope rows (artefact class + max threshold + conditions)
- **Instruction authority** — collection of InstructionAuthority rows (NEC4 clause references, etc.)
- **Signatory rights** — collection of SignatoryRight rows
- **Exclusions and escalations** — what the person cannot do and where to escalate
- **Competency declarations** — collection of CompetencyDeclaration rows (CDM duty-holder, AE Water HTM 04-01, ChPP, etc.)
- **Formal appointment** — appointed by, appointment date, appointment reference, review date
- **Audit trail** — IssuedRevisionId linking to the signed PDF document generated

Enforcement at three layers:

1. **At routing point.** When user tries to assign a Stakeholder as approver of a document, system checks the Stakeholder's Authority Statement for scope coverage. If insufficient, assignment is *refused* — not warned. Dropdown either excludes the person or shows them with red strikethrough and "ineligible — see why" expander.

2. **At approval click.** When the assigned approver clicks Approve, system re-checks authority at that moment. Document value may have changed since assignment; approver's authority may have lapsed. Belt and braces.

3. **At audit time.** Every authority-gated action logged with the Authority Statement version used to permit it. Audit shows the exact authority state at the moment of approval, even if authority is later changed.

The Authority Statement is itself a document: ISO 19650 number, Uniclass codes, state machine, signed PDF render, audit trail. It becomes the **first template in the UK pack** (before SOC, before Charter), because every Stakeholder needs one before any document can be assigned to them.

### 3.8 DocumentResponsibility — RACI per document revision

Discussed and specified though not built yet. Each Document has a collection of DocumentResponsibility rows (R/A/C/I assignments to Stakeholders). Defaults populated from the parent Template's RACI by RoleCode resolution at document creation. User can override per-instance.

Per-revision scope: each DocumentRevision has its own DocumentResponsibility set. Copied forward from prior revision by default. Freezes when revision reaches Published state.

This is the mechanism the Template Module will use for reviewer selection (decided in the 2026-05-12 session). The dropdown of available reviewers will be filtered to participants whose Authority Statement covers the document type at the threshold.

---

## 4. Layer 1 — Domain Model (complete)

### 4.1 Core structure

```
Project (existing — keep lean)
├── Id, Code, Name, AppointingPartyId
├── IsActive, CreatedAt, CreatedBy
└── SetupId (FK ProjectSetup, nullable — null until Setup initiated)
```

```
ProjectSetup (new aggregate root)
├── Id, ProjectId (FK Project, unique — 1:1)
├── Status (enum: NotStarted, InProgress, Complete, Locked)
├── CompletedAt, CompletedBy (nullable)
├── LockedAt, LockedBy (nullable)
├── Governance (FK GovernanceConfig)
├── RiskProfile (FK RiskProfile)
├── EnvironmentalFactors (FK EnvironmentalFactors)
├── Tailoring (FK TailoringDecisions)
├── LifecycleDefinition (FK LifecycleDefinition)
├── InformationRequirements (FK InformationRequirements)
```

```
Stakeholder (project-scoped standalone entity)
├── Id, ProjectId (FK Project)
├── UserId (FK User, nullable — populated when Stakeholder is also a CIMS user)
├── StakeholderType (Internal_Person | External_Person | Internal_Organisation | External_Organisation | RegulatoryAuthority)
├── OrganisationName, RoleCode, RoleName
├── PersonName, PersonJobTitle, PersonEmailAddress, PersonPhoneNumber (nullable)
├── Address fields (nullable)
├── AppointmentBasis (text)
├── EffectiveFrom, EffectiveTo (nullable)
├── Notes (500 char, non-analyzable)
├── IsActive (soft delete)
├── AuthorityStatementId (FK AuthorityStatement, nullable — to be populated in Sprint 2)
├── audit fields
```

```
GovernanceConfig
├── Id, ProjectSetupId (FK)
├── Key role FKs: SRO, Sponsor, ProjectManager, PMConsultancy (all → Stakeholder, nullable)
├── BoardChairStakeholderId, BoardQuorumThreshold
├── BoardMembers (collection of ProjectBoardMember)
├── DecisionAuthorities (collection of DecisionAuthority — full RACI)
├── MeetingCadences (collection)
├── ReportingObligations (collection)
```

```
RiskProfile
├── Id, ProjectSetupId (FK)
├── OverallAppetite (enum), AppetiteRationale
├── ContextFactors (collection of StrategicRiskContext with IAPC structure)
├── ToleranceStatements (collection — qualitative, regional-pack-extensible)
```

```
AuthorityStatement (one per Stakeholder per Project)
├── Id, StakeholderId (FK, unique), ProjectId (FK)
├── Status (enum: Draft, Issued, Active, Superseded, Revoked)
├── Financial authority fields
├── ApprovalScopes (collection)
├── InstructionAuthorities (collection)
├── SignatoryRights (collection)
├── ExclusionsAndEscalations (collection)
├── CompetencyDeclarations (collection)
├── AppointedBy (FK Stakeholder), AppointmentDate, AppointmentReference, ReviewDate
├── IssuedRevisionId (FK DocumentRevision — the signed PDF)
```

```
RiskScoring (shared infrastructure used by RiskProfile and future Risk Register)
├── Likelihood scale (VeryLow / Low / Medium / High / VeryHigh — configurable per regional pack)
├── Impact scale (Negligible / Minor / Moderate / Major / Severe)
├── Rating matrix (Low / Medium / High / Extreme)
├── RequiredAction mapping (Accept / Monitor / MitigatePlanned / MitigateUrgent / Escalate)
```

### 4.2 Sub-entities not yet specified

Four remaining sub-entities of ProjectSetup are still placeholder shapes from yesterday's design and need full Layer 2 work in a future session:

- **TailoringDecisions** — development approach (defaulted to Predictive), performance domain emphasis per PMBOK 8, artefacts in/out of scope with rationale, narrative tailoring justification
- **LifecycleDefinition** — phases, gates, milestones at project-config level (not the schedule)
- **EnvironmentalFactors** — Internal EEFs, External EEFs, OPAs, ApplicableStandards (HBN/HTM references), ApplicableRegulations (CDM 2015, BSA 2022)
- **InformationRequirements** — ISO 19650-2 specific: PIR, AIR, EIR at project-config level

These are scoped (yesterday's decisions stand) but not field-level designed. Estimated 1.5–2 hours of focused design work to complete.

### 4.3 Cross-cutting capabilities

Four patterns established this session that apply across the platform, not just to Project Setup:

**Regional pack base set + project-specific extensions.** Used in: tolerance dimensions (base nine for UK NHS + project extensions like ParticulateContamination for cleanrooms), risk context categories, role codes, decision types, instruction authority types, competency types. Pattern: regional pack defines a default catalogue; per-project rows can extend.

**RACI as universal assignment mechanism.** Used in: Decision Authority (project-level), DocumentResponsibility (per-document), eventually task assignment in Actions module, eventually risk ownership in Risk Register. Singular Accountable always. Plural R/C/I.

**AuthorityStatement gates every responsibility assignment.** Used at: every approval routing, every signature collection, every escalation point. Three-layer enforcement: route, click, audit.

**Shared RiskScoring infrastructure.** Used by: RiskProfile (strategic context scoring), future Risk Register (line-item scoring), eventually any module that needs to qualify uncertainty. Same scales, same matrix, same rating computation across the platform.

---

## 5. Real-World Walk-Through — Findings

Mid-session, user requested a real-world scenario test against Stakeholder, Governance, and RiskProfile. Eduard-as-PM-Consultancy walking through Acacia Ward Project Setup, sub-entity by sub-entity. Eight design holes surfaced. All resolved.

| # | Hole | Resolution |
|---|------|------------|
| 1 | "Trust as ClientOrganisation Stakeholder" awkward — Trust is already at Project.AppointingPartyId | Project.AppointingPartyId is sole client representation. Stakeholders are project participants only. |
| 2 | Stakeholder ↔ User relationship missing — needed for Template Module reviewer routing | Added `Stakeholder.UserId` (FK User, nullable). Populated when Stakeholder is also a CIMS user. |
| 3 | Same person, two roles = two rows = UX duplication | Data model unchanged (two rows is correct). UI layer adds Person autocomplete to mitigate data entry tedium. |
| 4 | Stakeholder Register grows over project lifecycle — can't require completeness for Setup Lock | Setup completion requires a minimum role set (SRO, Sponsor, PM, PM Consultancy lead), not full register. Designers/contractors added post-appointment. |
| 5 | Governance role FKs vs Stakeholder RoleCode must align | Both use same regional-pack enum. UI distinguishes RoleCode (project role) from PersonJobTitle (employer job title). |
| 6 | Decision Authority FK to Stakeholder — but conceptually role-based assignment wanted | Pattern 3 supports this — FK points to current role-holder; role transitions deactivate old row and create new row, references can be repointed. UI shows "current role-holders" view. |
| 7 | `NextReviewDate` (date) doesn't accommodate event-based triggers ("At Gateway 0") | Added `NextReviewTrigger` (text, nullable). Validation: at least one of date or trigger must be populated. |
| 8 | `MitigationAction.TargetDate` same problem | Same fix: added `TargetTrigger` (text, nullable). |

Of these:
- 1, 4 were *real* design decisions
- 2, 7, 8 were *additive field-level* fixes
- 3, 5, 6 were *UI-layer concerns* flagged for Layer 3

Conclusion from the walk-through: the model is 80%+ right out of pure modelling, with the remaining 20% surfaced via real scenarios. **Walk-throughs are doing their job.** The remaining four sub-entities (Tailoring, Lifecycle, Environmental, Information Requirements) should be walked through the same way before they're considered build-ready.

---

## 6. Sprint 1 — Stakeholder Register Foundation

### 6.1 Scope

Build a working Stakeholder Register inside Project Setup. Pattern 3 unified entity. Full CRUD. Audit trail. UK role codes hardcoded for Sprint 1 (regional pack mechanics come later). No AuthorityStatement integration yet (Sprint 2). No GovernanceConfig coupling yet. No Setup-Lock validation yet.

The deliverable is locked-down Stakeholder data infrastructure that subsequent sprints build on top of.

### 6.2 Entity (final)

Per section 4.1 above. Full field list locked.

### 6.3 RoleCode enum (Sprint 1 — hardcoded UK set)

~35 codes covering: Internal Trust roles (SRO, Sponsor, ProjectManager, CapitalProjectsManager, BIMManager, InformationManager, AuthorisingEngineer_Water, AuthorisingEngineer_Ventilation, AuthorisingEngineer_Decontamination, InfectionControlLead, ClinicalDirector, EstatesDirector, ChiefOperatingOfficer, ChiefExecutive, DirectorOfNursing, FinanceDirector), PM Consultancy / external professional roles (PMConsultancy, PrincipalDesigner_CDM, PrincipalContractor_CDM, Architect, MEEngineer, StructuralEngineer, HealthcarePlanner, CostManager, BuildingSurveyor, Designer_CDM), Regulatory / authority roles (BuildingControl, PlanningAuthority, CQC, HSE, EnvironmentAgency, LocalAuthority_HazardousSubstances, FireAuthority, UtilitiesAuthority), Contractor roles for future use (MainContractor, MEContractor, SubContractor_Specialist, Commissioning_Specialist), Other (with note that PersonJobTitle should clarify).

### 6.4 Validation rules

- OrganisationName required, trimmed
- RoleCode required, from enum
- PersonName required if StakeholderType is `*_Person`; optional otherwise
- PersonEmailAddress format-validated if provided
- EffectiveFrom required, defaults to today
- EffectiveTo must be ≥ EffectiveFrom if set
- Notes max 500 char

Both server-side and client-side enforcement, using the live "Cannot save" panel pattern that BUG-019's fix established.

### 6.5 Endpoints

- `POST /api/v1/projects/{projectId}/stakeholders` — create
- `GET /api/v1/projects/{projectId}/stakeholders` — list (with filters: includeInactive, roleCode, search)
- `GET /api/v1/projects/{projectId}/stakeholders/{id}` — detail
- `PUT /api/v1/projects/{projectId}/stakeholders/{id}` — update
- `POST /api/v1/projects/{projectId}/stakeholders/{id}/deactivate` — soft delete
- `POST /api/v1/projects/{projectId}/stakeholders/{id}/reactivate` — undelete

No hard delete, ever.

### 6.6 UI

- Sidebar entry: new "PROJECT SETUP" section above "PROJECT", with "Stakeholders" as the only sub-item for Sprint 1 (others grey/hidden until built)
- List page at `/projects/{id}/setup/stakeholders` — table with tabs (Active / All / Inactive), search, RoleCode filter, row actions (edit, deactivate/reactivate)
- Create/edit via modal (popover provider now works post-BUG-012 fix) with sections: Identity, Contact, Appointment, CIMS user link, Notes
- Live preview chip and live "Cannot save" panel — patterns established in DocumentRegister

### 6.7 Audit events

- `stakeholder.created` (full row payload)
- `stakeholder.updated` (field-level diff)
- `stakeholder.deactivated`
- `stakeholder.reactivated`

### 6.8 Permissions (Sprint 1)

Open for Sprint 1 — any project member can CRUD. Audit captures who did what. Tightening waits until AuthorityStatement lands in Sprint 2. Logged as **TKT-011 — Tighten Stakeholder permissions when AuthorityStatement lands**.

### 6.9 Out of scope for Sprint 1

AuthorityStatement, GovernanceConfig coupling, Setup-Lock validation, regional pack mechanics, import/export, bulk operations, email notifications, organisation hierarchy, multi-language.

### 6.10 Sprint 1 build sequence

Two-phase, mirroring the rename pattern:

**Phase 1 — Read-only inspection** (Claude Code's first task):
- Read existing `StakeholdersService.cs:2118` and `StakeholdersController.cs:703`
- Read existing `Stakeholder` entity if present
- Read existing tests
- Report current state: does it match Pattern 3 or need refactor?

**Phase 2 — Refactor or extend** (decided after Phase 1 report):
- If existing matches: add UI + Pattern 3 enforcement
- If existing doesn't match: refactor with new migration; preserve existing tests where compatible

### 6.11 Acceptance criteria

11 criteria including: entity created with migrations; all CRUD endpoints implemented and authenticated; validation enforced both sides; list/create/edit/deactivate UI working; audit events firing; unit tests cover validation; integration tests cover endpoints; manual smoke test passes against Acacia Ward (5–10 stakeholders created, some edited, one deactivated, audit trail visible).

---

## 7. Build Sequencing — Revised

Yesterday's design doc proposed 12–17 weeks for Template Module + dependencies. Today's session reshapes the sequence because Project Setup is now first, not Template Module.

| Sprint | Deliverable | Estimated effort |
|--------|-------------|------------------|
| 1 | Stakeholder Register CRUD (this sprint) | 1 week |
| 2 | AuthorityStatement entity + structured fields capture | 1–2 weeks |
| 3 | GovernanceConfig | 1–2 weeks |
| 4 | RiskScoring shared infrastructure + RiskProfile | 1–2 weeks |
| 5 | TailoringDecisions, LifecycleDefinition, EnvironmentalFactors, InformationRequirements (one sprint? Maybe two) | 2 weeks |
| 6 | ProjectSetup status machine, Setup-Lock validation, Setup Change Request workflow scaffolding | 1 week |
| 7 | Project Setup UX wizard / panel-based flow integration | 1 week |
| 8 | Template Module skeleton — *generate Authority Statement PDF as first template* | 2 weeks |
| 9 | SOC template (Green Book Strategic Outline Case) as second template | 1 week |
| 10+ | Charter, OBC, FBC, register templates, plan templates, ongoing | Continuous |

Approximate total to "Project Setup complete + first two templates": **10–12 weeks of focused build**.

This is slower than yesterday's 12–17-week estimate for Template Module alone — because we've added Project Setup *before* Template Module. The total runway is roughly the same; the value sequencing is better. After Sprint 1 you have a working Stakeholder Register. After Sprint 4 you have a working risk-aware Project Setup. After Sprint 8 you have generated documents with enforced authority. Each sprint produces demonstrable value, rather than waiting 12 weeks for "the Template Module."

Assumes 1.0 FTE focus. Realistic if contracting work continues at current rate: actual calendar elapsed time may be longer than build weeks suggest.

---

## 8. Risks Worth Flagging

### 8.1 Scope of "Project Setup" was understated

We set out today to "design Project Setup." We added: RACI as cross-cutting pattern, AuthorityStatement with three-layer enforcement, shared RiskScoring infrastructure, regional pack patterns. Each addition was justified. Cumulatively they are significantly more than "Project Setup" — they are the foundation of Genera PM as a governance platform.

This isn't bad — it's accurate to what the product needs. But it does mean: **build effort for what we're calling "Project Setup" is closer to 6–7 sprints than 1–2**. The methodology ("build one module, test it, lock it down") is good. The label "Project Setup" understates what's inside.

### 8.2 Schedule Optimisation Platform deprioritised by drift

Memory says Schedule Optimisation Platform (Genera Schedule) is the *lead* commercial product. Today's session built design for Genera PM. The cumulative time spent on Genera PM design + build over the past two days is significant; equivalent time on Genera Schedule would advance it materially.

This is the same flag I raised in yesterday's design doc. Worth a deliberate strategic review separate from any design or build session: *which product is actually the lead, by time investment, not stated intent?*

### 8.3 Authority Statement enforcement complexity

Three-layer enforcement (route, click, audit) is described conceptually but not specified. Real implementation needs:
- Authority check service with caching (authority lookups happen frequently)
- Decision rules engine (combine ApprovalScope rows with threshold comparisons)
- UI affordances for "this person is ineligible — see why"
- Audit log schema enrichment to capture authority state at time of decision

None of this is hard, but it's at least a sprint's worth of work that's currently bundled inside "AuthorityStatement" in Sprint 2.

### 8.4 Regional pack mechanics deferred indefinitely

We've decided everything is regional-pack-extensible (role codes, decision types, instruction authorities, risk dimensions, tolerance dimensions, competency types). But the regional pack architecture itself is deferred until the second region is built. Sprint 1's RoleCode enum is hardcoded C#. So is everything else regional-pack-eligible.

This is fine *until* we want to ship a second region — at which point we have to refactor every hardcoded enum into a configuration-driven catalogue. That refactor is significant. Budgeting it is worth doing before US/AU work starts.

### 8.5 Walk-through method needs to be applied to remaining sub-entities

Eight design holes in three sub-entities walked through. Probably similar density in TailoringDecisions, LifecycleDefinition, EnvironmentalFactors, InformationRequirements. Walking through Acacia Ward against each will surface them. **Do not build these four sub-entities without walk-through first.** That's a 1-hour exercise per sub-entity at most.

---

## 9. Carry-Forward Register

Bugs and tickets from prior sessions, with today's additions.

### 9.1 Bugs

| Ref | Description | Status |
|---|---|---|
| BUG-009 | AntiforgeryValidationException not handled on auth state change | Open |
| BUG-010 | Circuit termination has no user-facing recovery | Open |
| BUG-011 (revised) | Audit trail double-write inconsistent across entities | Open |
| BUG-013 | CSP blocks Google Fonts | Open |
| BUG-014 | Browser Link blocked by dev CSP | Open |
| BUG-016 | SuperAdmin cross-tenant project invisible in own list | Open |
| BUG-020 | EmptyLayout.razor has same MudPopoverProvider gap MainLayout had | Open — proactive fix recommended |

Resolved (verified, merged via master @ 9ca9d63): BUG-012, BUG-017, BUG-018, BUG-019.

### 9.2 Observations

| Ref | Description | Status |
|---|---|---|
| OBS-003 | Dashboard "Active Projects" count vs list mismatch | Open |
| OBS-004 | Document number preview shows pre-sanitised project code | Open (largely moot post-BUG-017) |
| OBS-005 | Post-create navigation goes straight to Register Document, not back to /projects | Open |

### 9.3 Tickets

| Ref | Description | Status |
|---|---|---|
| TKT-008 | Dev/test seeding infrastructure decision | Open |
| TKT-009 | Dev DB cleanup: leftover smoke/test rows | Open (will be moot after Phase 4 of PR #85 if drop-and-recreate proceeds) |
| TKT-010 | Schedule module ownership: CIMS vs Schedule Optimisation Platform vs shared core | Open — strategic |
| TKT-011 | Tighten Stakeholder permissions when AuthorityStatement lands | Open — Sprint 2 dependency |
| TKT-012 (new) | Verify PR #85 (rename) locally and merge | Open — Friday |
| TKT-013 (new) | Public website rebrand: Genera CIMS → Genera PM on genera-systems.com | Open — Friday or later |
| TKT-014 (new) | Read genera-systems.com CSS, extract design tokens for Sprint 1 UI integration | Open — before Sprint 1 build starts |
| TKT-015 (new) | Walk-through method applied to TailoringDecisions, LifecycleDefinition, EnvironmentalFactors, InformationRequirements before they're built | Open — design pre-build |
| TKT-016 (new) | Authority Statement three-layer enforcement: specify in detail before Sprint 2 build | Open — Sprint 2 dependency |

---

## 10. Closed Decisions Reference

Flat list for quick lookup. All locked unless explicitly reopened.

1. Methodology: build one module, test it, lock it down
2. Project Setup is the first module built (before Template Module)
3. Module architecture: Model B (separate ProjectSetup aggregate, Project stays lean)
4. Setup status machine: four states with Locked + Setup Change Request workflow
5. Stakeholder Register: Pattern 3 unified entity (org + role + person triple per row)
6. Stakeholder sequencing: option 4 (clean standalone data model from start, UI inside Project Setup for v1)
7. Trust as client: represented at Project.AppointingPartyId, not as Stakeholder row (Hole #1 resolution)
8. Stakeholder.UserId added for CIMS-user linkage (Hole #2 resolution)
9. Same-person-two-roles = two rows + Person autocomplete UI mitigation (Hole #3)
10. Setup-Lock requires minimum role set, not full Stakeholder register (Hole #4)
11. RoleCode and Governance FKs use same regional-pack enum (Hole #5)
12. Decision Authority: full RACI (singular Accountable, plural R/C/I)
13. Pattern 3 supports role-based reassignment via row deactivation + new row (Hole #6)
14. RiskProfile: strategic context only at Setup stage (not detailed Risk Register)
15. RiskProfile follows IAPC structure: Identify, Analyse, Plan, Check
16. Risk scoring: full 5×5 Likelihood × Impact matrix on shared RiskScoring infrastructure
17. Tolerance statements: qualitative narrative, regional-pack-extensible
18. NextReviewDate has NextReviewTrigger alternative for event-based reviews (Hole #7)
19. MitigationAction.TargetDate has TargetTrigger alternative (Hole #8)
20. AuthorityStatement: structured data + signed PDF document, attached to Stakeholder
21. AuthorityStatement enforced at three layers: routing, approval click, audit
22. AuthorityStatement is the first template in the UK pack (before SOC, before Charter)
23. DocumentResponsibility per revision, defaults from template RACI, freezes on Publish
24. Cross-cutting pattern: regional pack base set + project-specific extensions
25. Cross-cutting pattern: RACI as universal assignment mechanism
26. Cross-cutting pattern: AuthorityStatement gates every responsibility assignment
27. Cross-cutting pattern: shared RiskScoring infrastructure used by RiskProfile and Risk Register
28. Sprint 1: Stakeholder Register CRUD only, AuthorityStatement deferred to Sprint 2
29. Sprint 1 permissions: open for any project member (TKT-011 tightens later)
30. Sprint 1 sidebar: new "PROJECT SETUP" section above existing "PROJECT" section
31. Sprint 1 build starts with read-only inspection of existing StakeholdersService before refactor-or-extend decision
32. Product rename: Genera CIMS → Genera PM (public); CimsApp → GeneraPm (code)
33. JWT claim types renamed: cims:role → genera:role; cims:org → genera:org
34. JWT Issuer/Audience: GeneraPm / GeneraPmClient
35. EF migrations: keep filenames and class names (history); update internals; rename DbContextModelSnapshot

---

## 11. Next Session Brief

The starting state for the next working session:

- This document has been read.
- Layer 1 is settled.
- Three of seven sub-entities have Layer 2 field-level design complete (Stakeholder, GovernanceConfig, RiskProfile + AuthorityStatement).
- Sprint 1 is fully scoped and ready to hand to Claude Code, conditional on PR #85 (rename) being verified and merged.
- Four sub-entities still need Layer 2 design (Tailoring, Lifecycle, Environmental, Information Requirements).
- Layers 3–5 are not started.

### Decision points awaiting Friday:

1. **Verify PR #85 locally.** `git checkout rename/cimsapp-to-generapm`, `dotnet build`, `dotnet test`, parity check against master. Mark PR ready for review when green.
2. **Phase 4 — drop and recreate dev DB.** After PR merged.
3. **Public website rebrand.** Genera CIMS → Genera PM on genera-systems.com.
4. **Read genera-systems.com CSS, extract design tokens.** Needed before Sprint 1 UI build.

### Next design work:

In any order, in future sessions:
- Layer 2 field-level design for TailoringDecisions
- Layer 2 field-level design for LifecycleDefinition
- Layer 2 field-level design for EnvironmentalFactors
- Layer 2 field-level design for InformationRequirements
- Walk-through against Acacia Ward for each of the above before considering build-ready
- Layer 3 — UX flow for Project Setup
- Layer 4 — Setup-complete validation and Setup-Lock gating rules
- Authority Statement three-layer enforcement detailed spec (Sprint 2 prerequisite)

### Strategic review (separate from build/design):

- Schedule Optimisation Platform vs Genera PM priority. Time investment vs stated intent.

---

## Appendix — Session Timeline (2026-05-13)

| Time | Event |
|---|---|
| Morning, opening | Continuing from prior session. "Build one module, test it, lock it down" articulated as methodology. Project Setup proposed as first. |
| Morning | Three-question modal: confirm direction (Yes, full Project Setup first), when to design (tonight in original framing — actually all morning), design doc update (Leave as-is, add Project Setup as separate doc). |
| Morning — Layer 0 | Module scope confirmed: six sub-entities in, Risk Register and Stakeholder Register (initially) and Schedule baseline out. |
| Morning — Layer 1 | Model B (separate aggregate) confirmed. Four-state status machine confirmed. Stakeholder option 4 (clean standalone, UI inside Project Setup) confirmed. |
| Morning — Layer 2.1 Stakeholder | Pattern 3 confirmed (unified org+role+person triple). Field list locked. Notes field 500 char acceptable. |
| Morning — Layer 2.2 GovernanceConfig | Full RACI confirmed over simpler matrix. Structure locked. |
| Late morning — Layer 2.3 RiskProfile | First draft over-engineered. User correction: high-level only at Setup stage. Major redesign. IAPC structure (Identify, Analyse, Plan, Check) confirmed. Shared RiskScoring infrastructure surfaced. Tolerance dimensions regional-pack-extensible. |
| Late morning — Real-world walk-through | Acacia Ward scenario applied to Stakeholder, Governance, RiskProfile. Eight design holes found and resolved. |
| Late morning — AuthorityStatement insight | User: "responsibility sheet for each Stakeholder when assigned to a document." Refined to: structured authority data + signed PDF document, enforced at three layers. Becomes first UK pack template. |
| Late morning — Sprint 1 planning | Stakeholder Register CRUD scoped. Two open forks identified (existing service inspection, sidebar placement). |
| Late morning — Product rename | User: "I don't like CIMS." Website inspection revealed Genera CIMS is the established public brand. User decision: rename Genera CIMS to Genera PM (public + code). |
| Midday — Rename execution | Phase 1 inventory, Phase 2 rename map with three open questions (JWT claims, Issuer/Audience, migration internals), all resolved. Phase 3 applied. Phase 5 pushed as draft PR #85. Phase 4 (DB) deferred to laptop. |
| Afternoon — Sprint 1 forks resolved | Inspection-first methodology for existing service. Sidebar placement option A (new section above PROJECT). |
| Afternoon — This document written | Consolidation. |

---

*End of design document v1.0.*
