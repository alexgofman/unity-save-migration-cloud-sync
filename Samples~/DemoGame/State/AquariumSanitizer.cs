using System.Collections.Generic;

namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// Limits for a loaded <see cref="AquariumSave"/>. The numbers belong to this invented
    /// game; a real one picks bounds that no legitimate player can reach.
    /// </summary>
    public sealed class AquariumSanitizer : ISanitizer<AquariumSave>
    {
        public const int MaxTanks = 6;
        public const int MaxFishLevel = 50;
        public const int MaxPerDecoration = 99;
        public const int MaxFoodPerKind = 9999;
        public const long MaxBoostSeconds = 7 * 24 * 60 * 60;

        private readonly IClock _clock;

        public AquariumSanitizer(IClock clock)
        {
            _clock = clock;
        }

        public void Sanitize(AquariumSave save, SanitizeReport report)
        {
            save.RepairMissingParts(report);
            save.Wallet.RemoveNegativeBalances(report);
            save.Tanks = report.Clamp(nameof(save.Tanks), save.Tanks, 1, MaxTanks);

            SanitizeFish(save.Fish, report);
            ClampCounts(save.DecorationCounts, nameof(save.DecorationCounts), MaxPerDecoration, report);
            ClampCounts(save.FoodStock, nameof(save.FoodStock), MaxFoodPerKind, report);

            // Timestamps in the save are compared with "now" by game logic. One that lies in
            // the future (or further ahead than the game can grant) would keep a timer or a
            // boost running indefinitely.
            long now = _clock.UtcNowSeconds;
            save.LastFedUtcSeconds = report.Clamp(nameof(save.LastFedUtcSeconds), save.LastFedUtcSeconds, 0, now);
            save.BoostEndsUtcSeconds = report.Clamp(
                nameof(save.BoostEndsUtcSeconds), save.BoostEndsUtcSeconds, 0, now + MaxBoostSeconds);
        }

        private static void SanitizeFish(List<FishRecord> fish, SanitizeReport report)
        {
            for (int i = fish.Count - 1; i >= 0; i--)
            {
                FishRecord one = fish[i];
                if (one == null || string.IsNullOrEmpty(one.Species))
                {
                    fish.RemoveAt(i);
                    report.Corrected("Fish[" + i + "]", "no species", "removed");
                    continue;
                }

                one.Level = report.Clamp("Fish[" + i + "].Level", one.Level, 1, MaxFishLevel);
            }
        }

        private static void ClampCounts(Dictionary<string, int> counts, string field, int max, SanitizeReport report)
        {
            // A dictionary cannot be changed while it is being enumerated, so collect first.
            List<string> outOfRange = null;
            foreach (KeyValuePair<string, int> entry in counts)
            {
                if (entry.Value < 0 || entry.Value > max)
                {
                    if (outOfRange == null) outOfRange = new List<string>();
                    outOfRange.Add(entry.Key);
                }
            }

            if (outOfRange == null) return;

            for (int i = 0; i < outOfRange.Count; i++)
            {
                string key = outOfRange[i];
                counts[key] = report.Clamp(field + "[" + key + "]", counts[key], 0, max);
            }
        }
    }
}
