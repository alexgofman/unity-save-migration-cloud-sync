# Save Migration & Cloud Sync for Unity

Offline-first saves for Unity 6: versioned migrations and a load-time sanitizer, a local store that
survives being killed mid-write, and a cloud backup that uploads in the background, notices when
the cloud copy is newer, and restores it through the same code that loads a local save.

Extracted and refactored from production code. The core is plain C# with no engine reference, so
its tests run with `dotnet test` and no Unity licence.

## Why this exists: a postmortem

In production, the save system this package was extracted from lost progress for good: for the
players it hit, the game came up empty, and kept coming up empty. The cause was a chain of small
faults, each of which looked harmless on its own.

1. **A timestamp less durable than the save it described.** The save file was written to disk on
   every save. The time of that save, which the launch-time cloud comparison depends on, went into
   a key-value store that is only flushed to disk now and then. When the operating system killed
   the app, the save survived and its timestamp did not.
2. **A prompt that stated a conclusion.** On the next launch the local timestamp read as zero, so
   any cloud copy looked newer. The player was told that a newer save existed in the cloud and
   asked whether to restore it. Saying yes was the reasonable answer to that sentence. The case had
   been considered and judged harmless: one unnecessary prompt, and the player keeps the local save.
3. **A restore path that was not the load path.** The download was deserialized with the
   serializer's default settings, while the state classes keep their setters private. Collections
   and nested objects came back, so the result looked like a save. Every scalar behind a private
   setter (balances, counters, names) came back as its default. The restore wrote that state to
   disk and uploaded it, replacing both good copies.
4. **Persistence that depended on game data.** The save routine skipped writing unless one
   particular field of the state was set, which had been a convenient way of not saving before the
   player had started. The restore had just blanked that field. From then on every save, in every
   session, was skipped without a word.

Fixing the fourth fault exposed a fifth during development: with the content check gone, a
component that saved during start-up, before the load had run, wrote the empty placeholder state
over the real file. The content check had been doing a second job that nobody had written down.

None of these is exotic. What they share is that each safeguard was implicit: a flush that usually
happened, two read paths that were assumed to be the same, a field that stood in for a question
nobody had asked directly.

## The invariants that fixed it

Each fault maps to a rule. In this package each rule is a type or a single code path, and has a
test with its name on it.

| # | Rule | Where it lives | Test |
|---|---|---|---|
| 1 | The timestamp that decides "is the cloud newer?" is as durable as the save it describes. | `AtomicFileSaveStore` writes it in the header of the save file, so the two are written and swapped in together. | `TheTimestamp_IsOnDiskAsSoonAsTheSaveIs` |
| 2 | A restore is a load: same serializer settings, same migrations, same sanitizer. | `SavePipeline` is the only way from bytes to a state. The session and the coordinator share one instance. | `Restore_RunsTheSameMigrationAndSanitizerAsALocalLoad` |
| 3 | What is written is what is read, including members with private setters. | `JsonSaveSerializer`, `PrivateSetterContractResolver` | `Restore_KeepsEverythingBehindPrivateSetters`, and `DefaultJsonNetSettings_SilentlyResetThoseScalars` pins the failure down |
| 4 | The in-memory state may be written only once it is known to supersede what is on disk. | `SaveGate`, opened in exactly four ways by `SaveSession` | `SaveBeforeLoad_IsRefused_AndTheSaveOnDiskIsUntouched` |
| 5 | Once a save exists, whether it is updated never depends on game data. | The "has the player begun?" predicate is consulted only while no file exists. | `OnceASaveExists_ThatPredicateCanNoLongerBlockSaving` |
| 6 | A save that did not happen says so. | `SaveResult`, raised through `SaveSession.SaveCompleted` for every outcome | `EveryOutcome_IsRaised_SoASaveThatDidNotHappenIsVisible` |

