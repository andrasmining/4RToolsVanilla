# 4RTools Vanilla 0.6.81

## Live quantity-reader correction

- Read both dark and light digits on the selected quantity field. Live Vanilla
  popups render black digits on a light-blue selection; the former white-digit
  reconstruction erased those numbers before OCR. Keep the existing confidence,
  capacity and fresh-capture checks.
- Compare additional integer-scale OCR views without smoothing the observed
  pixels. Require at least three agreeing readings across multiple scales and a
  majority of readings that match the visible digit count. Lone readings and
  small pluralities among conflicting results no longer authorize input.
- Require the recognized digit count to match the visible glyph count, so OCR
  cannot silently drop a minus sign or another extra mark and accept the result.
- Cover the actual cropped 305 and 273 Mastela quantity dialogs as offline
  regression fixtures. These contain only the item label, number and OK button.

This release also includes the Cart confirmation and disconnect-recovery fixes
from 0.6.80 described below.

## Cart quantity confirmation

- Confirm the untouched stack quantity when fresh verified weights prove the
  entire carried inventory fits in the Cart, below the 75% precision threshold.
  An unreadable number no longer blocks this safe transfer. Require a newly
  appearing, unique stable quantity popup, recheck capacity before Enter and
  verify Cart-weight progress afterward.
- Cancel a recognized quantity popup without depending on number OCR. Wait for
  actual dismissal rather than treating a delayed response or lost blue selection
  as success. This prevents a quantity-reading failure from also blocking cleanup
  and causing a client-restart loop.
- Retain exact count and capacity checks for limited transfers. Leave an already
  correct number untouched; use Home/Shift+End for replacement and readback instead
  of the custom control's unreliable Ctrl+A behavior. Log numeric-recognition
  evidence when the count cannot be verified.

## Disconnect recovery

- Diagnose minimized clients using the existing background window-capture path.
  The former zero-size client-area check could miss a disconnect while minimized.
- Freshly check the affected screen when movement stalls or its state becomes
  unavailable, including when continuous visual monitoring is disabled.
- Bound capture waits and outstanding capture workers. Discard late images and
  keep native capture failures from bypassing the movement-restart deadline.
- Preserve two-observation confirmation of exact disconnect/logout dialogs,
  serialized replacement, healthy-client isolation, STOP cancellation, farming
  holds and the separate confirmed server-outage retry interval.

## Validation

Earlier session logs confirmed two quantity-reader failures followed by failed
popup cancellation and affected-client restarts. During subsequent live testing,
the new default-quantity path transferred 305 and 273 Mastela respectively:
Cart weight rose from 21 to 936 and from 21 to 840. Both clients resumed verified
farming movement. The captured quantity crops then identified the dark-digit
reader defect corrected in this release. The nearly-full Cart count path is
covered offline; a nearly-full live Cart was not manufactured for testing.

The live 0.6.80 supervisor also replaced one client after 181 seconds without
verified movement despite an Unknown screen, then verified its login and farming
movement while leaving the other client online. This verifies the stall fallback;
it does not establish visual detection of the other PC's disconnect dialog.

Regression coverage exercises unreadable numbers, capacity boundaries, coherent
resource observations, stale/replaced windows, delayed dismissal, minimized
test-owned windows and unavailable-state recovery. Windows release gates cover
Debug/Release tests, native test-owned processes, isolated mock UI, portable
packaging and real updater discovery/download/staging. These checks are separate
from live-game validation and do not claim reproduction on the other PC.
