# Training walkthroughs

Three role-based scripts for v1.0 pilot training. Each is
markdown + numbered steps; record screencasts following these
scripts (B-124 / v1.1) when video production is required.

- [SuperAdmin / OrgAdmin walkthrough](superadmin-walkthrough.md)
  — bootstrap a tenant, invite a PM, verify isolation. ~15 min.
- [Project Manager walkthrough](projectmanager-walkthrough.md)
  — create a project, drive a CDE document through Published,
  raise + respond to an RFI, work the risk register. ~20 min.
- [Site User walkthrough](siteuser-walkthrough.md) — mobile-shaped:
  log a diary, raise an NCR, transition through to Closed. ~10 min.

## Recording videos (post-S21)

The walkthrough scripts are commit-ready. Production of training
videos is a v1.1 obligation (B-124). Recommended approach:

1. Use a screencast tool (OBS, Loom, ScreenPal).
2. Step through each walkthrough verbatim. The "Expected"
   blocks are the cues for the narration.
3. For the Site User walkthrough specifically, use a mobile
   device emulator at 375px width (browser dev tools) so the
   responsive UI behaviour is visible.
4. Aim for 3-5 minute videos per role — most users won't watch
   longer.

## Maintaining the walkthroughs

The scripts reference v1.0 surfaces. When v1.1 lands changes,
update the walkthroughs in lock-step:

- New page → new step
- Removed feature → step crossed out with a sprint reference
- UX changes → screenshot + step text updated

The walkthrough docs live in `docs/training/` because they're
operator-onboarding material; user-facing module docs live in
`docs/user/`.
