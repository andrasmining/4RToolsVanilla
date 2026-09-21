# 4RTools Vanilla 0.6.68

## Private repository and local releases

Development has moved to **andrasmining/4RToolsVanilla**, preserving Git history,
tags and historical release assets. GitHub Actions is disabled. The local
`scripts/release-local.ps1` command builds, tests, renders mock layouts, packages
and optionally publishes directly to the private GitHub release.

The portable ZIP bundles local OCR engines, English models, native dependencies
and required licenses. End-user PCs need Windows and .NET Framework 4.7.2 or later
4.x; updates download prebuilt packages without compiling the repository.

## Private updates

CHECK FOR UPDATES authenticates private release metadata and asset downloads.
UPDATE ACCESS on Data & updates accepts a repository read token protected with
Windows DPAPI for the current user. Environment and existing GitHub CLI
credentials are also supported. Tokens stay out of logs and packages and are
never forwarded to download CDNs. ZIP checksums, payload manifests and executable
versions remain mandatory before installation.

**One-time migration:** the public v0.6.67 updater points to the old repository.
Extract this private portable ZIP and use its executable once; compatible
settings remain under the same per-user data folder. Later releases use the
private updater. Offline PCs can receive the complete ZIP from an authorized
online PC. Publishing or checking GitHub requires connectivity.

## Verified selections

- Offer Global, Manila, Singapore, Tokyo, Hong Kong, Los Angeles, Australia and
  UAE while preserving the original four saved numeric identities. Recognize the
  configured name and confirm its highlight in two fresh captures before Enter.
- Recognize **Vanilla MMO** in the server dialog. Status text such as Crowded,
  list order and guessed dialog percentages cannot select a server.
- Confirm credential-field focus, verify the exact username, and check repeated
  password masks before submission. Credential images and passwords are not
  logged or sent to an OCR service.
- Share the character selector across automatic and TESTS paths. Require a
  recognized screen, selected-slot evidence, verified keyboard transitions and
  the final target before Enter. Each input is bound to fresh window, focus and
  geometry evidence; stale captures cannot authorize an action.

## Validation and limits

Both local Debug/Release regression suites passed, covering multi-scale and softened
synthetic recognition, credential and focus failures, all initial/target
character-slot combinations, private-updater authentication and download guards,
relocated package OCR/smoke checks, test-owned native processes and the mock UI
layout matrix. All 14 private-updater tests, four native process cases and 27 mock
UI cases passed. Rendering uses a separate non-input desktop that is never shown.
The seven existing local build warnings are three framework facade version
conflicts and four unassigned test-field warnings; no new warnings were added.

No live Vanilla/Gepard/RDP validation, client observation or gameplay input was
performed. The supplied screenshots do not expose the unobscured fifteen-slot
screen, so its actual card styling and row-major numbering remain unverified.
Unknown layouts, unreadable text and ambiguous selection/focus stop safely.
Arbitrary blur and future skins are not guaranteed. Release verification does
not install the application on the user's PC or VPS.

All launcher recovery, Cart/Mail, HP safety, movement recovery, persistence and
log-rotation behavior from v0.6.67 is retained.
