# Repository operating policy

Read this file at the start of every task in this repository. It applies to the
coding agent and every delegated agent. Later explicit user instructions take
precedence; update this file when they establish a new long-term repository
policy.

## Role and responsibility

The coding agent is the hands-on engineer and sole code implementer. The user
does not edit code or perform development work. For requested features, fixes,
refactors, diagnostics, automation, UI, memory/state reading, packaging, tests,
builds, releases, and repository cleanup, inspect the repository, implement the
work, test it, review it, commit it, push it, and verify the push yourself.

Do not substitute a plan, pseudocode, suggested commands, TODO list, or directions
for the completed work. Never ask the user to edit C# or JSON, locate methods,
paste code, change project files, compile, package, configure developer tooling,
discover memory offsets, or use Cheat Engine. Instructions for future engineers
may be documented, but must not become work the user has to finish.

Ask the user only when an external fact or unavoidable in-game interaction is
required and cannot be obtained otherwise. Request one simple, specific action
at a time, such as "Please move exactly one cell to the right." Then continue
the technical investigation yourself.

## Inspect before changing anything

The current repository is the source of truth. Do not rely on conversation
descriptions when the files now differ. At the beginning of a task:

1. Run `git status`; inspect the current branch, tracking branch, configured
   remotes, recent commits, staged changes, and unstaged/untracked work.
2. Find applicable `AGENTS.md` files and additional contributor instructions.
3. Read the current files to be changed and understand their callers,
   dependencies, existing behavior, and local modifications.
4. Fetch/pull and reconcile remote changes when appropriate, preserving all
   existing work. Never merge or switch branches blindly over local changes.

Existing modifications may belong to the user or earlier agent sessions. Treat
them as valuable. Before changing or deleting existing work, understand its
purpose and whether it is still used, and preserve useful functionality. Do not
overwrite newer work, discard experiments that are still needed, or remove
changes merely because they are inconvenient.

## Final integration and branch cleanup

Completed work belongs in remote `main`, not a leftover working branch. Verify
that `main` contains the final tested commit, then delete completed task branches
locally and remotely. Remove obsolete temporary workflows and patch payloads.
The normal finished state is only `main`; preserve valid concurrent work before
integrating or removing an older task branch. Never delete release tags.

A genuine external blocker may leave one clearly identified working branch.
Resume it on the next pass, solve or replace the failed approach, integrate the
result into `main`, and delete it. Do not accumulate unfinished branches or hide
a failed implementation by abandoning it on another branch.

## Mandatory Git workflow

All meaningful completed work must be committed. All commits must be pushed to
the configured remote. Completed implementation must not remain only in the
local working tree. Apply this workflow throughout the task:

1. Inspect status, branch, repository, and remotes.
2. Fetch and reconcile remote changes when appropriate.
3. Make a focused, coherent change.
4. Run the relevant validation and fix failures.
5. Review `git diff`, deliberately stage the intended files, and review
   `git diff --staged`, including new files.
6. Commit with a concise, descriptive message.
7. Verify the repository, current branch, and intended push remote; run
   `git push` or explicitly push the appropriate current branch.
8. Verify that the remote branch contains the committed SHA.
9. Continue with the next logical piece of work.

Prefer coherent incremental commits, such as diagnostics infrastructure, a
memory resolver, a state model, a rule, a UI, tests, packaging, or a live-validation
fix. Avoid both meaningless line-by-line commits and an enormous final commit
containing unrelated changes. Each commit should build when reasonably possible.

Example messages:

- `feat: add Vanilla read-only diagnostics`
- `feat: add smart teleport automation`
- `feat: add SP recovery sequence`
- `fix: validate target state before teleporting`
- `build: add portable release packaging`
- `test: cover automation cooldown logic`

Never commit secrets, credentials, personal absolute paths, unrelated junk,
temporary memory dumps, or generated debugging artifacts unless an artifact is
intentionally required for the repository.

## Branches, remote changes, and push failures

Inspect the existing branch structure first. Continue on the current branch if
it is already the intended working branch. Create a clearly named development
branch only when appropriate; do not create unnecessary branches or commit to
an unrelated branch. Never switch branches in a way that loses existing work.

If the remote advances, fetch and safely rebase or merge as appropriate,
preserve both sets of work, resolve conflicts carefully, rerun relevant tests,
and then push. Do not casually rewrite shared history. Never use
`git reset --hard`, `git clean -fd`, destructive checkout, or force push to make
problems disappear. A force push requires an explicit user instruction and a
clearly safe reason; it is not part of the normal workflow.

Pushing is part of completion. Do not infer success from a local commit or claim
a push succeeded without checking the remote. If authentication, permissions,
branch protection, or remote configuration prevents pushing:

- Preserve the work in local commits.
- Report the exact blocker, current branch, and commit SHA.
- Continue independent local engineering when possible, subject to the initial
  policy gate below, and retry pushing when the blocker is resolved.
