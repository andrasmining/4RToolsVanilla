# Vanilla diagnostics engineering record

For the current mapping inventory and discovery/validation fixes from
2026-09-13, see the [discovery session record](vanilla-discovery-2026-09-13.md).
The [actual-product follow-up](vanilla-product-access-2026-09-13.md) corrects the
scope of that external-host result and records the normal product launch tests.
The sections below retain historical results and do not imply those reads
succeeded again.

## Current companion validation

The companion now includes the production rule engine, UI, portable profiles,
exact build-profile matching and guarded ordinary window input. Release and
Debug pass 88 offline test groups. See [release notes](../RELEASE-NOTES.md) for
the final portable package, checksums and current limitations.

The packaged EXE passed a relocated startup test and a separate production
read-only session check against a restarted Vanilla process with the same
fingerprint. The visible packaged application was also launched successfully.
An external shell attempt to click its elevated Connect button through ordinary
window messages failed with Win32 error 5 (access denied); that UI-control path
was stopped. This was a message to the companion, not gameplay input or evidence
of a Gepard rejection. No stronger access or security change was attempted.

## Continued runtime discovery

On 2026-09-06 the current client executable was fingerprinted as SHA-256
`7eb420579690bd2f5c81b42fa69888cb3d144486d3c275a19073f5216698b3ef`,
PE machine `0x014c` (x86), image timestamp `1648704405`, image size `15839232`.
The main image's writable `.data` section was read successfully (3,989,092 bytes)
using exact, bounded reads. Plausible HP/maxHP/SP/maxSP quartets are candidates
only; matching displayed values across changes is still required. No game code,
security module, heap actor data, or packets were scanned or modified.

The reusable developer command `--vanilla-discover <pid> --output <new-path>`
records executable identity. `--scan` additionally scans only writable,
non-executable `.data`/`.bss` sections of the main image, with a 16 MiB total
bound and immediate stop on any read failure. `--stats HP,MaxHP,SP,MaxSP` narrows
matches to a known visible comparison. Only up to 512 candidates are exported,
so an unfiltered candidate list is not exhaustive. No raw dump is written.
`--capture <new-png-path>` uses normal PrintWindow; `--restore-window` explicitly
restores a minimized window first. Current normal window captures return black
pixels even after restoration, so visible character values have been requested
as external comparison evidence. No capture/security workaround was attempted.

Both configurations passed 25 offline test groups after this discovery change,
including stable PE fingerprinting and malformed-image rejection.

The diagnostics window provides optional read-only state observations and a deterministic demo that works without Vanilla. The companion's Smart Teleport, stuck detection, SP recovery and additional rules are implemented and tested with synthetic observations. No Vanilla gameplay field has been verified yet, so live actions remain unavailable.

Normal startup opens the companion. Original 4RTools features remain available separately; the upstream executable updater is excluded from the fork. Diagnostics and snapshot entry points create no stock macro workers. A successfully built or launched modified executable is not proof of accepted gameplay inputs.

## Build and test

From the repository root in PowerShell:

```powershell
& .\scripts\build.ps1 -VanillaRelease
```

The script finds Visual Studio MSBuild with `vswhere`, restores `packages.config`, rebuilds the entire solution in Release and Debug, and runs the corresponding `Tests/bin/<configuration>/Vanilla.Diagnostics.Tests.exe` console test runner. Use `-Configuration Release` for a single configuration or `-MSBuildPath <path>` to select an existing MSBuild installation. A failed build or test stops the script.

