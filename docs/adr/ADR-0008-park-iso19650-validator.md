# ADR-0008 — Park Sessions 1-2 ISO 19650 filename validator until Sprint 8

**Status:** Accepted (2026-04-24, Sprint S0, Session 3)
**Supersedes:** —

## Context

Sessions 1 and 2 (before this manual was consulted) shipped a
filename validator at `CimsApp/Services/Iso19650/…` with a Blazor page
at `/tools/iso19650-validator`. The work implements 12 checks from
PAFM Appendix F.9 — which is the Definition of Done for Sprint 8
(module S8, "ISO 19650 / MIDP").

PAFM-SD Chapter 0.4 ("Integration debt") is explicit: the ISO 19650
wizard is "opened in Sprint 8. Not Sprint 1. Not Sprint 4. Not 'when
I feel like it.'" The current sprint is S0 — Multi-tenant Foundation.
The validator is therefore off-roadmap work already merged to `main`.

Three options were considered in Session 3:

1. Park the validator on `main`, stop extending it during S0–S7,
   reconcile it with the rest of the Sprint 8 module (naming wizard,
   MIDP, TIDP, CDE states, metadata per F.9) at S8 kickoff.
2. Revert the Session 1-2 commits. History is clean; tested working
   code is discarded.
3. Formally pull Sprint 8 forward via Ch 17 change control, re-plan
   subsequent sprints. Material re-sequencing, no reason given other
   than the work already exists.

## Decision

Option 1 — **park, don't extend, reconcile at S8.**

- No new features, schema changes, or integration into
  `DocumentRevision` upload during Sprints 0–7.
- If a task naturally touches `Services/Iso19650/`, treat it as scope
  drift and halt.
- Known issues documented for Sprint 8 to resolve:
  - `Services/Iso19650/Iso19650ReferenceData.cs` is a narrow subset
    of `Core/Iso19650Codes.cs`; reconciliation needed (Role / Type /
    Suitability coverage, `MD` vs `M2`/`M3`, missing `S0` in the
    validator whitelist, etc.).
  - Validator's `Numbering` check doesn't do collision detection
    across Originator × Type × Role as PAFM F.9 #3 requires — it
    only checks well-formedness plus the `0126` reserved template.
  - Validator's `Revision` check only verifies well-formedness; PAFM
    F.9 #6 requires "no skips, no repeats" which needs history.
  - Real Uniclass / IFC reference data (NBS feed) replaces the
    hard-coded placeholders in `Iso19650ReferenceData`.

## Alternatives considered

**Option 2 (revert)** — rejected. The work is tested (13 unit tests
green), published, and represents real effort that will be revisited.
Discarding violates the PAFM-SD Ch 0.1 failure mode "I under-count what
exists, treat it as throwaway, and discard hard-won progress".

**Option 3 (formal scope pull-forward)** — rejected. No operational
reason to pull S8 ahead of S0–S7. The whole point of the roadmap is
that S0 foundations enable everything after them; front-loading an
information-compliance module before multi-tenant isolation is unsound.

## Consequences

**Positive:**
- Preserves existing work without compounding the off-roadmap
  position.
- Creates a clear S8 pre-flight checklist via the known-issues list
  above.
- Honours PAFM-SD Ch 0.4 without the disruption of reverts.

**Negative:**
- The `/tools/iso19650-validator` page remains reachable during S0–S7.
  A user who finds it may treat it as production-ready. Mitigation:
  no action in S0 — the page is inside an internal tools area and
  won't be advertised. Consider a "beta" banner in S7 if retained.
- Sessions 1-2 never raised a Ch 17 change record at the time. This
  ADR retroactively documents that; no further change-register entry
  is required.

## Related

- PAFM-SD Ch 0.4 (Integration debt), Ch 17 (Change control).
- `docs/sprint-log/s0.md` Day 1 decisions.
- Session 1-2 commits `7581c54` through `faae7ed` on `main`.