- Clearly identify the task as not fully pushed until verification succeeds.

## Initial policy gate and first commit

Before any further feature implementation after introducing this policy,
`AGENTS.md` must exist, be reviewed, be committed, and be successfully pushed to
the configured remote branch. This initial gate remains in force if its push
fails; the general permission to continue local work after later push failures
does not override it.

For the first policy commit:

1. Review this file and run `git status`.
2. Stage only `AGENTS.md`, unless another existing change is intentionally part
   of that commit. Preserve all other uncommitted work.
3. Commit with exactly `chore: add repository agent workflow`.
4. Push the current branch to its configured remote.
5. Verify that the remote branch contains the commit before further feature work.

## Implementation quality and autonomy

Make focused production-quality changes. Prefer clear abstractions, reusable
logic, explicit validation, useful diagnostics, deterministic behavior, and
robust error handling. Preserve working upstream behavior and avoid unnecessary
application redesign.

Avoid temporary hacks, duplicate implementations, scattered magic values,
abandoned experiments, unnecessary architectural rewrites, TODO-only work, and
placeholder features presented as finished. Evolve a necessary prototype into
production code or remove it when it is no longer needed before completion.

## Product direction

The Vanilla workspace is now the primary product surface. Keep it first and use
it for recovery, live read-only client state, automation, temporary actions,
diagnostics, updates, and future Vanilla-specific features. Up to two Vanilla
clients should be visible and manageable without switching to the legacy UI.
The original 4RTools interface remains available only as a secondary compatibility
tab; preserve useful stock code and reuse proven memory/input components, but do
not make new Vanilla workflows depend on the legacy single-client selector.
Diagnostics and discovery support address mapping and verification. Selecting a
process or reading bytes does not by itself prove gameplay semantics.

If the requirement is clear, inspect, implement, test, commit, push, and continue
without asking after each small change. Work until the requested deliverable is
actually complete. Do not stop merely because one desired field cannot be
discovered: investigate other permitted reliable signals that can satisfy the
same high-level requirement, without circumventing a blocked read or action.

## Mandatory end-to-end completion and release

For implementation tasks, the default deliverable is a usable, integrated,
published release unless the user explicitly requests source-only work. A plan,
local edit, commit, push, pull request, queued workflow, green compile, or draft
release is NOT completion. Own implementation, tests, integration, versioning,
packaging, publication, and verification in the current session. Inspect failed
jobs, fix their causes, rerun the existing gates, and verify the final non-draft
release, tag/source SHA, downloadable assets, checksums, and update discovery.
Do not end with running CI, instructions for the user to build/release, or a
promise of future/background work. Respect branch protection and permissions.
For a genuine tool/permission/platform blocker, preserve completed work and
report exactly what failed, what was completed, and what remains unverified.
Never fabricate test, deployment, release, or in-game success.

When asked to update project instructions first, provide the copyable block
within the requested character limit before implementation, then persist the
enduring policy here without deleting unrelated valid rules. This automation
repository is `andrasmining/4RTools`; the archived `andrasmining/cinder-index`
market dashboard is a different project and must not receive autobattle changes.

## Post-login Autobattle verification and steady-state recovery

Automatic Autobattle/slave hotkeys are authorized only for a client that 4RTools
itself has freshly launched/restarted/relogged. Adopting, restoring, maximizing,
focusing or observing an already-running healthy client must NEVER arm or send an
automatic resume hotkey. Manual TESTS -> Resume hotkey remains an explicit diagnostic.

After a restarted/relogged client reaches the expected username + character with
fresh verified X/Y/map/living HP, wait the configured post-login settle (currently
10 seconds), then run the shared bounded movement-recovery verifier. Each of at most
three cycles sends the configured Autobattle/Resume hotkey, observes fresh X/Y for
up to 10 seconds, and only if the character is still stationary attempts the saved
verified Smart Teleport flow; after that teleport attempt, observe X/Y for up to
another 10 seconds. Stop immediately on any verified X or Y movement: once movement
is seen, do not teleport, do not continue the current 10-second wait, and do not send
another recovery input. Smart Teleport still authorizes Enter only after its expected
warp popup is positively detected and cleared. A missing/unconfigured teleport hotkey
means that phase is skipped safely, never replaced by blind input. After all three
input cycles, continue passive X/Y observation until a hard 180-second recovery
deadline; only then may ordinary restart/relogin escalation begin for continued
stationarity. Missing/unverified/stale state is not zero and is not movement; transient
Loading after a verified warp authorizes no new input until fresh gameplay state returns.

During normal Online supervision, Smart Teleport is the first stationary self-heal.
Do not send steady-state Autobattle wakeup hotkeys. Track fresh verified X/Y only.
The persisted no-movement restart threshold defaults to 180 seconds and is configurable
from 60 to 3600 seconds. Smart Teleport defaults to 60 seconds per character, so the
default restart threshold leaves room for repeated teleport attempts before escalation.
If fresh verified movement still has not occurred by the restart threshold, restart
only that affected client regardless of generic visual classification. A healthy
sibling remains untouched and dual recovery remains globally serialized.

