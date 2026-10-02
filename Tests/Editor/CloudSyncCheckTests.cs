using System.Threading.Tasks;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// "Is the cloud save newer?" and what the coordinator does while that is undecided.
    /// </summary>
    public class CloudSyncCheckTests
    {
        [Test]
        public void NoSaveInTheCloud_NothingToDecide()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.None));
                Assert.That(h.NewerEvents, Is.Empty);
            }
        }

        [Test]
        public void CloudSaveOlderThanTheLocalOne_NothingToDecide()
        {
            using (var h = new SyncHarness(TestSaves.Current(), localAgeSeconds: 60))
            {
                h.SeedCloud(TestSaves.Current(), ageSeconds: 3600);

                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(check.LocalUtcSeconds, Is.EqualTo(h.Session.LastSavedUtcSeconds));
                Assert.That(check.CloudUtcSeconds, Is.EqualTo(h.Backend.StoredAtUtcSeconds));
                Assert.That(h.NewerEvents, Is.Empty);
            }
        }

        [TestCase(0, CloudCheckStatus.LocalIsCurrent)]
        [TestCase(90, CloudCheckStatus.LocalIsCurrent)]
        [TestCase(91, CloudCheckStatus.CloudIsNewer)]
        [TestCase(3600, CloudCheckStatus.CloudIsNewer)]
        public void CloudSave_CountsAsNewer_OnlyBeyondTheTolerance(int cloudAheadBySeconds, CloudCheckStatus expected)
        {
            using (var h = new SyncHarness(TestSaves.Current(), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(), ageSeconds: 7200 - cloudAheadBySeconds);

                Assert.That(h.Check().Status, Is.EqualTo(expected));
            }
        }

        [Test]
        public void ADevicesOwnUpload_IsNotMistakenForANewerSave()
        {
            // The cloud timestamp trails the local one by the wait plus the upload. That gap
            // is what the tolerance is for.
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save();
                h.Advance(30);
                h.Tick();

                Assert.That(h.Backend.StoredAtUtcSeconds - h.Session.LastSavedUtcSeconds, Is.EqualTo(30));
                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
            }
        }

        [Test]
        public void UploadThatLandsLate_MovesTheLocalTimestampUp_SoTheDeviceDoesNotAskAboutItsOwnSave()
        {
            // Three failed attempts push the upload 100 s past the save it carries: more than
            // the tolerance. Without following the cloud timestamp, the next launch would
            // offer this device its own save as "newer".
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.FailNextUploads = 3;
                h.Save();
                long savedAt = h.Session.LastSavedUtcSeconds;
                foreach (double wait in new double[] { 30, 10, 20, 40 })
                {
                    h.Advance(wait);
                    h.Tick();
                }

                Assert.That(h.Pushes[3].Outcome, Is.EqualTo(PushOutcome.Uploaded));
                Assert.That(h.Backend.StoredAtUtcSeconds, Is.EqualTo(savedAt + 100));
                Assert.That(h.Session.LastSavedUtcSeconds, Is.EqualTo(savedAt + 100), "the session follows the cloud timestamp");
                Assert.That(h.Store.SavedAtUtcSeconds, Is.EqualTo(savedAt + 100), "and so does the save on disk");
                Assert.That(TestSaves.Parse(h.Store.Payload).Purse.Coins, Is.EqualTo(41), "whose content is unchanged");
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False, "and which does not count as a new change");

                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(h.NewerEvents, Is.Empty);
            }
        }

        [Test]
        public void ProgressFromAnEarlierSession_ThatNeverReachedTheCloud_IsUploadedAfterTheFirstLook()
        {
            // The dirty flag lives in memory. A session that ended before its upload did
            // leaves a local save that is later than the cloud copy, and that is enough to
            // know an upload is owed.
            using (var h = new SyncHarness(TestSaves.Current(coins: 75), localAgeSeconds: 60))
            {
                h.SeedCloud(TestSaves.Current(coins: 40), ageSeconds: 3600);

                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                h.Advance(30);
                h.Tick();
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(75));
            }
        }

        [Test]
        public void LocalSaveWithNoCloudCopyYet_IsBackedUpAfterTheFirstLook()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 75), localAgeSeconds: 60))
            {
                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));

                h.Advance(30);
                h.Tick();

                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(75));
            }
        }

        [Test]
        public void NewInstall_WithNothingSavedYet_HasNothingToBackUp()
        {
            using (var h = new SyncHarness())
            {
                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));

                h.Advance(600);

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
            }
        }

        [Test]
        public void NewerCloudSave_RaisesTheEvent_AndPutsUploadsOnHold()
        {
            using (var h = new SyncHarness(TestSaves.Current(), localAgeSeconds: 7200))
            {
                byte[] cloud = h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);

                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.CloudIsNewer));
                Assert.That(h.NewerEvents, Has.Count.EqualTo(1));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));

                // The game keeps running and saving while the player reads the prompt.
                h.Save();
                h.Advance(30);
                h.Save();
                h.Advance(600);

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "no upload may replace the copy the player is being asked about");
                Assert.That(h.Backend.StoredPayload, Is.SameAs(cloud));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True, "and the local changes are not forgotten");
            }
        }

        [Test]
        public void KeepLocal_LiftsTheHold_AndTheLocalSaveReplacesTheCloudCopy()
        {
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();

                h.Sync.KeepLocal();
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.None));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                h.Advance(30);
                h.Tick();

                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(40));
            }
        }

        [Test]
        public void AfterKeepLocal_TheQuestionIsNotAskedAgain()
        {
            // The kept save is two hours old and nothing saves it again. Once it has been
            // uploaded, its timestamp follows the cloud copy's, so the next look finds the two
            // in agreement.
            using (var h = new SyncHarness(TestSaves.Current(coins: 40), localAgeSeconds: 7200))
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);
                h.Check();
                h.Sync.KeepLocal();
                h.Advance(30);
                h.Tick();

                Assert.That(h.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
                Assert.That(h.NewerEvents, Has.Count.EqualTo(1), "only the original detection");
            }
        }

        [Test]
        public void KeepLocal_WithoutAConflict_DoesNothing()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Sync.KeepLocal();
                h.Advance(600);

                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
            }
        }

        [Test]
        public void NewInstall_WithASaveInTheCloud_IsOfferedTheRestore()
        {
            using (var h = new SyncHarness())
            {
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);

                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.CloudIsNewer));
                Assert.That(check.LocalUtcSeconds, Is.EqualTo(0));
            }
        }

        [Test]
        public void LocalSaveThatCannotBeRead_CountsAsNoLocalSave()
        {
            // The file on disk is damaged. Whatever its timestamp said, the cloud copy is the
            // only usable one, so the restore has to be offered.
            using (var h = new SyncHarness(load: false))
            {
                h.Store.Unreadable = true;
                h.Session.Load();
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 86400);

                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.CloudIsNewer));
                Assert.That(check.LocalUtcSeconds, Is.EqualTo(0));
            }
        }

        [Test]
        public void ServiceThatCannotBeReached_IsUnavailable_NotNoCloudSave()
        {
            // "Nothing there" and "could not ask" must not look the same: the first allows
            // uploads, the second must not.
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.FailNextInfoRequests = 1;

                CloudCheckResult check = h.Check();

                Assert.That(check.Status, Is.EqualTo(CloudCheckStatus.Unavailable));
                Assert.That(check.Error, Is.EqualTo(CloudError.Network));

                h.Save();
                h.Sync.FlushNow();
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "the cloud is still unknown");
            }
        }

        [Test]
        public void CheckThatNeverAnswers_TimesOut()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.HoldRequests = true;
                Task<CloudCheckResult> check = h.Sync.CheckCloudAsync();

                h.Advance(19);
                Assert.That(check.IsCompleted, Is.False);

                h.Advance(1);
                CloudCheckResult result = SyncHarness.Completed(check);
                Assert.That(result.Status, Is.EqualTo(CloudCheckStatus.Unavailable));
                Assert.That(result.Error, Is.EqualTo(CloudError.Timeout));
                Assert.That(h.Backend.LastToken.IsCancellationRequested, Is.True);
            }
        }

        [Test]
        public void WhileSignedOut_TheCheckAnswersAtOnce_WithoutARequest()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.IsSignedIn = false;

                CloudCheckResult result = SyncHarness.Completed(h.Sync.CheckCloudAsync());

                Assert.That(result.Status, Is.EqualTo(CloudCheckStatus.Unavailable));
                Assert.That(result.Error, Is.EqualTo(CloudError.NotSignedIn));
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(0));
            }
        }

        [Test]
        public void TwoChecksAtOnce_ShareOneRequest()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.HoldRequests = true;
                Task<CloudCheckResult> first = h.Sync.CheckCloudAsync();
                Task<CloudCheckResult> second = h.Sync.CheckCloudAsync();

                h.Backend.ReleaseNext();
                h.Tick();

                Assert.That(h.Backend.InfoRequests, Is.EqualTo(1));
                Assert.That(SyncHarness.Completed(first).Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));
                Assert.That(SyncHarness.Completed(second).Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));
            }
        }

        [Test]
        public void CheckRequestedDuringAnUpload_WaitsForItsTurn()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);
                int looksBefore = h.Backend.InfoRequests;

                Task<CloudCheckResult> check = h.Sync.CheckCloudAsync();
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(looksBefore), "one request at a time");

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(looksBefore + 1));

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(SyncHarness.Completed(check).Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
            }
        }
    }
}
