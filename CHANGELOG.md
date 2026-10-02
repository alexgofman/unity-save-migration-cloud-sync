# Changelog

All notable changes to this package are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the package uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-10-02

First public version.

### Added

- `SaveMigrator<TState>`: ordered, idempotent, versioned migration steps. The migrator owns the
  version number; a save from a newer client is refused instead of opened.
- `ISanitizer<TState>` and `SanitizeReport`: a load-time plausibility pass that reports every value
  it corrects.
- `SavePipeline<TState>`: the single path from bytes to a usable state (deserialize, version check,
  migrate, sanitize), shared by local loads and cloud restores.
- `JsonSaveSerializer<TState>` with `PrivateSetterContractResolver`, so members with private setters
  survive a round trip.
- `AtomicFileSaveStore`: write to a pending file, keep the previous save as a backup, verify length
  and checksum on read. The save timestamp is stored in the file header.
- `SaveSession<TState>` and `SaveGate`: the in-memory state can only be written once it has been
  reconciled with the disk. Every save returns and raises a `SaveResult`.
- `CloudSyncCoordinator<TState>`: one request at a time, coalesced and throttled uploads, retry
  with back-off, request deadlines, a cloud check before the first upload, uploads on hold while a
  conflict is unresolved, and a restore that is all or nothing.
- `SaveSyncHost` and `LocalSaveStore` for Unity.
- `PlayFabEntityFileBackend` and `PlayFabAccountDeletion`, in an assembly that is only compiled
  when `SAVESYNC_PLAYFAB` is defined.
- Samples: a demo save with two migrations and a sanitizer, and a CloudScript handler for account
  deletion.
- `DotnetTests~`: runs the core tests with `dotnet test`, without Unity.