Exact freshly confirmed `Now Logging Out.` or `Disconnected from Server.` dialogs
are stronger evidence than a generic X/Y stall and may trigger replacement immediately
after two fresh matching captures. Likewise, a stable return to the login/service shell
after confirmed gameplay is a recovery event rather than a reason to wait for the
movement threshold. Unknown popups and ambiguous captures authorize no close/input.

Failed close/launch/login/restart cycles retry indefinitely with exponential backoff
using the configured base delay, doubling to a maximum one-hour interval. Do not impose
a finite client-restart terminal budget. Retries continue until verified recovery or
explicit STOP/configuration/client replacement. Every replacement again uses the
post-login settle and shared three-cycle Autobattle/teleport movement verifier with the 180-second recovery deadline.

## Terminal dialogs and sequential replacement

Treat verified `Now Logging Out.` and `Disconnected from Server.` dialogs as
terminal client states requiring close and replacement, including an assigned
existing client encountered during START. Confirm with fresh matching visual
observations. A generic white/modal rectangle, stale frame, failed capture or
unknown message cannot authorize closing a client or pressing Enter.

If one client fails, leave healthy siblings alone. If both fail, hold the same
global recovery lease through closing the first client, confirmed process exit,
replacement launch, login, verified Autobattle movement and minimization. Only
then may the next client close/restart. Failed attempts release their lease into
bounded backoff; missing configuration must not strand other accounts. Bind every
close/termination to the observed process identity and current runtime/operation.
Never treat an inaccessible process or failed exit query as a confirmed exit.
STOP, settings changes, client replacement and disposal cancel delayed actions.
Cover both exact dialogs, single-client and dual-client failure, native visual
matching, unknown-message rejection, close/exit ordering and cancellation tests.

## Required validation

Test every meaningful change as far as available tooling permits. Appropriate
checks include restore/build, full Release builds, unit/integration tests,
syntax checks, configuration serialization, timers/cooldowns, state transitions,
runtime diagnostics, live Vanilla validation, and portable-package launch tests.
For documentation-only changes, review content and diff rather than claiming
application behavior was tested.

Inspect the current solution and build tooling before running it. The project
currently targets .NET Framework 4.7.2; `scripts/build.ps1`, when present, rebuilds
the solution in Release and Debug and runs the offline diagnostics tests. The
agent must run the relevant checks itself. Record baseline warnings separately
from newly introduced issues.

A successful compile alone does not establish correct behavior. Distinguish
static validation, unit/integration validation, and actual live Vanilla
validation in documentation and reports. If a test fails, investigate and fix
the underlying issue, rerun it, and only then commit/push the completed change.

## Vanilla and Gepard boundaries

The user-provided Vanilla project context cites the following documented features:

- "4R Tools Supported"
- "Gepard 3.0 Protection"
- "24/7 Auto-Attack + Slave System"

This project may extend 4RTools for Vanilla-specific automation, but stock 4RTools
support must not be treated as permission for arbitrary modified binaries or
additional behavior. Keep the distinction between stock behavior and local
extensions explicit.

Do not bypass, disable, patch, evade, inject into, hide from, spoof, or otherwise
interfere with Gepard or any game security mechanism. Do not inject code into
Vanilla, manipulate packets, or communicate directly using the game server
protocol. Do not alter Vanilla installation files or account settings.

Prefer read-only client observation plus ordinary keyboard/mouse input through
the mechanisms already used by 4RTools. Use Vanilla's own Autobattle for movement
and combat; do not replace it with a monster scanner or bot pathfinding. If a
read or action is blocked by Gepard, stop that line of investigation and report
the exact operation and error. Do not use alternate memory-access paths, token
manipulation, handle duplication, injection, or protection changes as a workaround.

For live checks, use a test character when possible. Do not purchase, delete,
drop, or trade items, and do not automate aggressively. Before enabling a live
automated action, prove the corresponding read-only state signal in diagnostics.
Successful metadata queries or executable-header reads do not verify gameplay
fields or authorize live actions.

## Memory and state safety

Vanilla-specific discovery and automation must remain read-only with respect to
game memory. Do not use `WriteProcessMemory` or any equivalent to change HP, SP,
coordinates, targets, inventory, skill state, or any other game state. Memory is
observable input for decisions, never an action destination.

Keep state discovery separate from action/rule logic. Centralize Vanilla offsets,
pointer chains, and any permitted signatures/configuration. Validate addresses
and pointer widths against the actual process architecture; do not infer bitness
from an AnyCPU configuration name or silently truncate an address.

