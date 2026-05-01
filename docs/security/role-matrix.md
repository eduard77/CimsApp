# Endpoint role matrix

**Scope:** every HTTP endpoint in `CimsApp/Controllers/` as of 2026-04-24
(post-T-S0-08).
**Authorization model:** ADR-0010 two-tier (global + per-project).
**Role hierarchy** (low → high):
`Viewer < ClientRep < TaskTeamMember < InformationManager <
ProjectManager < OrgAdmin < SuperAdmin`.

## Legend

- **Global role** gates are `[Authorize(Roles = "...")]` attributes;
  the JWT's `cims:role` claim is read via ASP.NET role mapping.
- **Project role** gates use `HasMinimumRole(await
  GetProjectRoleAsync(db, projectId), UserRole.X)` inside the action
  body. The role is the caller's `ProjectMember.Role` on that specific
  project; any non-member is rejected with `ForbiddenException`
  regardless of global role.
- **Membership only** means `GetProjectRoleAsync` is called (which
  throws if the caller is not a project member) but no minimum role
  is required beyond membership.
- "—" in the Global or Project column means no gate of that tier
  applies at the endpoint level; deeper service-layer checks may still
  exist (noted in Comment).

## Auth

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| POST | `/api/v1/auth/register` | anonymous + invitation token | — | Sign-up. T-S0-11 closed SR-S0-01. Rate-limited to **10 / min per IP** (`anon-default` policy, B-002). |
| POST | `/api/v1/auth/login` | anonymous | — | Issues JWT. Rate-limited to **5 / min per IP** (`anon-login` policy, B-002). Plus progressive back-off: **5 consecutive failures from one IP within a 15-min sliding window → 429 LOGIN_LOCKOUT** (B-002 close, `LoginAttemptTracker`). Reset on successful login. |
| POST | `/api/v1/auth/refresh` | anonymous | — | Refresh-token-bearer auth. Rate-limited to **10 / min per IP** (`anon-default` policy, B-002). |
| POST | `/api/v1/auth/logout` | anonymous | — | Revokes the supplied refresh token. Rate-limited to **10 / min per IP** (`anon-default` policy, B-002 follow-on) — the endpoint does a DB lookup per call and was missing the rate limit applied to the other anonymous endpoints. |
| GET  | `/api/v1/auth/me` | authenticated | — | Profile self-read |
| POST | `/api/v1/auth/logout-everywhere` | authenticated | — | B-001 / ADR-0014. Self-service. Bumps caller's `TokenInvalidationCutoff`; all access tokens — including the one in this request — are rejected at the next authenticated request. |

## Users (admin)

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| POST | `/api/v1/users/{userId}/revoke-tokens` | `OrgAdmin`, `SuperAdmin` | — | B-001 / ADR-0014. Bumps target user's `TokenInvalidationCutoff`. OrgAdmin can target users in their own org only (tenant query filter); SuperAdmin can target any user (`IsSuperAdmin` branch in service uses `IgnoreQueryFilters` per ADR-0007). Cross-org attempts return 404 (existence not leaked). |
| POST | `/api/v1/users/{userId}/deactivate` | `OrgAdmin`, `SuperAdmin` | — | B-001 / ADR-0014. Atomically sets `IsActive = false` AND bumps `TokenInvalidationCutoff`. The IsActive flip kills every active session via the `TokenRevocation.IsRevoked` IsActive short-circuit; the cutoff is belt-and-braces. Same tenant-scoping as revoke-tokens. |

