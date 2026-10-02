using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// The save gate: which states may be written to disk, and what happens when one may not.
    /// </summary>
    public class SaveSessionTests
    {
        private FakeClock _clock;
        private InMemorySaveStore _store;

        [SetUp]
        public void SetUp()
        {
            _clock = new FakeClock();
            _store = new InMemorySaveStore();
        }

        [Test]
        public void SaveBeforeLoad_IsRefused_AndTheSaveOnDiskIsUntouched()
        {
            // Start-up order is the classic way to lose a save: something saves before the
            // load has run, and what it saves is the empty placeholder.
            byte[] onDisk = TestSaves.Bytes(TestSaves.Current(coins: 900));
            _store.Seed(onDisk, 111);
            SaveSession<TestSave> session = NewSession();

            SaveResult result = session.Save();

            Assert.That(result.Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));
            Assert.That(session.IsAuthoritative, Is.False);
            Assert.That(_store.Writes, Is.EqualTo(0));
            Assert.That(_store.Payload, Is.SameAs(onDisk));
        }

        [Test]
        public void SaveBeforeLoad_IsRefused_EvenWhenNothingIsOnDisk()
        {
            SaveSession<TestSave> session = NewSession();

            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));
            Assert.That(_store.Exists, Is.False);
        }

        [Test]
        public void StateBeforeLoad_IsAPlaceholder_NotNull()
        {
            SaveSession<TestSave> session = NewSession();

            Assert.That(session.State, Is.Not.Null);
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.None));
        }

        [Test]
        public void AfterLoad_SavingWorks_AndStampsTheSave()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current(coins: 900)), 111);
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();
            session.State.Purse.Add(100);
            _clock.UtcNowSeconds = 5000;
            SaveResult save = session.Save();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.Loaded));
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.LoadedFromDisk));
            Assert.That(save.Status, Is.EqualTo(SaveStatus.Saved));
            Assert.That(save.SavedAtUtcSeconds, Is.EqualTo(5000));
            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(5000));
            Assert.That(_store.SavedAtUtcSeconds, Is.EqualTo(5000));
            Assert.That(TestSaves.Parse(_store.Payload).Purse.Coins, Is.EqualTo(1000));
        }

        [Test]
        public void Load_TakesTheTimestampFromTheSave()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current()), 4242);
            SaveSession<TestSave> session = NewSession();

            session.Load();

            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(4242));
        }

        [Test]
        public void Load_MigratesAndSanitizes_BeforeTheStateIsVisible()
        {
            TestSave old = TestSaves.Version0(legacyPoints: 250);
            old.Lives = -3;
            _store.Seed(TestSaves.Bytes(old), 111);
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();

            Assert.That(load.Migration.StepsApplied, Is.EqualTo(2));
            Assert.That(session.State.SchemaVersion, Is.EqualTo(TestSaves.CurrentVersion));
            Assert.That(session.State.Lives, Is.EqualTo(1));
            Assert.That(session.State.Score, Is.EqualTo(250));
        }

        [Test]
        public void Load_WithNoSave_StartsANewStateAtTheCurrentSchema()
        {
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.NoSaveFound));
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.NoSaveOnDisk));
            Assert.That(session.State.SchemaVersion, Is.EqualTo(TestSaves.CurrentVersion),
                "a new save must not look like one from before versioning");
            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.Saved));
        }

        [Test]
        public void NoSaveYet_AndNothingWorthPersisting_IsSkipped()
        {
            SaveSession<TestSave> session = NewSession(worthPersisting: save => save.PlayerName.Length > 0);
            session.Load();

            SaveResult beforeTheNameIsChosen = session.Save();
            session.State.Rename("Ada");
            SaveResult afterwards = session.Save();

            Assert.That(beforeTheNameIsChosen.Status, Is.EqualTo(SaveStatus.SkippedNothingToPersist));
            Assert.That(afterwards.Status, Is.EqualTo(SaveStatus.Saved));
        }

        [Test]
        public void OnceASaveExists_ThatPredicateCanNoLongerBlockSaving()
        {
            // The trap: persistence gated on a piece of game data. Some code path clears the
            // field, the predicate turns false, and from then on every save is a no-op.
            SaveSession<TestSave> session = NewSession(worthPersisting: save => save.PlayerName.Length > 0);
            session.Load();
            session.State.Rename("Ada");
            session.Save();

            session.State.Rename(string.Empty);
            session.State.Purse.Add(50);
            SaveResult result = session.Save();

            Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
            Assert.That(TestSaves.Parse(_store.Payload).Purse.Coins, Is.EqualTo(50));
        }

        [Test]
        public void ThrowingPredicate_DoesNotBlockSaving()
        {
            SaveSession<TestSave> session = NewSession(worthPersisting: save => throw new InvalidOperationException("boom"));
            session.Load();

            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.Saved));
        }

        [Test]
        public void UnreadableSave_IsNotANewPlayer_NothingIsWrittenOverIt()
        {
            _store.Unreadable = true;
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();
            SaveResult save = session.Save();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.Corrupt));
            Assert.That(load.IsUsable, Is.False);
            Assert.That(session.IsAuthoritative, Is.False);
            Assert.That(save.Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));
            Assert.That(_store.Writes, Is.EqualTo(0));
            Assert.That(_store.Unreadable, Is.True, "the damaged save is still there to be recovered or inspected");
        }

        [Test]
        public void SaveThatCannotBeParsed_IsNotANewPlayerEither()
        {
            // The store's integrity check passed, but the payload is the start of a JSON
            // object and nothing more.
            byte[] garbage = { 0x7B, 0x22, 0x53 };
            _store.Seed(garbage, 111);
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.Corrupt));
            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));
            Assert.That(_store.Payload, Is.SameAs(garbage));
        }

        [Test]
        public void SaveNewerThanTheClient_IsNotOpened_AndNotOverwritten()
        {
            byte[] newer = TestSaves.Bytes(new TestSave { SchemaVersion = TestSaves.CurrentVersion + 1, Score = 777 });
            _store.Seed(newer, 111);
            SaveSession<TestSave> session = NewSession();

            LoadResult<TestSave> load = session.Load();
            SaveResult save = session.Save();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.NewerThanClient));
            Assert.That(session.State.Score, Is.EqualTo(0), "the newer save's data never reaches the game");
            Assert.That(save.Status, Is.EqualTo(SaveStatus.RefusedNotAuthoritative));
            Assert.That(_store.Payload, Is.SameAs(newer));
        }

        [Test]
        public void ResetToNew_IsTheDeliberateWayPastAnUnusableSave()
        {
            _store.Unreadable = true;
            SaveSession<TestSave> session = NewSession();
            session.Load();

            SaveResult reset = session.ResetToNew();

            Assert.That(reset.Status, Is.EqualTo(SaveStatus.Saved));
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.ExplicitReset));
            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.Saved));
        }

        [Test]
        public void InstallRestored_WritesEvenWhenNoSaveExists_AndOpensTheGate()
        {
            SaveSession<TestSave> session = NewSession();
            TestSave restored = TestSaves.Current(coins: 640);

            SaveResult result = session.InstallRestored(restored);

            Assert.That(result.Status, Is.EqualTo(SaveStatus.Saved));
            Assert.That(session.State, Is.SameAs(restored));
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.RestoredFromCloud));
            Assert.That(TestSaves.Parse(_store.Payload).Purse.Coins, Is.EqualTo(640));
        }

        [Test]
        public void InstallRestored_ThatCannotBeWritten_LeavesTheSessionAsItWas()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current(coins: 5)), 111);
            SaveSession<TestSave> session = NewSession();
            session.Load();
            TestSave before = session.State;
            _store.FailWrites = true;

            SaveResult result = session.InstallRestored(TestSaves.Current(coins: 640));

            Assert.That(result.Status, Is.EqualTo(SaveStatus.WriteFailed));
            Assert.That(session.State, Is.SameAs(before), "the game must not run on a state the disk does not hold");
            Assert.That(session.Authority, Is.EqualTo(SaveAuthority.LoadedFromDisk));
        }

        [Test]
        public void InstallRestored_RejectsAStateThatSkippedThePipeline()
        {
            SaveSession<TestSave> session = NewSession();

            Assert.Throws<ArgumentException>(() => session.InstallRestored(TestSaves.Version0()));
        }

        [Test]
        public void InstallRestored_TakesTheCloudCopysTimestamp_WhenThereIsOne()
        {
            SaveSession<TestSave> session = NewSession();
            _clock.UtcNowSeconds = 5000;

            SaveResult result = session.InstallRestored(TestSaves.Current(), cloudSavedAtUtcSeconds: 4200);

            Assert.That(result.SavedAtUtcSeconds, Is.EqualTo(4200));
            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(4200));
            Assert.That(_store.SavedAtUtcSeconds, Is.EqualTo(4200));
        }

        [Test]
        public void RaiseTimestamp_MovesTheStampOnDisk_AndLeavesTheContentAlone()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current(coins: 900)), 111);
            SaveSession<TestSave> session = NewSession();
            session.Load();
            session.State.Purse.Add(5);
            int savesRaised = 0;
            session.SaveCompleted += result => savesRaised++;

            bool raised = session.RaiseTimestamp(500);

            Assert.That(raised, Is.True);
            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(500));
            Assert.That(_store.SavedAtUtcSeconds, Is.EqualTo(500));
            Assert.That(TestSaves.Parse(_store.Payload).Purse.Coins, Is.EqualTo(900),
                "it rewrites what is on disk, not what is in memory");
            Assert.That(savesRaised, Is.EqualTo(0), "the content did not change, so this is not a save");
        }

        [Test]
        public void RaiseTimestamp_NeverMovesBackwards()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current()), 500);
            SaveSession<TestSave> session = NewSession();
            session.Load();

            Assert.That(session.RaiseTimestamp(400), Is.False);
            Assert.That(session.RaiseTimestamp(500), Is.False);
            Assert.That(_store.SavedAtUtcSeconds, Is.EqualTo(500));
            Assert.That(_store.Writes, Is.EqualTo(0));
        }

        [Test]
        public void RaiseTimestamp_BeforeLoad_TouchesNothing()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current()), 111);
            SaveSession<TestSave> session = NewSession();

            Assert.That(session.RaiseTimestamp(500), Is.False);
            Assert.That(_store.Writes, Is.EqualTo(0));
        }

        [Test]
        public void FailedWrite_IsReported_NotThrown_AndKeepsTheOldTimestamp()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current()), 111);
            SaveSession<TestSave> session = NewSession();
            session.Load();
            _store.FailWrites = true;

            SaveResult result = session.Save();

            Assert.That(result.Status, Is.EqualTo(SaveStatus.WriteFailed));
            Assert.That(result.IsFailure, Is.True);
            Assert.That(result.Error, Is.Not.Null);
            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(111));
        }

        [Test]
        public void SerializerThatThrows_IsReported_NotThrown_AndNothingIsWritten()
        {
            var pipeline = new SavePipeline<TestSave>(new ThrowingSerializer(), TestSaves.Migrator());
            var session = new SaveSession<TestSave>(_store, pipeline, () => new TestSave(), _clock);
            session.Load();

            SaveResult result = session.Save();

            Assert.That(result.Status, Is.EqualTo(SaveStatus.SerializationFailed));
            Assert.That(_store.Writes, Is.EqualTo(0));
        }

        [Test]
        public void EveryOutcome_IsRaised_SoASaveThatDidNotHappenIsVisible()
        {
            var outcomes = new List<SaveStatus>();
            SaveSession<TestSave> session = NewSession();
            session.SaveCompleted += result => outcomes.Add(result.Status);

            session.Save();
            session.Load();
            session.Save();
            _store.FailWrites = true;
            session.Save();

            Assert.That(outcomes, Is.EqualTo(new[]
            {
                SaveStatus.RefusedNotAuthoritative,
                SaveStatus.Saved,
                SaveStatus.WriteFailed
            }));
        }

        [Test]
        public void Erase_RemovesTheSave_AndTheProgressInMemory()
        {
            _store.Seed(TestSaves.Bytes(TestSaves.Current(coins: 900)), 111);
            SaveSession<TestSave> session = NewSession(worthPersisting: save => save.Purse.Coins > 0);
            session.Load();

            LocalWriteResult erased = session.Erase();
            SaveResult saveAfterwards = session.Save();

            Assert.That(erased.IsOk, Is.True);
            Assert.That(session.State.Purse.Coins, Is.EqualTo(0));
            Assert.That(session.LastSavedUtcSeconds, Is.EqualTo(0));
            Assert.That(saveAfterwards.Status, Is.EqualTo(SaveStatus.SkippedNothingToPersist),
                "nothing left in memory can bring the erased progress back");
            Assert.That(_store.Exists, Is.False);
        }

        [Test]
        public void Gate_StartsClosed_AndNeedsAReasonToOpen()
        {
            var gate = new SaveGate();

            Assert.That(gate.IsOpen, Is.False);
            Assert.That(gate.Decide(true, true), Is.EqualTo(SaveDecision.RefuseNotAuthoritative));
            Assert.Throws<ArgumentException>(() => gate.Open(SaveAuthority.None));
        }

        [Test]
        public void Gate_Open_WritesUnlessThereIsNothingToCreateASaveFor()
        {
            var gate = new SaveGate();
            gate.Open(SaveAuthority.LoadedFromDisk);

            Assert.That(gate.Decide(saveExistsOnDisk: true, stateIsWorthPersisting: false), Is.EqualTo(SaveDecision.Write));
            Assert.That(gate.Decide(saveExistsOnDisk: false, stateIsWorthPersisting: true), Is.EqualTo(SaveDecision.Write));
            Assert.That(gate.Decide(saveExistsOnDisk: false, stateIsWorthPersisting: false), Is.EqualTo(SaveDecision.SkipNothingToPersist));

            gate.Close();
            Assert.That(gate.Decide(saveExistsOnDisk: false, stateIsWorthPersisting: true), Is.EqualTo(SaveDecision.RefuseNotAuthoritative));
        }

        private SaveSession<TestSave> NewSession(Func<TestSave, bool> worthPersisting = null)
        {
            return new SaveSession<TestSave>(_store, TestSaves.Pipeline(), () => new TestSave(), _clock, worthPersisting);
        }

        private sealed class ThrowingSerializer : ISaveSerializer<TestSave>
        {
            public byte[] Serialize(TestSave state)
            {
                throw new InvalidOperationException("boom");
            }

            public TestSave Deserialize(byte[] payload)
            {
                throw new InvalidOperationException("boom");
            }
        }
    }
}
