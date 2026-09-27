# 4RTools Vanilla 0.6.83

## Editable emergency conditions

- Edit the emergency Weight, SP and HP limits directly on the Weight tab.
  Emergency fields save independently of Cart and e-mail settings when leaving
  the field or pressing Enter, with a visible saved/error status.
- Defaults remain Weight >50%, SP <25% and HP <50%. All three conditions must
  be true in the same fresh verified character snapshot before closing that
  affected client. Saved limits survive application restarts and updates.
- Unrelated Recovery or Weight settings edits preserve the emergency limits.
  Changing limits leaves existing emergency holds intact; CLEAR EMERGENCY HOLD
  remains the explicit action to allow recovery again.
- New emergency records include the limits used when the hold triggered along
  with the measured Weight/SP/HP values. The active rule stays visible separately
  from historical hold evidence.
- Keep the Weight controls reachable by scrolling at smaller window heights.

## Validation

Regression checks cover custom thresholds and strict boundaries, persistence,
invalid settings and failed saves, hold preservation and cancelled pending close
operations. Windows release gates include full Debug/Release tests, isolated
mock UI edits/layout checks, native test-process checks, portable packaging and
public updater discovery/download/staging. These checks do not claim live-game
validation of a custom emergency rule.
