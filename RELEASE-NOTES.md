# 4RTools Vanilla 0.6.72

## Public updater and release-pipeline hardening

- Public update discovery now resolves the stable tag from the canonical
  `github.com/.../releases/latest` redirect first and downloads exact release
  assets through canonical public release URLs. This avoids anonymous REST API
  rate-limit dependency on shared IPs and does not consult saved credentials.
- The authenticated GitHub API path remains a bounded fallback for compatibility.
  Credentials are still never forwarded to release-asset CDN hosts.
- Published-update verification now accepts the canonical public download path,
  unwraps asynchronous updater failures into actionable diagnostics, and still
  exercises the private-era v0.6.69 updater with authenticated API access.
- The release publisher now handles missing version tags safely under strict
  PowerShell mode. Every engineering PowerShell script is parser-validated before
  builds, preventing malformed publication helpers from reaching the release job.
  Publication remains gated on the exact clean `main` commit, uploaded asset hash
  verification, and real updater discovery/download/staging.

## Recovery and character selection

- Repair service-title recognition at the edge of narrow dialogs. Match the named
  proxy among all eight offered routes and verify the second Vanilla MMO service.
- Use the observed character-card layout, unique selected card, and occupied-slot
  evidence before navigation and confirmation. Reject empty/unknown targets rather
  than opening character creation. Recheck expected gameplay identity before resume.
- Preserve v0.6.69 server-outage detection, shared 15-minute retry timing, cancellation,
  and isolation of healthy clients.

## Cart maintenance and input ownership

- Stop attempting transfers once the verified cart reaches the 99% maintenance
  threshold. DONE still requires both cart >=99% and carried weight >=50%, with
  verified Autobattle STOP. Cart and mail switches remain independent.
- A category is no longer treated as proof of item identity or unit weight. Read a
  stable quantity dialog, calculate a conservative capacity-safe amount from its
  offered stack and verified carried/cart weights, focus the observed field, and
  verify the value before confirmation. Verify actual weight progress after drops.
- Use the verified mouse drag path with bounded holds and cancellation throughout.
  Three-attempt lag handling and approximately 60-second maintenance retries remain.
- Guard stopped cart work with fresh HP observations. Cumulative loss over ten
  percentage points aborts cart work. Clear only positively recognized quantity
  prompts, verify resumed movement, close panels where safe, minimize, and retry.
  Failed paused-state recovery can queue only the owned client's supervised restart;
  unknown ownership never authorizes input or closing another client.
- Temporary Actions now shares the real fleet monitor and recovery supervisor.
  Its input ownership blocks competing cart/recovery work rather than locking a
  separate, unused supervisor.

## Temporary Actions

- Record keyboard chords directly instead of restricting actions to dropdown keys.
- Capture and track the target within the selected client; verify foreground, hit
  ownership, and cancellation before ordinary macro-compatible mouse input.
- Finish an outstanding targeted cast, then verify a small move before requesting
  sit for SP rest. Do not label an unobserved movement as successful; bound retries
  and stop on invalid identity, death, or cancellation. Sitting itself is not a
  mapped read-only state, so a sit request is not independently proven seating.
- Compact controls with additional explanations in tooltips. Existing coordinate-only
  targets need one new capture so the new image/identity proof can be stored.

## Public updates and rollback

- Read public release metadata and assets anonymously first. Absent, corrupt, or
  stale stored tokens do not block successful public access. A bounded private
  fallback remains available and never forwards credentials to download CDNs.
- Require the portable ZIP checksum, complete payload manifest, executable version,
  and expected repository assets before staging.
- Before replacement, stop temporary actions and require cart/recovery quiescence.
  Back up managed destination files, verify replacement bytes, and roll back on
  copy/verification/early-start failure. User profiles remain outside the managed
  payload. This is not a power-loss-proof installer or a full application-health
  handshake; retained backup/journal evidence must not be mistaken for either.

## Validation and scope

Publication is gated on Windows Debug and Release builds and isolated regression
suites, portable package/OCR smoke checks, native test-owned recovery processes,
mock UI geometry/rendering, and exact clean-source provenance. Publication verifies
real release bytes and the real anonymous updater's discovery/download/staging
path, plus a v0.6.69 authenticated-client upgrade to this release. The verification
artifacts record the tested commit and outcomes; no game accounts or live desktop
credentials are used.

Hosted tests are not live Vanilla/Gepard/RDP gameplay. No claim is made that all
reported deaths have an established cause, that every skin/animation/DPI variant
is recognized, or that game protection will accept every ordinary input request.
Unknown or ambiguous observations fail closed rather than clicking guessed targets.

The existing v0.6.68/v0.6.69 private-era updater can use its valid update credential
against the same now-public repository. A client with no usable credential may
need this portable package once; v0.6.72 and later public updates need no token.
Pre-migration v0.6.67 points at the retired repository and also needs the portable
migration. Extract the complete package; do not copy only the executable.
