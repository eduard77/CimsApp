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

## Schedule & Programme

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET    | `/api/v1/projects/{projectId}/schedule/activities` | authenticated | membership | T-S4-05. List active activities ordered by Code. |
| GET    | `/api/v1/projects/{projectId}/schedule/activities/{activityId}` | authenticated | membership | T-S4-05. Single activity read; cross-project / cross-tenant 404 via the query filter. |
| POST   | `/api/v1/projects/{projectId}/schedule/activities` | authenticated | `TaskTeamMember+` | T-S4-05. Body `CreateActivityRequest`. Code unique within project; Duration ≥ 0; PercentComplete in [0, 1]; ConstraintDate required for SNET / SNLT / FNET / FNLT / MSO / MFO. AssigneeId, if set, must be a project member. Audit: `activity.created`. |
| PUT    | `/api/v1/projects/{projectId}/schedule/activities/{activityId}` | authenticated | `TaskTeamMember+` | T-S4-05. Body `UpdateActivityRequest` — all fields nullable for partial update. Code change re-validates uniqueness. AssigneeId change re-validates project membership. Switching ConstraintType to ASAP / ALAP clears the legacy ConstraintDate. Audit: `activity.updated` with `{ changedFields }`. |
| POST   | `/api/v1/projects/{projectId}/schedule/activities/{activityId}/deactivate` | authenticated | `ProjectManager+` | T-S4-05. Sets IsActive = false. **Rejected with `ConflictException` if the activity has any active dependencies** — caller must remove dependencies first. Idempotent rejection on already-deactivated. Audit: `activity.deactivated` with `{ code }`. |
| GET    | `/api/v1/projects/{projectId}/schedule/dependencies` | authenticated | membership | T-S4-03. List all dependencies in the project ordered by (PredecessorId, SuccessorId). |
| POST   | `/api/v1/projects/{projectId}/schedule/dependencies` | authenticated | `TaskTeamMember+` | T-S4-03. Body `AddDependencyRequest(predecessorId, successorId, type, lag)`. Type one of FS/SS/FF/SF; Lag in days, decimal, range -365..365. Self-loops, duplicate (Pred, Succ) pairs, and cycle-creating edges all rejected. Audit: `dependency.added`. |
| DELETE | `/api/v1/projects/{projectId}/schedule/dependencies/{dependencyId}` | authenticated | `TaskTeamMember+` | T-S4-03. Removes a single dependency. Audit: `dependency.removed`. |
| POST   | `/api/v1/projects/{projectId}/schedule/recompute` | authenticated | `ProjectManager+` | T-S4-05. Body `RecomputeScheduleRequest(dataDate?)`. Runs the CPM solver against active activities + dependencies; persists ES / EF / LS / LF / TotalFloat / FreeFloat / IsCritical to each activity. `dataDate` defaults to `Project.StartDate`; `ValidationException` if neither is set. Returns `{ projectStart, projectFinish, activitiesCount, criticalActivitiesCount }`. Audit: `schedule.recomputed`. |
| GET    | `/api/v1/projects/{projectId}/schedule/baselines` | authenticated | membership | T-S4-06. List captured baselines on the project, newest first. Returns header summaries only (id, label, capturedAt, capturedById, activitiesCount, projectFinishAtBaseline). |
| POST   | `/api/v1/projects/{projectId}/schedule/baselines` | authenticated | `ProjectManager+` | T-S4-06. Body `CreateBaselineRequest(label)`. Snapshots every active activity at capture time (Code, Name, Duration, EarlyStart, EarlyFinish, IsCritical). Multiple baselines per project allowed; baselines are immutable after capture. Audit: `schedule_baseline.captured`. |
| GET    | `/api/v1/projects/{projectId}/schedule/baselines/{baselineId}/comparison` | authenticated | membership | T-S4-06. Activity-by-activity comparison of a baseline vs the current schedule. Returns `BaselineComparisonDto` with per-activity start / finish / duration variances (current minus baseline, days), plus `IsNewSinceBaseline` and `IsRemovedSinceBaseline` flags for the activity-set delta. |
| POST   | `/api/v1/projects/{projectId}/schedule/import` | authenticated | `ProjectManager+` | T-S4-09. Multipart `file` (MS Project XML, schemas.microsoft.com/project namespace). Maps `<Tasks>` → Activity (Code = `MSP-{UID}`) and `<PredecessorLink>` → Dependency (Type 0/1/2/3 → FF/FS/SF/SS, LinkLag tenths-of-minutes → days at 8h/day). Rejected with `ConflictException` if the project already has any active activities — into-empty only, same shape as T-S1-03 CBS import. CPM is NOT auto-recomputed; caller decides when to call `/schedule/recompute`. Audit: `schedule.imported`. Export reverse path deferred to v1.1 / B-031 per CR-005. |
| GET    | `/api/v1/projects/{projectId}/schedule/gantt` | authenticated | membership | T-S4-11. Returns `GanttDto(projectStart, projectFinish, activities[], dependencies[])` for Gantt-renderer consumption. Per-activity Start / Finish prefer CPM-computed `EarlyStart` / `EarlyFinish`; fall back to `ScheduledStart` / `ScheduledFinish` if the solver hasn't run yet. Network-view (PERT) endpoint deferred to v1.1 / B-033 per CR-005. |
| GET    | `/api/v1/projects/{projectId}/schedule/lps/lookahead?weekStarting=...` | authenticated | membership | T-S4-07 LPS lookahead board. Optional `weekStarting` filter (any day-of-week is accepted; service normalises to the Monday of that ISO week). Active entries only. |
| POST   | `/api/v1/projects/{projectId}/schedule/lps/lookahead` | authenticated | `TaskTeamMember+` | T-S4-07. Body `CreateLookaheadEntryRequest(activityId, weekStarting, constraintsRemoved, notes?)`. Service normalises `weekStarting` to Monday and validates that the Activity is active in the same project. Audit: `lookahead.added`. |
| PUT    | `/api/v1/projects/{projectId}/schedule/lps/lookahead/{lookaheadId}` | authenticated | `TaskTeamMember+` | T-S4-07. Body `UpdateLookaheadEntryRequest(constraintsRemoved?, notes?)`. Toggle of the constraints-removed flag during the weekly LPS meeting. No-op rejected. Audit: `lookahead.updated`. |
| DELETE | `/api/v1/projects/{projectId}/schedule/lps/lookahead/{lookaheadId}` | authenticated | `TaskTeamMember+` | T-S4-07. Soft-delete (sets IsActive = false). Audit: `lookahead.removed`. |
| GET    | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans` | authenticated | membership | T-S4-07. List Weekly Work Plans on the project, newest week first. |
| POST   | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans` | authenticated | `ProjectManager+` | T-S4-07. Body `CreateWeeklyWorkPlanRequest(weekStarting, notes?)`. Unique within project per (project, week). Audit: `weekly_plan.created`. |
| GET    | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans/{wwpId}` | authenticated | membership | T-S4-07. Returns `WeeklyWorkPlanDto` with commitments + computed PPC (= 100 × completed / committed). PPC is computed on read and never persisted — always reflects current commitment state. |
| POST   | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans/{wwpId}/commitments` | authenticated | `TaskTeamMember+` | T-S4-07. Body `AddCommitmentRequest(activityId, notes?)`. Activity must be active in the same project. Duplicate (WWP, Activity) pairs rejected. Audit: `weekly_commitment.added`. |
| PUT    | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans/{wwpId}/commitments/{commitmentId}` | authenticated | `TaskTeamMember+` | T-S4-07. Body `UpdateCommitmentRequest(completed, reason?, notes?)`. **`Completed = false` without a Reason is rejected** — the whole point of LPS is to surface root causes. Audit: `weekly_commitment.updated`. |
| DELETE | `/api/v1/projects/{projectId}/schedule/lps/weekly-plans/{wwpId}/commitments/{commitmentId}` | authenticated | `TaskTeamMember+` | T-S4-07. Hard-delete a commitment — the commitment never made it onto a WWP that was meant to track it. Audit: `weekly_commitment.removed`. |
| GET  | `/api/v1/projects/{projectId}/communications` | authenticated | membership | T-S3-07. Lists active communications-matrix rows ordered by ItemType then Frequency. The matrix is a planning view — soft-deleted rows excluded. |
| POST | `/api/v1/projects/{projectId}/communications` | authenticated | `TaskTeamMember+` | T-S3-07. Body `CreateCommunicationItemRequest(itemType, audience, frequency, channel, ownerId, notes?)`. F.4 axes: ItemType = "what", Audience = "who" (free-text in v1.0), Frequency = "when", Channel = "how". OwnerId must be a member of the project (`ValidationException` otherwise). Audit: `communication.created`. |
| PUT  | `/api/v1/projects/{projectId}/communications/{itemId}` | authenticated | `TaskTeamMember+` | T-S3-07. Body `UpdateCommunicationItemRequest` — all fields nullable for partial update. Owner change re-validates project membership. Already-deactivated rejected with `ConflictException`; no-op rejected with `ValidationException`. Audit: `communication.updated` with `{ changedFields }`. |
| POST | `/api/v1/projects/{projectId}/communications/{itemId}/deactivate` | authenticated | `ProjectManager+` | T-S3-07. Sets IsActive = false; idempotent rejection on already-deactivated. Audit: `communication.deactivated` with `{ itemType }`. |

## Change Control

PAFM-SD F.6. Five-state ChangeRequest workflow per `Core/ChangeWorkflow.cs`:
Raised → Assessed → Approved | Rejected → Implemented → Closed.
Reject is allowed from Raised or Assessed only; once Approved the
only forward path is Implemented → Closed. State machine + role
gates enforced via `ChangeWorkflow.CanTransition`; service maps
to `ConflictException` on invalid transitions and `ForbiddenException`
on insufficient role.

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/change-requests` | authenticated | membership | T-S5-05. Optional `?state=...` and `?category=...` filters. Ordered by RaisedAt desc. |
| GET  | `/api/v1/projects/{projectId}/change-requests/{id}` | authenticated | membership | T-S5-05. Cross-tenant 404 via the query filter. |
| POST | `/api/v1/projects/{projectId}/change-requests` | authenticated | `TaskTeamMember+` | T-S5-04. Body `RaiseChangeRequestRequest(title, description?, category, bsaCategory, programmeImpactSummary?, costImpactSummary?, estimatedCostImpact?, estimatedTimeImpactDays?)`. Auto-generates `CR-NNNN`; initial state Raised. Audit: `change_request.raised`. |
| POST | `/api/v1/projects/{projectId}/change-requests/{id}/assess` | authenticated | `InformationManager+` | T-S5-04. Body `AssessChangeRequestRequest(assessmentNote, programmeImpactSummary?, costImpactSummary?, estimatedCostImpact?, estimatedTimeImpactDays?, bsaCategory?)`. State Raised → Assessed. AssessmentNote required. IM may refine impact summaries / estimates / BSA categorisation. Audit: `change_request.assessed`. |
| POST | `/api/v1/projects/{projectId}/change-requests/{id}/approve` | authenticated | `ProjectManager+` | T-S5-04 / T-S5-06. Body `ApproveChangeRequestRequest(decisionNote, createVariation)`. State Assessed → Approved. DecisionNote required. **`createVariation = true`** atomically spawns an S1 Variation in state Raised, with EstimatedCostImpact / EstimatedTimeImpactDays carried over from the CR; the CR's `GeneratedVariationId` carries the FK link. Two audit events emit: `change_request.variation_created` (when applicable) + `change_request.approved`. |
| POST | `/api/v1/projects/{projectId}/change-requests/{id}/reject` | authenticated | `ProjectManager+` | T-S5-04. Body `RejectChangeRequestRequest(decisionNote)`. State Raised|Assessed → Rejected. DecisionNote required. **Rejected after Approved** rejected with `ConflictException` via the state machine. Audit: `change_request.rejected`. |
| POST | `/api/v1/projects/{projectId}/change-requests/{id}/implement` | authenticated | `ProjectManager+` | T-S5-04. Body `ImplementChangeRequestRequest(note?)`. State Approved → Implemented. Marks the change as actioned in delivery. Audit: `change_request.implemented`. |
| POST | `/api/v1/projects/{projectId}/change-requests/{id}/close` | authenticated | `ProjectManager+` | T-S5-04. Body `CloseChangeRequestRequest(note?)`. State Implemented → Closed. Skipping Implement (Approved → Closed direct) rejected with `ConflictException`. Closed is terminal. Audit: `change_request.closed`. |

