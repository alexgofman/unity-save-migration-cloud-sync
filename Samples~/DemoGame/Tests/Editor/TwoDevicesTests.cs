using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SaveSync.Samples.DemoGame.Tests
{
    /// <summary>
    /// The whole stack on real files: two installations, each with its own save folder,
    /// sharing one folder that stands in for the cloud.
    /// </summary>
    public class TwoDevicesTests
    {
        private string _root;

        [SetUp]
        public void CreateFolder()
        {
            _root = Path.Combine(Path.GetTempPath(), "savesync-devices-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void RemoveFolder()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void ProgressBackedUpOnOneDevice_IsRestoredOnANewInstall_AndStaysAfterARelaunch()
        {
            // Device A plays and backs up.
            using (Device a = NewDevice("device-a"))
            {
                Assert.That(a.Session.Load().Status, Is.EqualTo(LoadStatus.NoSaveFound));
                Assert.That(a.Check().Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));

                a.Session.State.Rename("Mira");
                a.Session.State.Wallet.Earn(320, 4);
                a.Session.State.AddDecoration("coral");
                Assert.That(a.Session.Save().Status, Is.EqualTo(SaveStatus.Saved));

                a.Sync.FlushNow();
                a.Sync.Tick();
                Assert.That(a.Sync.HasUnsyncedChanges, Is.False);
            }

            // Device B is a new install for the same player.
            using (Device b = NewDevice("device-b"))
            {
                Assert.That(b.Session.Load().Status, Is.EqualTo(LoadStatus.NoSaveFound));
                Assert.That(b.Check().Status, Is.EqualTo(CloudCheckStatus.CloudIsNewer));

                RestoreResult restore = b.Restore();

                Assert.That(restore.Status, Is.EqualTo(RestoreStatus.Restored));
                Assert.That(b.Session.State.KeeperName, Is.EqualTo("Mira"));
                Assert.That(b.Session.State.Wallet.Coins, Is.EqualTo(320));
                Assert.That(b.Session.State.Wallet.Pearls, Is.EqualTo(4));
                Assert.That(b.Session.State.DecorationCounts["coral"], Is.EqualTo(1));
            }

            // Device B again, after a relaunch: the save is on its own disk now and there is
            // nothing left to ask.
            using (Device b = NewDevice("device-b"))
            {
                Assert.That(b.Session.Load().Status, Is.EqualTo(LoadStatus.Loaded));
                Assert.That(b.Session.State.Wallet.Coins, Is.EqualTo(320));
                Assert.That(b.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(b.Sync.HasUnsyncedChanges, Is.False);
            }
        }

        [Test]
        public void SaveStartedBeforeLoading_NeverReachesTheDiskOrTheCloud()
        {
            using (Device a = NewDevice("device-a"))
            {
                a.Session.Load();
                a.Check();
                a.Session.State.Rename("Mira");
                a.Session.State.Wallet.Earn(320);
                a.Session.Save();
                a.Sync.FlushNow();
                a.Sync.Tick();
            }

            // Next launch: something saves before the load has run.
            using (Device a = NewDevice("device-a"))
            {
                SaveResult early = a.Session.Save();
                a.Sync.FlushNow();
                a.Sync.Tick();

                Assert.That(early.Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));

                a.Session.Load();
                Assert.That(a.Session.State.Wallet.Coins, Is.EqualTo(320), "the save on disk is intact");
                Assert.That(a.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
            }
        }

        private Device NewDevice(string name)
        {
            return new Device(Path.Combine(_root, name), Path.Combine(_root, "cloud"));
        }

        private sealed class Device : IDisposable
        {
            public readonly SaveSession<AquariumSave> Session;
            public readonly CloudSyncCoordinator<AquariumSave> Sync;

            public Device(string saveFolder, string cloudFolder)
            {
                var clock = new SystemClock();
                SavePipeline<AquariumSave> pipeline = AquariumSaves.CreatePipeline(clock);
                Session = AquariumSaves.CreateSession(new AtomicFileSaveStore(saveFolder, "aquarium"), pipeline, clock);
                Sync = new CloudSyncCoordinator<AquariumSave>(
                    Session, pipeline, new LocalFolderCloudBackend(cloudFolder), clock);
            }

            public CloudCheckResult Check()
            {
                return Finish(Sync.CheckCloudAsync());
            }

            public RestoreResult Restore()
            {
                return Finish(Sync.RestoreAsync());
            }

            public void Dispose()
            {
                Sync.Dispose();
            }

            private T Finish<T>(Task<T> task)
            {
                Sync.Tick();
                Assert.That(task.IsCompleted, Is.True, "The coordinator should have completed this task by now.");
                return task.Result;
            }
        }
    }
}
