# 4RTools Vanilla 0.6.85

## Settings save automatically

- Remove manual settings Save buttons from Weight, character editing, diagnostics,
  automation rules, update access, and legacy profile/server editors. Existing
  recovery, temporary-action and stock settings continue saving automatically.
- Save completed edits automatically and flush pending valid changes when an
  editor closes. Show saved/error feedback, preserving the last valid settings
  when a draft is invalid or storage fails.
- Keep diagnostic edits bound to their original profile when switching profiles.
  Loading legacy forms no longer rewrites settings or duplicates event handlers.
  The legacy sound switch now persists with the profile.
- Keep passwords masked and protected. A blank SMTP password retains the saved
  password; an explicit Clear action removes it. Existing account credentials
  remain protected when editing unrelated fields.
- Preserve explicit Create, Copy, Import, Export and Delete actions for profiles
  and records. New characters and servers are created only with valid details;
  subsequent edits save automatically.
- Persist recovery settings before changing active supervision, so a failed
  settings write cannot cancel recovery work or change running-client policy.

## Validation

Regression coverage includes reload, pending edits on close, profile ownership,
invalid drafts, failed writes, encrypted credentials, account creation and
catalog rollback. Windows release gates run full Debug/Release tests, isolated
native UI/layout checks, test-owned process checks, portable packaging, and
public updater discovery/download/staging. These checks do not operate live
game clients or send real e-mail.
