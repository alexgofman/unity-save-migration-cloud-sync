using System;
using NUnit.Framework;

namespace SaveSync.Tests
{
    public class SaveMigratorTests
    {
        [Test]
        public void SaveFromBeforeVersioning_RunsEveryStepInOrder()
        {
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();
            TestSave save = TestSaves.Version0(legacyPoints: 250);

            MigrationResult result = migrator.Migrate(save);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.Migrated));
            Assert.That(result.FromVersion, Is.EqualTo(0));
            Assert.That(result.ReachedVersion, Is.EqualTo(2));
            Assert.That(result.StepsApplied, Is.EqualTo(2));
            Assert.That(save.SchemaVersion, Is.EqualTo(2));
            Assert.That(save.Lives, Is.EqualTo(1), "step 0 -> 1");
            Assert.That(save.Score, Is.EqualTo(250), "step 1 -> 2");
            Assert.That(save.LegacyPoints, Is.EqualTo(0), "step 1 -> 2 clears what it moved");
        }

        [Test]
        public void SaveAtVersion1_RunsOnlyTheRemainingStep()
        {
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();

            // Lives is 0 here on purpose: if step 0 -> 1 ran again it would raise it to 1.
            var save = new TestSave { SchemaVersion = 1, Lives = 0, LegacyPoints = 70 };

            MigrationResult result = migrator.Migrate(save);

            Assert.That(result.StepsApplied, Is.EqualTo(1));
            Assert.That(save.SchemaVersion, Is.EqualTo(2));
            Assert.That(save.Lives, Is.EqualTo(0), "the step from version 0 must not run for a version 1 save");
            Assert.That(save.Score, Is.EqualTo(70));
        }

        [Test]
        public void SaveAtCurrentVersion_IsLeftAlone()
        {
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();
            var save = new TestSave { SchemaVersion = 2, Lives = 0, LegacyPoints = 70 };

            MigrationResult result = migrator.Migrate(save);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.UpToDate));
            Assert.That(result.StepsApplied, Is.EqualTo(0));
            Assert.That(save.Lives, Is.EqualTo(0));
            Assert.That(save.LegacyPoints, Is.EqualTo(70));
        }

        [Test]
        public void MigratingTwice_ChangesNothingTheSecondTime()
        {
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();
            TestSave save = TestSaves.Version0(legacyPoints: 250);
            migrator.Migrate(save);
            byte[] afterFirst = TestSaves.Bytes(save);

            MigrationResult second = migrator.Migrate(save);

            Assert.That(second.Status, Is.EqualTo(MigrationStatus.UpToDate));
            Assert.That(TestSaves.Bytes(save), Is.EqualTo(afterFirst));
        }

        [Test]
        public void EveryStep_IsIdempotent_OnDataAlreadyInTheNewShape()
        {
            // The case the router cannot protect against: data in the new shape that is
            // stamped with an old version. Each step runs again and has to be a no-op.
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();
            TestSave save = TestSaves.Version0(legacyPoints: 250);
            migrator.Migrate(save);
            byte[] migrated = TestSaves.Bytes(save);

            save.SchemaVersion = 0;
            MigrationResult again = migrator.Migrate(save);

            Assert.That(again.StepsApplied, Is.EqualTo(2), "both steps ran a second time");
            Assert.That(TestSaves.Bytes(save), Is.EqualTo(migrated), "and changed nothing");
        }

        [Test]
        public void SaveNewerThanTheClient_IsRefusedUntouched()
        {
            SaveMigrator<TestSave> migrator = TestSaves.Migrator();
            var save = new TestSave { SchemaVersion = 3, Lives = 0, LegacyPoints = 70 };

            MigrationResult result = migrator.Migrate(save);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.NewerThanClient));
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.ClientVersion, Is.EqualTo(2));
            Assert.That(save.SchemaVersion, Is.EqualTo(3), "the version must not be lowered");
            Assert.That(save.Lives, Is.EqualTo(0));
            Assert.That(save.LegacyPoints, Is.EqualTo(70));
        }

        [Test]
        public void NegativeVersion_IsRejected()
        {
            var save = new TestSave { SchemaVersion = -1 };

            MigrationResult result = TestSaves.Migrator().Migrate(save);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.InvalidVersion));
            Assert.That(result.IsUsable, Is.False);
        }

        [Test]
        public void TheMigratorSetsTheVersion_NotTheStep()
        {
            // The step below "forgets" the version and even tries to set a wrong one.
            SaveMigrator<TestSave> migrator = new SaveMigrator<TestSave>()
                .AddStep(0, "forgets the version", save => { })
                .AddStep(1, "sets a wrong version", save => save.SchemaVersion = 99);
            var state = new TestSave();

            MigrationResult result = migrator.Migrate(state);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.Migrated));
            Assert.That(state.SchemaVersion, Is.EqualTo(2));
        }

        [Test]
        public void FailingStep_StopsTheChain_AtTheLastCompletedVersion()
        {
            bool laterStepRan = false;
            SaveMigrator<TestSave> migrator = new SaveMigrator<TestSave>()
                .AddStep(0, "completes", save => save.Lives = 1)
                .AddStep(1, "throws", save => throw new InvalidOperationException("boom"))
                .AddStep(2, "never reached", save => laterStepRan = true);
            var state = new TestSave();

            MigrationResult result = migrator.Migrate(state);

            Assert.That(result.Status, Is.EqualTo(MigrationStatus.StepFailed));
            Assert.That(result.IsUsable, Is.False);
            Assert.That(result.FailedStep, Is.EqualTo("throws"));
            Assert.That(result.Error, Is.TypeOf<InvalidOperationException>());
            Assert.That(result.StepsApplied, Is.EqualTo(1));
            Assert.That(state.SchemaVersion, Is.EqualTo(1), "the next attempt resumes from the failed step");
            Assert.That(laterStepRan, Is.False);
        }

        [Test]
        public void StepsMustBeAddedInOrder_WithoutGaps()
        {
            var migrator = new SaveMigrator<TestSave>();
            migrator.AddStep(0, "first", save => { });

            Assert.Throws<ArgumentException>(() => migrator.AddStep(2, "skips version 1", save => { }));
            Assert.Throws<ArgumentException>(() => migrator.AddStep(0, "registered twice", save => { }));
            Assert.That(migrator.CurrentVersion, Is.EqualTo(1));
        }

        [Test]
        public void CurrentVersion_IsTheNumberOfSteps()
        {
            Assert.That(new SaveMigrator<TestSave>().CurrentVersion, Is.EqualTo(0));
            Assert.That(TestSaves.Migrator().CurrentVersion, Is.EqualTo(TestSaves.CurrentVersion));
        }
    }
}
