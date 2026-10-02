using System;
using System.IO;
using NUnit.Framework;

namespace SaveSync.Samples.DemoGame.Tests
{
    /// <summary>
    /// The demo state through the real file store: save, "relaunch", load.
    /// </summary>
    public class AquariumRoundTripTests
    {
        private string _folder;

        [SetUp]
        public void CreateFolder()
        {
            _folder = Path.Combine(Path.GetTempPath(), "savesync-demo-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void RemoveFolder()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }

        [Test]
        public void EverythingThePlayerEarned_IsThereAfterARelaunch()
        {
            var clock = new FixedClock(1_700_000_000);
            SaveSession<AquariumSave> session = NewSession(clock);
            session.Load();
            session.State.Rename("Mira");
            session.State.Wallet.Earn(320, 4);
            session.State.Wallet.TrySpend(20);
            session.State.Tanks = 2;
            session.State.Fish.Add(new FishRecord { Species = "guppy", Level = 3 });
            session.State.AddDecoration("coral");
            session.State.AddDecoration("coral");
            session.State.AddFood("flakes", 20);

            Assert.That(session.Save().Status, Is.EqualTo(SaveStatus.Saved));

            SaveSession<AquariumSave> relaunched = NewSession(clock);
            LoadResult<AquariumSave> load = relaunched.Load();

            Assert.That(load.Status, Is.EqualTo(LoadStatus.Loaded));
            AquariumSave save = relaunched.State;
            Assert.That(save.KeeperName, Is.EqualTo("Mira"));
            Assert.That(save.Wallet.Coins, Is.EqualTo(300), "behind a private setter in a nested object");
            Assert.That(save.Wallet.Pearls, Is.EqualTo(4));
            Assert.That(save.Tanks, Is.EqualTo(2));
            Assert.That(save.Fish[0].Species, Is.EqualTo("guppy"));
            Assert.That(save.DecorationCounts["coral"], Is.EqualTo(2));
            Assert.That(save.FoodStock["flakes"], Is.EqualTo(20));
            Assert.That(relaunched.LastSavedUtcSeconds, Is.EqualTo(1_700_000_000));
        }

        [Test]
        public void HasStarted_IsNotWrittenIntoTheSave()
        {
            var clock = new FixedClock(1_700_000_000);
            SavePipeline<AquariumSave> pipeline = AquariumSaves.CreatePipeline(clock);

            string json = System.Text.Encoding.UTF8.GetString(pipeline.Write(AquariumSave.CreateNew()));

            Assert.That(json, Does.Not.Contain("HasStarted"));
            Assert.That(json, Does.StartWith("{\"SchemaVersion\":"));
        }

        private SaveSession<AquariumSave> NewSession(IClock clock)
        {
            var store = new AtomicFileSaveStore(_folder, "aquarium");
            return AquariumSaves.CreateSession(store, AquariumSaves.CreatePipeline(clock), clock);
        }
    }
}
