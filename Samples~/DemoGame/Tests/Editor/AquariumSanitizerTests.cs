using System.Text;
using NUnit.Framework;

namespace SaveSync.Samples.DemoGame.Tests
{
    public class AquariumSanitizerTests
    {
        private const long Now = 1_700_000_000;

        private static PipelineResult<AquariumSave> Read(string json)
        {
            return AquariumSaves.CreatePipeline(new FixedClock(Now)).Read(Encoding.UTF8.GetBytes(json));
        }

        [Test]
        public void ImpossibleValues_AreClamped_AndEachCorrectionIsReported()
        {
            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":2,\"Wallet\":{\"Coins\":-50,\"Pearls\":12},\"Tanks\":400," +
                "\"Fish\":[{\"Species\":\"guppy\",\"Level\":9000}]," +
                "\"DecorationCounts\":{\"coral\":5000,\"castle\":2}," +
                "\"FoodStock\":{\"flakes\":-3,\"pellets\":123456}}");

            AquariumSave save = result.State;
            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Ok));
            Assert.That(save.Wallet.Coins, Is.EqualTo(0));
            Assert.That(save.Wallet.Pearls, Is.EqualTo(12), "a valid value is left alone");
            Assert.That(save.Tanks, Is.EqualTo(AquariumSanitizer.MaxTanks));
            Assert.That(save.Fish[0].Level, Is.EqualTo(AquariumSanitizer.MaxFishLevel));
            Assert.That(save.DecorationCounts["coral"], Is.EqualTo(AquariumSanitizer.MaxPerDecoration));
            Assert.That(save.DecorationCounts["castle"], Is.EqualTo(2));
            Assert.That(save.FoodStock["flakes"], Is.EqualTo(0));
            Assert.That(save.FoodStock["pellets"], Is.EqualTo(AquariumSanitizer.MaxFoodPerKind));
            Assert.That(result.Sanitize.Corrections, Has.Count.EqualTo(6));
        }

        [Test]
        public void TimestampsAheadOfTheClock_AreBroughtBack()
        {
            long tenYears = 10L * 365 * 24 * 60 * 60;

            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":2,\"Tanks\":1," +
                "\"LastFedUtcSeconds\":" + (Now + 500) + "," +
                "\"BoostEndsUtcSeconds\":" + (Now + tenYears) + "}");

            Assert.That(result.State.LastFedUtcSeconds, Is.EqualTo(Now));
            Assert.That(result.State.BoostEndsUtcSeconds, Is.EqualTo(Now + AquariumSanitizer.MaxBoostSeconds));
        }

        [Test]
        public void BoostThatIsStillRunning_IsKept()
        {
            long endsInAnHour = Now + 3600;

            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":2,\"Tanks\":1,\"BoostEndsUtcSeconds\":" + endsInAnHour + "}");

            Assert.That(result.State.BoostEndsUtcSeconds, Is.EqualTo(endsInAnHour));
            Assert.That(result.Sanitize.HasCorrections, Is.False);
        }

        [Test]
        public void PartsStoredAsNull_AreRecreated()
        {
            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":2,\"Tanks\":1,\"KeeperName\":null,\"Wallet\":null,\"Fish\":null," +
                "\"DecorationCounts\":null,\"FoodStock\":null}");

            AquariumSave save = result.State;
            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Ok));
            Assert.That(save.KeeperName, Is.EqualTo(string.Empty));
            Assert.That(save.Wallet, Is.Not.Null);
            Assert.That(save.Fish, Is.Not.Null);
            Assert.That(save.DecorationCounts, Is.Not.Null);
            Assert.That(save.FoodStock, Is.Not.Null);
            Assert.That(result.Sanitize.Corrections, Has.Count.EqualTo(5));
        }

        [Test]
        public void OldSaveWithANullPart_StillMigrates()
        {
            // Migration runs before the sanitizer, so a step must cope with a missing part
            // on its own.
            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":1,\"Tanks\":1,\"DecorationCounts\":null,\"Decorations\":[\"coral\"]}");

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Ok));
            Assert.That(result.State.DecorationCounts["coral"], Is.EqualTo(1));
        }

        [Test]
        public void FishWithoutASpecies_AreRemoved()
        {
            PipelineResult<AquariumSave> result = Read(
                "{\"SchemaVersion\":2,\"Tanks\":1," +
                "\"Fish\":[null,{\"Species\":\"guppy\",\"Level\":2},{\"Species\":\"\",\"Level\":7}]}");

            Assert.That(result.State.Fish, Has.Count.EqualTo(1));
            Assert.That(result.State.Fish[0].Species, Is.EqualTo("guppy"));
        }

        [Test]
        public void ValidSave_IsLeftExactlyAsItIs()
        {
            var clock = new FixedClock(Now);
            var store = new MemoryStore();
            SavePipeline<AquariumSave> pipeline = AquariumSaves.CreatePipeline(clock);
            SaveSession<AquariumSave> session = AquariumSaves.CreateSession(store, pipeline, clock);
            session.Load();
            session.State.Rename("Mira");
            session.State.Wallet.Earn(320, 4);
            session.State.AddDecoration("coral");
            session.State.AddFood("flakes", 20);
            session.State.LastFedUtcSeconds = Now - 60;
            session.Save();

            PipelineResult<AquariumSave> result = pipeline.Read(store.Payload);

            Assert.That(result.Migration.Status, Is.EqualTo(MigrationStatus.UpToDate));
            Assert.That(result.Sanitize.HasCorrections, Is.False);
            Assert.That(pipeline.Write(result.State), Is.EqualTo(store.Payload));
        }
    }
}
