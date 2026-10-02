using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// A session and a coordinator wired to the in-memory store, the fake service and the
    /// fake clock, plus the few moves the coordinator tests repeat.
    /// </summary>
    internal sealed class SyncHarness : IDisposable
    {
        public const double PushInterval = 30;
        public const double RequestTimeout = 20;
        public const double RetryBase = 10;
        public const double RetryMax = 80;
        public const long Tolerance = 90;

        public readonly FakeClock Clock = new FakeClock();
        public readonly InMemorySaveStore Store = new InMemorySaveStore();
        public readonly FakeCloudBackend Backend;
        public readonly SavePipeline<TestSave> Pipeline = TestSaves.Pipeline();
        public readonly SaveSession<TestSave> Session;
        public readonly CloudSyncCoordinator<TestSave> Sync;
        public readonly List<PushReport> Pushes = new List<PushReport>();
        public readonly List<CloudCheckResult> NewerEvents = new List<CloudCheckResult>();

        /// <param name="localSave">Put on disk before the session loads. Null for a new install.</param>
        /// <param name="localAgeSeconds">How long ago that save was written.</param>
        /// <param name="load">False leaves the session unloaded, as during early start-up.</param>
        public SyncHarness(TestSave localSave = null, long localAgeSeconds = 0, bool load = true)
        {
            Backend = new FakeCloudBackend(Clock);
            if (localSave != null)
            {
                Store.Seed(TestSaves.Bytes(localSave), Clock.UtcNowSeconds - localAgeSeconds);
            }

            Session = new SaveSession<TestSave>(Store, Pipeline, () => new TestSave { Lives = 1 }, Clock);
            if (load) Session.Load();

            var options = new CloudSyncOptions
            {
                MinPushIntervalSeconds = PushInterval,
                RequestTimeoutSeconds = RequestTimeout,
                RetryBaseDelaySeconds = RetryBase,
                RetryMaxDelaySeconds = RetryMax,
                ClockSkewToleranceSeconds = Tolerance
            };
            Sync = new CloudSyncCoordinator<TestSave>(Session, Pipeline, Backend, Clock, options);
            Sync.PushCompleted += Pushes.Add;
            Sync.CloudNewerDetected += NewerEvents.Add;
        }

        /// <summary>
        /// A player whose local save and cloud copy are the same and whose cloud has already
        /// been looked at: nothing is owed, and the next save is free to be uploaded.
        /// </summary>
        public static SyncHarness ReadyToUpload()
        {
            var harness = new SyncHarness(TestSaves.Current());
            harness.SeedCloud(TestSaves.Current(), ageSeconds: 0);
            Assert.That(harness.Check().Status, Is.EqualTo(CloudCheckStatus.LocalIsCurrent));
            Assert.That(harness.Sync.HasUnsyncedChanges, Is.False);
            return harness;
        }

        public void Dispose()
        {
            Sync.Dispose();
        }

        public void Tick()
        {
            Sync.Tick();
        }

        /// <summary>Lets time pass, then gives the coordinator a turn.</summary>
        public void Advance(double seconds)
        {
            Clock.Advance(seconds);
            Sync.Tick();
        }

        /// <summary>Changes the state and saves it, as gameplay would.</summary>
        public SaveResult Save(long addCoins = 1)
        {
            Session.State.Purse.Add(addCoins);
            return Session.Save();
        }

        /// <summary>Puts a save into the fake service, stored the given time ago.</summary>
        public byte[] SeedCloud(TestSave save, long ageSeconds)
        {
            byte[] payload = TestSaves.Bytes(save);
            Backend.Seed(payload, Clock.UtcNowSeconds - ageSeconds);
            return payload;
        }

        /// <summary>Runs a cloud check to completion and returns its result.</summary>
        public CloudCheckResult Check()
        {
            Task<CloudCheckResult> check = Sync.CheckCloudAsync();
            Sync.Tick();
            return Completed(check);
        }

        /// <summary>Runs a restore to completion and returns its result.</summary>
        public RestoreResult Restore()
        {
            Task<RestoreResult> restore = Sync.RestoreAsync();
            Sync.Tick();
            return Completed(restore);
        }

        /// <summary>What the service holds, parsed.</summary>
        public TestSave CloudState()
        {
            return TestSaves.Parse(Backend.StoredPayload);
        }

        /// <summary>
        /// Reads the result of a task the coordinator should already have completed. Asserting
        /// first means a missing completion fails the test instead of blocking it.
        /// </summary>
        public static T Completed<T>(Task<T> task)
        {
            Assert.That(task.IsCompleted, Is.True, "The coordinator should have completed this task by now.");
            return task.Result;
        }
    }
}
