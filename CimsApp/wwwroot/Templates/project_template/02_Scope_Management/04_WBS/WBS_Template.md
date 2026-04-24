# WORK BREAKDOWN STRUCTURE (WBS)

**Document Reference:** [PRJ-CODE]-WBS-001
**Revision:** P01
**CDE Status:** Shared · Suitability: S3

---

## 1. WBS PRINCIPLES

- Decomposed to the **work package level** — each can be estimated, scheduled, and assigned to a single owner
- **100% rule** — the WBS represents 100% of project scope; no more, no less
- Each element has a **unique code** following the hierarchy
- **Deliverable-oriented** — decomposed around outputs, not activities

---

## 2. WBS STRUCTURE (Construction Example)

```
1.0 PROJECT [Project Name]
│
├── 1.1 PROJECT MANAGEMENT
│   ├── 1.1.1 Planning & Controls
│   ├── 1.1.2 Reporting
│   ├── 1.1.3 Quality Management
│   └── 1.1.4 Risk Management
│
├── 1.2 DESIGN
│   ├── 1.2.1 Architectural
│   ├── 1.2.2 Structural
│   ├── 1.2.3 MEP
│   ├── 1.2.4 Civil / External Works
│   └── 1.2.5 BIM Coordination
│
├── 1.3 PROCUREMENT
│   ├── 1.3.1 Main Contractor
│   ├── 1.3.2 Specialist Subcontractors
│   ├── 1.3.3 Long-Lead Items
│   └── 1.3.4 Materials & Equipment
│
├── 1.4 ENABLING WORKS
│   ├── 1.4.1 Site Survey
│   ├── 1.4.2 Demolition
│   ├── 1.4.3 Utility Diversions
│   ├── 1.4.4 Site Setup & Hoarding
│   └── 1.4.5 Temporary Services
│
├── 1.5 SUBSTRUCTURE
│   ├── 1.5.1 Excavation
│   ├── 1.5.2 Foundations
│   ├── 1.5.3 Ground Slab
│   ├── 1.5.4 Basement Works
│   └── 1.5.5 Drainage Below Slab
│
├── 1.6 SUPERSTRUCTURE
│   ├── 1.6.1 Frame (concrete / steel)
│   ├── 1.6.2 Upper Floors
│   ├── 1.6.3 Roof Structure
│   ├── 1.6.4 Stairs & Ramps
│   └── 1.6.5 External Walls
│
├── 1.7 BUILDING ENVELOPE
│   ├── 1.7.1 Cladding / Facade
│   ├── 1.7.2 Roof Coverings
│   ├── 1.7.3 Windows & Curtain Walling
│   ├── 1.7.4 External Doors
│   └── 1.7.5 Waterproofing
│
├── 1.8 INTERNAL FIT-OUT
│   ├── 1.8.1 Internal Walls & Partitions
│   ├── 1.8.2 Internal Doors
│   ├── 1.8.3 Floor Finishes
│   ├── 1.8.4 Wall Finishes
│   ├── 1.8.5 Ceiling Finishes
│   └── 1.8.6 Fittings, Furnishings, Equipment (FF&E)
│
├── 1.9 MEP SERVICES
│   ├── 1.9.1 Mechanical (HVAC)
│   ├── 1.9.2 Electrical
│   ├── 1.9.3 Plumbing & Public Health
│   ├── 1.9.4 Fire Protection
│   ├── 1.9.5 BMS / Controls
│   └── 1.9.6 Lifts & Conveyance
│
├── 1.10 EXTERNAL WORKS
│   ├── 1.10.1 Site Drainage
│   ├── 1.10.2 Hardscape & Paving
│   ├── 1.10.3 Soft Landscaping
│   ├── 1.10.4 Boundary Treatments
│   └── 1.10.5 External Lighting
│
├── 1.11 COMMISSIONING
│   ├── 1.11.1 Systems Testing
│   ├── 1.11.2 Balancing & Regulation
│   ├── 1.11.3 Witness Testing
│   └── 1.11.4 Integrated Systems Test
│
└── 1.12 HANDOVER & CLOSEOUT
    ├── 1.12.1 As-Built Documentation
    ├── 1.12.2 O&M Manuals
    ├── 1.12.3 Training
    ├── 1.12.4 Warranties
    └── 1.12.5 Final Accounts
```

---

## 3. WBS DICTIONARY

*One entry per WBS code. This is the authoritative definition of the work.*

### 1.5.2 — Foundations

| Field | Value |
|---|---|
| **WBS Code** | 1.5.2 |
| **Name** | Foundations |
| **Description** | Design, fabrication, and installation of all building foundations including pile caps, ground beams, and pad foundations. |
| **Scope** | All foundation elements shown on drawings S-200 to S-250 |
| **Exclusions** | Ground improvement; excavation (see 1.5.1) |
| **Acceptance Criteria** | ITP-005 passed; concrete strength ≥ [X] MPa |
| **Schedule** | Start: [date]  End: [date] |
| **Budget** | £[amount] |
| **Responsible** | [Subcontractor] / [Site Engineer] |
| **Risks** | R-012 (ground conditions), R-023 (weather) |
| **Dependencies** | Predecessor: 1.5.1 (Excavation). Successor: 1.5.3 (Ground Slab) |

*Repeat for every work package.*

---

## 4. ASSOCIATED BASELINES

The WBS is the foundation for:
- **Schedule baseline** (activities rolled up to WBS)
- **Cost baseline** (budgets allocated to WBS codes)
- **Control accounts** (where EVM is measured)
