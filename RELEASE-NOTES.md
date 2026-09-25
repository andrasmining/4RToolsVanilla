# 4RTools Vanilla 0.6.80

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

Local session logs confirmed two quantity-reader failures followed by failed
popup cancellation and affected-client restarts. The logs did not retain a
quantity screenshot, so they cannot independently establish the displayed count.

Regression coverage exercises unreadable numbers, capacity boundaries, coherent
resource observations, stale/replaced windows, delayed dismissal, minimized
test-owned windows and unavailable-state recovery. Windows release gates cover
Debug/Release tests, native test-owned processes, isolated mock UI, portable
packaging and real updater discovery/download/staging. These checks are separate
from live-game validation and do not claim reproduction on the other PC.