Unknown fields are optional and must not appear as valid zero, false, no target,
or idle state. Fail safely, retain useful errors, stop on denied/failed reads,
and invalidate stale observations. Do not silently swallow new diagnostic
failures or retry around protection. Record how candidate fields were verified,
including relog, map change, client restart, and PC restart where relevant.

## Local launch and portable releases

For this user's development and live-validation sessions, launch the application
from an executable inside this GitHub repository with this repository as its
working directory. This is the user's established local environment requirement.
Do not alter antivirus/security settings yourself. Keep build caches and logs
out of source control; their storage location is separate from application launch.

Run the Vanilla product with administrator privileges (`requireAdministrator`) so
its integrity level matches the elevated Vanilla MMO clients it observes. This is
the intentional product runtime model. A failed read must still remain read-only:
do not use elevation as a gateway to injection, memory writes, token manipulation,
handle duplication, protection changes, or alternate access paths.

For release-oriented tasks, done means a usable artifact as well as source code.
When requested, produce `dist/<release-folder>/` and
`dist/<portable-release>.zip`. The user must be able to copy/unzip the package
onto another Windows PC and run it without Visual Studio, Git, NuGet, or source
code. Handle dependencies and clearly document any required Windows/.NET runtime.

The agent owns building, dependency handling, packaging, checksums, release notes,
and validation, including package launch testing where possible. Retain the
upstream MIT license and all required copyright/permission notices, including
`Copyright (c) 2022 4RTools`, and include them with distributed artifacts.

## Release/version and persistent-data policy

Use conservative pre-1.0 versioning while the Vanilla integration is still being
stabilized live. Prefer patch releases for iterative fixes and UX/reliability
improvements, minor releases for coherent larger milestones, and do not approach
or declare 1.0 merely because several internal changes landed. A 1.0 release
requires explicit product readiness and substantial live validation.

User configuration must survive application upgrades. Keep writable user data
outside versioned release folders under the stable per-user application-data
root, migrate compatible older schemas automatically, and preserve old data when
a safe migration cannot be proven. Release ZIPs must not contain mutable user
profile/recovery/log directories. Backwards-incompatible schema changes require
an explicit migration or a clear, intentional reset path rather than silent
corruption or accidental default replacement.

Normal end-user operation should be one 4RTools application/window. Integrate
Vanilla recovery, automation, diagnostics, data paths, and update status into the
main Vanilla workspace instead of creating competing top-level manager windows.
The main 4RTools window must never use tray-only hiding as its minimized state:
when the user minimizes it, keep it visible as a normal Windows taskbar item and
preserve the minimized window state. An existing legacy tray icon may remain for
compatibility, but it must not replace the taskbar window or cause minimize-to-tray
behavior. The normal user-visible window states are active/restored or taskbar-
minimized.

Keep CHECK FOR UPDATES and the current version/update status together in a
separate right-aligned status area of the top Vanilla header. Keep ordinary
workspace/debug actions on the left so update/version information reads as status
rather than as part of the main action cluster.

The Vanilla UI is Full-HD-first and resolution-responsive. Treat ordinary
1920x1080-class desktops and RDP sessions around 1980x1020 as primary layouts;
do not reserve vertical space on the assumption that a 2K display is available.
Use compact headers and client cards, proportional table/list columns, and
responsive panel breakpoints: broad screens may place related status/log panels
side-by-side, while narrower screens should stack them. The Recovery Characters/Log
boundary must be user-draggable at runtime; default around 2:1, but let the user
shrink Characters when more log width is useful. Every workspace and embedded tool
must keep critical controls reachable through sensible scrolling
when the available width or height is smaller. Prefer reflow and content-aware
sizing over fixed tall sections or fixed column widths that clip important data.
Keep regression coverage for the main layout breakpoints and portable smoke test.

Recovery settings are auto-save UI: do not require a separate Save button. The
launcher path, account edits, recovery/watchdog switches, and per-account proxy
selection must persist automatically and surface a brief success/error indication.
Launch arguments are intentionally hidden/empty. The managed-client count is not
an independent setting: derive it from the number of enabled account rows (up to
two). Proxy selection is account-specific and must follow the account being
started or recovered, not one shared global proxy control.

During the current live-hardening phase, global debug logging is ON by default.
Keep one Debug log checkbox and one COPY DEBUG LOG action in the left action area
of the top Vanilla header. Every normal 4RTools process startup must create a
brand-new process-owned debug file named with its startup timestamp and PID
(`debug-yyyyMMdd-HHmmss-fff-pPID.log`). Never reuse or append to a shared live
`debug.log`; overlapping updater/old/new processes must therefore be unable to
mix sessions. Legacy fixed `debug.log` is migrated/archived once. Rotation for a
long-running session creates timestamp/PID session part files, never a cross-session
append. Do not concatenate old debug sessions back into COPY DEBUG LOG: the copied
bundle should use the current process's debug session plus current
application/startup/recovery/memory/update logs, while summary counts may scan
recent archives. Every application-managed `.log` file is hard capped at 10 MiB;
rotate before exceeding that cap and split any oversized legacy/migrated log into
bounded timestamped parts during startup migration. Keep bounded history rather
than allowing log families to grow without limit.
Debug mode should record process/PID changes, stage/visual transitions, launcher
evidence, focus attempts, clicks/keys/hotkeys, and errors with timestamps while
never logging passwords or typed secret contents. Keep dense operational UIs compact: explanatory prose belongs in hover tooltips/info glyphs rather than persistent multi-line labels unless the text is an active warning/error/status the user must see without hovering. Remove or reduce this temporary
always-on default only when the user explicitly asks after hardening is complete.

