4RTools Vanilla
==============

Independent fork of 4RTools, not an official upstream or Vanilla MMO release.
Copyright (c) 2022 4RTools. See LICENSE and THIRD-PARTY-NOTICES.txt.

Starting the application
-----------------------
Extract the entire release folder and run 4RTools-Vanilla.exe. Keep its .config,
VanillaBuilds, x86 and tessdata folders and the bundled runtime DLLs beside it.
Windows with Microsoft .NET Framework 4.7.2
or a later 4.x runtime is required. The application targets x86, supports x64
Windows and requests administrator privileges to match elevated game clients.
No Visual Studio, Git, NuGet, SDK or source tree is needed to run the package.

Vanilla is the primary workspace. Original 4RTools remains a compatibility tab.
The redundant Vanilla Automation tab has been removed; Smart Teleport is configured
directly on each character in Recovery & relog.
Minimizing keeps the main window on the Windows taskbar; it does not hide it
exclusively in the system tray. Closing the main window exits the application.

Recovery and Autobattle
-----------------------
Set the Launcher path, then add character profiles with credentials, character
slot, per-character proxy and the resume hotkey configured for Vanilla Autobattle.
Settings auto-save; there is no separate Save button. Any number of profiles may
be stored, but at most two characters may be enabled and managed simultaneously.

START completes one client's startup before advancing to the next. After a real
login/relog/replacement reaches verified gameplay, the client settles for 10 seconds,
then the configured resume hotkey is sent. Fresh verified X/Y is checked for movement
for 10 seconds. Without movement, ownership/focus is revalidated and the hotkey is
retried, for THREE TOTAL hotkey attempts.

The three-hotkey sequence belongs to startup/recovery only. Healthy already-running
clients are adopted without toggling, and steady-state stillness does not send an
Autobattle wakeup hotkey. Smart Teleport is the first stationary self-heal. Recovery &
relog also has a no-movement restart threshold, default 180 seconds and configurable
from 60 to 3600 seconds. If no verified X/Y movement occurs by that threshold, only
the affected client is restarted.

Known Now Logging Out. / Disconnected from Server. dialogs may recover sooner after
two fresh matching captures. A stable return to the login shell after confirmed
gameplay also triggers recovery. Unknown popups receive no blind input.

Failed close/launch/login/restart cycles retry indefinitely with exponential backoff.
The delay starts from the configured base, doubles after failures and is capped at one
hour between attempts. There is no finite three-client-restart shutdown budget.
Healthy siblings remain untouched and recovery input stays globally serialized.

STOP and active configuration/client-ownership changes cancel stale input.

Automatic minimization is user-presence aware for ordinary/adopted clients: they are
left visible until at least 60 seconds visible and 60 seconds without cursor movement.
A client freshly launched/relogged by 4RTools is minimized immediately after verified
Autobattle movement.

Smart Teleport
--------------
Configure Smart Teleport in each character row. It is keyed by username + character
name and automatically follows the verified running PID; no process selector is used.
Each character stores its own enable flag, live-captured teleport hotkey and idle X/Y
timeout (60 seconds by default). Fresh verified X/Y movement resets the timer; target,
combat and casting state are not required.

At timeout, 4RTools sends the configured hotkey to that owned Vanilla window using
ordinary background Windows messages, then positively detects the Select an Area to
Warp popup before sending Enter to the selected first option. If the popup is not
recognized, Enter is never sent. Unknown/stale coordinates and ownership changes fail
closed.

Weight / Cart management
------------------------
The Weight tab can trigger ordinary UI-only Cart maintenance from verified read-only
CurrentWeight/MaxWeight. Each character row has its own Weight switch; shared Weight-tab settings apply only to rows whose Weight switch is enabled. The default trigger is 50% and is configurable. Use, Equip
and Etc inventory categories are selectable. Weight has its own configurable Autobattle
STOP hotkey (Alt+3 by default), plus Inventory and Cart hotkeys. 4RTools sends that STOP
command before opening Inventory/Cart; it never reuses the character ResumeHotkey to stop.
After cleanup it starts Autobattle again through the existing verified per-character
ResumeHotkey/X-Y path and minimizes.

