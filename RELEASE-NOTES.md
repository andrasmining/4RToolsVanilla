# 4RTools Vanilla 0.6.84

## Emergency and completed-farming notifications

- Add an independent **E-mail on emergency** checkbox beside the editable
  emergency conditions. It uses the saved SMTP configuration, independently of
  the normal Weight e-mail switch and each character's Mail checkbox. Emergency
  client closure never waits for SMTP.
- Normal farming completion still requires Cart >=99% AND carried weight >=50%,
  with Cart automation enabled and Autobattle STOP verified. The normal e-mail
  switch and the character's Mail checkbox enable its DONE notification.
- Add **Close client when farming is complete** to the Weight settings. After
  verified STOP and input cleanup, this optionally closes only the completed
  character's client. Closing does not depend on e-mail being enabled or succeeding.
  Both new options default to off.
- Retain completed-farming holds across application restarts, including cold
  startup, so intentionally closed clients are not automatically relaunched.
  CLEAR WEIGHT/CART HOLD on the Weight tab releases completed-farming holds;
  CLEAR EMERGENCY HOLD remains separate.
- Show Emergency or Completed status and the specific reason directly on each
  character's top resource card, including recorded values, time, client-close
  result and e-mail result. Closed clients retain their stop card without stale
  live resource values. Existing emergency holds also show their recorded reason.
  Other stopped/error states and unavailable memory reads show their specific
  descriptions on the character card as well.
- Persist character-bound notifications so delivery can continue after the game
  client exits. Failed delivery retries after five minutes. A delivery interrupted
  by an application restart is marked uncertain and is not automatically resent.

## Validation

Regression coverage includes independent switches, saved configuration, durable
holds and startup guards, affected-client close ordering and cancellation,
notification retries and duplicate prevention, and isolation of healthy siblings.
Windows release gates include full Debug/Release tests, isolated mock UI and
layout checks, native test-process checks, portable packaging, and public updater
discovery/download/staging. These checks use simulated SMTP and test-owned
processes; they do not claim live-game or real e-mail delivery validation.
