# 4RTools Vanilla 0.6.65

## Independent Cart and e-mail policy per character

Cart maintenance and Weight e-mail are no longer one combined per-character switch.

Each saved character now has two independent policies in **Recovery & relog**:

- **Cart** — enables UI-only automatic Cart maintenance for that character.
- **Mail** — enables e-mail notifications for that character.

The character list shows compact **Cart** and **Mail** columns, and the character editor exposes separate checkboxes. Existing legacy profiles remain compatible: when the new split fields are absent, both inherit the old combined `WeightEnabled` value. Once the split fields are explicitly saved, they take precedence and are preserved through clone/catalog/discovery/identity enrichment.

The shared Weight tab still owns common thresholds, category choices, hotkeys and SMTP transport. Its **Cart master** and **E-mail master** remain global kill switches; the matching per-character switch must also be enabled before a character can use that feature.

## E-mail meaning now follows whether Cart maintenance is active

The same per-character Mail switch now has the intended interpretation:

- **Cart inactive + Mail ON:** carried-weight warning e-mail at the configured Weight threshold. This supports characters without a Cart, for example a character where you want notification around 45% carried weight.
- **Cart active + Mail ON:** carried-weight-only warnings are suppressed. E-mail instead follows the Cart/farming state:
  - exact **Cart 100%** remains the early Cart-full milestone notification;
  - **Cart >=99% AND carried weight >=50%** is the combined DONE milestone after the verified Autobattle STOP.
- **Cart ON + Mail OFF:** Cart maintenance runs normally with no e-mail notifications.
- **Cart OFF + Mail OFF:** neither automatic Cart maintenance nor Weight mail runs for that character.

This prevents a Cart-managed character from generating an irrelevant carried-weight-only warning while still preserving useful Cart-full and final combined-full notifications.

## Compact UI; explanations moved to hover help

Long instructional paragraphs have been removed from the Weight/Cart and character-management surfaces.

- Weight/Cart uses compact section controls plus small info/help glyphs.
- Detailed behavior is available through mouse-hover tooltips.
- Character editor Cart, Mail and Smart Teleport behavior is documented via hover help rather than persistent prose.
- The Recovery character table remains compact; Cart and Mail states are visible directly.
- The password-state header was shortened to **Pwd** so the new policy columns still fit narrow/RDP layouts; full cell values remain available through tooltips.
- The old long Recovery helper paragraph is hidden in the simplified production UI.

Active warnings, errors, live state and short status messages remain visible without hovering.

## Existing Cart safety behavior retained

This release preserves the live-hardened Weight/Cart behavior from v0.6.64:

- Autobattle STOP must be verified by **5 continuous seconds of unchanged X/Y** inside a **10-second** window, with at most **3 STOP attempts**.
- One cumulative HP baseline is retained from the beginning of the STOP sequence through the entire stopped Cart operation.
- HP dropping by **more than 10 percentage points**, or verified HP becoming unavailable after STOP, aborts Cart work, resumes Autobattle through verified movement, minimizes, and retries Cart maintenance after about **60 seconds**.
- The HP guard remains active during panel work, waits and mouse drag/drop.
- Cart precision filling begins at **75% Cart usage**.
- Mastela Fruit / Use = **3 weight**; Peco Feather / Etc = **1 weight**; Cart capacity = **10000**.
- Quantity-aware filling never intentionally exceeds remaining Cart capacity.
- Cart transfers keep deliberate source/drop holds with faster cursor travel (**6 steps x ~35 ms**).
- A transfer receives up to **3** bounded attempts; pure transfer non-progress resumes Autobattle and retries about a minute later rather than creating a permanent hold.
- Farming completion remains **Cart >=99% + carried >=50%**.
- Per-process timestamped debug logs and the **10 MiB** per-file hard cap remain unchanged.

## Validation

Before versioning v0.6.65, the full Windows pipeline passed on the final feature implementation:

- shipped Vanilla build-profile validation;
- complete Debug diagnostics/regression suite;
- Release build/package/smoke tests;
- native test-owned recovery checks;
- mock-data UI rendering/layout validation across desktop, RDP-sized and enlarged-text cases.

New regression coverage includes:

- independent Cart/Mail policy persistence per character;
- legacy combined Weight-policy fallback;
- Cart/Mail edits surviving discovery/enrichment;
- adaptive Mail mode selection for Cart vs non-Cart characters;
- compact visible Cart/Mail character columns;
- narrow-grid fitting without unnecessary horizontal scrollbars.

The engineering runner cannot reproduce the user's live Vanilla/Gepard/RDP gameplay. The split policy/e-mail routing and UI are validated offline; the next live farming session remains the runtime-validation boundary for real Cart/SMTP behavior.