## Organisations

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/organisations` | authenticated | — | B-007. **Non-SuperAdmin callers see only their own organisation** (controller-level scoping; ADR-0003 leaves Organisation intentionally unfiltered at the DbContext level since it's the tenant anchor used during pre-auth flows). SuperAdmin sees every active organisation per ADR-0007. |
| POST | `/api/v1/organisations` | anonymous | — | Sign-up flow creates an org **and** mints a 24h bootstrap invitation token in the response. ADR-0011. Rate-limited to **10 / min per IP** (`anon-default` policy, B-002). |
| POST | `/api/v1/organisations/{orgId}/invitations` | `OrgAdmin`, `SuperAdmin` | — | Mint a 7-day invitation token (max 30) for a future user. OrgAdmin can only mint for their own organisation; SuperAdmin can mint for any (mirrors ADR-0012). Body: `{ email?, expiresInDays? }`. ADR-0011, commit `3839468`. |

## Projects

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects` | authenticated | — | Service filters to caller's memberships |
| POST | `/api/v1/projects` | `OrgAdmin`, `SuperAdmin` | — | Admin-only (T-S0-08, commit `37013fc`). `AppointingPartyId` locked to caller's organisation; `SuperAdmin` may create under any org (audited with `project.created.superadmin_bypass`) — see ADR-0012 and commit `c83a8a9`. |
| GET  | `/api/v1/projects/{projectId}` | authenticated | — | Service enforces membership |
| POST | `/api/v1/projects/{projectId}/members` | authenticated | `ProjectManager+` | Service-layer guard (SR-S0-05 close): the new member's `User.OrganisationId` must equal the project's `AppointingPartyId`. Cross-org attempts throw `ValidationException`. v1.0 simple model per ADR-0012; B2B contractor membership via `ProjectAppointment` is post-v1.0. |

## CDE

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/cde/containers` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/cde/containers` | authenticated | `InformationManager+` | |

