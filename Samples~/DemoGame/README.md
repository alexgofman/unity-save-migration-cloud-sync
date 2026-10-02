# Demo Game sample

An invented aquarium save, used to show the package on a state with a realistic shape.

| File | What it shows |
|---|---|
| `State/AquariumSave.cs` | A save class: balances behind private setters, collections, members added after launch. |
| `State/AquariumMigrations.cs` | Two migration steps: a default correction and a data move, both idempotent. |
| `State/AquariumSanitizer.cs` | Load-time limits, with every correction reported. |
| `State/AquariumSaves.cs` | How the pipeline and the session are put together. |
| `Cloud/LocalFolderCloudBackend.cs` | A folder that stands in for a cloud service. |
| `DemoBootstrap.cs` | The launch sequence: load, bind the host, compare with the cloud, answer the question. |
| `Tests/Editor` | Tests for the migrations and the sanitizer on saves written the way older builds stored them, and two end-to-end tests on real files. |

## Trying it

1. Add `DemoBootstrap` to an empty GameObject. A `SaveSyncHost` is added with it.
2. Enter Play mode and watch the console.
3. Use the component's context menu:
   - **Earn 100 coins and save** changes the state and saves it. The upload follows after the push
     interval, or at once when Play mode ends.
   - **Simulate a reinstall** erases the local save and keeps the cloud copy. Leave Play mode and
     enter it again: the launch now finds a cloud save and no local one.
   - **Restore from cloud** and **Keep local** answer that question.
   - **Print state** shows what the session holds.

Without the `SAVESYNC_PLAYFAB` scripting define the "cloud" is a folder next to the save, under
`Application.persistentDataPath`. With the define and the PlayFab SDK in the project, the sample
signs in with the device identifier and uses the title configured in the PlayFab SDK settings.

The component has been compiled against the Unity 6000.0 engine assemblies but, like the rest of
the Unity-side code in this package, has not been run in the Editor as part of this repository. The
tests in `Tests/Editor` have been run with `dotnet test`.