## Multi-client reconnect and outage policy

Vanilla supports up to two managed clients on this PC, but automated recovery UI
input must be globally serialized. Never run launcher/proxy/login/server/character
selection or resume-hotkey recovery workflows for two clients in parallel. One
client owns the recovery lease from launcher start through confirmed gameplay and
bounded autobattle movement verification; other clients remain queued until that client is
Online or its attempt fails/backoffs. Existing healthy clients must not be
disturbed merely because another client is recovering.

While the reconnect supervisor is running, healthy managed Vanilla clients are
minimized by default and left running. Initial startup and recovery use the same
serialized policy: finish one client through confirmed gameplay and verified autobattle movement, minimize it, then allow the next queued client to start. If one
client disconnects later, keep every other healthy client minimized and untouched,
recover only the affected client, and minimize that client again after successful
return to gameplay. If a healthy supervised client is manually restored/on-screen,
the supervisor should minimize it again on its next observation cycle.

Cold startup is stricter than merely holding a nominal recovery flag. Do not
start the periodic multi-client supervisor until the startup orchestrator has
finished the current enabled client through gameplay, verified autobattle movement, and
confirmed minimization. Only then may the next missing enabled account launch.
If focus or visual recognition fails during cold startup, leave the current client
running, stop/fail closed, and do not close it or advance to a later account.

Before every automated click, key, credential entry, server/character action, or
resume hotkey, bring the intended Vanilla window to the foreground and verify it
actually owns focus. Drive startup primarily from observed expected UI states
(proxy list, login controls, server dialog, character-ready surface, gameplay)
with short human-like settle delays after recognition. Avoid long blind sleeps
when the next expected screen can be detected; poll the expected state and act
shortly after stable recognition instead.

After gameplay has been confirmed, a detected disconnect/logged-out modal or a
return to the login/service shell is a recovery event: close that affected
Vanilla client and recover it through the normal launcher path. If a client exits
outright, queue the same recovery path. Do not rely on an artificial close/restart
test as the primary validation of outages; keep the manual network-drop test for
real disconnect behavior.

Failed unattended recovery attempts must use exponential backoff rather than a
hot retry loop. The current policy starts from the configured base delay (30
seconds by default), doubles after each failed attempt, and caps the interval at
one hour. A confirmed successful return to gameplay resets the failure/backoff
state. Keep retry/backoff state visible in logs/status and preserve fail-closed
visual recognition: when a required UI region cannot be identified confidently,
do not guess a click location or type credentials.

Resolution/DPI robustness is a product requirement. Prefer current-client visual
recognition, client-relative normalized geometry, and structural layout detection
over absolute desktop pixels. Tests for recognized login/proxy/server surfaces
must cover multiple resolutions and softened/resampled rendering. Do not claim
arbitrary future UI changes are guaranteed; unknown layouts must stop safely and
produce useful captures/logs.

## Resolution-agnostic character selection

Never select a Vanilla character or GAME START by fixed/normalized grid coordinates.
Character-select automation must use focus-verified keyboard navigation from a
clamped known origin, driven by the configured one-based slot, and the existing
read-only username + character-name identity must verify the resulting gameplay
before any Autobattle hotkey is sent. Character-name evidence is preferred when
available, but unavailable selection-screen memory must not be guessed or replaced
with OCR/coordinate assumptions. Unknown or contradictory identity fails closed.
Keep this path resolution/DPI agnostic and cover every target slot from every
possible initial selection in deterministic tests. Any future visual interaction
must detect the actual control/region first and click inside that detected region;
do not reintroduce hard-coded character-grid or GAME START coordinates.

## Cleanliness, documentation, and final reporting

Before finalizing substantial work, review for abandoned experiments, temporary
files, memory dumps, unintended logs, personal paths, and credentials/secrets.
Remove only understood, unneeded artifacts without losing user work. Update
`.gitignore` where appropriate and retain genuinely useful diagnostics.

Update documentation when behavior changes materially. Release notes should
concisely describe changes, tests, actual live tests, and known limitations.
Documentation must not require the user to finish the engineering work.

After completing a task, report:

