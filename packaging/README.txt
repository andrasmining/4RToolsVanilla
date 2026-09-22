4RTools Vanilla
==============
Independent 4RTools fork; not an official upstream or Vanilla MMO release.
Copyright (c) 2022 4RTools. See LICENSE and THIRD-PARTY-NOTICES.txt.

Starting and preserving settings
-------------------------------
Extract the complete version folder and run 4RTools-Vanilla.exe. Keep its .config,
VanillaBuilds, x86, tessdata, tessdata-best and all runtime DLLs together. Do not
copy only the executable. Windows with .NET Framework 4.7.2 or a later 4.x runtime
is required; the application is x86 and supports x64 Windows. It requests elevation
to match elevated clients. No compiler, SDK, source tree or GitHub CLI is required.
Minimizing keeps the taskbar window; closing exits the application.

Persistent data is under %LOCALAPPDATA%\4RTools Vanilla, outside release folders.
Compatible profiles and recovery settings survive updates. Passwords use Windows
DPAPI and must be entered separately per Windows user/machine. Packages contain
no private credentials, profiles or mutable user-data directories.

Public updates
--------------
The canonical repository is now public: andrasmining/4RToolsVanilla.
Windows GitHub Actions builds, validates and publishes portable packages.
CHECK FOR UPDATES downloads a prebuilt binary; it never builds on your PC.

v0.6.72 and later resolve the public stable release/assets anonymously first. No token is needed for successful public access. The normal public path resolves
the canonical github.com latest-release redirect and direct release-download URLs,
avoiding anonymous REST API rate-limit dependency. Missing, expired or unreadable
saved credentials do not block it. UPDATE ACCESS retains a bounded private fallback;
credentials never follow redirects to download CDNs or enter logs/profile exports.

Installed v0.6.68/v0.6.69 clients still require their old valid update credential.
With it, they can upgrade from the same now-public repository. Without it, extract
this complete portable ZIP once. v0.6.67 points at the retired repository and also
needs the portable migration. Existing per-user settings remain in place.

The updater verifies ZIP checksum, complete manifest, repository assets and binary
version. Staging precedes interruption of automation; active Cart/recovery blocks
installation. Managed destination files are backed up before replacement; copy,
verification or early-start failure rolls back. User profiles are not replaced.
Backups/journals are retained, but this is not a power-loss-proof transaction or
a full application-health handshake. Offline use only needs an obtained ZIP.

Recovery and identity
---------------------
Set launcher, credentials, character slot/name, proxy and Autobattle resume hotkey.
Settings auto-save. Many character rows may be stored; at most two can be enabled.
Rows use username plus character identity. Discovery does not guess unknown slots,
passwords or proxies. Existing configured slots/secrets remain preserved.

Startup/recovery is sequential. Named services require observed text/highlights;
credentials require verified focus and readback. Character selection observes its
15-card layout and unique selected frame. Empty/unknown targets receive no final
Enter; expected gameplay identity is checked before resume. No fixed character-slot
or GAME START coordinates are used. Supported evidence may still fail on an
unrecognized skin, scaling, animation or remote-desktop capture.

Actual login/relog settles for ten seconds, then attempts the configured resume
hotkey up to three times with fresh X/Y movement verification. Failed recovery
cycles retry with exponential backoff capped at one hour. Healthy siblings are
not toggled/restarted. Known disconnect/logout dialogs can trigger earlier recovery
with two fresh matching observations. Unknown popups receive no blind Enter.
A verified Server Closed.(1) outage uses a shared fixed fifteen-minute retry schedule.
Please wait... alone proves neither outage nor successful recovery.

Smart Teleport defaults to sixty seconds per enabled character and sends ordinary
background messages to the owned client. Enter requires a positively observed warp
popup. The longer restart threshold defaults to 180 seconds, configurable 60-3600;
teleport attempts do not reset it without actual verified movement. Only the owned
failing client is replaced. STOP/identity changes cancel stale input.

Weight and Cart
---------------
Cart and Mail are independently switchable globally and per character. Mail-only
mode uses its warning threshold. Cart DONE requires verified Cart >=99%, carried
weight >=50% and verified Autobattle STOP. Normal Cart triggering defaults to 50%.
STOP defaults to Alt+3 and is separate from the configured resume hotkey.

STOP must establish continuous fresh X/Y stillness before panel input. The HP guard
uses the pre-STOP baseline: a cumulative drop over ten percentage points or missing
fresh HP aborts Cart work. Only recognized quantity prompts are cleared before
verified resume, minimization and roughly sixty-second retry. A failed resume may
queue an identity-bound supervised restart. User STOP/changed ownership withholds
further input and retains a manual hold when pause may have occurred.

Tabs are recognized structurally; blue Favorite styling does not mean selected.
Two fresh first-slot observations authorize a drag or an empty-tab transition.
Drops use the detected Cart interior, not an empty slot. Real drag holds and bounded
retries tolerate lag; delayed Cart progress is checked before duplicate input.
At Cart >=99%, no more transfer is attempted (including 9993/10000).

A category is not an item identity/unit weight. Read the stable offered stack count,
calculate a conservative amount from verified weights/capacity, focus the observed
field and verify the typed value before Enter. Unknown prompts or values receive
no guessed input. Conservative quantities can leave some space unused.

Temporary Actions, UI and logs
------------------------------
Record action and sit/stand chords directly. Capture a target within the selected
client; the captured image and identity are revalidated before ordinary mouse input.
Old coordinate-only targets need a new capture. Capture reachable nearby ground
for SP rest. Finish a pending targeted cast, then observe a short move settling
before requesting sit. Unverified movement/death/identity changes/STOP cancel input.
Sitting itself is not mapped: sit requested is not proof of seating. Lack of SP
recovery has a bounded stop. Temporary Actions shares the main fleet/supervisor
input lease with Cart and recovery.

The Characters/Log divider is draggable. TESTS includes Smart Teleport now and
Weight/Cart clean now with production guards. Per-launch debug histories rotate
at ten MB; COPY DEBUG LOG includes recent Cart/teleport action summaries. Do not
publish personal incident logs, credentials or screenshots with private details.

Validation and limits
---------------------
VERSION.txt records version, architecture and exact source commit. SHA256SUMS.txt
covers payload files; the release ZIP has an adjacent .sha256 file. RELEASE-NOTES.md
distinguishes implemented fixes, Windows tests and real-world limitations.

CI covers Debug/Release regressions, package/OCR smoke checks, native test-owned
process lifecycle, and isolated mock UI rendering. Published updater probes use
real new anonymous and old authenticated discovery/download/staging paths without
installing on a user's machine or launching gameplay. This is not live Vanilla,
Gepard or remote-desktop testing, proof of every skin/DPI, or a diagnosis of all
previously reported deaths.

Game memory remains read-only. No injections, packets, game-file changes or Gepard
bypasses are used. Unknown state is not reinterpreted as successful gameplay/input.
