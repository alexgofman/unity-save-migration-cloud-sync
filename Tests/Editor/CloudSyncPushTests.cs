using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// Uploads: coalescing, throttling, single flight, retry and timeout, driven with the
    /// fake clock and the fake service.
    /// </summary>
    public class CloudSyncPushTests
    {
        [Test]
        public void BurstOfSaves_BecomesOneUpload_AfterTheInterval()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save();
                h.Advance(5);
                h.Save();
                h.Advance(5);
                h.Save();

                h.Advance(19);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "29 s after the first save: still collecting");

                h.Advance(1);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1), "30 s after the first save: one upload");

                h.Tick();
                Assert.That(h.Pushes, Has.Count.EqualTo(1));
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Uploaded));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(43), "the one upload carries all three saves");

                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1), "nothing changed, so nothing more is sent");
            }
        }

        [Test]
        public void SavesDuringAnUpload_GetOneFollowUp_AndNeverASecondUploadInParallel()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);
                Assert.That(h.Sync.IsUploadInFlight, Is.True);

                h.Save();
                h.Advance(5);
                h.Save();
                h.Advance(5);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1), "saves during an upload must not start another");

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(h.Pushes, Has.Count.EqualTo(1));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True, "the saves made during the upload are not in it");

                h.Advance(29);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1), "the follow-up waits a full interval");

                h.Advance(1);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(2));

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(43));

                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(2), "one follow-up, not one per save");
            }
        }

        [Test]
        public void WhatIsUploaded_IsTheSaveOnDisk_NotUnsavedChangesInMemory()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save(addCoins: 10);
                h.Session.State.Purse.Add(500);

                h.Advance(30);
                h.Tick();

                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(50));
                Assert.That(h.Backend.StoredPayload, Is.EqualTo(h.Store.Payload), "byte for byte the local save");
            }
        }

        [Test]
        public void AnUpload_CarriesTheSaveAsItWasWhenItStarted()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save(addCoins: 10);
                h.Advance(30);

                h.Save(addCoins: 5);
                h.Backend.ReleaseNext();
                h.Tick();

                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(50));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);
            }
        }

        [Test]
        public void FailedUpload_IsRetried_WithoutWaitingForAnotherSave()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.FailNextUploads = 1;
                h.Save();
                h.Advance(30);
                h.Tick();

                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Failed));
                Assert.That(h.Pushes[0].Error, Is.EqualTo(CloudError.Network));
                Assert.That(h.Pushes[0].RetryInSeconds, Is.EqualTo(SyncHarness.RetryBase));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True, "a failed upload leaves the change unsynced");
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(40), "the cloud still holds the previous upload");

                // No save happens from here on. The retry has to come from the coordinator.
                h.Advance(9);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));

                h.Advance(1);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(2));

                h.Tick();
                Assert.That(h.Pushes[1].Outcome, Is.EqualTo(PushOutcome.Uploaded));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);
                Assert.That(h.Sync.ConsecutiveFailures, Is.EqualTo(0));
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(41));
            }
        }

        [Test]
        public void RetryDelay_DoublesWithEachFailure_UpToTheCap()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.FailNextUploads = 5;
                h.Save();
                h.Advance(30);
                h.Tick();

                double[] expectedDelays = { 10, 20, 40, 80, 80 };
                for (int attempt = 0; attempt < expectedDelays.Length; attempt++)
                {
                    PushReport report = h.Pushes[attempt];
                    Assert.That(report.Outcome, Is.EqualTo(PushOutcome.Failed));
                    Assert.That(report.ConsecutiveFailures, Is.EqualTo(attempt + 1));
                    Assert.That(report.RetryInSeconds, Is.EqualTo(expectedDelays[attempt]));

                    h.Advance(expectedDelays[attempt]);
                    h.Tick();
                }

                Assert.That(h.Pushes[5].Outcome, Is.EqualTo(PushOutcome.Uploaded));
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(6));
            }
        }

        [Test]
        public void UploadThatNeverAnswers_TimesOut_IsCancelled_AndRetried()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);
                CancellationToken token = h.Backend.LastToken;

                h.Advance(19);
                Assert.That(h.Pushes, Is.Empty, "19 s in: still waiting");
                Assert.That(token.IsCancellationRequested, Is.False);

                h.Advance(1);
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.TimedOut));
                Assert.That(h.Pushes[0].Error, Is.EqualTo(CloudError.Timeout));
                Assert.That(token.IsCancellationRequested, Is.True, "the backend is told to stop");
                Assert.That(h.Sync.IsUploadInFlight, Is.False);
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                // The slot is free again: one stuck request must not end uploads for the session.
                h.Advance(10);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(2));

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(h.Pushes[1].Outcome, Is.EqualTo(PushOutcome.Uploaded));
            }
        }

        [Test]
        public void AnswerThatArrivesAfterTheTimeout_IsIgnored()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Backend.IgnoreCancellation = true;
                h.Save();
                h.Advance(30);
                h.Advance(20);
                Assert.That(h.Pushes, Has.Count.EqualTo(1));
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.TimedOut));

                // The abandoned request completes after all.
                h.Backend.ReleaseNext();
                h.Tick();

                Assert.That(h.Pushes, Has.Count.EqualTo(1), "no second report for a request that was given up");
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True, "and the retry is still owed");
            }
        }

        [Test]
        public void AnswerThatArrivedInTime_IsNotDiscarded_WhenNobodyTickedForAWhile()
        {
            // A paused app does not tick. If the upload finished before the pause ended, its
            // result counts, however long ago it was started.
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);

                h.Backend.ReleaseNext();
                h.Clock.Advance(3600);
                h.Tick();

                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Uploaded));
            }
        }

        [Test]
        public void FlushNow_UploadsWithoutWaitingForTheInterval()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save();

                h.Sync.FlushNow();

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
                h.Tick();
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False);

                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1), "the countdown the flush replaced does not fire as well");
            }
        }

        [Test]
        public void FlushNow_DuringAnUpload_DoesNotStartASecondOne()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);
                h.Save();

                h.Sync.FlushNow();

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);
            }
        }

        [Test]
        public void FlushNow_WithNothingChanged_SendsNothing()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Sync.FlushNow();

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
            }
        }

        [Test]
        public void TheCloudIsLookedAt_BeforeTheFirstUpload()
        {
            // No explicit check was made, so the coordinator does not know what an upload
            // would overwrite. It has to find out first.
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Save();
                h.Advance(30);
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(1));
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));

                h.Tick();
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
                h.Tick();

                // Once is enough for the session.
                h.Save();
                h.Advance(30);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(2));
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(1));
            }
        }

        [Test]
        public void FlushNow_BeforeTheCloudHasBeenLookedAt_DoesNotUploadBlind()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Save();

                h.Sync.FlushNow();

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);
            }
        }

        [Test]
        public void TheFirstLook_FindingANewerCloudSave_HoldsTheUpload()
        {
            // Two devices. This one saved, then lost its connection before it could upload.
            // In the meantime the other one played on and uploaded.
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.IsSignedIn = false;
                h.Save();
                h.Clock.Advance(4000);
                byte[] fromTheOtherDevice = h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 400);

                h.Backend.IsSignedIn = true;
                h.Tick();
                h.Tick();

                Assert.That(h.NewerEvents, Has.Count.EqualTo(1));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.ConflictPending));

                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "the owed upload must not overwrite the newer save");
                Assert.That(h.Backend.StoredPayload, Is.SameAs(fromTheOtherDevice));
            }
        }

        [Test]
        public void FailedFirstLook_IsRetried_AndStillComesBeforeTheUpload()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.FailNextInfoRequests = 1;
                h.Save();
                h.Advance(30);
                h.Tick();
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0), "no upload while the cloud is unknown");
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                h.Advance(9);
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(1));
                h.Advance(1);
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(2));

                h.Tick();
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
            }
        }

        [Test]
        public void WhileSignedOut_NothingIsSent_AndTheChangeIsNotForgotten()
        {
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.IsSignedIn = false;
                h.Save();
                h.Advance(600);
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(0));
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                h.Backend.IsSignedIn = true;
                h.Tick();
                h.Tick();
                h.Tick();

                Assert.That(h.Pushes, Has.Count.EqualTo(1));
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Uploaded));
            }
        }

        [Test]
        public void StateThatWasNeverLoaded_IsNeverUploaded()
        {
            // A save exists on disk but could not be read, so the session holds a
            // placeholder. Whatever happens, that placeholder must not reach the cloud.
            using (var h = new SyncHarness(load: false))
            {
                h.Store.Unreadable = true;
                h.Session.Load();
                h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: 3600);

                h.Check();
                h.Sync.KeepLocal();
                h.Advance(600);

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(999));
            }
        }

        [Test]
        public void AfterTheLocalSaveWasErased_TheEmptyStateIsNotUploadedOverTheCloudCopy()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save(addCoins: 500);
                h.Session.Erase();

                h.Advance(600);

                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
                Assert.That(h.Pushes, Is.Empty, "there is nothing to upload, so there is no failed attempt either");
                Assert.That(h.CloudState().Purse.Coins, Is.EqualTo(40), "the backup is still what it was");
            }
        }

        [Test]
        public void BackendThatThrows_CountsAsAFailedUpload()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.ThrowOnUpload = true;
                h.Save();
                h.Advance(30);
                h.Tick();

                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Failed));
                Assert.That(h.Pushes[0].Error, Is.EqualTo(CloudError.Unexpected));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);
            }
        }

        [Test]
        public void Suspend_StopsUploads_AndAbandonsTheOneInFlight()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Backend.HoldRequests = true;
                h.Save();
                h.Advance(30);
                CancellationToken token = h.Backend.LastToken;

                h.Sync.Suspend();

                Assert.That(token.IsCancellationRequested, Is.True);
                Assert.That(h.Pushes[0].Outcome, Is.EqualTo(PushOutcome.Cancelled));
                Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.Suspended));
                Assert.That(h.Sync.HasUnsyncedChanges, Is.True);

                h.Save();
                h.Advance(600);
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
            }
        }

        [Test]
        public void Resume_LooksAtTheCloudAgain_BeforeUploading()
        {
            using (SyncHarness h = SyncHarness.ReadyToUpload())
            {
                h.Save();
                h.Sync.Suspend();
                h.Advance(600);
                int looksBefore = h.Backend.InfoRequests;

                h.Sync.Resume();
                h.Tick();
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(looksBefore + 1));
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));

                h.Tick();
                Assert.That(h.Backend.UploadsStarted, Is.EqualTo(1));
            }
        }

        [Test]
        public void Resume_DoesNotTrustALookThatWasStartedBeforeIt()
        {
            // The look below was asked while the backend still pointed at the old cloud save.
            // Its answer must not count as knowing what is there after the resume.
            using (var h = new SyncHarness(TestSaves.Current()))
            {
                h.Backend.HoldRequests = true;
                Task<CloudCheckResult> check = h.Sync.CheckCloudAsync();
                CancellationToken staleLook = h.Backend.LastToken;
                h.Sync.Suspend();

                h.Sync.Resume();
                h.Tick();

                Assert.That(staleLook.IsCancellationRequested, Is.True);
                Assert.That(h.Backend.InfoRequests, Is.EqualTo(2), "a new look replaces the stale one");
                Assert.That(check.IsCompleted, Is.False, "and the caller waits for the new answer");

                h.Backend.ReleaseNext();
                h.Tick();
                Assert.That(SyncHarness.Completed(check).Status, Is.EqualTo(CloudCheckStatus.NoCloudSave));
            }
        }

        [Test]
        public void AfterDispose_NothingIsSent_AndRequestsAreAnsweredAsUnavailable()
        {
            SyncHarness h = SyncHarness.ReadyToUpload();
            h.Sync.Dispose();

            h.Save();
            h.Advance(600);

            Assert.That(h.Backend.UploadsStarted, Is.EqualTo(0));
            Assert.That(SyncHarness.Completed(h.Sync.CheckCloudAsync()).Status, Is.EqualTo(CloudCheckStatus.Unavailable));
            Assert.That(SyncHarness.Completed(h.Sync.RestoreAsync()).Status, Is.EqualTo(RestoreStatus.Unavailable));
        }

        [Test]
        public void Options_RejectATolerance_ThatCannotCoverADevicesOwnUpload()
        {
            var options = new CloudSyncOptions
            {
                MinPushIntervalSeconds = 30,
                RequestTimeoutSeconds = 20,
                ClockSkewToleranceSeconds = 49
            };

            Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

            options.ClockSkewToleranceSeconds = 50;
            Assert.DoesNotThrow(() => options.Validate());
            Assert.DoesNotThrow(() => new CloudSyncOptions().Validate());
        }
    }
}