- What changed and the important implementation decisions.
- Tests performed and what was actually validated live.
- Resulting commit SHA(s), branch, and verified push status.
- Release artifact locations, when applicable.
- Any actual remaining limitation or exact blocker.

Do not present unverified behavior, unfinished features, or unpushed commits as
completed work.


## Primary autofarming movement watchdog

For enabled supervised autofarming clients, fresh verified X/Y movement is the primary
steady-state health signal. Missing, unreadable, unverified and stale coordinates remain
invalid, not zero, and do not reset the elapsed stall. Use monotonic elapsed time,
fingerprinted read-only observations and per-client/session baselines. Credit verified
intermediate movement even when the latest coordinates return to the previous values.
Do not accrue the watchdog during startup/recovery ownership.

Smart Teleport is the first-line stationary recovery for characters that enable it.
Its default per-character timeout is 60 seconds. Steady-state supervision itself sends
no Autobattle hotkey. When the persisted no-movement restart threshold is reached
(default 180 seconds; allowed 60-3600), restart only the affected client under the
global recovery lease. The restart timer must not be reset merely because Smart
Teleport briefly owned its serialized background-input lease; only verified movement,
session/map replacement, STOP/configuration/client replacement, or actual recovery
lifecycle resets/re-baselines it.

After a real launch/relog/replacement reaches verified gameplay, wait 10 seconds and
send that row's configured Autobattle/slave hotkey through the shared ResumeHotkey
verifier. Observe fresh verified X/Y for 10 seconds and retry at most three total
hotkey attempts. If movement still cannot be established, the affected recovery cycle
fails into exponential retry/backoff; do not stop permanently after three client
restarts. Backoff continues indefinitely and caps at one hour between attempts.

Recognized terminal logout/disconnect dialogs may trigger replacement immediately after
two fresh matching observations, without waiting for the X/Y threshold. Unknown popups
receive no blind input. Keep one global recovery lease from close through verified
exit, relaunch, login, post-login Autobattle movement verification and minimization;
simultaneous failures remain sequential and healthy siblings stay untouched. Verify
process/session/character ownership before input/close. STOP, configuration or client
replacement cancels stale work. Never reopen a failed reader to evade a blocked memory
read.

Manual TESTS must expose safe actions for the selected character to run Smart Teleport
now and Weight/Cart cleaning now through the exact production paths. These tests may
bypass only the automatic trigger condition (idle/weight threshold); they must not
bypass identity, input-lease, popup, quantity-dialog, cancellation or ownership guards.

Global debug logging must record structured start/result events for every automatic or
manual Smart Teleport and Weight/Cart maintenance action. COPY DEBUG LOG must include a
last-24-hours action summary with teleport attempts/completions and Cart attempts/
completions/items moved, plus the detailed timestamped events.

## Character roster and identity discovery

The recovery table is a CHARACTER roster, not one row per login account. Its
first columns are Enabled, Cart, Mail, Smart Teleport, Smart Teleport seconds and
Smart Teleport hotkey. Then show Description, Username, Slot, Character name and
the existing resume/password/proxy/PID/status fields. Cart and Mail are independent
per-character policies; detailed shared thresholds, hotkeys, category choices and
SMTP transport stay on the Weight tab. Several rows may share
one username; at most two rows may be enabled. Preserve row IDs, encrypted
passwords and proxy associations. Keep historical JSON names for migration.

Discover running characters at startup and as fresh verified observations arrive.
Add missing characters once, disabled. Never guess passwords, proxy, username or
slot. Match processes by independently verified character identity, not PID order,
description or username alone. Ambiguous matches remain unbound. Only populate
fields supported by verified memory mappings. The current profile includes user-supplied login username copies at module offsets
0xD343F8 and 0xD39159; use both in shared state/diagnostics and require agreement
before identity use. Character slots are not yet mapped and stay unknown/editable. A name
verified after this tool's own successful configured login can enrich that exact
row, never transfer credentials to another character. Passive discovery must not
cancel recovery; edits, disabling, character/session replacement, STOP and disposal
must invalidate stale ownership. Cover migration, deduplication, multiple chars
per username, null slots, enabled limits and ownership in regression/native UI tests.


## Username + character-name identity

The logical unique key is the complete username + character-name pair. Use it
consistently in discovery, validation, process binding, removal suppression and
confirmation; never substitute name-only or PID order. Keep persistent row GUIDs
for credential/proxy compatibility. Missing/invalid username observations defer
automatic row creation rather than creating new username-less rows.

Enrich a configured legacy username-only row in place when one compatible row
and one fresh observed character for that username identify it unambiguously.
A missing memory slot is not a reason to duplicate that row. Preserve description,
slot, password, proxy, enabled state and ID. Multiple candidate rows/characters
remain unresolved, never guessed. Only untouched empty auto-discovery duplicates
may be folded into that configured row; user-configured rows must survive.
Record screenshot provenance accurately; automated tests do not prove independent
live relog/restart validation of a supplied address.