The target remains .NET Framework 4.7.2. If its reference assemblies are absent, the script downloads the pinned Microsoft reference package into `%LOCALAPPDATA%\4RTools-Engineering\net472` and supplies `TargetFrameworkRootPath` to MSBuild. This requires no machine-wide installation or retargeting. NuGet restore and an uncached reference package require network access. The reference package supplies compilation assets; the application still needs a compatible installed .NET Framework runtime. [Microsoft reference package 1.0.3](https://www.nuget.org/packages/Microsoft.NETFramework.ReferenceAssemblies.net472/1.0.3)

Build and test logs are saved beneath `%LOCALAPPDATA%\4RTools-Engineering\builds\<timestamp>`. `-CacheRoot <path>` changes this root. Compiled outputs stay in the existing ignored `bin`/`obj` directories, including those under `Tests`. The script copies the unchanged MIT `LICENSE` next to application and test executables. It does not start the game or the 4RTools GUI.

The script was executed for the completed diagnostics implementation on 2026-09-06. Both Release and Debug rebuilt successfully with **0 errors and the same 6 baseline warnings**. Each configuration passed **24 offline test groups**, covering optional observations, timestamp transitions, immutable status snapshots, module relocation, both pointer widths, null/overflow ranges, denied/partial reads, process exit/mismatch, disposal, map/profile/snapshot serialization, poll bounds, and synthetic demo provenance. Logs are retained at `%LOCALAPPDATA%\4RTools-Engineering\builds\20260906-230040-495`. The tests use fake memory and in-memory JSON; they do not open game processes or edit user profiles.

## Baseline before source changes

The unchanged source baseline was commit `0cad2f6`. Its Release build targets `.NETFramework,Version=v4.7.2` and declares `AnyCPU`. Inspection of the emitted PE/CLR flags found `I386`, `ILOnly`, and `Preferred32Bit`: on this Windows machine the application runs as a 32-bit process. The configuration name alone must not be used to infer runtime pointer width.

The first baseline build failed with `MSB3644` because the .NET Framework 4.7.2 reference assemblies were missing. Restoring packages with MSBuild `/restore /p:RestorePackagesConfig=true` and supplying the official per-user reference pack produced a successful unmodified Release build: **0 errors, 6 warnings**.

| Baseline warning | Location / affected assembly |
| --- | --- |
| MSB3277 | Conflicting `System.Runtime` versions |
| MSB3277 | Conflicting `System.IO` versions |
| MSB3277 | Conflicting `System.Diagnostics.Tracing` versions |
| CS0618 | Obsolete `Thread.Suspend`, `Utils/_4RThread.cs:46` |
| CS0168 | Unused exception variable, `Forms/ClientUpdaterForm.cs:41` |
| CS0414 | Assigned but unused field, `Model/ATKDEFMode.cs:27` |

These warnings predate this extension. Original logs are retained outside the repository at `%LOCALAPPDATA%\4RTools-Engineering\baseline-0cad2f6\build-initial.log` and `build-release.log`.

The MIT license remains unchanged, including `Copyright (c) 2022 4RTools`. Its copyright and permission notice must accompany distributed copies or substantial portions of the software. [Upstream license](https://github.com/4RTools/4RTools/blob/main/LICENSE)

### Stock-equivalent and official binary comparison

| Artifact | SHA-256 | Observed launch result |
| --- | --- | --- |
| Unchanged local Release build | `0B96126646664892CF9632F304E32404C325A97D4E937DBB83EDF6CB5AC3366F` | Launched as PID 17692; 4RTools v2.10.0 window observed |
| Downloaded official upstream v2.10.0 release executable | `2A452C46C33671607A6CFAA0CF3278A858640F2F2C8F7E33EB8799DA9FA4DEEA` | Two ordinary launch attempts from the external evidence directory returned `Access is denied` before a process ID was returned; the user later demonstrated a running upstream v2.10.0 window |

Artifacts are retained separately under `%LOCALAPPDATA%\4RTools-Engineering\baseline-0cad2f6\local-unchanged` and `upstream-release`. The user reported antivirus blocking the official binary, reported changing that setting independently, and explicitly requested one retry. The same launch attempt again returned `Access is denied`, with the hash unchanged. The second failure's cause is unconfirmed. The user subsequently supplied a screenshot of a running upstream v2.10.0 window with an empty Ragnarok Client selector and the application OFF. Its executable hash was not independently established from that screenshot. Earlier external-directory launch failures are therefore environment-specific evidence, not proof that upstream cannot run or that Gepard rejects it. No alternate launch mechanism or protection changes were attempted by this work.

The user then requested that subsequent binaries and launch working directories remain within this GitHub repository. Build outputs already use the repository's `bin` directories. Run the commands below from the repository root. Retained baseline artifacts and diagnostic logs remain outside the repository; they are evidence files, not the location for subsequent launches.

Vanilla was already running as PID 22308 during the baseline observation. Its normal window remained present. The bundled supported-server list and the upstream list checked during the session had no Vanilla entry; the remote list contained 136 entries. The stock process selector only presents processes matched by that mechanism, so Vanilla could not be selected using the shipped configuration. No guessed server entry or memory addresses were added.

| Baseline question | Evidence / limit |
| --- | --- |
| Vanilla process is running | PID 22308, `Vanilla MMO.exe`; PIDs are session-specific |
| Supported-server recognition | No matching Vanilla entry in the checked lists |
| Stock attachment | Not established; stock selector excluded Vanilla |
| Stock `ReadProcessMemory` | Not tested against a known Vanilla gameplay address |
| Stock ordinary key / `PostMessage` behavior | Not tested in-game |
| Stock macros / spammer | Not tested in-game |
| Unsupported-client warning | Source behavior inspected; warning display not established. User screenshot confirms an empty selector and OFF state |
| Upstream versus unchanged local Gepard reaction | Inconclusive; both a local launch and user-observed upstream window exist, while external-directory launch attempts failed |

### Modified diagnostics: actual live observation

After implementation, the repository build was launched from the repository working directory using `--vanilla-snapshot 22308`, with an empty field map. At `2026-09-06T20:59:32Z`, this completed with exit code 0. The read-only `OpenProcess` request used `0x1010` (`PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ`), and process/module metadata enumeration succeeded.

| Live observation | Result |
| --- | --- |
| Process name / PID | `Vanilla MMO` / 22308 |
| Executable filename | `Vanilla MMO.exe`; full installation path was not retained |
| Main module base | `0x00400000` |
| Target pointer size | 4 bytes, confirming a 32-bit Vanilla process |
| Gameplay fields | All unavailable, because no candidate addresses were configured |
| Actual memory bytes read by this metadata snapshot | None; opening a read-capable handle is not an RPM success test |

The snapshot is retained at `%LOCALAPPDATA%\4RTools-Engineering\baseline-0cad2f6\vanilla-metadata-1.json`. This is evidence for the modified diagnostics observer only; it does not retroactively establish stock selector attachment, input delivery, gameplay field readability, or permission for automation.

A subsequent explicit `--probe` at `2026-09-06T21:00:53.6567351Z` completed with exit code 0. Using the same read-only access mask, it requested exactly two bytes at the observed main-module base `0x00400000`. `ReadProcessMemory` returned `4D-5A` (`MZ`), the executable header signature. This proves a successful bounded read of that header in this session. It says nothing about HP, SP, target, combat, or other gameplay addresses. The result is retained in `baseline-0cad2f6\vanilla-header-probe-1.json` under the same engineering directory.

The new diagnostics window also launched from `bin\Release` with the repository as its working directory (PID 27772), remained responsive, and listed `Vanilla MMO.exe` PID 22308. Its title identifies it as the local read-only diagnostics extension. Controlled gameplay comparisons have not yet been performed. The next runtime work needs visible test-character HP/SP and current coordinates as reference observations, followed by the comparisons below; no values or addresses have been assumed while awaiting those samples.

## Architecture and extension boundaries

The existing application is a .NET Framework WinForms application. The stock process/server selection and static addresses are coupled to its supported-server configuration. The new diagnostic source is separate from the stock memory reader and input workers, so adding a candidate Vanilla map cannot silently change existing server behavior.

| Existing component | Assessed behavior and extension implication |
| --- | --- |
| `Model/Client.cs` | Uses signed 32-bit absolute HP/name addresses. HP values are at offsets 0/4, SP at 8/12, and 100 raw status values start at HP base + `0x474`. Recognition tests the first same-name definition for positive HP. These are stock layout assumptions, not Vanilla addresses. |
| `Utils/ProcessMemoryReader.cs` | Opens with VM operation/read/write rights, ignores native read success/full-byte count, and has no `IDisposable` contract. No caller writing process memory was found. The new reader has its own narrower contract; stock consumers retain existing behavior. |
| `Model/Macro.cs` | Triggered ordered key sequences with delays and optional mouse messages, executed through `_4RThread` and window `PostMessage`. This is an eventual input-adapter reference, not a diagnostics dependency. |
| `Model/AHK.cs` | Key repeat/click behavior mainly uses `PostMessage`; speed mode additionally uses `mouse_event` and `keybd_event`. None of these paths is started by diagnostics. |
| `Model/AutoRefreshSpammer.cs` | A worker posts a configured key then sleeps for its configured interval. Future timed actions should share a scheduler rather than duplicate workers. |
| `Model/Autopot.cs` | Reads stock HP/SP thresholds and posts key down/up messages. It cannot validate Vanilla SP merely because an offset returns bytes. |
| `Model/Autobuff.cs` and status recovery | Interpret stock status identifiers and dispatch configured keys. Vanilla status meanings require their own verification. |
| `Model/Profile.cs` | JSON profiles initially contain object-valued sections; later updates can contain serialized JSON strings. The new optional diagnostics settings accept either form and missing older sections. |
| Forms and notifications | Eighteen designer-backed feature forms are hosted as borderless MDI children in tabs. Synchronous profile/on/off notifications control stock actions. Diagnostics stays independent of global action enablement. |
| Startup and worker lifetime | Normal startup enters an updater that can replace the application, then server-list update and the container. `_4RThread.Stop` uses obsolete thread suspension. The separate diagnostics entry point and disposable state source avoid inheriting those behaviors. |

`Model/Vanilla/VanillaClientState.cs` defines typed optional observations and `IStateSource`. A consumer must check `IsAvailable` before reading a value; unavailable data is not equivalent to zero, false, or no target. Every snapshot has a session ID, UTC sample time, connection information, and per-field addresses, errors, provenance, last observed times, and last changed times. A first sample is not a transition. A new session or failed observation cannot inherit another session's activity history.

`VanillaMemoryMap` centralizes candidate addressing. A mapping can use an absolute address, an offset from a named module, or up to eight target-width pointer dereferences. No Vanilla offsets or signatures are bundled. There is no broad memory scanner, debugger attachment, injection, remote write, packet interface, or input action in this diagnostic path.

The read-only native reader requests the process access needed for querying and reading. Windows requires `PROCESS_VM_READ` for `ReadProcessMemory`; a failed call must retain its native error, and even a reported success must have returned the full requested byte count. Process access failure is a diagnostic result, not a reason to request stronger rights or change protection settings. [ReadProcessMemory](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-readprocessmemory), [process access rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights)

Addresses are represented as unsigned 64-bit values and validated against the actual target pointer size before conversion to native pointers. Pointer arithmetic is checked. The current 32-bit application rejects a 64-bit target before attempting cross-architecture module reads. Supporting such a target would require an independently validated observer build; silently truncating addresses is not supported.

Any memory read failure stops that source, disposes its process handle, and invalidates observations and transition times. There is no automatic retry or reopening after failure. Diagnostics reports the stopped state and underlying error. Stop that line of investigation if access is denied or protection reacts; do not reconnect as a workaround.

## Run diagnostics without Vanilla

```powershell
& .\bin\Release\4RTools-Vanilla.exe --vanilla-diagnostics --demo
```

Demo observations are explicitly labeled synthetic. They allow UI, optional values, and timestamp behavior to be exercised without opening a game process or sending input. Passing unit tests or watching demo target changes does not verify the meaning of a Vanilla field.

To open the diagnostics window for manual observation:

```powershell
& .\bin\Release\4RTools-Vanilla.exe --vanilla-diagnostics
```

Use **Refresh processes** to list running `Vanilla MMO.exe` clients, then **Connect read-only**. Keep the map's `Fields` empty for metadata-only observation. Load a map only when there is a concrete candidate address with documented provenance. An empty map deliberately leaves gameplay values unavailable. **Mark controlled action** adds a timestamped note; **Export current snapshot** saves the latest observation and retained notes, not a memory dump or full observation history. **Disconnect** clears the displayed values and closes the source.

The **Advanced diagnostics** tab contains the map JSON and **Load map**. **Save settings to profile** persists the map and poll interval in the existing profile's optional `VanillaDiagnostics` section. The interval defaults to 500 ms and is bounded to 250–10000 ms. Standalone diagnostics uses the Default stock profile. The diagnostics window stops observation when its associated stock profile changes. Stock/diagnostic profiles live under `Profile` beside the executable; companion preferences use `Profiles/Vanilla` beside the executable. The launch working directory remains the repository root for this local environment. Diagnostic settings do not contain an automation enable switch.

The command-line snapshot entry point is also separate from normal startup:

```powershell
$observationName = '4RTools-Engineering\vanilla-snapshot-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json'
$observationFile = Join-Path $env:LOCALAPPDATA $observationName
& .\bin\Release\4RTools-Vanilla.exe --vanilla-snapshot 22308 --output $observationFile
```

Replace the example PID with the current process ID. `--map <path>` optionally supplies a candidate map. The output directory must exist; the build script creates the default engineering directory used above. Snapshot output must be a new filename. A snapshot without a map cannot establish that gameplay memory is readable. Process metadata and actual memory reads are distinct observations.

The optional `--probe` requests only the two-byte header at the discovered main-module base and includes `HeaderProbe` alongside `Snapshot` in the JSON output. Use it as a one-off transport check, not a field discovery tool. A failed probe stops immediately and writes the error; it does not proceed to field reads or try another access mechanism.

### Address map format

The valid empty template contains no guessed addresses:

```json
{
  "SchemaVersion": 1,
  "ProcessName": null,
  "Evidence": null,
  "Fields": {}
}
```

For a configured map, `ProcessName` must identify the target executable name without a directory. `Fields` maps the supported field names to mappings with these properties:

| Property | Meaning |
| --- | --- |
| `Module` | Optional module filename; when present, `Address` is an offset from that module's loaded base |
| `Address` | Unsigned decimal or `0x` hexadecimal string; an absolute address cannot be zero |
| `PointerOffsets` | Ordered unsigned offset strings; for each, read a pointer at the current address using the target width, then add the offset |
| `Encoding` | `UInt32`, `UInt64`, `Int32`, `Utf8`, `Boolean8`, or `UInt32Array`, restricted by the destination field |
| `ByteCount` | String/status read length, 1–256 bytes; status arrays require a multiple of four |
| `Evidence` | Human-provided notes on the source and verification of this candidate; not an automatic verified flag |

HP/SP and action state use `UInt32`; coordinates use `Int32`; target IDs allow `UInt32` or `UInt64`; name/map use `Utf8`; Autobattle uses `Boolean8`; status effects use raw `UInt32Array` values. Raw status/action values carry no inferred gameplay semantics. The schema bounds file size, JSON depth, field count, pointer depth, and read size, and rejects unknown properties and invalid encodings.

## Runtime discovery and evidence matrix

The running client becomes necessary when checking actual process access and correlating candidate fields with controlled in-game behavior. The client being present is insufficient to infer a test character's status or a memory location. This framework does not discover a target ID by guessing addresses or interpreting unrelated memory.

| Field / signal | Current validation | Controlled comparison required | Relog | Map change | Client restart | PC restart |
| --- | --- | --- | --- | --- | --- | --- |
| HP / max HP | Unverified; unavailable | Compare displayed HP before/after ordinary changes and max-value changes | Untested | Untested | Untested | Untested |
| SP / max SP | Unverified; unavailable | Compare displayed SP before/after permitted skill use/recovery | Untested | Untested | Untested | Untested |
| Character name | Unverified; unavailable | Match displayed test character and a subsequent login | Untested | Untested | Untested | Untested |
| X / Y | Unverified; unavailable | Known coordinates and separate one-cell movement on each axis | Untested | Untested | Untested | Untested |
| Target ID | Unverified; unavailable | No target, A, deselect, B, attack, kill, Autobattle with/without nearby targets | Untested | Untested | Untested | Untested |
| Action / combat state | Unverified; unavailable | Separate idle, movement, attacks, casts, target death, and loading | Untested | Untested | Untested | Untested |
| Map / map ID | Unverified; unavailable | Compare known maps and transition/loading behavior; current schema supports a map string | Untested | Untested | Untested | Untested |
| Autobattle enabled | Unverified; unavailable | Toggle the built-in feature and compare fighting versus idle while still enabled | Untested | Untested | Untested | Untested |
| Status effects | Unverified; unavailable | Compare one known effect appearing and expiring; record raw identifiers | Untested | Untested | Untested | Untested |
| Target/action change times | Framework behavior testable with synthetic samples only | Correlate with separately verified target/action fields | Untested | Untested | Untested | Untested |

For each candidate, record the executable version/hash, address expression, encoding, snapshot timestamps, visible in-game state, and comparison results. A moving value is a candidate, not proof. In particular, target ID zero has no assigned meaning until no-target/target/death comparisons establish it. Stable coordinates alone do not prove the character is stuck, and no target alone does not prove farming is active.

If one signal cannot be observed, evaluate another known, permitted read-only signal against the same controlled states. If ordinary reads or actions are blocked, retain the exact error and stop that investigation. No protection bypass, game-file alteration, memory write, code injection, or server-protocol action is part of this work.

## Gates for live automation

Before a live rule can be enabled, its required observations must be positively verified on Vanilla and remain available/fresh. A stopped source, loading, a new session, unknown activity, or an unsupported required field suppresses actions. Diagnostics never emit gameplay input. The companion scheduler remains gated while the current build has no verified gameplay mappings.

| Implemented feature | Required evidence and behavior before live enabling |
| --- | --- |
| Smart Teleport | Explicit farming selection and verified idle/no-target/combat/casting meaning; configurable 10-second idle defaults, 15-second cooldown default, 3-second grace, configured normal hotkey, transition suppression and timer reset |
| Fixed interval teleport | Separately selectable explicit mode, configured interval/hotkey and the applicable live-action permission/safety gate |
| Stuck detection | Verified X/Y plus expected farming and no combat; separate timer and single-action cooldown |
| SP recovery | Positively verified SP/max SP; configurable threshold and generic key/wait sequence; respect verified important-action state when available |
| Shared rules | A common state-driven scheduler with deterministic clock/cooldown tests, optional field handling, persistent settings, and an adapter to the existing permitted key mechanism |

The console tests validate local state, addressing, failure behavior, and serialization without Vanilla. Live permission, real field meanings, stock key delivery, and lifecycle stability require separate evidence; a green build does not satisfy those gates.
# Verified login selections

Proxy choices are Global, Manila, Singapore, Tokyo, Hong Kong, Los Angeles,
Australia and UAE. Existing numeric settings keep their original meaning.
Startup, recovery and TESTS recognize the `Select Service` form and the exact
configured name, click inside the observed name, then require two fresh captures
showing that name highlighted before submitting. List order is not an input.
The game-server step recognizes `Vanilla MMO`; `Crowded` is status text.

Credential entry recognizes the login service and separate fields, then requires
native caret evidence or repeated visual caret blinking in the intended field.
It verifies the exact visible username before password entry, checks repeated
known password mask glyphs and the expected count, and rechecks before Enter.
TESTS -> Submit credentials uses the same guards. Credentials and credential
frames are not sent to OCR services, written to capture files, or logged. OCR is
local and the English model/native runtime are bundled with the release.

Character selection requires explicit character-screen labels, fifteen detected
cards and one selected frame. It measures the grid, uses keyboard navigation,
checks each transition and edge clamp, and confirms the target before Enter.
TESTS -> Character uses that same path and waits for gameplay identity. Configured
slot numbering is row-major; existing fresh username/character checks still gate
Autobattle. No character-grid or GAME START coordinate clicks remain.

Recognition tests use synthetic text/forms with several fonts, DPI scales,
resolutions, positions, row orders and softened resampling. These are not live
Vanilla screenshots. The available user captures do not expose the unobscured
fifteen-slot screen, so its actual card styling and slot numbering remain
unverified. Unsupported layouts, unreadable text, ambiguous highlights and unknown
focus stop selection safely; there is no promise of recognition at arbitrary blur
or with every future skin. Windows CI also runs the packaged OCR engine against a
generated label from the extracted portable ZIP. It does not interact with a game
or install an update on the user's PC/VPS.