In the game, rule 1 was met differently: the timestamp store is flushed often enough that it can
lag by no more than half the comparison tolerance, and on pause. Putting the timestamp into the
save file removes the window instead of bounding it. Rule 2 is also stricter here: in the game a
restore runs the same migrations and sanitizer as a local load, but the two still read through
different serializers.

Reviewing the code for the same kind of fault while refactoring produced more rules. These are
implemented and tested here; they did not exist in this form in the shipped game.

| Rule | Why | Test |
|---|---|---|
| An unreadable save is not a new player. | A truncated file that reads as "nothing" starts a new game, which is then saved over the file and uploaded over the backup. | `UnreadableSave_IsNotANewPlayer_NothingIsWrittenOverIt` |
| A kill during a save leaves a complete save on disk. | Writing in place truncates first. | `KilledBetweenTheTwoMoves_IsRecovered` and the tests around it |
| A save from a newer client is refused, not opened. | The older client would drop the fields it does not know and write the rest back under the newer version number. | `SaveNewerThanTheClient_IsNotOpened_AndNotOverwritten` |
| A failed upload is retried on its own. | The dirty flag used to be cleared when the upload started and never restored. | `FailedUpload_IsRetried_WithoutWaitingForAnotherSave` |
| Every request has a deadline. | One request that never answers would occupy the upload slot for the session. | `UploadThatNeverAnswers_TimesOut_IsCancelled_AndRetried` |
| Nothing is uploaded before the cloud has been looked at. | An upload made blind can destroy a newer copy. | `TheCloudIsLookedAt_BeforeTheFirstUpload` |
| Uploads are on hold while it is undecided which copy wins. | The countdown would otherwise upload the local save while the player is reading the prompt, and "restore" would hand back the local save. | `NewerCloudSave_RaisesTheEvent_AndPutsUploadsOnHold` |
| A restore that fails never becomes "local wins". | The player asked for the cloud copy; uploading over it is the opposite. | `FailedRestore_NeverBecomesLocalWins_EvenWithoutAPriorConflict` |
| What is uploaded is the save on disk. | The backup is then always, byte for byte, something this device saved first. | `WhatIsUploaded_IsTheSaveOnDisk_NotUnsavedChangesInMemory` |
| After an upload or a restore the local timestamp follows the cloud copy's. | A device must not mistake its own late upload for a newer save. | `UploadThatLandsLate_MovesTheLocalTimestampUp_SoTheDeviceDoesNotAskAboutItsOwnSave` |

## What is in the package

```
Runtime/Core      plain C#, no UnityEngine
  Migration         SaveMigrator<TState>: ordered, idempotent, versioned steps
  Sanitizing        ISanitizer<TState>, SanitizeReport
  Serialization     ISaveSerializer<TState>, JsonSaveSerializer<TState> (Json.NET)
  Pipeline          SavePipeline<TState>: bytes -> deserialize -> version check -> migrate -> sanitize
  Local             ILocalSaveStore, AtomicFileSaveStore
  Session           SaveSession<TState>, SaveGate, SaveResult, LoadResult
  Cloud             ICloudSaveBackend, CloudSyncCoordinator<TState>, CloudSyncOptions
  Time              IClock, SystemClock
Runtime/Unity     LocalSaveStore (persistentDataPath), SaveSyncHost (tick, flush on pause and quit)
Runtime/PlayFab   PlayFabEntityFileBackend, PlayFabAccountDeletion  (own assembly, off by default)
Tests/Editor      EditMode tests for the core
Samples~          a demo save with two migrations and a sanitizer; a CloudScript handler
DotnetTests~      a net8.0 project that links the core, the tests and the demo
```

## Installing

Unity 6000.0 or newer. In the Package Manager choose **Install package from git URL** and enter the
URL of this repository. The only dependency is `com.unity.nuget.newtonsoft-json`, which the
Package Manager adds.

## Using it

```csharp
public sealed class MySave : IVersionedState
{
    public int SchemaVersion { get; set; }
    public int Lives { get; set; }
    public long Coins { get; private set; }      // private setters are fine
    public void Earn(long coins) { Coins += coins; }
}
```

