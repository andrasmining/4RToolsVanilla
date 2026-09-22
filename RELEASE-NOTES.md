# 4RTools Vanilla 0.6.73

## Public one-click self-update

- The current application updater is public-only. It resolves the stable release
  through the canonical `github.com/andrasmining/4RToolsVanilla/releases/latest`
  path and canonical public release assets. Its metadata fallback is anonymous and
  the current client no longer consults saved GitHub credentials.
- Remove the obsolete **UPDATE ACCESS** control from the application. Legacy
  private-era clients can still use their historical authenticated path for a
  one-time migration, but v0.6.73 itself requires no token.
- Accepting an offered update is now a single action: 4RTools downloads and fully
  verifies the public release immediately. If recovery or Cart currently owns the
  serialized input path, the verified update stays pending and waits for a safe
  quiescent point instead of asking the user to retry. It then applies and restarts
  automatically.
- Existing safety remains intact: portable ZIP checksum, complete payload manifest,
  executable version and source identity are verified before replacement. Managed
  files are backed up and copy/verification/early-start failures roll back without
  replacing persistent profiles/settings.

## Validation policy

Windows GitHub Actions is the release-completion environment for this project.
The pipeline builds Debug and Release, runs the complete isolated regression suite,
validates shipped build profiles, package/smoke checks, native test-owned recovery
processes, mock UI rendering, release asset hashes, source provenance and the real
published updater discovery/download/staging path. Live Vanilla/Gepard/RDP testing
is not required unless explicitly requested and is not claimed by this release.

## Migration

v0.6.72 can discover v0.6.73 anonymously and self-update directly. Older
v0.6.68/v0.6.69 clients can still migrate through their historical credential if
available; otherwise use the complete portable package once. v0.6.67 points at the
retired repository and requires the portable migration.