## Procurement

PAFM-SD F.7. Procurement strategy + tender packages + tenders +
evaluation matrix + award/contract + early warnings + compensation
events. Strategy is single-row-per-project (upsert); packages /
tenders / contracts / EWs / CEs are project-scoped collections.

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/procurement/strategy` | authenticated | membership | T-S6-02. Returns the project's procurement strategy or `null` if not yet captured. |
| PUT  | `/api/v1/projects/{projectId}/procurement/strategy` | authenticated | `TaskTeamMember+` | T-S6-02. Body `UpsertProcurementStrategyRequest(approach, contractForm, estimatedTotalValue?, keyDates?, packageBreakdownNotes?)`. Upsert — first call creates, subsequent calls update. Audit: `procurement_strategy.created` (first write) / `procurement_strategy.updated` (subsequent). |
| POST | `/api/v1/projects/{projectId}/procurement/strategy/approve` | authenticated | `ProjectManager+` | T-S6-02. Records `ApprovedById` + `ApprovedAt`. Re-approval allowed in v1.0 (timestamps refresh) — real workflows revisit strategies after risk reviews. Audit: `procurement_strategy.approved`. |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages` | authenticated | membership | T-S6-03. Optional `?state=Draft|Issued|Closed` filter. Active packages only. Newest first. |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}` | authenticated | membership | T-S6-03. Cross-tenant 404 via the query filter. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages` | authenticated | `TaskTeamMember+` | T-S6-03. Body `CreateTenderPackageRequest(name, description?, estimatedValue?, issueDate?, returnDate?)`. Auto-generates `TP-NNNN`; initial state Draft. ReturnDate must be after IssueDate when both supplied. Audit: `tender_package.created`. |
| PUT    | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}` | authenticated | `TaskTeamMember+` | T-S6-03. Body `UpdateTenderPackageRequest` — partial update. **Update of Issued / Closed packages rejected with `ConflictException`** (the package is frozen post-issue). No-op rejected. Audit: `tender_package.updated` with `{ changedFields }`. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}/issue` | authenticated | `ProjectManager+` | T-S6-03. State Draft → Issued. Records `IssuedById` + `IssuedAt`; freezes the package details. State machine + role gate via `Core/TenderPackageWorkflow`. Audit: `tender_package.issued`. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}/close` | authenticated | `ProjectManager+` | T-S6-03 (also called from T-S6-06 Award). State Issued → Closed. Closed is terminal. Audit: `tender_package.closed`. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}/deactivate` | authenticated | `ProjectManager+` | T-S6-03. Soft-delete (sets IsActive = false). **Only Draft packages can be deactivated** — Issued / Closed packages are part of the audit chain. Idempotent rejection on already-deactivated. Audit: `tender_package.deactivated`. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{id}/award` | authenticated | `ProjectManager+` | T-S6-06. Body `AwardTenderPackageRequest(awardedTenderId, awardNote, contractForm?, contractStartDate?, contractEndDate?)`. Atomically: winning Tender → Awarded; all other active Tenders → Rejected automatically; package → Closed; spawns a `Contract` row with `CON-NNNN` numbering carrying BidAmount + BidderName + BidderOrganisation. ContractForm defaults to `ProcurementStrategy.ContractForm` if a strategy exists, else `Other`; explicit override accepted. Audit: `tender.awarded` (winner), `tender.rejected` (each loser), `tender_package.closed`, `contract.created` — all in one transactional SaveChanges. |
| GET    | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/early-warnings` | authenticated | membership | T-S6-07. Lists EWs for the contract, newest first. Optional `?state=Raised|UnderReview|Closed` filter. |
| GET    | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/early-warnings/{ewId}` | authenticated | membership | T-S6-07. Cross-tenant 404 via the query filter. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/early-warnings` | authenticated | `TaskTeamMember+` | T-S6-07. Body `RaiseEarlyWarningRequest(title, description?)`. NEC4 clause-15 notice. Allowed only against Active contracts; raised against Closed contracts rejected with `ConflictException`. Audit: `early_warning.raised`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/early-warnings/{ewId}/review` | authenticated | `InformationManager+` | T-S6-07. Body `ReviewEarlyWarningRequest(responseNote)`. State Raised → UnderReview. ResponseNote required (the analysis IS the review). Audit: `early_warning.reviewed`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/early-warnings/{ewId}/close` | authenticated | `ProjectManager+` | T-S6-07. Body `CloseEarlyWarningRequest(closureNote?)`. State UnderReview → Closed. Closed is terminal. Audit: `early_warning.closed`. |
| GET    | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events` | authenticated | membership | T-S6-08. NEC4 clause-60.1 list. Optional `?state=Notified|Quoted|Accepted|Rejected|Implemented` filter. Newest first. |
| GET    | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events/{ceId}` | authenticated | membership | T-S6-08. Cross-tenant 404 via the query filter. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events` | authenticated | `TaskTeamMember+` | T-S6-08. Body `NotifyCompensationEventRequest(title, description?)`. Auto-generates `CE-NNNN`; initial state Notified. **Allowed only against Active contracts**; rejected with `ConflictException` against Closed contracts. Audit: `compensation_event.notified`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events/{ceId}/quote` | authenticated | `TaskTeamMember+` | T-S6-08. Body `QuoteCompensationEventRequest(estimatedCostImpact, estimatedTimeImpactDays, quotationNote)`. State Notified → Quoted. EstimatedCostImpact ≥ 0. State machine + role gate via `Core/CompensationEventWorkflow`. Audit: `compensation_event.quoted`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events/{ceId}/accept` | authenticated | `ProjectManager+` | T-S6-08. Body `DecideCompensationEventRequest(decisionNote)`. State Quoted → Accepted. Audit: `compensation_event.accepted`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events/{ceId}/reject` | authenticated | `ProjectManager+` | T-S6-08. Body `DecideCompensationEventRequest(decisionNote)`. State Notified|Quoted → Rejected. Notified→Rejected covers NEC4 clause 61.4 ("PM notifies it is not a CE"). Rejected is terminal. Audit: `compensation_event.rejected`. |
| POST   | `/api/v1/projects/{projectId}/procurement/contracts/{contractId}/compensation-events/{ceId}/implement` | authenticated | `ProjectManager+` | T-S6-08. Body `ImplementCompensationEventRequest(note?)`. State Accepted → Implemented. Implemented is terminal. **v1.0 limitations:** PM 4-week notification deadline → B-048; contractor 3-week quotation deadline + deemed-acceptance → B-049; risk-allowance pricing rules → B-050. Audit: `compensation_event.implemented`. |