```csharp
var clock = new SystemClock();

// One pipeline. The session loads through it and the coordinator restores through it.
var pipeline = new SavePipeline<MySave>(
    new JsonSaveSerializer<MySave>(),
    new SaveMigrator<MySave>()
        .AddStep(0, "Lives: at least one", save => { if (save.Lives < 1) save.Lives = 1; }),
    new MySanitizer());

var session = new SaveSession<MySave>(new LocalSaveStore("slot1"), pipeline, () => new MySave { Lives = 1 }, clock);

// Load before anything else. Until then the session holds a placeholder and refuses to save it.
LoadResult<MySave> load = session.Load();
if (!load.IsUsable)
{
    // A save exists that this build cannot use. It is left untouched and nothing can be saved
    // over it until you decide: restore from the cloud, or session.ResetToNew() on purpose.
}

session.State.Earn(10);
SaveResult saved = session.Save();   // a failed write is returned, not thrown
```

Cloud backup is optional and sits on top:

```csharp
var sync = new CloudSyncCoordinator<MySave>(session, pipeline, backend, clock);
GetComponent<SaveSyncHost>().Bind(session, sync);   // ticks every frame, flushes on pause and quit

sync.CloudNewerDetected += check => ShowRestorePrompt(check.CloudUtcSeconds, check.LocalUtcSeconds);
await sync.CheckCloudAsync();                       // once at launch, after Load

// From the prompt:
RestoreResult restored = await sync.RestoreAsync(); // then rebuild the game from session.State
// or
sync.KeepLocal();
```

After a successful restore `session.State` is a different object. Reloading the scene is the
simplest way to make sure nothing still points at the old one.

`Samples~/DemoGame` has the same wiring as a component (`DemoBootstrap`), with a folder standing
in for the cloud so the restore flow can be tried without an account at any service.

### Writing migrations

- Add steps in order. The step from version N is the N-th one added, so the schema version of a
  build is simply the number of steps and cannot drift from the code.
- Make every step idempotent: guard on the data it changes, not on the version. A step can meet
  data that is already in the new shape, and must then change nothing.
- Do not set the version in a step. The migrator does that after the step returns.
- Never edit or reorder a step that has shipped. Add a new one.
- A state that is new in this build is stamped with the current version by the session. Create
  states through the factory you give the session.

The sanitizer runs after migration, on every source, before the state is visible to the game. It
gets a `SanitizeReport` and should record what it changes; the core has no logger.

### Cloud sync behaviour

| Option | Default | Meaning |
|---|---|---|
| `MinPushIntervalSeconds` | 30 | A burst of saves is collected for this long, then uploaded once. |
| `RequestTimeoutSeconds` | 20 | After this, a request is cancelled and counted as failed. |
| `RetryBaseDelaySeconds` | 10 | Delay before the first retry. Doubles with each failure. |
| `RetryMaxDelaySeconds` | 300 | Upper bound for that delay. |
| `ClockSkewToleranceSeconds` | 90 | How much later the cloud copy must be to count as newer. Must cover interval plus timeout; `Validate()` checks it. |

The defaults are starting values, not measurements.

The coordinator has no timers or threads. It is advanced by `Tick()`, which `SaveSyncHost` calls
once per frame, and it measures intervals on a monotonic real-time clock, so it works with a time
scale of zero. Results, both events and the tasks returned by `CheckCloudAsync` and `RestoreAsync`,
are delivered on the calling thread from inside `Tick()`, or from the call itself when the answer
is immediate.

When an account changes under the backend (signing in to another account on the same device), call
`Suspend()` first, then `RestoreAsync()`, then `Resume()`. A restore that fails leaves uploads on
hold.

### PlayFab

`Runtime/PlayFab` is a separate assembly that only compiles when the scripting define symbol
`SAVESYNC_PLAYFAB` is set, so the package builds and its tests run without the PlayFab SDK. With
the SDK in the project (its assembly is named `PlayFab`), add the symbol in Player Settings.