Use/Equip/Etc category tabs are detected from the live Inventory panel, slot grid and
separator structure; no fixed or percentage category coordinates are used. The blue Fav
appearance is only styling, not an active-tab signal. The selected category is detected
structurally from the tab whose right edge is open into the Inventory body while inactive
tabs retain their right border. If a click is not positively confirmed, 4RTools re-detects
the rail and retries through a bounded deterministic set of safe interior points while
polling fresh visual state. For item traversal, only the first inventory slot is authoritative:
4RTools classifies it against a fresh detected empty-slot reference, requiring two consecutive
occupied captures before dragging and two consecutive empty captures before advancing. Cart
drops may land anywhere inside the detected Cart item body and rotate deterministically among
safe detected interior points; an empty destination slot is not required. For stack transfers
Enter is pressed only after a quantity dialog is positively detected. A quantity-one item has no
dialog and receives no Enter. Major Cart steps are visible in the Recovery log and global debug
log. If the UI cannot be identified safely, first-slot state stays ambiguous, the Cart rejects a
transfer, or progress cannot be verified, input stops and only that character
is held for manual Cart emptying with Autobattle left OFF. That hold survives unrelated
settings and supervisor STOP/START changes and is removed only by the explicit hold-clear
action. Memory access remains read-only; inventory state is never read/written from game memory.

Persistent configuration and updates
------------------------------------
User data is stored outside the versioned release folder under:

  %LOCALAPPDATA%\4RTools Vanilla\

Compatible old profile/settings data is migrated without deleting the original
copy. The release ZIP contains no personal profiles, recovery credentials or
mutable user-data folders. Passwords are protected with Windows DPAPI, are not
logged and must be entered separately on each Windows user/machine.

CHECK FOR UPDATES and version status are at the right of the Vanilla header.
The updater verifies the downloaded ZIP checksum and its payload manifest before
applying an update. The Data & updates page displays the actual paths in use.

Validation and integrity
------------------------
VERSION.txt records the version, architecture, source commit and build status.
RELEASE-NOTES.md distinguishes automated Windows build, regression, package and
mock-data UI validation from actual live Vanilla/Gepard gameplay testing.
SHA256SUMS.txt lists the payload hashes; the release ZIP has an adjacent .sha256
file. A passing build or mock UI test does not prove live-game behavior.

Vanilla observation remains read-only. No game-memory writes, injections,
packet manipulation, game-file changes or Gepard bypasses are performed.
Unknown observations are never reinterpreted as valid gameplay state.

Autofarming health watchdog
------------------------------
Fresh verified X/Y is the primary steady-state health signal. Smart Teleport defaults
to 60 seconds per enabled character and gets the first chance to recover ordinary
stationary gameplay. The longer restart threshold defaults to 180 seconds. Unchanged,
missing, unreadable, unverified and stale coordinates never count as movement.

Smart Teleport attempts do not reset the restart deadline unless verified movement
actually occurs. At the restart threshold only the affected client is replaced.
Replacement startup again uses the 10-second settle + up to three verified resume
hotkey attempts. Failed cycles continue through exponential backoff capped at one hour.

Both known disconnect/logout messages can trigger replacement sooner after fresh
two-sample confirmation. If both clients fail, recovery stays sequential through close,
exit confirmation, relaunch, login, movement verification and minimization. A healthy
sibling remains untouched. STOP/configuration/client replacement cancels delayed work.

Manual diagnostics and debug history
------------------------------------
The Recovery TESTS menu contains Smart Teleport now (selected) and Weight/Cart clean
now (selected). These run the production identity/ownership/visual/input paths and
bypass only the automatic idle/weight trigger.

Every automatic/manual Smart Teleport and Cart-maintenance action is timestamped in the
global debug log. COPY DEBUG LOG starts with a last-24-hours action summary showing
teleport attempts/completions and Cart attempts/completions/items moved, followed by the
detailed event trail.

Character roster
----------------
One row represents one character, not one account. Several rows may share a
username; at most two may be enabled. The Recovery Characters/Log divider is draggable so the
user can temporarily enlarge either pane. The list starts with Enabled, Weight and
Smart Teleport, then Smart Teleport seconds and hotkey. Description, Username,
Slot and Character name follow. Weight remains on/off only in this list; detailed
Weight/Cart settings are on the Weight tab. Running characters are discovered automatically
from fresh verified memory and added once, disabled. Existing secrets/proxies stay
unchanged. The unique key is username plus character name, not name alone.
The shared reader includes both supplied username addresses; agreeing values fill
the username automatically. Both values/addresses are visible in Diagnostics as
UserName and UserNameMirror. Missing/conflicting usernames do not create new rows.
A unique configured legacy username row learns its matching character name in
place, keeping its slot, password, proxy, enabled state and ID. Ambiguous matches
are not guessed. Slots remain unmapped; configured slots are preserved and unknown
slots stay blank. Discovery never guesses slot 1, passwords or proxies.

Character selection is keyboard-driven from a clamped grid origin; fixed character-slot and GAME START coordinates are not used.