## Cost & Commercial

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| POST | `/api/v1/projects/{projectId}/cbs/import` | authenticated | `ProjectManager+` | T-S1-03. Multipart `file` (CSV). Header: `Code,Name,ParentCode,Description,SortOrder`. Import-into-empty only — re-import / merge deferred. Audit: `cbs.imported`. |
| PUT  | `/api/v1/projects/{projectId}/cbs/{itemId}/budget` | authenticated | `ProjectManager+` | T-S1-04. Body `{ "budget": <decimal\|null> }`. Sets / clears the planned budget on a single CBS line. `decimal(18,2)`; currency follows `Project.Currency`. Negative values rejected. Audit: `cbs.line_budget_set` with `previous` / `current` in detail. |
| GET  | `/api/v1/projects/{projectId}/cbs/rollup` | authenticated | membership | T-S1-05. Per-line committed-vs-budget rollup: one row per CBS line with `budget`, `committed` (sum of commitment Amounts), and `variance` (`budget - committed` when budget set, else `null`). Flat — tree aggregation deferred to T-S1-07 EVM. |
| PUT  | `/api/v1/projects/{projectId}/cbs/{itemId}/schedule` | authenticated | `ProjectManager+` | B-017. Body `SetLineScheduleRequest(scheduledStart?, scheduledEnd?)`. Sets / clears the line's scheduled date range. If both ends are set, `Start < End` required. Audit: `cbs.line_schedule_set` with `previousStart` / `previousEnd` / `currentStart` / `currentEnd` in detail. |
| PUT  | `/api/v1/projects/{projectId}/cbs/{itemId}/progress` | authenticated | `ProjectManager+` | B-017. Body `SetLineProgressRequest(percentComplete?)`. Stored as a fraction in [0, 1]. Drives EV (T-S1-07) and valuation auto-derivation (T-S1-09) when wired. Audit: `cbs.line_progress_set` with `previous` / `current` in detail. |
| POST | `/api/v1/projects/{projectId}/commitments` | authenticated | `ProjectManager+` | T-S1-05. Body `CreateCommitmentRequest`. Records a `PO` or `Subcontract` against a CBS line in the same project. `Amount > 0` required. Audit: `commitment.created` with `cbsLineId`, `type`, `amount`, `reference` in detail. |
| POST | `/api/v1/projects/{projectId}/cost-periods` | authenticated | `ProjectManager+` | T-S1-06. Body `CreatePeriodRequest(label, start, end, plannedCashflow?)`. Opens a new reporting / accounting period on the project. `StartDate < EndDate` required; period overlap with existing periods is allowed. Optional `PlannedCashflow` (T-S1-11) sets the baseline at create time; can be set later via the dedicated baseline endpoint. Audit: `cost_period.opened`. |
| POST | `/api/v1/projects/{projectId}/cost-periods/{periodId}/close` | authenticated | `ProjectManager+` | T-S1-06. Closes the period. Once closed, `actuals` cannot target it. Re-open is not supported in v1.0. Already-closed → `ConflictException`. Audit: `cost_period.closed`. |
| PUT  | `/api/v1/projects/{projectId}/cost-periods/{periodId}/baseline` | authenticated | `ProjectManager+` | T-S1-11. Body `SetPeriodBaselineRequest(plannedCashflow?)`. Sets / clears the period's baseline planned cashflow. Allowed on both Open and Closed periods (re-baselining is a forecast adjustment, not an actual mutation). Audit: `cost_period.baseline_set`. |
| POST | `/api/v1/projects/{projectId}/actuals` | authenticated | `ProjectManager+` | T-S1-06. Body `RecordActualRequest(cbsLineId, periodId, amount, reference?, description?)`. Records an actual cost against a CBS line in a specific open period. `Amount > 0` required; closed period rejected with `ConflictException`. Audit: `actual_cost.recorded`. |
| GET  | `/api/v1/projects/{projectId}/cashflow` | authenticated | membership | T-S1-11. Project-level S-curve. Returns `CashflowDto(currency, points)` with one `CashflowPeriodPoint` per CostPeriod ordered by StartDate. Each point carries `BaselinePlanned` (period), `CumulativeBaseline`, `Actual`, `CumulativeActual`, `Forecast` (actuals up to the latest actual period; baseline-projected from there). |
| GET  | `/api/v1/projects/{projectId}/cashflow/by-line` | authenticated | membership | T-S1-11 wire-up via B-017. Per-CBS-line breakdown. Returns `CashflowByLineDto(currency, lines)`; each `CashflowLineSeries` carries the line's Budget / ScheduledStart / ScheduledEnd plus one point per CostPeriod with `BaselinePlanned` (Budget × overlap-fraction(period range, schedule range)) and `Actual` (Σ ActualCost where line + period match). Lines with no schedule contribute zero across the baseline series. |
| GET  | `/api/v1/projects/{projectId}/evm?dataDate=...` | authenticated | membership | B-017 EVM service integration (T-S1-07 closure). Project-level snapshot at the supplied data date (defaults to UTC now). Per CBS line: BAC contribution = Budget; EV = Budget × PercentComplete; PV = Budget × linear-schedule-fraction(dataDate, ScheduledStart, ScheduledEnd). AC = Σ ActualCost.Amount across the project (no data-date gate). Returns the full `Evm.EvmSnapshot` (CV, SV, CPI, SPI, EAC, EacAtypical, EacScheduleAndCost, ETC, VAC, TcpiToBac, TcpiToEac). |
| POST | `/api/v1/projects/{projectId}/variations` | authenticated | `TaskTeamMember+` | T-S1-08. Body `RaiseVariationRequest`. Raises a new Variation in state `Raised`. Title required. Optional CBS line link must belong to the same project. `VariationNumber` auto-generated as `VAR-NNNN`. Audit: `variation.raised`. |
| POST | `/api/v1/projects/{projectId}/variations/{variationId}/approve` | authenticated | `ProjectManager+` | T-S1-08. Body `VariationDecisionRequest(decisionNote?)`. Transitions Raised → Approved. Already-decided variation rejected with `ConflictException`. Approved variations record the decision but do not auto-adjust budget / commitments — manual baseline integration is expected. Audit: `variation.approved`. |
| POST | `/api/v1/projects/{projectId}/variations/{variationId}/reject` | authenticated | `ProjectManager+` | T-S1-08. Body `VariationDecisionRequest(decisionNote?)`. Transitions Raised → Rejected. Already-decided variation rejected with `ConflictException`. Audit: `variation.rejected`. |
| POST | `/api/v1/projects/{projectId}/payment-certificates` | authenticated | `ProjectManager+` | T-S1-09. Body `CreatePaymentCertificateDraftRequest(periodId, cumulativeValuation, cumulativeMaterialsOnSite, retentionPercent)`. Creates a Draft certificate against an existing CostPeriod. One certificate per period (unique). NEC4 cumulative semantics per ADR-0013. Audit: `payment_certificate.draft_created`. |
| PUT  | `/api/v1/projects/{projectId}/payment-certificates/{certificateId}` | authenticated | `ProjectManager+` | T-S1-09. Body `UpdatePaymentCertificateDraftRequest`. Edits a Draft. Issued certificates rejected with `ConflictException`. Audit: `payment_certificate.draft_updated`. |
| POST | `/api/v1/projects/{projectId}/payment-certificates/{certificateId}/issue` | authenticated | `ProjectManager+` | T-S1-09. Transitions Draft → Issued. Snapshots the sum of Approved Variations into `IncludedVariationsAmount`; the certificate becomes immutable. Audit: `payment_certificate.issued`. |
| GET  | `/api/v1/projects/{projectId}/payment-certificates/{certificateId}` | authenticated | membership | T-S1-09. Returns `PaymentCertificateDto` with computed gross / retention / net / previously-certified / amount-due. For Drafts the variations sum is a live preview; for Issued it is the snapshot. |

