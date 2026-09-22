# 4RTools Vanilla

Independent 4RTools fork for Vanilla MMO. The **Vanilla** workspace is primary;
**Original 4RTools** remains available for compatibility. The canonical repository
is public: [`andrasmining/4RToolsVanilla`](https://github.com/andrasmining/4RToolsVanilla).
Windows GitHub Actions builds and validates the application. A successful validated
`main` push publishes a new version only when its version tag is not already owned
by different source. Existing release assets are never silently replaced.

Vanilla's Autobattle controls movement and combat. This companion uses verified
read-only state and ordinary client-targeted keyboard/mouse input around it.
It does not write game memory, inject code, manipulate packets, modify game files,
or bypass Gepard. Permission to use stock 4RTools is not blanket approval of every
fork feature. Follow the server's applicable rules.

## Install and update

Get the complete portable ZIP from the latest stable GitHub Release. Extract its
entire version folder and run **4RTools-Vanilla.exe**. Keep the configuration,
`VanillaBuilds`, `x86`, `tessdata`, `tessdata-best` and runtime DLLs together.
Do not copy only the executable. Windows with .NET Framework 4.7.2 or a later 4.x
runtime is required; the app is x86 and works on x64 Windows. It requests elevation
to match elevated clients. No compiler, SDK, source tree or GitHub CLI is needed
on the user's PC. Minimizing retains the taskbar window; closing exits the app.

User data is outside version folders under `%LOCALAPPDATA%\4RTools Vanilla`.
Compatible profiles and recovery settings survive updates. Passwords use Windows
DPAPI and must be entered separately for each Windows user/machine. Packages
contain no personal credentials, profiles or mutable user-data directories.

**v0.6.72 and later resolve the public stable release and assets anonymously first.**
A missing, expired or unreadable saved token cannot block successful public access.
The normal public path uses the canonical `github.com/.../releases/latest` redirect and
canonical release-download URLs, avoiding anonymous REST API rate-limit dependency.
A bounded private-API fallback remains available through **Data & updates > UPDATE
ACCESS**. Credentials are never forwarded to release-asset CDNs or included in logs.

The installed updater matters during migration: v0.6.68/v0.6.69 still require their
previous valid update credential, even though this repository is now public. With
that credential they can download the current public release from the same repository. Without it,
use this complete portable ZIP once. v0.6.67 points at the retired repository and
also needs the portable migration. After installing v0.6.71 or later, subsequent public updates need no token.

CHECK FOR UPDATES verifies the ZIP checksum, complete payload manifest, executable
version and repository assets. It stages before disturbing automation, then stops
Temporary Actions and refuses installation while Cart/recovery owns input. Managed
files are backed up, replacements verified, and ordinary copy/verification/early
startup failure rolls back. Profiles remain outside the transaction. Backup and
journal files are retained; this is **not** a power-loss-proof installer or a full
application-health handshake. Offline use needs only a previously obtained ZIP.

## Recovery and character identity

Set the launcher, credentials, configured character slot/name, proxy and Autobattle
resume hotkey. Settings auto-save. Multiple rows may share a username; at most two
characters may be enabled simultaneously. The roster uses username plus character,
not a name alone. Fresh agreeing username observations can enrich a unique legacy
row without replacing its secrets, slot or proxy. Unknown slots remain unknown;
discovery does not guess slot one or manufacture credentials.

Startup and recovery are serialized. All eight named proxies and the subsequent
Vanilla MMO service use observed text/highlight evidence. Credentials require
verified field focus and visible username/password-mask readback. Character
selection uses an observed fifteen-card layout, unique selected frame, occupied
configured target, verified keyboard transitions and a fresh final confirmation.
It rejects empty/unknown targets instead of entering character creation. Expected
gameplay identity is checked before resuming automation. No fixed character-slot
or GAME START coordinates are used.

After real login/relog, gameplay settles for ten seconds; the configured resume
hotkey is followed by ten seconds of fresh X/Y verification. There are at most
three hotkey attempts per recovery cycle. Failed cycles continue with exponential
backoff capped at one hour. A healthy adopted client is not toggled just because
it is stationary or its sibling needs recovery. Movement proves movement, not combat.

Fresh matching **Now Logging Out.** / **Disconnected from Server.** dialogs can
trigger earlier recovery of only the affected client; exit is confirmed before
replacement. Unknown popups do not receive blind Enter. A verified **Server
Closed.(1)** outage uses a shared fixed fifteen-minute retry schedule, cleared
only by verified gameplay. **Please wait...** alone proves neither outage nor
recovery. STOP, ownership changes and relevant settings edits cancel stale input.

## Smart Teleport and health monitoring

Each character has its own Smart Teleport switch, captured hotkey and stationary
threshold, default sixty seconds. Only fresh verified X/Y establishes movement or
stationary duration. Ordinary background messages target the owned client; Enter
requires two positive observations of its warp-selection popup. Unknown fields do
not become zero, idle, no target or successful input.

The longer no-movement restart threshold defaults to 180 seconds and is configurable
from 60 to 3600 seconds. Teleport attempts do not postpone it without actual verified
movement. Restart affects only the owned failing client. Ordinary visible/adopted
clients minimize only after the user-presence grace period; a freshly recovered
client minimizes after verified movement.

## Weight / Cart

Cart and Mail have independent global and per-character switches. Mail without
Cart uses its carried-weight warning threshold. With Cart enabled, DONE requires
both verified Cart >=99% and carried weight >=50%, plus verified Autobattle STOP.
The normal Cart trigger defaults to 50% carried weight. Enabled Use/Equip/Etc
categories share configurable Inventory, Cart and dedicated STOP hotkeys; STOP
defaults to Alt+3 and is independent of the resume hotkey.

Before panel input, STOP must establish continuous fresh X/Y stillness. It has a
bounded three-attempt sequence. HP is tracked from the pre-STOP baseline: a
cumulative drop over ten percentage points, or missing fresh verified HP, aborts
Cart work. Paused-state recovery clears only recognized quantity prompts, verifies
resume movement and minimizes before a roughly sixty-second retry. Failed resume
may queue an identity-bound supervised restart. User STOP/changed ownership instead
withholds further input and preserves a manual hold when pause is possible.

Inventory tabs are identified structurally; blue Favorite styling is not selection.
Two fresh first-slot classifications are required before dragging or declaring the
tab empty. Drops use the detected Cart interior, not an empty destination slot.
Real mouse holds and bounded retries tolerate lag; delayed Cart-weight progress is
checked before retrying to avoid duplicate drags. Cart >=99% receives no more drags,
including a 9993/10000 Cart.

A category never proves item identity or unit weight. A stable offered stack count
and verified carried/Cart weights determine a conservative capacity-safe amount.
The observed numeric field must be focused and typed readback must match before
Enter. An unknown prompt/value receives no guessed quantity or blind confirmation.
This conservative calculation can leave unused space rather than overfill it.

## Temporary Actions and diagnostics

Record keyboard chords directly. Capture a target inside the selected client;
the image/identity proof is tracked and input checks current window ownership.
Older coordinate-only targets require a new capture. Capture reachable nearby
ground for SP rest. A pending targeted cast finishes first, then observed movement
and settling must precede the sit request. Movement failure/death/session change
or STOP cancels pending input. Sitting itself is not a mapped read-only state:
“sit requested” is not proof of being seated. Lack of SP recovery has a bounded stop.

Temporary Actions shares the main fleet and supervisor input lease, preventing
competing Cart/recovery input. Long explanations are in tooltips. Recovery's
Characters/Log divider is draggable. TESTS includes Smart Teleport now and Weight/
Cart clean now using the same production guards, bypassing only trigger thresholds.
Debug logs rotate at ten MB, with per-launch history; COPY DEBUG LOG includes recent
Cart/teleport events. Do not publish private incident logs or credentials.

## Engineering and evidence

Windows validation builds Debug and Release with the full isolated regression
suite, validates shipped profiles, packages and smoke-tests the exact Release
binary, checks native test-owned recovery processes, and renders mock UI layouts.
No test attaches to a game account. Publication verifies uploaded asset hashes and
runs the real new anonymous updater plus the old v0.6.69 authenticated updater
against the published release, including discovery, download and staging.

For local Windows engineering, install Visual Studio 2022 Build Tools/.NET desktop
MSBuild, PowerShell 5.1 and Git. The first restore acquires cached dependencies;
the release script supports subsequent offline builds. Version must match
`Properties/AssemblyInfo.cs`:

```powershell
.\scripts\release-local.ps1 -Version 0.6.72 -Restore
.\scripts\release-local.ps1 -Version 0.6.72 -Replace
```

`-Publish` additionally requires authenticated GitHub CLI and exact clean remote
`main`. The normal CI publisher is `scripts/publish-reviewed-release.ps1` and runs
only after its same-commit Windows validation job succeeds. `VERSION.txt` records
source identity; `SHA256SUMS.txt` covers the payload and the ZIP has an adjacent
checksum. See [release notes](RELEASE-NOTES.md) and [diagnostics](docs/vanilla-diagnostics.md).

Automated Windows, HTTP and synthetic-image results do not prove live Vanilla,
Gepard, remote-desktop timing, every skin/DPI, or the cause of previously reported
deaths. Unsupported observations fail closed. No claim of universal reliability
or anti-cheat input acceptance is made.

## Attribution

Independent of upstream 4RTools and Vanilla MMO. MIT licensing retains
`Copyright (c) 2022 4RTools`. LICENSE and third-party notices accompany every ZIP.
