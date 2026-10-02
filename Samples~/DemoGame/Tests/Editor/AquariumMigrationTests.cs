using System.Text;
using NUnit.Framework;

namespace SaveSync.Samples.DemoGame.Tests
{
    /// <summary>
    /// The demo's two migrations, run on saves written out the way older builds stored them.
    /// </summary>
    public class AquariumMigrationTests
    {
        // Written by a build from before the version member, the tank count and the
        // decoration counts existed.
        private const string SchemaZeroSave =
            "{\"KeeperName\":\"Mira\",\"Wallet\":{\"Coins\":320,\"Pearls\":4}," +
            "\"Fish\":[{\"Species\":\"guppy\",\"Level\":3}]," +
            "\"Decorations\":[\"coral\",\"castle\",\"coral\"]}";

        // Written by a schema 1 build: it knows about tanks, decorations are still a list.
        private const string SchemaOneSave =
            "{\"SchemaVersion\":1,\"KeeperName\":\"Mira\",\"Wallet\":{\"Coins\":320,\"Pearls\":4}," +
            "\"Tanks\":3,\"Decorations\":[\"coral\",\"castle\",\"coral\"]}";

        private static SavePipeline<AquariumSave> Pipeline()
        {
            return AquariumSaves.CreatePipeline(new FixedClock(1_700_000_000));
        }

        [Test]
        public void SaveFromBeforeVersioning_IsBroughtToTheCurrentSchema()
        {
            PipelineResult<AquariumSave> result = Pipeline().Read(Encoding.UTF8.GetBytes(SchemaZeroSave));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Ok));
            Assert.That(result.Migration.FromVersion, Is.EqualTo(0));
            Assert.That(result.Migration.StepsApplied, Is.EqualTo(2));

            AquariumSave save = result.State;
            Assert.That(save.SchemaVersion, Is.EqualTo(2));
            Assert.That(save.Tanks, Is.EqualTo(1), "0 -> 1: every aquarium has a tank");
            Assert.That(save.DecorationCounts["coral"], Is.EqualTo(2), "1 -> 2: the list became counts");
            Assert.That(save.DecorationCounts["castle"], Is.EqualTo(1));
            Assert.That(save.Decorations, Is.Empty, "1 -> 2: and the list was emptied");

            Assert.That(save.KeeperName, Is.EqualTo("Mira"), "the rest of the save is untouched");
            Assert.That(save.Wallet.Coins, Is.EqualTo(320));
            Assert.That(save.Wallet.Pearls, Is.EqualTo(4));
            Assert.That(save.Fish, Has.Count.EqualTo(1));
            Assert.That(result.Sanitize.HasCorrections, Is.False);
        }

        [Test]
        public void SchemaOneSave_KeepsItsTanks_AndOnlyConvertsTheDecorations()
        {
            PipelineResult<AquariumSave> result = Pipeline().Read(Encoding.UTF8.GetBytes(SchemaOneSave));

            Assert.That(result.Migration.StepsApplied, Is.EqualTo(1));
            Assert.That(result.State.Tanks, Is.EqualTo(3));
            Assert.That(result.State.DecorationCounts["coral"], Is.EqualTo(2));
        }

        [Test]
        public void MigratedSave_WrittenAndReadAgain_IsNotMigratedTwice()
        {
            SavePipeline<AquariumSave> pipeline = Pipeline();
            AquariumSave migrated = pipeline.Read(Encoding.UTF8.GetBytes(SchemaZeroSave)).State;

            PipelineResult<AquariumSave> reread = pipeline.Read(pipeline.Write(migrated));

            Assert.That(reread.Migration.Status, Is.EqualTo(MigrationStatus.UpToDate));
            Assert.That(reread.State.DecorationCounts["coral"], Is.EqualTo(2));
        }

        [Test]
        public void EachStep_RunASecondTime_ChangesNothing()
        {
            SavePipeline<AquariumSave> pipeline = Pipeline();
            AquariumSave save = pipeline.Read(Encoding.UTF8.GetBytes(SchemaZeroSave)).State;
            byte[] migrated = pipeline.Write(save);

            AquariumMigrations.GiveEveryAquariumATank(save);
            AquariumMigrations.CountDecorations(save);

            Assert.That(pipeline.Write(save), Is.EqualTo(migrated));
        }

        [Test]
        public void GiveEveryAquariumATank_DoesNotTakeTanksAway()
        {
            var save = new AquariumSave { Tanks = 4 };

            AquariumMigrations.GiveEveryAquariumATank(save);

            Assert.That(save.Tanks, Is.EqualTo(4));
        }

        [Test]
        public void CountDecorations_AddsToCountsThatAlreadyExist_AndSkipsEmptyIds()
        {
            var save = new AquariumSave();
            save.AddDecoration("coral");
            save.Decorations.Add("coral");
            save.Decorations.Add(string.Empty);
            save.Decorations.Add(null);

            AquariumMigrations.CountDecorations(save);

            Assert.That(save.DecorationCounts, Has.Count.EqualTo(1));
            Assert.That(save.DecorationCounts["coral"], Is.EqualTo(2));
            Assert.That(save.Decorations, Is.Empty);
        }

        [Test]
        public void NewGame_StartsAtTheCurrentSchema_WithOneTank()
        {
            var store = new MemoryStore();
            var clock = new FixedClock(1_700_000_000);
            SaveSession<AquariumSave> session = AquariumSaves.CreateSession(store, AquariumSaves.CreatePipeline(clock), clock);

            session.Load();

            Assert.That(session.State.SchemaVersion, Is.EqualTo(AquariumMigrations.CreateMigrator().CurrentVersion));
            Assert.That(session.State.Tanks, Is.EqualTo(1));
        }

        [Test]
        public void NoSaveFileIsCreated_UntilThePlayerHasStarted()
        {
            var store = new MemoryStore();
            var clock = new FixedClock(1_700_000_000);
            SaveSession<AquariumSave> session = AquariumSaves.CreateSession(store, AquariumSaves.CreatePipeline(clock), clock);
            session.Load();

            SaveResult beforeStarting = session.Save();
            session.State.Rename("Mira");
            SaveResult afterStarting = session.Save();

            Assert.That(beforeStarting.Status, Is.EqualTo(SaveStatus.SkippedNothingToPersist));
            Assert.That(afterStarting.Status, Is.EqualTo(SaveStatus.Saved));
        }
    }
}
