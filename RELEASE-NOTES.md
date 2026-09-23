# 4RTools Vanilla 0.6.74

## Sequential login recognition

- Fix the cold-start/recovery failure that could occur immediately after a proxy
  such as **Tokyo** had been correctly selected: the login form recognizer could
  reject the native `Vanilla MMO` service combobox before username/password
  entry, so the safety guard deliberately typed nothing.
- Keep the same fail-closed credential policy, but make the fixed service-label
  fallback tolerant of DPI scaling, wide native comboboxes and softened/RDP
  rendering. Trailing dropdown chrome is separated from the observed label and
  proportionally scaled glyphs are compared without using configured credentials
  as recognition hints.
- Allow a bounded ~15-second login-form appearance window after proxy selection
  instead of the previous ~6-second window. No credential input is sent until the
  named login form, separate username/password controls and intended field focus
  are positively verified.
- Add non-secret credential-stage diagnostics when login-form recognition is
  blocked or later recovers. Login captures and credential contents are still not
  written to disk or logs.
- Add regression coverage for a wide, high-DPI, bicubic-softened/RDP-style
  `Vanilla MMO` combobox and similarly spelled negative identities.

## Validation and update path

Windows GitHub Actions builds Debug and Release, runs the complete isolated
regression suite, validates shipped build profiles, packages/smoke-tests the exact
Release build, runs native test-owned recovery checks and isolated mock UI checks.
The release publisher then verifies the exact main SHA, release ZIP/checksum,
public `releases/latest`, anonymous updater discovery/download/staging and the
legacy migration path before leaving the release stable.

Live Vanilla/Gepard/RDP gameplay is not claimed by this release; the reported
failure was diagnosed from the supplied runtime log and guarded with Windows
synthetic recognition regressions.

## Migration

v0.6.73 can discover and install v0.6.74 through the public one-click updater.