## Reporting & Dashboards

PAFM-SD F.8. Per-role dashboards (T-S7-02), MPR data aggregator
(T-S7-03 — PDF deferred to v1.1 / B-055), KPI cards (T-S7-04),
custom report builder (T-S7-05). All read endpoints; only the
custom-report-definition write paths produce mutations + audit.

| Method | Route | Global role | Project role | Comment |
|---|---|---|---|---|
| GET  | `/api/v1/projects/{projectId}/dashboards/pm` | authenticated | membership | T-S7-02. PM dashboard cards: open RFIs, open Actions, open ChangeRequests, open EarlyWarnings, open CompensationEvents, open Risks. |
| GET  | `/api/v1/projects/{projectId}/dashboards/cm` | authenticated | membership | T-S7-02. CM (Commercial Manager) dashboard cards: total CBS budget, total committed, total actuals, raised + approved variations, latest payment certificate. |
| GET  | `/api/v1/projects/{projectId}/dashboards/sm` | authenticated | membership | T-S7-02. SM (Site Manager) dashboard cards: active lookaheads, latest WWP + computed PPC, open Actions, open Early Warnings. |
| GET  | `/api/v1/projects/{projectId}/dashboards/im` | authenticated | membership | T-S7-02. IM (Information Manager) dashboard cards: documents by CdeState (4 cards), open RFIs, ChangeRequests awaiting assessment. |
| GET  | `/api/v1/projects/{projectId}/dashboards/hse` | authenticated | membership | T-S7-02. HSE dashboard sparse in v1.0 — single placeholder card pointing at S12 (HSE module integration) + B-059 backlog entry. |
| GET  | `/api/v1/projects/{projectId}/dashboards/client` | authenticated | membership | T-S7-02. Client dashboard cards: project status, estimated finish (max EarlyFinish across active activities), raised + approved variations, latest baseline. |
| GET  | `/api/v1/projects/{projectId}/reports/mpr?from=&to=` | authenticated | membership | T-S7-03. Monthly Project Report data aggregator. Returns `MprDto` with seven sections: ExecutiveSummary, Programme, Cost, Risk, Changes, Issues, Stakeholders. Optional `from` / `to` ISO-8601 UTC query params; defaults to last full calendar month. JSON only — PDF rendering deferred to v1.1 / B-055. Section layout is best-effort inference from PMBOK / NEC4 templates pending canonical PAFM Ch 30 paste. |
| GET  | `/api/v1/projects/{projectId}/reports/kpi` | authenticated | membership | T-S7-04. Project-level KPI card list mapped to PAFM-SD Ch 2.6 v1.0 success criteria: Module Activity (last 30d), MPR Period Coverage, Critical Path Activities, Cost Spent vs Budget (CPI proxy), Schedule Completion (SPI proxy), RFI Avg Response (last 30d), Overdue Actions. Honest v1.0 proxies — genuine EVM CPI/SPI requires per-line progress signal (v1.1). |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/tenders` | authenticated | membership | T-S6-04. Lists tenders submitted against the package, ordered by BidAmount asc. |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/tenders/{tenderId}` | authenticated | membership | T-S6-04. Cross-tenant 404 via the query filter. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/tenders` | authenticated | `TaskTeamMember+` | T-S6-04. Body `SubmitTenderRequest(bidderName, bidderOrganisation?, contactEmail?, bidAmount)`. **TenderPackage must be in Issued state** — submissions against Draft / Closed packages rejected with `ConflictException`. SubmittedAt = UtcNow. BidAmount > 0. Audit: `tender.submitted`. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/tenders/{tenderId}/withdraw` | authenticated | `TaskTeamMember+` | T-S6-04. Body `WithdrawTenderRequest(note)`. State Submitted → Withdrawn. Note required. Withdrawn is terminal — re-submission would be a new Tender row. Audit: `tender.withdrawn`. |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/evaluation-criteria` | authenticated | membership | T-S6-05. Lists criteria for the package, ordered by Type then Name. |
| POST   | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/evaluation-criteria` | authenticated | `ProjectManager+` | T-S6-05. Body `AddEvaluationCriterionRequest(name, type, weight)`. Type ∈ {Price, Quality}; Weight ∈ [0, 1]. **Only allowed in package state Draft** — once Issued the criteria are frozen so bidders see stable rules. Audit: `evaluation_criterion.added`. |
| PUT    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/evaluation-criteria/{criterionId}` | authenticated | `ProjectManager+` | T-S6-05. Body `UpdateEvaluationCriterionRequest` — partial update. Draft only. No-op rejected. Audit: `evaluation_criterion.updated` with `{ changedFields }`. |
| DELETE | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/evaluation-criteria/{criterionId}` | authenticated | `ProjectManager+` | T-S6-05. Hard-delete. Draft only. Audit: `evaluation_criterion.removed`. |
| PUT    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/tenders/{tenderId}/scores/{criterionId}` | authenticated | `InformationManager+` | T-S6-05. Body `SetEvaluationScoreRequest(score, notes?)`. Score ∈ [0, 100]. Upsert (one score per (tender, criterion); re-scoring updates in place but emits a fresh audit row). **Allowed only when TenderPackage is Issued and Tender is in Submitted / Evaluated state** (not Awarded / Rejected / Withdrawn). Audit: `evaluation_score.set`. |
| GET    | `/api/v1/projects/{projectId}/procurement/tender-packages/{packageId}/evaluation-matrix` | authenticated | membership | T-S6-05. Returns the `EvaluationMatrixDto` with per-tender weighted overall scores + per-criterion breakdown. **`IsValid = true` iff Σ weights ≈ 1.0** (epsilon 0.0001) — UI should show a "weights don't sum to 1.0" warning when false. OverallScore is null for tenders missing any score. Withdrawn tenders excluded. |

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