## Risk & Opportunities

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/risks` | authenticated | membership | T-S2-05. List active risks on a project, ordered by Score descending then CreatedAt ascending. Closed risks included (filter client-side if needed). |
| GET  | `/api/v1/projects/{projectId}/risks/matrix` | authenticated | membership | T-S2-05. Returns the 25-cell 5×5 P×I matrix (`List<RiskMatrixCell>` with Probability, Impact, Score, RiskIds). Always 25 cells in row-major order from (1,1) to (5,5); empty cells carry an empty RiskIds list. Closed risks excluded — heat-maps show the live register only. |
| POST | `/api/v1/projects/{projectId}/risks` | authenticated | `TaskTeamMember+` | T-S2-04. Body `CreateRiskRequest(title, description?, categoryId?, probability, impact, ownerId?, responseStrategy?, responsePlan?, contingencyAmount?)`. Probability and Impact validated to 1..5; Score = P×I computed server-side. Status defaults to `Identified`. Optional CategoryId must belong to the same project. Audit: `risk.created`. |
| PUT  | `/api/v1/projects/{projectId}/risks/{riskId}` | authenticated | `TaskTeamMember+` | T-S2-04. Body `UpdateRiskRequest` — all fields nullable for partial update. Probability or Impact changes recompute Score. Setting `Status = Closed` rejected (use the close endpoint so the audit history carries `risk.closed`). No-op calls (no fields set) rejected with `ValidationException`. Already-Closed risks rejected with `ConflictException`. Audit: `risk.updated` with `{ changedFields, scoreBefore, scoreAfter }`. |
| POST | `/api/v1/projects/{projectId}/risks/{riskId}/close` | authenticated | `ProjectManager+` | T-S2-04. Sets Status = Closed; idempotent rejection on already-Closed. Audit: `risk.closed` with `{ previousStatus }`. |
| POST | `/api/v1/projects/{projectId}/risks/{riskId}/assess` | authenticated | `TaskTeamMember+` | T-S2-06. Body `RecordQualitativeAssessmentRequest(notes)`. Sets QualitativeNotes, AssessedAt = UtcNow, AssessedById = caller. Bumps Status `Identified` → `Assessed` if currently Identified; otherwise leaves Status unchanged. Re-assessment overwrites previous notes (passive history via AuditInterceptor before/after JSON). Already-Closed risks rejected with `ConflictException`. Audit: `risk.qualitative_assessed` with `{ statusTransition?, notesLength }`. |
| POST | `/api/v1/projects/{projectId}/risks/{riskId}/quantify` | authenticated | `TaskTeamMember+` | T-S2-07. Body `RecordQuantitativeAssessmentRequest(bestCase, mostLikely, worstCase, distribution)`. Sets the 3-point estimate plus chosen Distribution shape (Triangular / Pert / Beta). Validates `BestCase ≤ MostLikely ≤ WorstCase` and non-negative values. Re-assessment overwrites. Distribution sampling itself lands in T-S2-08 (cost-side Monte Carlo); this endpoint just stores the analyst's inputs. Already-Closed risks rejected. Audit: `risk.quantitative_assessed` with `{ bestCase, mostLikely, worstCase, distribution }`. |
| GET  | `/api/v1/projects/{projectId}/risks/monte-carlo?iterations=N&seed=S` | authenticated | membership | T-S2-08. Cost-side Monte Carlo simulation across quantified, non-Closed risks. Default `iterations` = 10,000, server enforces minimum 1,000 per ADR / F.3. Optional `seed` for deterministic runs (testing, snapshotting); omitted → fresh entropy each call. Returns `MonteCarloResult { iterationsRun, min, mean, max, p10, p50, p80, p90 }` in the project's currency. Schedule-side MC deferred to v1.1 (B-028). |
| POST | `/api/v1/projects/{projectId}/risks/{riskId}/drawdowns` | authenticated | `TaskTeamMember+` | T-S2-09. Body `RecordRiskDrawdownRequest(amount, occurredAt, reference?, note?)`. Records a contingency drawdown event against the risk. Amount > 0 enforced; cumulative drawdowns may exceed Risk.ContingencyAmount (over-runs visible, not blocked). Already-Closed risks rejected. Cross-module link to specific Commitment / ActualCost rows deferred to v1.1 (B-030). Audit: `risk.drawdown_recorded` with `{ riskId, amount, occurredAt, reference }`. |
| GET  | `/api/v1/projects/{projectId}/risks/{riskId}/drawdowns` | authenticated | membership | T-S2-09. List drawdowns recorded against a risk, ordered by OccurredAt asc then CreatedAt asc. |

## Stakeholder & Communications

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/stakeholders` | authenticated | membership | T-S3-03. List active stakeholders ordered by Score desc then Name asc. |
| GET  | `/api/v1/projects/{projectId}/stakeholders/matrix` | authenticated | membership | T-S3-04. Returns the 25-cell 5×5 Power/Interest matrix (`List<StakeholderMatrixCell>` with Power, Interest, Score, StakeholderIds). Always 25 cells in row-major order from (1,1) to (5,5); empty cells carry an empty StakeholderIds list. Deactivated stakeholders excluded — heat-map shows the live register only. |
| POST | `/api/v1/projects/{projectId}/stakeholders` | authenticated | `TaskTeamMember+` | T-S3-03. Body `CreateStakeholderRequest(name, organisation?, role?, email?, phone?, power, interest, engagementApproach?, engagementNotes?)`. Power and Interest validated to 1..5; Score = P×I computed server-side. EngagementApproach auto-computed at 3-as-midpoint Mendelow if not supplied. Audit: `stakeholder.created`. |
| PUT  | `/api/v1/projects/{projectId}/stakeholders/{stakeholderId}` | authenticated | `TaskTeamMember+` | T-S3-03. Body `UpdateStakeholderRequest` — all fields nullable for partial update. Power/Interest changes recompute Score and (if EngagementApproach not also explicitly set) the Mendelow quadrant. Already-deactivated rejected with `ConflictException`. No-op rejected with `ValidationException`. Audit: `stakeholder.updated` with `{ changedFields, scoreBefore, scoreAfter }`. |
| POST | `/api/v1/projects/{projectId}/stakeholders/{stakeholderId}/deactivate` | authenticated | `ProjectManager+` | T-S3-03. Sets IsActive = false; idempotent rejection on already-deactivated. Audit: `stakeholder.deactivated` with `{ name }`. |
| POST | `/api/v1/projects/{projectId}/stakeholders/{stakeholderId}/engagements` | authenticated | `TaskTeamMember+` | T-S3-06. Body `RecordEngagementRequest(type, occurredAt, summary, actionsAgreed?)`. Records one interaction with the stakeholder. `Summary` required; cross-tenant or wrong-project stakeholder ID 404s via the query filter. Audit: `engagement.recorded` with `{ stakeholderId, type, occurredAt, hasActions }`. |
| GET  | `/api/v1/projects/{projectId}/stakeholders/{stakeholderId}/engagements` | authenticated | membership | T-S3-06. Lists most-recent engagements with the stakeholder, newest first, capped at 200 entries. Full pagination is a v1.1 candidate. |

