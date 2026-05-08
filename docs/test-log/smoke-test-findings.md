# Smoke Test Findings

## Findings

### BUG-003 — Client Organisation dropdown displays Guid.Empty as selected value

- **Phase / Step:** Phase 3, Step 17 (New Project dialog inspection, pre-fill)
- **Severity:** medium (cosmetic but confusing; suggests broken default-state binding)
- **Status:** open
- **Area:** projects, dialog, forms

**Repro:**
1. Click + New Project on /projects.
2. Observe Client Organisation field shows "00000000-0000-0000-0000-000000000000".

**Expected:** Empty placeholder text (e.g. "Select client organisation") until user makes a choice.
**Actual:** Raw Guid.Empty rendered as if it were a valid selection.

**Likely cause:** Field bound to Guid value with no null-handling in display template, or default initialised to Guid.Empty with no placeholder fallback.

**Acceptance criteria:**
- Empty/unset state renders placeholder text, never a Guid.
- Required-field validation on submit rejects Guid.Empty as a non-selection.
- UI test asserts placeholder visible on initial render.

---

### BUG-005 — Sector is a free-text field; should be a controlled list

- **Phase / Step:** Phase 3, Step 17
- **Severity:** medium (data quality; not a functional defect today)
- **Status:** open
- **Area:** projects, taxonomy

**Detail:** Sector accepts arbitrary free text. Per PAFM v2.0 and the future
public-sector reporting / framework-application requirements, sector must be
a controlled vocabulary so projects can be aggregated and filtered consistently.

**Acceptance criteria:**
- Sector field becomes a dropdown sourced from a Sectors lookup table.
- Initial seed list (proposal): Healthcare, Education, Defence, Pharmaceutical,
  Life Sciences, Residential, Commercial, Industrial, Civils / Infrastructure,
  Energy, Heritage. Final list to be confirmed.
- Existing free-text values migrated where possible; unmatched flagged for review.
- Admin Console (Sprint 14) edits the list; no per-project additions.

---

### TASK-003 — New Project form missing PAFM v2.0 initiation fields

- **Phase / Step:** Phase 3, Step 17
- **Severity:** n/a (scope gap, not a defect)
- **Status:** open — scope decision needed before deciding sprint placement
- **Area:** projects, initiation

**Missing fields vs PAFM v2.0:**
- Contract form (NEC4 / JCT / D&B / etc.) — controlled list
- IPA gateway alignment — controlled list (Gate 0 through Gate 5, plus N/A)
- Project board / sponsor — text or person picker
- Start date
- Target completion date

**Decision needed:**
- Add to initiation form now (one sprint), or
- Capture later in a separate "Project Setup" step post-creation, or
- Defer to Admin Console / project template (Sprint 14) so the template
  pre-populates these for all new projects of a type.

**Initial preference:** Start date, target completion, and contract form
in the initiation dialog (these aren't optional in any UK PM context).
Sponsor and IPA gateway move to a "Governance" tab that opens immediately
after project creation. Decide before this gets built.
