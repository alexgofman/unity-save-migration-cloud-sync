using System.Collections.Generic;

namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// The schema history of <see cref="AquariumSave"/>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Schema 0: saves written before the version member existed.</item>
    /// <item>Schema 1: <see cref="AquariumSave.Tanks"/> was added. Old saves read 0 for it
    /// and the intended minimum is 1.</item>
    /// <item>Schema 2: decorations went from a list of ids to a count per id.</item>
    /// </list>
    /// To change the schema again, add a step from version 2 at the end. Never edit or
    /// reorder a step that has shipped: saves that already ran it would not run it again.
    /// </remarks>
    public static class AquariumMigrations
    {
        public static SaveMigrator<AquariumSave> CreateMigrator()
        {
            return new SaveMigrator<AquariumSave>()
                .AddStep(0, "Tanks: every aquarium has at least one", GiveEveryAquariumATank)
                .AddStep(1, "Decorations: list of ids -> count per id", CountDecorations);
        }

        /// <summary>
        /// A default-correction step: the member is new, so old saves carry the type's
        /// default instead of the game's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A property initializer would also give a missing member the right value, but it
        /// would not repair saves in which the 0 is already stored, and it would make the
        /// result depend on how the serializer treats missing members.
        /// </para>
        /// <para>
        /// Idempotent because it only writes when the value is below the minimum. A save
        /// that already has two tanks keeps two.
        /// </para>
        /// </remarks>
        public static void GiveEveryAquariumATank(AquariumSave save)
        {
            if (save.Tanks < 1) save.Tanks = 1;
        }

        /// <summary>
        /// A data-move step: the old list is folded into the new dictionary.
        /// </summary>
        /// <remarks>
        /// Idempotent because the old list is cleared once it has been counted, and an empty
        /// list is the guard. Without the clear, a second run would count every decoration
        /// twice.
        /// </remarks>
        public static void CountDecorations(AquariumSave save)
        {
            List<string> legacy = save.Decorations;
            if (legacy == null || legacy.Count == 0) return;

            for (int i = 0; i < legacy.Count; i++)
            {
                string id = legacy[i];
                if (string.IsNullOrEmpty(id)) continue;
                save.AddDecoration(id);
            }

            legacy.Clear();
        }
    }
}
