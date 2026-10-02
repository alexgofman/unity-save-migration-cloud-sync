using System.Collections.Generic;

namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// The save of a small invented aquarium game. It exists to show the package on a state
    /// with a realistic shape: balances behind private setters, collections, fields that
    /// were added after launch, and a field that changed its representation.
    /// </summary>
    public sealed class AquariumSave : IVersionedState
    {
        public int SchemaVersion { get; set; }

        public string KeeperName { get; private set; } = string.Empty;

        public Wallet Wallet { get; private set; } = new Wallet();

        public List<FishRecord> Fish { get; private set; } = new List<FishRecord>();

        /// <summary>
        /// Added in schema 1. A save from before reads 0 here, but every aquarium has at
        /// least one tank; the first migration step corrects that.
        /// </summary>
        public int Tanks { get; set; }

        /// <summary>
        /// Schemas 0 and 1: one entry per decoration owned. Replaced by
        /// <see cref="DecorationCounts"/> in schema 2 and kept only so old saves can be read.
        /// </summary>
        public List<string> Decorations { get; private set; } = new List<string>();

        /// <summary>Schema 2: how many of each decoration the player owns.</summary>
        public Dictionary<string, int> DecorationCounts { get; private set; } = new Dictionary<string, int>();

        public Dictionary<string, int> FoodStock { get; private set; } = new Dictionary<string, int>();

        public long LastFedUtcSeconds { get; set; }

        public long BoostEndsUtcSeconds { get; set; }

        /// <summary>The state of a player who has just installed the game.</summary>
        public static AquariumSave CreateNew()
        {
            return new AquariumSave { Tanks = 1 };
        }

        /// <summary>
        /// "Has the player begun?", for the rule that no save file is created before that.
        /// Any sign of progress counts, so that no single cleared field can switch saving
        /// off. A method, not a property, so that it is not written into the save.
        /// </summary>
        public bool HasStarted()
        {
            return KeeperName.Length > 0 || Fish.Count > 0 || Wallet.Coins > 0 || Wallet.Pearls > 0 || Tanks > 1;
        }

        public void Rename(string keeperName)
        {
            KeeperName = keeperName ?? string.Empty;
        }

        public void AddDecoration(string id)
        {
            if (DecorationCounts == null) DecorationCounts = new Dictionary<string, int>();
            DecorationCounts.TryGetValue(id, out int owned);
            DecorationCounts[id] = owned + 1;
        }

        public void AddFood(string kind, int amount)
        {
            if (FoodStock == null) FoodStock = new Dictionary<string, int>();
            FoodStock.TryGetValue(kind, out int stock);
            FoodStock[kind] = stock + amount;
        }

        /// <summary>
        /// Recreates members that a save can legitimately lack or carry as null: parts added
        /// after the save was written, or damaged data. A new instance is the correct state
        /// for each of them. The class repairs itself because only it can assign these
        /// members.
        /// </summary>
        public void RepairMissingParts(SanitizeReport report)
        {
            if (KeeperName == null)
            {
                KeeperName = string.Empty;
                report.Corrected(nameof(KeeperName), "null", "empty");
            }

            if (Wallet == null)
            {
                Wallet = new Wallet();
                report.Corrected(nameof(Wallet), "null", "new");
            }

            if (Fish == null)
            {
                Fish = new List<FishRecord>();
                report.Corrected(nameof(Fish), "null", "new");
            }

            if (Decorations == null) Decorations = new List<string>();

            if (DecorationCounts == null)
            {
                DecorationCounts = new Dictionary<string, int>();
                report.Corrected(nameof(DecorationCounts), "null", "new");
            }

            if (FoodStock == null)
            {
                FoodStock = new Dictionary<string, int>();
                report.Corrected(nameof(FoodStock), "null", "new");
            }
        }
    }

    /// <summary>
    /// Balances change only through methods, so the setters are private. This is the kind of
    /// object a default Json.NET read brings back empty.
    /// </summary>
    public sealed class Wallet
    {
        public long Coins { get; private set; }

        public long Pearls { get; private set; }

        public void Earn(long coins, long pearls = 0)
        {
            Coins += coins;
            Pearls += pearls;
        }

        public bool TrySpend(long coins)
        {
            if (coins < 0 || coins > Coins) return false;
            Coins -= coins;
            return true;
        }

        /// <summary>
        /// The wallet owns the answer to "what is a valid balance?", so the sanitizer asks it
        /// instead of repeating the rule.
        /// </summary>
        public void RemoveNegativeBalances(SanitizeReport report)
        {
            Coins = report.Clamp("Wallet.Coins", Coins, 0, long.MaxValue);
            Pearls = report.Clamp("Wallet.Pearls", Pearls, 0, long.MaxValue);
        }
    }

    public sealed class FishRecord
    {
        public string Species { get; set; }

        public int Level { get; set; }
    }
}