## Documents

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/documents` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/documents` | authenticated | `TaskTeamMember+` | T-S0-08 (commit `c26d451`) |
| GET  | `/api/v1/projects/{projectId}/documents/{documentId}` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/documents/{documentId}/transition` | authenticated | CDE state machine | Service validates `CanTransition(from, to, role)` per `CdeStateMachine` |

## RFIs

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/rfis` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/rfis` | authenticated | `TaskTeamMember+` | T-S0-08 |
| POST | `/api/v1/projects/{projectId}/rfis/{rfiId}/respond` | authenticated | `TaskTeamMember+` | T-S0-08 floor + **B-006** ownership: if `AssignedToId` is set, only that user OR an `InformationManager+` may respond. Unassigned RFIs remain open to any caller at the floor. |

## Actions

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/actions` | authenticated | membership | |
| POST | `/api/v1/projects/{projectId}/actions` | authenticated | `TaskTeamMember+` | T-S0-08 |
| PATCH | `/api/v1/projects/{projectId}/actions/{actionId}` | authenticated | `TaskTeamMember+` | T-S0-08 floor + **B-005** ownership: if `AssigneeId` is set, only that user OR a `ProjectManager+` may update. Unassigned actions remain open to any caller at the floor. |

## Audit

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/audit` | authenticated | `InformationManager+` | |

## Project Templates

Routes under `/api/projects/{projectId}/templates` (note: unversioned
prefix, predates the `api/v1/` convention).

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/projects/{projectId}/templates` | authenticated | membership | T-S0-08 added the membership check (commit `7bda674`) |
| GET  | `/api/projects/{projectId}/templates/{templateId}/content` | authenticated | membership | T-S0-08 |
| PUT  | `/api/projects/{projectId}/templates/{templateId}/content` | authenticated | `InformationManager+` | T-S0-08 |
| POST | `/api/projects/{projectId}/provision` | `OrgAdmin`, `SuperAdmin` | — | T-S0-08; admin-only re-provisioning |

