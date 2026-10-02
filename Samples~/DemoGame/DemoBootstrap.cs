using System;
using System.IO;
using System.Threading.Tasks;
using SaveSync.Unity;
using UnityEngine;
#if SAVESYNC_PLAYFAB
using SaveSync.PlayFabIntegration;
#endif

namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// The launch sequence of a game that uses the package, on the demo aquarium save.
    /// </summary>
    /// <remarks>
    /// Add this component to an empty GameObject (a <see cref="SaveSyncHost"/> is added with
    /// it) and enter Play mode. The steps are logged to the console and the component's
    /// context menu has actions to change the save, restore the cloud copy and keep the
    /// local one.
    /// </remarks>
    [RequireComponent(typeof(SaveSyncHost))]
    public sealed class DemoBootstrap : MonoBehaviour
    {
        private SaveSession<AquariumSave> _session;
        private CloudSyncCoordinator<AquariumSave> _sync;

        private void Start()
        {
            _ = RunSafelyAsync();
        }

        private void OnDestroy()
        {
            _sync?.Dispose();
        }

        private async Awaitable RunSafelyAsync()
        {
            try
            {
                await RunAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private async Awaitable RunAsync()
        {
            var clock = new SystemClock();
            SavePipeline<AquariumSave> pipeline = AquariumSaves.CreatePipeline(clock);
            _session = AquariumSaves.CreateSession(new LocalSaveStore("aquarium"), pipeline, clock);

            // 1. Load first. Until this has run the session holds a placeholder, and any
            //    save attempted by other start-up code is refused instead of replacing the
            //    file on disk.
            LoadResult<AquariumSave> load = _session.Load();
            Debug.Log("[Demo] Load: " + load.Status + (load.Migration != null ? ", " + load.Migration : string.Empty));
            if (load.Sanitize != null)
            {
                for (int i = 0; i < load.Sanitize.Corrections.Count; i++)
                {
                    Debug.LogWarning("[Demo] Sanitizer corrected " + load.Sanitize.Corrections[i]);
                }
            }

            // 2. Cloud. The coordinator shares the pipeline with the session, so a restore
            //    reads a save exactly the way the load above did.
            ICloudSaveBackend backend = await CreateBackendAsync();
            if (this == null) return;

            _sync = new CloudSyncCoordinator<AquariumSave>(_session, pipeline, backend, clock);
            _sync.CloudNewerDetected += OnCloudNewerDetected;
            GetComponent<SaveSyncHost>().Bind(_session, _sync);

            // 3. Compare once at launch, so the question (if there is one) is asked before
            //    the player has invested in the local state. Uploads stay on hold from the
            //    moment a newer cloud save is found until it is answered.
            CloudCheckResult check = await _sync.CheckCloudAsync();
            if (this == null) return;

            Debug.Log("[Demo] Cloud check: " + check.Status);

            if (!load.IsUsable && check.Status != CloudCheckStatus.CloudIsNewer)
            {
                // A save exists that this build cannot use and there is no cloud copy to fall
                // back on. Nothing is written until the game decides; a real game would tell
                // the player here and offer SaveSession.ResetToNew as a deliberate choice.
                Debug.LogError("[Demo] The local save cannot be used (" + load.Status + ") and was left untouched.");
            }
        }

        private void OnCloudNewerDetected(CloudCheckResult check)
        {
            // A real game shows a dialog here. Say what each choice does to the player's
            // progress; a prompt that only states "the cloud save is newer" invites a tap on
            // the wrong button.
            Debug.Log(
                "[Demo] The cloud save (" + Describe(check.CloudUtcSeconds) + ") is newer than the local one (" +
                Describe(check.LocalUtcSeconds) + "). Choose 'Restore from cloud' or 'Keep local' in the context menu.");
        }

        [ContextMenu("Demo/Earn 100 coins and save")]
        private void EarnAndSave()
        {
            if (_session == null) return;

            _session.State.Rename("Keeper");
            _session.State.Wallet.Earn(100);
            Debug.Log("[Demo] Save: " + _session.Save() + ". Coins: " + _session.State.Wallet.Coins);
        }

        [ContextMenu("Demo/Restore from cloud")]
        private void RestoreFromCloud()
        {
            if (_sync != null) _ = RestoreAsync();
        }

        [ContextMenu("Demo/Keep local")]
        private void KeepLocal()
        {
            _sync?.KeepLocal();
            Debug.Log("[Demo] Keeping the local save. It replaces the cloud copy at the next upload.");
        }

        [ContextMenu("Demo/Simulate a reinstall (erase the local save)")]
        private void SimulateReinstall()
        {
            if (_session == null) return;

            // Uploads are suspended first so that nothing from this session reaches the cloud
            // afterwards: the point is to keep the cloud copy and lose the local one.
            _sync?.Suspend();
            Debug.Log(
                "[Demo] Local save erased: " + _session.Erase().IsOk +
                ". Leave Play mode and enter it again to start as a new install with a cloud save.");
        }

        [ContextMenu("Demo/Print state")]
        private void PrintState()
        {
            if (_session == null) return;

            AquariumSave save = _session.State;
            Debug.Log(
                "[Demo] " + save.KeeperName + ": " + save.Wallet.Coins + " coins, " + save.Tanks + " tank(s), schema v" +
                save.SchemaVersion + ", saved " + Describe(_session.LastSavedUtcSeconds) +
                (_sync != null && _sync.HasUnsyncedChanges ? ", upload pending" : string.Empty));
        }

        private async Awaitable RestoreAsync()
        {
            RestoreResult result = await _sync.RestoreAsync();
            if (this == null) return;

            Debug.Log("[Demo] Restore: " + result.Status + (result.Message != null ? " (" + result.Message + ")" : string.Empty));

            // After a successful restore the session holds a different state object. A game
            // has to rebuild whatever it derived from the old one; reloading the scene is the
            // simplest way to be sure nothing still points at it.
        }

#if SAVESYNC_PLAYFAB
        private async Task<ICloudSaveBackend> CreateBackendAsync()
        {
            // The title id comes from the PlayFab SDK settings of the project.
            var backend = new PlayFabEntityFileBackend("aquarium");
            CloudResult<string> signIn = await backend.SignInWithCustomIdAsync(
                SystemInfo.deviceUniqueIdentifier, destroyCancellationToken);
            Debug.Log("[Demo] PlayFab sign-in: " + signIn);
            return backend;
        }
#else
        private Task<ICloudSaveBackend> CreateBackendAsync()
        {
            string folder = Path.Combine(Application.persistentDataPath, "DemoCloud");
            return Task.FromResult<ICloudSaveBackend>(new LocalFolderCloudBackend(folder));
        }
#endif

        private static string Describe(long utcSeconds)
        {
            return utcSeconds <= 0
                ? "never"
                : DateTimeOffset.FromUnixTimeSeconds(utcSeconds).ToString("u");
        }
    }
}