```csharp
var backend = new PlayFabEntityFileBackend("save");
CloudResult<string> signIn = await backend.SignInWithCustomIdAsync(SystemInfo.deviceUniqueIdentifier);
```

The title id is read from the PlayFab SDK settings of the project. Nothing in this package sets
one. Saves are stored as an Entity File on the player's title entity; an upload is three requests
(get an upload URL, send the bytes, finalize) and an interrupted one is aborted on the next attempt.

Any other service fits behind `ICloudSaveBackend`: four methods and one flag.

## Running the tests

```
dotnet test DotnetTests~
```

That project links `Runtime/Core`, `Tests/Editor` and the pure part of the demo, and compiles them
with the C# language version Unity 6000.0 uses. The same test files form EditMode assemblies for
the Unity Test Runner (add the package to `testables` in the project manifest).

## Status and limits

Read this before relying on it.

**What has and has not been run**

- The design and the six rules in the first table come from production code, rules 1 and 2 in the
  weaker form described above. This refactored package has not itself been run in a shipped build.
- The core is covered by about 180 unit tests, run with `dotnet test` on .NET 8. They use a fake
  backend whose requests can be held, failed and released, a fake clock, an in-memory store, and
  for the file store and the demo a real temporary folder. One test drives the coordinator with
  seeded random sequences of saves, outages, restores and suspends and checks the upload rules
  after every step. The tests were also checked against the code: 39 behaviours were broken one
  at a time (the save gate, the private-setter resolver, the retry, the deadline, the hold and so
  on) and each break failed at least one test.
- The Unity-side code (`SaveSyncHost`, `LocalSaveStore`, the demo bootstrap) and both test
  assemblies compile against the Unity 6000.0 engine assemblies. They have not been run in the
  Editor or in a player, and the tests have not been run in the Unity Test Runner.
- `PlayFabEntityFileBackend` compiles against the PlayFab Unity SDK. The sequence of requests is
  the one used in production, but the adapter in this refactored form has not been exercised
  against a live title.
- The CloudScript sample has not been deployed.

**Last write wins, by wall clock**

"Newer" means: the time the service stored the cloud copy is later than the time the local save was
written, by more than the tolerance. Two clocks are involved and nothing is merged. Known
consequences:

- A device whose clock is far ahead stamps its saves in the future and will not notice a newer
  cloud save from another device until real time catches up.
- A device whose clock is behind by more than the tolerance can, after saving again following an
  upload, see that upload as newer on the next launch. Restoring it loses nothing that was in the
  upload, but it rolls back what was saved after it.
- A new install that cannot reach the cloud on its first launch starts a new save. When it comes
  online, that save is the later one and replaces the older cloud copy. Remembering, on disk, the
  cloud version a save was last synced with, and treating first contact as a conflict, would close
  this. It is not implemented.
- The upload flag is kept in memory. Progress that was not uploaded before the app closed is
  picked up on the next launch by comparing timestamps, not from a persisted queue.

**Other limits**

- Upload on pause is best effort and was not measured on devices. Unity stops running the player
  loop when a mobile app goes to the background, and both the PlayFab SDK callbacks and this
  package's `Tick()` run on that loop, so an upload that needs several round trips will normally
  complete only after the app returns. The local save, which `SaveSyncHost` writes first, is what
  protects the last seconds of play.
- `Save()` is synchronous: serialize, write, flush to the device. For a large state on a slow
  device, save less often or pass `flushToDisk: false` and accept the window that opens.
- The local file's checksum detects truncation and damage. It is not a signature, and the
  sanitizer is a plausibility filter, not anti-cheat.
- One session, one coordinator, one thread. No save slots, no partial saves.
- With IL2CPP and managed code stripping, the save classes must survive stripping (`link.xml` or
  `[Preserve]`), as for any type Json.NET reaches through reflection.
- A collection given content in a property initializer keeps it when a save is read, and the saved
  items are added after it. Initialize collections empty.

## Licence

MIT. See [LICENSE](LICENSE).
