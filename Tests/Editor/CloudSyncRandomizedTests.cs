using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// Random sequences of everything a game and a network can do to the coordinator, with
    /// the upload rules checked after every step. The seeds are fixed, so a failure names the
    /// sequence that produced it and can be replayed.
    /// </summary>
    public class CloudSyncRandomizedTests
    {
        private const int Sequences = 200;
        private const int StepsPerSequence = 60;

        [Test]
        public void RandomSequences_NeverBreakTheUploadRules_AndAlwaysSettle()
        {
            for (int seed = 0; seed < Sequences; seed++)
            {
                RunSequence(seed);
            }
        }

        private static void RunSequence(int seed)
        {
            var random = new Random(seed);
            string context = "seed " + seed;

            using (var h = new SyncHarness(random.Next(2) == 0 ? TestSaves.Current() : null, localAgeSeconds: 600))
            {
                if (random.Next(2) == 0) h.SeedCloud(TestSaves.Current(coins: 999), ageSeconds: random.Next(2) == 0 ? 60 : 7200);

                // The rules are checked at the moment an upload reaches the service. That
                // moment is inside the coordinator, which guards against a backend that
                // throws, so a violation is written down here and asserted outside.
                var violations = new List<string>();
                int looksAtLastResume = 0;
                h.Backend.UploadArrived = payload =>
                {
                    // One request at a time.
                    if (h.Backend.RequestsInFlight != 0) violations.Add("an upload overlapped another request");

                    // Never while it is undecided which copy wins, or while suspended.
                    if (h.Sync.Hold != PushHold.None) violations.Add("an upload started while on hold (" + h.Sync.Hold + ")");

                    // Only a save that is on this device, exactly as it is on disk.
                    if (!h.Session.IsAuthoritative) violations.Add("a placeholder was uploaded");
                    if (!SameBytes(payload, h.Store.Payload)) violations.Add("the upload is not the save on disk");

                    // Never before the cloud has been looked at, and again after a resume.
                    if (h.Backend.LooksAnswered <= looksAtLastResume) violations.Add("an upload started before the cloud was looked at");
                };

                for (int step = 0; step < StepsPerSequence; step++)
                {
                    switch (random.Next(14))
                    {
                        case 0:
                        case 1:
                        case 2:
                            h.Save();
                            break;
                        case 3:
                        case 4:
                            h.Advance(random.Next(1, 45));
                            break;
                        case 5:
                            h.Tick();
                            break;
                        case 6:
                            h.Backend.HoldRequests = !h.Backend.HoldRequests;
                            break;
                        case 7:
                            if (h.Backend.RequestsInFlight > 0) h.Backend.ReleaseNext();
                            break;
                        case 8:
                            h.Backend.IsSignedIn = random.Next(4) != 0;
                            break;
                        case 9:
                            if (random.Next(2) == 0) h.Backend.FailNextUploads = random.Next(3);
                            else h.Backend.FailNextInfoRequests = random.Next(2);
                            h.Backend.FailNextDownloads = random.Next(2);
                            break;
                        case 10:
                            h.Sync.FlushNow();
                            break;
                        case 11:
                            h.Sync.CheckCloudAsync();
                            break;
                        case 12:
                            if (random.Next(2) == 0) h.Sync.RestoreAsync();
                            else h.Sync.KeepLocal();
                            break;
                        default:
                            if (random.Next(2) == 0)
                            {
                                h.Sync.Suspend();
                            }
                            else
                            {
                                if ((h.Sync.Hold & PushHold.Suspended) != 0) looksAtLastResume = h.Backend.LooksAnswered;
                                h.Sync.Resume();
                            }

                            break;
                    }

                    h.Tick();
                    Assert.That(violations, Is.Empty, context + ", step " + step);
                    Assert.That(h.Backend.RequestsInFlight, Is.LessThanOrEqualTo(1), context + ", step " + step);
                }

                Settle(h, context);
            }
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        // Whatever happened, once the network works again and every question is answered the
        // coordinator must come to rest with the cloud holding exactly the local save.
        private static void Settle(SyncHarness h, string context)
        {
            h.Backend.HoldRequests = false;
            h.Backend.IgnoreCancellation = false;
            h.Backend.FailNextUploads = 0;
            h.Backend.FailNextInfoRequests = 0;
            h.Backend.FailNextDownloads = 0;
            h.Backend.IsSignedIn = true;
            h.Backend.UploadArrived = null;
            while (h.Backend.RequestsInFlight > 0) h.Backend.ReleaseNext();

            h.Sync.Resume();
            h.Tick();
            h.Sync.KeepLocal();
            for (int i = 0; i < 40; i++) h.Advance(60);

            Assert.That(h.Sync.Hold, Is.EqualTo(PushHold.None), context + ": still on hold after settling");
            Assert.That(h.Sync.IsRequestInFlight, Is.False, context + ": a request is still in flight after settling");

            if (h.Session.LastSavedUtcSeconds > 0)
            {
                Assert.That(h.Sync.HasUnsyncedChanges, Is.False, context + ": changes are still unsynced after settling");
                Assert.That(h.Backend.StoredPayload, Is.EqualTo(h.Store.Payload), context + ": the cloud does not hold the local save");
            }
        }
    }
}
