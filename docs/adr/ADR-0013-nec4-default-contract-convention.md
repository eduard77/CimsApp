# ADR-0013 — NEC4 selected as v1.0 default contract convention for payment certificate semantics

**Status:** Accepted (2026-04-27, Sprint S1, T-S1-09)
**Supersedes:** —

## Context

PAFM-SD Appendix F.2 sixth-and-seventh bullets call for payment
certificate generation (T-S1-09) and Construction Act payment / pay-less
notices (originally T-S1-10, deferred to backlog `B-014` per CR-003).
Both depend on a contractual convention that determines how money flows
from client to contractor. The two conventions in play in UK
construction are:

1. **NEC4** — engineering-procurement-construction (EPC) suite, common
   on infrastructure / public-sector works. Uses "Price for Work Done
   to Date" (PWDD) cumulative semantics, retention released at
   defects-correction / practical completion, materials on site
   typically excluded from retention.
2. **JCT** — Joint Contracts Tribunal suite, common on commercial
   building works. Uses interim valuations under the Construction Act
   notice regime, retention bands that release in halves at practical
   completion / end of defects, materials on site sometimes included
   in retention (varies by JCT form).

The two conventions diverge in three places that matter to certificate
math:

- **Whether retention applies to materials on site.** NEC4 typically
  excludes; JCT typically includes.
- **Cumulative-net vs interim-net presentation.** NEC4 PWDD is
  cumulative throughout the works; JCT certificates can be presented
  as interim-period values that reconcile to a cumulative total. Both
  end up at the same cumulative net but the surface shape of a single
  certificate differs.
- **Pay Less Notice / Payment Notice timing.** Both contracts sit
  inside the Construction Act 1996 (as amended 2011) regime, but the
  notice periods, default substitutes, and pay-less calculation
  differ. This is the substance of the deferred T-S1-10 / B-014.

Sprint S1's kickoff risk #1 explicitly calls out this divergence as a
"semantics are easy to get subtly wrong" risk and prescribes the
mitigation: pick one convention as the v1.0 default, document the
choice in an ADR, defer the alternative to v1.1.

## Decision

**NEC4** is the v1.0 default contract convention for payment certificate
semantics across CIMS.

Concretely, this means the v1.0 PaymentCertificate calculation is:

```
CumulativeGross    = CumulativeValuation
                   + IncludedVariationsAmount
                   + CumulativeMaterialsOnSite
RetentionBase      = CumulativeValuation + IncludedVariationsAmount
                                            (materials excluded — NEC4)
RetentionAmount    = RetentionBase × (RetentionPercent / 100)
CumulativeNet      = CumulativeGross − RetentionAmount
PreviouslyCertified = Σ AmountDue from prior Issued certificates on this project
AmountDue          = CumulativeNet − PreviouslyCertified
```

`IncludedVariationsAmount` is snapshotted at issue time as the sum of
`EstimatedCostImpact` over all Variations on the project that are in
state `Approved` at that moment. Variations approved AFTER a
certificate is issued land in the next certificate's snapshot — they
do not retroactively change the issued certificate.

JCT-flavoured certificates are NOT supported in v1.0. Construction
Act notices are NOT supported in v1.0 (deferred to B-014, recoupled
when an external customer engagement makes the compliance gap material
— see CR-003).

## Consequences

- The PaymentCertificate calculation is hard-wired to the NEC4
  convention. A project does not carry a `ContractType` flag and the
  service does not branch on one. Adding JCT later means either a
  dedicated `JctPaymentCertificate` service or a `ContractType` flag
  on `Project` that switches the math — that decision belongs in a
  follow-up ADR if and when JCT is required.
- The retention rate (`RetentionPercent`) lives on the certificate
  itself rather than on `Project`. Real NEC4 contracts pin retention
  in the Contract Data, so a project-level field would be more
  faithful to the contract structure. The certificate-level field is
  a v1.0 simplification — promoting retention to `Project` is a
  v1.1 / v1.2 refinement that does not require a contract-convention
  ADR.
- Materials-on-site amount is user-entered, cumulative, manually
  assessed. A future task could derive it from a materials-tracking
  module if one is built; out of S1 scope.
- Valuation (PWDD) is user-entered, cumulative, manually assessed.
  Auto-derivation from a CBS progress signal is the same v1.1
  candidate that blocks the EVM PV / EV service-integration deferred
  in T-S1-07.
- Construction Act notice flows (B-014) are JCT- and NEC4-aware —
  when revisiting the deferral, both conventions need notice support
  even if only NEC4 covers the certificate math.

## Alternatives considered

- **Both NEC4 and JCT in v1.0.** Rejected: doubles the cost-domain
  surface area for a sprint that is already CR-003-trimmed; the user's
  pilot project context is NEC4-shaped; JCT can land when a real
  JCT-shaped engagement appears.
- **Generic "valuation + retention" without convention.** Rejected as
  a false economy — every cost convention has corner-case math that a
  generic implementation either gets subtly wrong or balloons into
  configuration that is itself a mini-DSL.
- **Defer the entire payment certificate task.** Rejected: a v1.0
  pilot construction project needs interim payment plumbing, even if
  the surrounding notice flows are deferred. Issuing a certificate is
  the minimum payment artefact.
