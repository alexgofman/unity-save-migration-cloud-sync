using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// Restoring the cloud save: it goes through the same pipeline as a local load, it is all
    /// or nothing, and a restore that did not happen never turns into "local wins".
    /// </summary>
    public class CloudSyncRestoreTests
    {
        [Test]
        public void Restore_RunsTheSameMigrationAndSanitizerAsALocalLoad()
        {
            using (var h = new SyncHarness())
            {
                TestSave old = TestSaves.Version0(coins: 40, legacyPoints: 250);
                old.Lives = -3;
                h.SeedCloud(old, ageSeconds: 86400);

                RestoreResult result = h.Restore();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.Restored));
                Assert.That(result.Migration.StepsApplied, Is.EqualTo(2));
                TestSave state = h.Session.State;
                Assert.That(state.SchemaVersion, Is.EqualTo(TestSaves.CurrentVersion));
                Assert.That(state.Lives, Is.EqualTo(1));
                Assert.That(state.Score, Is.EqualTo(250));
                Assert.That(state.LegacyPoints, Is.EqualTo(0));
            }
        }

        [Test]
        public void Restore_KeepsEverythingBehindPrivateSetters()
        {
            // The restore that lost data read the download with different serializer settings
            // than a normal load. Here there is only one way to read a save.
            using (var h = new SyncHarness())
            {
                TestSave cloud = TestSaves.Current(playerName: "Ada", coins: 1234);
                cloud.Badges.Add("first-steps");
                h.SeedCloud(cloud, ageSeconds: 86400);

                h.Restore();

                Assert.That(h.Session.State.PlayerName, Is.EqualTo("Ada"));
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(1234));
                Assert.That(h.Session.State.Badges, Is.EqualTo(new[] { "first-steps" }));

                // And what was restored is what a later launch reads back.
                h.Save(addCoins: 1);
                var afterRelaunch = new SaveSession<TestSave>(h.Store, TestSaves.Pipeline(), () => new TestSave(), h.Clock);
                afterRelaunch.Load();
                Assert.That(afterRelaunch.State.PlayerName, Is.EqualTo("Ada"));
                Assert.That(afterRelaunch.State.Purse.Coins, Is.EqualTo(1235));
            }
        }

        [Test]
        public void Restore_OnANewInstall_CreatesTheLocalSave()
        {
            using (var h = new SyncHarness())
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);
                Assert.That(h.Store.Exists, Is.False);

                RestoreResult result = h.Restore();

                Assert.That(result.IsRestored, Is.True);
                Assert.That(h.Store.Exists, Is.True);
                Assert.That(h.Session.Authority, Is.EqualTo(SaveAuthority.RestoredFromCloud));
                Assert.That(TestSaves.Parse(h.Store.Payload).Purse.Coins, Is.EqualTo(999));
            }
        }

        [Test]
        public void Restore_ReplacesLocalProgress_LiftsTheHold_AndLeavesNothingToUpload()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));

                RestoreResult result = h.Restore();

                Assert.That(result.IsRestored, Is.True);
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(999));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.None));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);

                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "the two copies are equal; uploading it back would be pointless");
            }
        }

        [Test]
        public void AfterARestore_TheQuestionIsNotAskedAgain()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                h.Restore();

                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(h.NewerEvents, Has.Count.EqualTo(1));
            }
        }

        [Test]
        public void AfterARestore_OnADeviceWhoseClockRunsBehind_TheQuestionIsNotAskedAgain()
        {
            // The service stored the cloud save ten minutes into this device's future. A
            // restore stamped with the device clock alone would look older than the copy it
            // came from, and every launch would offer the same restore again.
            using (var h = new SyncHarness())
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: -600);

                h.Restore();

                Assert.That(h.Session.LastSavedUtcSeconds, Is.EqualTo(h.Backend.StoredAtUtcSeconds));
                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
            }
        }

        [Test]
        public void Restore_OfASaveNewerThanThisClient_IsRefused_AndNothingLocalChanges()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                var fromANewerClient = new TestSave { SchemaVersion = TestSaves.CurrentVersion + 1, Score = 777 };
                byte[] cloud = h.SeedCloud(fromANewerClient, ageSeconds: 3600);
                h.Check();
                byte[] localBefore = h.Store.Payload;
                TestSave stateBefore = h.Session.State;

                RestoreResult result = h.Restore();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.NewerThanClient));
                Assert.That(h.Session.State, Is.SameAs(stateBefore));
                Assert.That(h.Store.Payload, Is.SameAs(localBefore));
                Assert.That(h.Session.Authority, Is.EqualTo(SaveAuthority.LoadedFromDisk));

                // The older client must not overwrite the newer save either: it would replace
                // data it cannot read with data the newer client has moved on from.
                h.Save();
                h.Advance(600);
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.Backend.StoredPayload, Is.SameAs(cloud));
            }
        }

        [Test]
        public void Restore_OfADamagedDownload_IsRefused_AndNothingLocalChanges()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.Backend.Seed(Encoding.UTF8.GetBytes("{\"SchemaVersion\":2,\"Pur"), h.Clock.UtcNowSeconds - 3600);
                h.Check();
                TestSave stateBefore = h.Session.State;

                RestoreResult result = h.Restore();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.Corrupt));
                Assert.That(h.Session.State, Is.SameAs(stateBefore));
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(40));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));
            }
        }

        [Test]
        public void Restore_ThatCannotDownload_KeepsUploadsOnHold_AndCanBeTriedAgain()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                h.Backend.FailNextDownloads = 1;

                RestoreResult first = h.Restore();

                Assert.That(first.Status, Is.EqualTo(RestoreStatus.DownloadFailed));
                Assert.That(first.Error, Is.EqualTo(CloudError.Network));
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(40));

                h.Save();
                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "the copy the player asked for is still there");

                RestoreResult second = h.Restore();
                Assert.That(second.IsRestored, Is.True);
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(999));
            }
        }

        [Test]
        public void FailedRestore_NeverBecomesLocalWins_EvenWithoutAPriorConflict()
        {
            // Account recovery on a second device: the cloud save is older than the few
            // minutes of play on this device, so by timestamps "local is current". The player
            // asks for the cloud save and the download fails. Uploading now would destroy
            // exactly the save they came for.
            using (var h = new SyncHarness(TestSaves.Current(coins: 3), localAgeSeconds: 60))
            {
                byte[] cloud = h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);
                h.Sync.Suspend();
                h.Backend.FailNextDownloads = 1;

                RestoreResult result = h.Restore();
                h.Sync.Resume();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.DownloadFailed));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));

                h.Save();
                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.Backend.StoredPayload, Is.SameAs(cloud));
            }
        }

        [Test]
        public void Restore_WithNothingInTheCloud_SaysSo_AndLetsUploadsContinue()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40)))
            {
                RestoreResult result = h.Restore();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.NoCloudSave));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.None));

                h.Save();
                h.Advance(30);
                h.Tick();
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(41));
            }
        }

        [Test]
        public void Restore_WhenTheDiskRejectsTheWrite_ChangesNothing()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                TestSave stateBefore = h.Session.State;
                h.Store.FailWrites = true;

                RestoreResult result = h.Restore();

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.LocalWriteFailed));
                Assert.That(h.Session.State, Is.SameAs(stateBefore), "the game must not continue on a state the disk does not hold");
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));
            }
        }

        [Test]
        public void Restore_AbandonsAnUploadInFlight_SoItCannotReplaceTheCopyBeingRestored()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save(addCoins: 500);
                h.Advance(30);
                CancellationToken upload = h.Backend.LastToken;

                Task<RestoreResult> restore = h.Sync.RestoreAsync();

                Assert.That(upload.IsCancellationRequested, Is.True);
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Cancelled));

                h.Backend.ReleaseNext();
                h.Tick();

                Assert.That(SyncHarness.Completed(restore).IsRestored, Is.True);
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(40), "the cloud copy, not the local progress");
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(40));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);
            }
        }

        [Test]
        public void SavesMadeWhileTheRestoreWasDownloading_AreNotUploadedOverIt()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                h.Backend.HoldRequests = true;
                Task<RestoreResult> restore = h.Sync.RestoreAsync();

                h.Save();
                h.Advance(5);
                h.Backend.ReleaseNext();
                h.Tick();

                Assert.That(SyncHarness.Completed(restore).IsRestored, Is.True);
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(999));

                h.Backend.HoldRequests = false;
                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
            }
        }

        [Test]
        public void TwoRestoreCalls_ShareOneDownload()
        {
            using (var h = new SyncHarness())
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);
                h.Backend.HoldRequests = true;

                Task<RestoreResult> first = h.Sync.RestoreAsync();
                Task<RestoreResult> second = h.Sync.RestoreAsync();

                Assert.That(second, Is.SameAs(first));
                Assert.That(h.Backend.Downloads, Is.EqualTo(1));
            }
        }

        [Test]
        public void RestoreThatNeverAnswers_TimesOut_AndChangesNothing()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40)))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);
                h.Backend.HoldRequests = true;
                Task<RestoreResult> restore = h.Sync.RestoreAsync();

                h.Advance(20);

                RestoreResult result = SyncHarness.Completed(restore);
                Assert.That(result.Status, Is.EqualTo(RestoreStatus.DownloadFailed));
                Assert.That(result.Error, Is.EqualTo(CloudError.Timeout));
                Assert.That(h.Session.State.Purse.Coins, Is.EqualTo(40));
            }
        }

        [Test]
        public void WhileSignedOut_RestoreIsUnavailable_WithoutARequest()
        {
            using (var h = new SyncHarness())
            {
                h.Backend.IsSignedIn = false;

                RestoreResult result = SyncHarness.Completed(h.Sync.RestoreAsync());

                Assert.That(result.Status, Is.EqualTo(RestoreStatus.Unavailable));
                Assert.That(result.Error, Is.EqualTo(CloudError.NotSignedIn));
                Assert.That(h.Backend.Downloads, Is.EqualTo(0));
            }
        }

        [Test]
        public void Dispose_AnswersARestoreThatIsStillWaiting()
        {
            var h = new SyncHarness();
            h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);
            h.Backend.HoldRequests = true;
            Task<RestoreResult> restore = h.Sync.RestoreAsync();

            h.Sync.Dispose();

            Assert.That(SyncHarness.Completed(restore).Status, Is.EqualTo(RestoreStatus.Unavailable));
            Assert.That(h.Backend.LastToken.IsCancellationRequested, Is.True);
        }
    }
}