## VPS update delivery is mandatory

The user's VPS is updated through the application's GitHub-release updater.
Every implementation task must finish with a tested public stable release,
explicitly marked Latest, unless the user explicitly requests source-only work.
A chat ZIP, Actions artifact, branch, commit, PR or queued release is not an
alternative deliverable. Do not stop at those intermediate states.

Verify the public releases/latest response and both expected portable ZIP and
checksum assets. Exercise the previously published application's real updater
against the endpoint, verify downloads and source identity, integrate all work
into main and remove completed task branches. Diagnose and repair failures;
replace unsuitable approaches instead of handing development back to the user.
State a genuine unavoidable external blocker accurately only after exhausting
practical authorized alternatives. Never claim publication or VPS installation
without evidence; testing update discovery is not installing on the user's VPS.

## Weight / Cart maintenance

Weight-triggered Cart maintenance must remain read-only-memory + ordinary UI input. Reuse the shared fleet reader for verified carried weight, Cart weight and movement; do not inspect/write inventory item memory and never write game memory. For the verified Vanilla fingerprint, Cart current/max weight are module-relative UInt32 fields at 0xD34B3C / 0xD34B40; MaxCartWeight is accepted only when it equals the fixed Vanilla Cart capacity 10000 and CurrentCartWeight is within 0..10000. Inventory/Cart/category interaction must be dynamically detected from the current client image, never hard-coded desktop coordinates or fixed/percentage category-tab coordinates. Do not add randomized cursor positions or randomized timing for anti-cheat/bot-detection evasion; robustness must come from detected control bounds, deterministic safe interior retry points, and bounded waits driven by observed UI state. Before opening Inventory/Cart, the affected character must own the serialized input lease and the dedicated configurable Weight Autobattle STOP hotkey (default Alt+3) must be positively verified from fresh read-only X/Y. One STOP send is not enough: after each send require at least 5 continuous seconds of unchanged verified X/Y inside a 10-second attempt window. Any movement resets that 5-second stillness window. If STOP is not verified, resend only on the next 10-second attempt boundary, for at most 3 STOP attempts. Inventory/Cart input is forbidden until this stationary proof succeeds. The first fresh HP percentage observed for the STOP sequence is also the damage baseline. During STOP verification and for the entire period Autobattle is intentionally stopped—including panel detection, category selection, quantity handling, waits and mouse-down/drag/drop holds—keep polling fresh verified HP. If HP falls by more than 10 percentage points from that baseline, or fresh verified HP becomes unavailable after STOP, abort Cart work immediately: restore the normal input cancellation mode, resume Autobattle through the shared verified movement routine, close any open Inventory/Cart panels best-effort, minimize the client, and retry Cart maintenance after about 60 seconds. The >10-point HP drop is cumulative across STOP retries and Cart work; do not re-baseline after each retry. If all 3 STOP attempts remain moving/unverified without HP danger, do not open either panel; release Cart ownership and retry maintenance later rather than interacting with a moving client. Apply the same STOP+HP verifier before arming the final farming-complete hold, and delay another completion-stop attempt for about 60 seconds after a failed/dangerous stop. Never reuse the character ResumeHotkey as the STOP command. Detect the actual Use/Equip/Etc tab rail from panel/slot/separator structure and positively verify the selected tab. Within a category, only the first inventory slot is authoritative because Vanilla compacts items to the front: classify that first slot against a fresh visually detected empty-slot reference, require two consecutive Occupied captures before dragging, and two consecutive Empty captures before advancing. A Cart transfer may drop anywhere inside the positively detected Cart item body; vary only through a small deterministic sequence of safely inset panel-interior points, never stochastic anti-detection coordinates, and never require the destination slot itself to be empty. Cart drags must keep deliberate source/drop timing for lag tolerance, but cursor travel itself should be quick: hold the source before mouse-down, move through a short deterministic path, hold briefly over the Cart before mouse-up, then allow a post-release settle. Current policy is approximately 250 ms source hold, 6 movement steps at ~35 ms each, 300 ms destination hold and 500 ms post-release settle; do not slow cursor travel back to the earlier ~1.2-second movement path unless live evidence requires it. If a drag produces no verified Cart-weight increase, do not fail after one attempt: wait, re-check for delayed Cart progress, re-detect the first slot, and retry the same transfer through up to three bounded slow drag attempts. Delayed Cart-weight evidence must suppress a duplicate retry. If all three attempts still show no verified Cart-weight increase, treat that as a transient UI/RDP/client-lag deferral rather than a manual-hold condition: close the panels, resume Autobattle through the normal verified movement path, minimize, and automatically retry Cart maintenance after about 60 seconds. Manual hold remains for genuinely unsafe/ambiguous states such as lost ownership or an unresolved late quantity dialog. `Enter` is authorized only after a freshly and positively detected quantity dialog; quantity-one transfers have no dialog and receive no Enter. Weight/Cart progress must be visible live in the Recovery log as well as the global debug log. Pure transfer non-progress follows the bounded three-attempt -> resume -> one-minute retry path above. Genuinely ambiguous first-slot/modal state, lost UI ownership, incoherent weight evidence, or another state where safe resume/retry cannot be established fails closed with Autobattle OFF and a manual hold for only that character until explicitly cleared. Successful maintenance resumes through the shared verified per-character ResumeHotkey/X-Y path and then minimizes. At or above 75% Cart weight, never blind-transfer an unknown item/stack: use only verified unit-weight rules plus the positively detected quantity dialog and verify Cart-weight increase after every transfer. The current verified farming rules are Mastela Fruit in Use = 3 weight and Peco Feather in Etc = 1 weight; Equip remains unknown and therefore stops precision filling from the 75% precision threshold. Fill toward but never beyond 10000. Do not send a Cart-full-only mail; the user now requires BOTH capacity limits before a Cart-mode notification. Farming-complete STOP does not require exact 100%: once verified Cart weight is at least 99% and carried weight is at least 50%, send the dedicated Autobattle STOP command, hold that character out of automatic recovery, and send a DONE mail through the same saved SMTP transport. This completed-farming hold is cleared only by the explicit Weight/Cart hold-clear action. Per-character Cart and Mail activation are independent. The Weight-tab Cart/e-mail masters and transport/threshold/hotkey settings are shared configuration, but neither may authorize work for a character whose matching row switch is off. Cart OFF + Mail ON means carried-weight warning mail at the configured threshold. Cart ON + Mail ON suppresses both carried-only and Cart-only warning mail and sends one combined DONE notification (Cart >=99% AND carried >=50%) after a verified completion STOP. Cart ON + Mail OFF performs maintenance with no mail. Cart OFF + Mail OFF performs neither. Preserve legacy combined WeightEnabled JSON by treating missing split fields as that legacy value; once split fields are explicitly saved they take precedence and identity/discovery must never rewrite them.

