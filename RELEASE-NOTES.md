# 4RTools Vanilla 0.6.79

## Reliable credential replacement

- Clear remembered login text with Home, Shift+End and Backspace. Vanilla's
  custom login control does not reliably select all text with Ctrl+A, which
  allowed repeated login attempts to append usernames.
- Clear a freshly identified, clicked field before requiring its blinking
  caret. Full text can hide that caret. Require a visibly empty field and
  independently confirmed focus before typing the actual username or password.
- Accept the observed one-pixel bottom-border repaint of the same control;
  continue rejecting changed positions, widths, windows and stale input proofs.
- Calibrate the accurate reader for small login text while retaining exact,
  case-sensitive username comparison on repeated fresh frames and rejection of
  confident contradictory readings. No configured name is supplied as an OCR hint.
- Preserve exact username and password-mask checks, foreground ownership,
  cancellation and saved settings. Cart quantity entry keeps its existing input
  sequence. Earlier server-selection, cursor-parking, farming emergency and
  temporary-action fixes remain included.

## Validation

Live diagnosis reproduced accumulated username text after Ctrl+A replacement.
One ordinary selection-and-Backspace sequence cleared the whole field, and
independent focus verification then succeeded. No credential images or secret
contents were exported.

The production credential flow accepted the second saved account, selected its
configured character, verified its living gameplay identity/map/position and
confirmed movement after the configured Autobattle hotkey. The first farming
client stayed running throughout that second-account test.

Regression coverage includes retained text after failed or partial clearing,
empty-field recognition, caret visibility, control-border repaint, cancellation
and ownership changes. Release gates cover full Windows Debug/Release tests,
native test-owned processes, isolated mock UI, portable packaging and updater
discovery/download/staging separately from live-game evidence.
