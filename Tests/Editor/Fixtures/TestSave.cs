using System.Collections.Generic;

namespace SaveSync.Tests
{
    /// <summary>
    /// A nested object whose only content is a scalar behind a private setter: the shape that
    /// a default Json.NET read silently resets.
    /// </summary>
    internal sealed class Purse
    {
        public long Coins { get; private set; }

        public void Add(long amount)
        {
            Coins += amount;
        }
    }

    /// <summary>
    /// The smallest state that still has everything the tests need: a version, a scalar
    /// behind a private setter at the root and in a nested object, a collection, and two
    /// fields with a migration history.
    /// </summary>
    internal sealed class TestSave : IVersionedState
    {
        public int SchemaVersion { get; set; }

        public string PlayerName { get; private set; } = string.Empty;

        /// <summary>Added in schema 1. Older saves read 0; the intended minimum is 1.</summary>
        public int Lives { get; set; }

        /// <summary>Replaced by <see cref="Score"/> in schema 2.</summary>
        public long LegacyPoints { get; set; }

        public long Score { get; set; }

        public Purse Purse { get; private set; } = new Purse();

        public List<string> Badges { get; private set; } = new List<string>();

        public void Rename(string playerName)
        {
            PlayerName = playerName;
        }
    }

    internal sealed class TestSanitizer : ISanitizer<TestSave>
    {
        public const int MaxLives = 9;

        public void Sanitize(TestSave state, SanitizeReport report)
        {
            state.Lives = report.Clamp("Lives", state.Lives, 1, MaxLives);
            state.Score = report.Clamp("Score", state.Score, 0, long.MaxValue);
        }
    }

    internal static class TestSaves
    {
        /// <summary>The schema version the test migrator ends at.</summary>
        public const int CurrentVersion = 2;

        public static SaveMigrator<TestSave> Migrator()
        {
            return new SaveMigrator<TestSave>()
                .AddStep(0, "Lives: raise to the minimum of 1", save =>
                {
                    if (save.Lives < 1) save.Lives = 1;
                })
                .AddStep(1, "LegacyPoints: fold into Score", save =>
                {
                    if (save.LegacyPoints == 0) return;
                    save.Score += save.LegacyPoints;
                    save.LegacyPoints = 0;
                });
        }

        public static SavePipeline<TestSave> Pipeline()
        {
            return new SavePipeline<TestSave>(new JsonSaveSerializer<TestSave>(), Migrator(), new TestSanitizer());
        }

        public static byte[] Bytes(TestSave save)
        {
            return new JsonSaveSerializer<TestSave>().Serialize(save);
        }

        public static TestSave Parse(byte[] payload)
        {
            return new JsonSaveSerializer<TestSave>().Deserialize(payload);
        }

        /// <summary>A save as a client from before versioning would have written it.</summary>
        public static TestSave Version0(string playerName = "Ada", long coins = 40, long legacyPoints = 250)
        {
            var save = new TestSave { SchemaVersion = 0, Lives = 0, LegacyPoints = legacyPoints };
            save.Rename(playerName);
            save.Purse.Add(coins);
            save.Badges.Add("first-steps");
            return save;
        }

        /// <summary>A save at the current schema.</summary>
        public static TestSave Current(string playerName = "Ada", long coins = 40)
        {
            var save = new TestSave { SchemaVersion = CurrentVersion, Lives = 3 };
            save.Rename(playerName);
            save.Purse.Add(coins);
            return save;
        }
    }
}