## Smart Teleport character policy

Integrated Smart Teleport is configured per saved username + character row, never by manually selecting a process. Store an independent enable flag, live-captured hotkey and stationary timeout (default 60 seconds) on the character profile and automatically bind it to the fresh verified running identity/PID. Trigger only from fresh verified read-only X/Y remaining unchanged; do not require target/combat/casting evidence. Unknown/stale/unverified coordinates, movement, map/session replacement or ownership changes reset/defer the timer.

Teleport input may use ordinary targeted Windows background messages to the verified owned Vanilla window; do not foreground/restore it as a fallback. After the configured hotkey, send Enter only after positively recognizing the expected Select an Area to Warp popup with the first option selected, and verify the modal clears. No popup means no Enter. Keep this work serialized with recovery and Weight/Cart input and cancellable on STOP/settings/client/character replacement.

## Combined-capacity notification completion (2026-09-20)

In active Cart mode, notify only after Cart >=99% AND carried weight >=50% plus a verified completion STOP. Neither capacity alone sends mail. Without active Cart maintenance, Mail uses the configured carried-weight warning threshold. Re-check current shared and per-character Mail settings immediately before dispatch so queued work cannot use an obsolete enabled switch. Reserve a single in-flight DONE send per character. Mail-only profile edits must preserve Cart/recovery input ownership; Cart, identity, enabled-state or input-setting edits retain normal cancellation. Use EffectiveCartMaintenanceEnabled in every Cart guard, never the legacy WeightEnabled field directly.

## Launcher update reset exception (2026-09-20)

Always start through Vanilla Launcher.exe / patcher.exe, never bypass updates with
a direct game launch. A supplied game path may resolve to its adjacent launcher;
missing launcher means configuration failure, not a fallback.

The user authorizes a narrow exception to healthy-sibling isolation: a verified
launcher update progress screen with no GAME START and no progress/status change
for 60 continuous seconds may close same-installation patchers and BOTH game
clients. Missing Start alone, failed/unknown captures and changing progress never
authorize this. Preflight paths plus creation times twice, freshly reconfirm the
screen, retain the global recovery lease, close patchers then games and confirm
every exit plus a final empty process snapshot before restarting the launcher.
Use the existing bounded creation-time-pinned Windows close protocol. Metadata
uses limited query only, with no alternate access after failure.

Allow one reset per launch invocation, with a ten-minute supervisor cooldown.
Recognized changing update progress may extend waiting to a ten-minute hard bound.
STOP, settings/generation, runtime/PID/session replacement cancel pending closes
and delayed restart. Preserve disabled characters and Cart/manual/completion holds.
Restore enabled characters sequentially through login, verified movement and
minimization; cold startup revisits earlier closed rows before supervision starts.
Ordinary recovery still leaves healthy siblings untouched. Log the evidence and
confirmed exits; do not claim a proven file lock from a frozen update screen alone.
