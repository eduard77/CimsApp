# Schedule & Programme

S4 module — CPM-driven activity scheduling with MS-Project-style
constraints, four dependency types, baselines, and Last Planner
System (LPS) per-week boards.

## Activities

Per-project `Activity` with Code, Name, Description, Duration
(days at v1.0), Start/Finish dates, plus an MS-Project-style
constraint (ASAP default; ALAP / SNET / SNLT / FNET / FNLT / MSO
/ MFO available). Predecessor relationships via
`ActivityDependency` rows: FS (Finish-to-Start, default) / SS /
FF / SF, with optional Lag (decimal days; negative = lead).

## CPM solver

`Core/Cpm.cs` runs the standard forward + backward pass:
Early Start / Early Finish / Late Start / Late Finish / Total
Float. Critical path = float ≤ 0. The solver respects activity
constraints during the passes.

## Baselines

`ScheduleBaseline` snapshots the activity set at a point in time
for variance tracking. Per-project there can be many; the
"current" baseline drives the variance reports.

## Last Planner System (LPS)

`/projects/{id}/lps` — per-week board of committed vs completed
activities. Reasons-for-non-completion enumerate (Resource
Unavailability, Material Delay, Weather, Design Change,
Prerequisite Incomplete, Scope Change, Access Issue, Other);
rolled into LPS health metrics on the dashboard.

## MS Project import / export

`Core/MsProjectXml.cs` reads / writes the .xml interchange
format. Imports map MSP activity IDs → Genera PM Activity rows;
predecessor links translate to ActivityDependency rows.

## Common gotchas

- **Hour-precision activities** are v1.1 — DurationUnit.Day is
  the only v1.0 shape (no calendar / non-working-day rules yet).
- **Resource leveling** is not in v1.0 — CPM only.