## Known-deferred checks

These are *not* bugs per current ADR-0010 scope, but likely ADR or
hardening candidates in future sprints.

- ~~`PATCH actions/{actionId}` does not verify the caller is the
  action's assignee.~~ **Closed by B-005 (2026-04-28).** Caller
  must be the assignee OR `ProjectManager+`; unassigned actions
  remain open to the floor.
- ~~`POST rfis/{rfiId}/respond` does not verify the caller is the
  intended responder (if any).~~ **Closed by B-006 (2026-04-28).**
  When `AssignedToId` is set, caller must be that user OR an
  `InformationManager+`; unassigned RFIs remain open to the floor.
- ~~`GET /api/v1/organisations` returns all organisations visible
  through the tenant query filter~~ — **the prior text was
  factually wrong; Organisation is intentionally unfiltered per
  ADR-0003, so the endpoint exposed every other org's Name/Code
  to any authenticated caller. Closed by B-007 (2026-04-28).**
  Non-SuperAdmin callers are now scoped to their own organisation
  at the controller layer; SuperAdmin retains the wider view per
  ADR-0007.
- ~~No rate limiting on `POST /auth/login` or `/auth/refresh`~~ —
  **Closed by B-002 (2026-04-28).** Per-IP fixed-window limits
  added: `login` 5 / min, `register` / `refresh` /
  `organisations create` 10 / min. CAPTCHA + email-verification
  remain v1.1 candidates (separate item if pre-customer onboarding
  warrants them).

## Update protocol

When a new endpoint is added or an existing gate is changed, update
this file **in the same commit** as the code change. Drift between
code and this matrix should be caught by code review and by T-S0-04
/ T-S0-06b-style behavioural tests against each gate.
