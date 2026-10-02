using System;
using System.Text;
using NUnit.Framework;

namespace SaveSync.Tests
{
    public class SavePipelineTests
    {
        [Test]
        public void Read_Migrates_ThenSanitizes()
        {
            // A sanitizer that records the version it was handed proves the order: it must
            // only ever see the current schema.
            int versionSeenBySanitizer = -1;
            var pipeline = new SavePipeline<TestSave>(
                new JsonSaveSerializer<TestSave>(),
                TestSaves.Migrator(),
                new RecordingSanitizer(save => versionSeenBySanitizer = save.SchemaVersion));

            PipelineResult<TestSave> result = pipeline.Read(TestSaves.Bytes(TestSaves.Version0()));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Ok));
            Assert.That(result.Migration.StepsApplied, Is.EqualTo(2));
            Assert.That(versionSeenBySanitizer, Is.EqualTo(2));
        }

        [Test]
        public void Read_ReportsWhatTheSanitizerCorrected()
        {
            TestSave tampered = TestSaves.Current();
            tampered.Lives = 500;
            tampered.Score = -20;

            PipelineResult<TestSave> result = TestSaves.Pipeline().Read(TestSaves.Bytes(tampered));

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.State.Lives, Is.EqualTo(TestSanitizer.MaxLives));
            Assert.That(result.State.Score, Is.EqualTo(0));
            Assert.That(result.Sanitize.Corrections, Has.Count.EqualTo(2));
            Assert.That(result.Sanitize.Corrections[0].ToString(), Is.EqualTo("Lives: 500 -> 9"));
        }

        [Test]
        public void Read_CleanSave_ReportsNoCorrections()
        {
            PipelineResult<TestSave> result = TestSaves.Pipeline().Read(TestSaves.Bytes(TestSaves.Current()));

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.Sanitize.HasCorrections, Is.False);
            Assert.That(result.Migration.Status, Is.EqualTo(MigrationStatus.UpToDate));
        }

        [Test]
        public void Read_SaveNewerThanTheClient_IsRefused()
        {
            var newer = new TestSave { SchemaVersion = TestSaves.CurrentVersion + 1 };

            PipelineResult<TestSave> result = TestSaves.Pipeline().Read(TestSaves.Bytes(newer));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.NewerThanClient));
            Assert.That(result.State, Is.Null, "a refused save must not be handed out");
        }

        [Test]
        public void Read_TruncatedPayload_IsCorrupt()
        {
            byte[] whole = TestSaves.Bytes(TestSaves.Current());
            var half = new byte[whole.Length / 2];
            Array.Copy(whole, half, half.Length);

            PipelineResult<TestSave> result = TestSaves.Pipeline().Read(half);

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Corrupt));
            Assert.That(result.State, Is.Null);
        }

        [TestCase("")]
        [TestCase("null")]
        [TestCase("not json at all")]
        [TestCase("{\"SchemaVersion\":2} trailing")]
        [TestCase("{\"SchemaVersion\":-4}")]
        public void Read_UnusablePayload_IsCorrupt(string text)
        {
            PipelineResult<TestSave> result = TestSaves.Pipeline().Read(Encoding.UTF8.GetBytes(text));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.Corrupt));
        }

        [Test]
        public void Read_NullPayload_IsCorrupt()
        {
            Assert.That(TestSaves.Pipeline().Read(null).Status, Is.EqualTo(PipelineStatus.Corrupt));
        }

        [Test]
        public void Read_FailingMigration_IsReported_AndNoStateIsHandedOut()
        {
            SaveMigrator<TestSave> migrator = new SaveMigrator<TestSave>()
                .AddStep(0, "throws", save => throw new InvalidOperationException("boom"));
            var pipeline = new SavePipeline<TestSave>(new JsonSaveSerializer<TestSave>(), migrator);

            PipelineResult<TestSave> result = pipeline.Read(TestSaves.Bytes(new TestSave()));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.MigrationFailed));
            Assert.That(result.State, Is.Null);
            Assert.That(result.Migration.FailedStep, Is.EqualTo("throws"));
        }

        [Test]
        public void Read_FailingSanitizer_IsReported_AndNoStateIsHandedOut()
        {
            var pipeline = new SavePipeline<TestSave>(
                new JsonSaveSerializer<TestSave>(),
                TestSaves.Migrator(),
                new RecordingSanitizer(save => throw new InvalidOperationException("boom")));

            PipelineResult<TestSave> result = pipeline.Read(TestSaves.Bytes(TestSaves.Current()));

            Assert.That(result.Status, Is.EqualTo(PipelineStatus.SanitizerFailed));
            Assert.That(result.State, Is.Null);
        }

        [Test]
        public void Read_WithoutSanitizer_StillMigrates()
        {
            var pipeline = new SavePipeline<TestSave>(new JsonSaveSerializer<TestSave>(), TestSaves.Migrator());

            PipelineResult<TestSave> result = pipeline.Read(TestSaves.Bytes(TestSaves.Version0()));

            Assert.That(result.IsOk, Is.True);
            Assert.That(result.State.SchemaVersion, Is.EqualTo(2));
            Assert.That(result.Sanitize.HasCorrections, Is.False);
        }

        private sealed class RecordingSanitizer : ISanitizer<TestSave>
        {
            private readonly Action<TestSave> _onSanitize;

            public RecordingSanitizer(Action<TestSave> onSanitize)
            {
                _onSanitize = onSanitize;
            }

            public void Sanitize(TestSave state, SanitizeReport report)
            {
                _onSanitize(state);
            }
        }
    }
}
