# 4RTools Vanilla

An independent fork of 4RTools for Vanilla MMO. The **Vanilla** workspace is the
primary interface; the **Original 4RTools** tab remains available for compatibility.
Development and releases now live in the private
[`andrasmining/4RToolsVanilla`](https://github.com/andrasmining/4RToolsVanilla)
repository. Git history, tags and historical release assets are preserved.
GitHub Actions is disabled; builds, validation and publication run locally.
Vanilla's own Autobattle controls movement and combat. This fork adds read-only
state observation and ordinary, client-targeted keyboard/mouse input around it.

## Portable application

Use the portable ZIP attached to the latest private GitHub Release while signed
into an account with repository access. Extract the entire
`4RTools-Vanilla-v<version>` folder and run **4RTools-Vanilla.exe**, keeping its
configuration and `VanillaBuilds` folder together. The application targets x86
and requires Windows with Microsoft .NET Framework 4.7.2 or a later 4.x runtime.
Visual Studio, Git, NuGet and the source tree are not required on the user's PC.
The application requests administrator privileges to match elevated clients.

Minimizing keeps the application on the Windows taskbar; it does not hide the
main window exclusively in the system tray. Closing the main window exits it.

User data lives under `%LOCALAPPDATA%\4RTools Vanilla`, outside versioned release
folders. Upgrades preserve compatible profiles and recovery settings. Passwords
use Windows DPAPI and must be entered separately for each Windows user/machine.
Release packages contain no personal profiles, passwords or mutable user-data
folders. See the included `README.txt`, `VERSION.txt` and `RELEASE-NOTES.md`.

### Updates and offline use

Local engineering builds produce the portable ZIP and checksums. When online,
the release script uploads those validated files directly to the private GitHub
release; GitHub does not build them. CHECK FOR UPDATES downloads the prebuilt
package with repository authentication and verifies it before installation.
End-user PCs do not download or compile the source tree.

On **Data & updates > UPDATE ACCESS**, save a GitHub fine-grained token scoped to
this repository with **Contents: Read** permission. It is protected with Windows
DPAPI for the current user/PC, kept outside profiles and logs, and can be cleared
from the same dialog. No GitHub CLI is needed with a saved token. Existing
`GH_TOKEN`/`GITHUB_TOKEN` environment credentials take priority, followed by the
saved token and then an existing GitHub CLI login. Never share tokens in logs or
release packages. Browser downloads instead use your normal GitHub sign-in.

The old public v0.6.67 application points to the old repository. Upgrade once
using the new private portable ZIP; compatible settings remain in the same
per-user data folder. Later private releases use the integrated updater.
For an offline PC, copy the complete ZIP from an authorized online PC and extract
it into a new folder. Building offline requires previously installed build tools
and cached dependencies; publishing or checking GitHub requires connectivity.

## Workspace and recovery

The compact top area shows up to two observed clients, including character name,
HP/SP, location and activity when the corresponding state is valid. Recovery &
relog, temporary actions, Weight/Cart, memory finding, diagnostics and data/update
settings share the same workspace. The former Vanilla Automation tab is removed;
Smart Teleport is configured directly per character in Recovery & relog. Debug controls stay on the left of the header;
update controls and version status stay on the right.

Any number of account profiles can be saved, with at most **two enabled clients**
at once. The **Characters / Log divider is draggable** in Recovery & relog, so the Log can be widened
temporarily without changing the default responsive layout. The character table starts with
**Enabled, Weight, Smart TP, TP sec, TP hotkey**, so each row's automation state is visible immediately. Weight is
only an on/off indicator there; detailed Weight/Cart settings stay on the Weight
tab. Recovery settings auto-save. Proxy selection belongs to each account,
including cold startup, recovery and diagnostic input. Startup and recovery are
serialized; a healthy client is not restarted or toggled merely because another
client needs recovery.

### Terminal disconnect recovery

The visual watchdog explicitly recognizes the reported **Now Logging Out.** and
**Disconnected from Server.** Message dialogs. Two fresh matching captures are
required. Only the affected client is closed; its actual process exit must be
confirmed before replacement. If both clients fail, one complete close/restart/
login/movement-verification/minimize sequence finishes before the other begins.
The same terminal check is performed for an assigned existing client during START.
Unknown popups are not dismissed with Enter or used as automatic close evidence.
Failed close/launch attempts retain diagnostics and use bounded retry backoff;
STOP, configuration changes or replaced client ownership cancel pending actions.
This depends on a readable supported dialog capture, not merely frozen HP or X/Y.

### Autofarming health watchdog

Fresh verified read-only X/Y movement is the primary steady-state health signal.
Smart Teleport is the first-line stationary self-heal for characters that enable it
(**60 seconds by default per character**). Normal Online supervision does **not** send
Autobattle wakeup hotkeys merely because a character is stationary.

Recovery & relog exposes a no-movement restart threshold, defaulting to **180 seconds**
and configurable from 60 to 3600 seconds. Unchanged, unavailable, unreadable,
unverified or stale coordinates do not reset that elapsed stall and are never
converted to zero. Smart Teleport attempts do not postpone the longer restart
deadline unless actual verified movement occurs. At the threshold, 4RTools restarts
only the affected client.

Known **Now Logging Out.** and **Disconnected from Server.** dialogs are stronger
evidence and can recover sooner after two fresh matching captures. A stable return to
the login/service shell after confirmed gameplay is also recovered without waiting for
the X/Y threshold. Recovery remains globally serialized from close through relaunch,
login, verified movement and minimization; a healthy sibling is not restarted/toggled.

Failed close/launch/login/restart attempts continue indefinitely with exponential
backoff: the configured base delay doubles up to a maximum interval of **one hour**.
There is no finite three-client-restart shutdown budget.

### Autobattle movement verification

After every actual login/relog/replacement, gameplay must first be verified. The client
then settles for **10 seconds** and the configured resume hotkey is sent. Fresh verified
X/Y is observed for **10 seconds**. Movement on either axis succeeds. Without movement,
ownership is revalidated/focused and the hotkey is sent again, for **three total hotkey
attempts**.

This three-hotkey verifier belongs to actual startup/recovery cycles; it is not the
steady-state stall response. If all three attempts fail, that recovery attempt fails
into the normal exponential retry/backoff path and later retries continue until
recovery succeeds or supervision is explicitly stopped/configuration or ownership
changes.

STOP, settings changes, replaced PIDs/sessions/characters, map changes, death, failed
reads, stale observations and lost input ownership prevent further automated input.
Movement confirms only movement, not combat.

### Manual diagnostics and overnight logging

The Recovery **TESTS** menu includes **Smart Teleport now (selected)** and
**Weight/Cart clean now (selected)**. These actions bypass only the automatic trigger
condition; they still use the selected character's verified identity/PID, shared input
lease, popup/quantity-dialog guards, cancellation and ownership checks.

Every automatic/manual Smart Teleport and Cart-maintenance action writes timestamped
structured events to the global debug log. **COPY DEBUG LOG** also includes a
last-24-hours summary with Smart Teleport attempts/completions and Weight/Cart
attempts/completions/items moved, followed by the detailed event trail.

### Smart Teleport

Smart Teleport is configured on each **username + character** row in Recovery & relog; no PID/process selection is required. Each character has its own enable switch, live-captured teleport hotkey and stationary timeout (**60 seconds by default**). The supervisor resolves the current PID from fresh verified character identity automatically.

The trigger is only fresh verified read-only X/Y movement. Target, combat and casting state are not required. Any verified coordinate change (including intermediate movement observed by the shared fleet reader) resets the idle timer; stale, missing or unverified coordinates never count as stationary.

When the timeout expires, Smart Teleport acquires the same serialized per-client input lease used by recovery/UI work and sends the configured hotkey directly to the owned Vanilla window with ordinary Windows background messages, without restoring or foregrounding the game. It then captures that same window and requires two fresh positive detections of the **Select an Area to Warp** dialog with its first choice selected before sending Enter. No recognized dialog means **no Enter**. The popup must then disappear before the action is considered complete. STOP/settings/PID/session/character replacement cancel stale work.

### Weight / Cart management

The **Weight** tab uses the verified read-only carried/max-weight fields. Each character row has its own **Weight** switch in Recovery & relog; the shared Weight-tab policy applies only to rows whose Weight switch is enabled. It can keep the existing
optional e-mail warning and can also trigger UI-only Cart maintenance at a configurable weight
percentage (50% by default). Use, Equip and Etc categories are independently selectable. The Weight
tab has a dedicated configurable **Autobattle STOP** hotkey (**Alt+3 by default**) plus Inventory
and Cart hotkeys. Cart maintenance sends that STOP command before any Inventory/Cart UI work; it
does **not** reuse the character ResumeHotkey. After cleanup, Autobattle is started again through
the character's existing verified Recovery ResumeHotkey/X-Y movement routine and then minimized.

Inventory contents are never read or modified through game memory. The Inventory **Use / Equip / Etc**
tab rail is detected from the live panel/slot/separator structure. Vanilla's blue **Fav** styling is
not treated as selection: the active category is identified structurally because its tab is open on
the right and merges into the Inventory body, while inactive tabs keep a closed vertical right
border. Category clicks target only detected tab bounds; if the first click is not positively
confirmed, 4RTools re-detects the rail and retries through a small deterministic set of safe interior
points while polling fresh UI state. Inside each category, only the **first inventory slot** is
authoritative because Vanilla compacts items to the front. 4RTools compares that first slot with a
freshly detected empty-slot reference and requires two consecutive **Occupied** captures before a
drag or two consecutive **Empty** captures before advancing. Cart drops do not need an empty
destination slot; they rotate deterministically through safe points inside the **detected Cart item
body**. There is no arbitrary screen-coordinate fallback. Stack `Enter` is sent only after a quantity
dialog is positively recognized; single-quantity transfers do not receive Enter. Every major Cart
step and each bounded category-click attempt is written live to the Recovery log and global debug
log. If a drag makes no verifiable progress, first-slot evidence stays ambiguous, or UI ownership
becomes uncertain,
4RTools stops input and holds only that character with Autobattle OFF for manual Cart emptying. The
hold survives unrelated settings and supervisor STOP/START changes and is removed only by the
explicit manual-hold clear action. Healthy siblings continue normally.

## State validity and boundaries

Shipped build profiles are matched to the executable fingerprint. The current
profile records verified HP/SP, character name, carried weight, X/Y and map
observations, plus the user-supplied corroborated username mappings. Unsupported or unknown fields are not treated as valid
zero, idle or no-target states. Target/combat/status-dependent rules remain gated
by their required evidence; enabling a UI option does not verify its game effect.

Vanilla memory access is read-only. The fork does not write game memory, inject
code, manipulate packets, modify game files or bypass Gepard. A blocked read or
action stops that operation rather than using an invasive alternate access path.
Stock 4RTools support must not be interpreted as blanket approval of every fork
feature. Current release notes distinguish implementation, automated validation
and actual live-game validation.

## Engineering and release validation

Use Visual Studio 2022 Build Tools with .NET desktop MSBuild and the x86 Visual C++
runtime redistributable files, Windows PowerShell 5.1, and Git. GitHub CLI is
required only for authenticated publication. The build script locates MSBuild
and can acquire the .NET Framework 4.7.2 reference pack during initial restore.

From a clean checkout of `main`:

```powershell
# Initial dependency restore, local validation and portable package:
.\scripts\release-local.ps1 -Version 0.6.68 -Restore

# Subsequent offline validation/package using cached dependencies:
.\scripts\release-local.ps1 -Version 0.6.68 -Replace

# Publish the locally validated release while connected and authenticated:
.\scripts\release-local.ps1 -Version 0.6.68 -Publish -Replace
```

Version must match `Properties/AssemblyInfo.cs`. Publication requires the tested
commit on remote `main`; it creates the version tag, uploads ZIP/checksum assets,
marks the stable release Latest and verifies the downloaded result. The local
package remains under `dist/4RTools-Vanilla-v<version>/` with its sibling portable
ZIP. GitHub Actions is disabled at repository level and no workflow is shipped.
`-Replace` preserves an existing local package under `dist/.previous` before
rebuilding; published release assets are never silently overwritten.

The existing Windows build scripts restore dependencies, build Debug/Release and
run the offline regression suite. Portable packaging checks x86/version metadata,
licenses, payload checksums and a relocated inert executable launch. The native
mock-data UI harness covers responsive layouts, enlarged text, large saved-account
lists, character discovery/editor behavior, legacy username migration and diagnostic
username/address rendering without observing live clients.

The local release command runs these gates before publication, then downloads
the private release assets and verifies their hashes and source identity against
the tested package. Rendering runs on a separate non-input Windows desktop that
is never displayed. Automated tests and mock UI rendering are not a live Vanilla
or Gepard gameplay test. See `RELEASE-NOTES.md` for the precise validation limits.

## Attribution and license

This fork is independent of upstream 4RTools and Vanilla MMO. The MIT license
retains `Copyright (c) 2022 4RTools`. Distributed third-party notices are included
in `packaging/THIRD-PARTY-NOTICES.txt` and every portable release.

Character selection is keyboard-driven from a clamped grid origin; fixed character-slot and GAME START coordinates are not used.
