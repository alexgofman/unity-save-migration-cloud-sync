namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// Puts the pieces for <see cref="AquariumSave"/> together. One pipeline instance is
    /// shared by the session (local loads and saves) and the cloud coordinator (uploads and
    /// restores), which is what guarantees that both read a save the same way.
    /// </summary>
    public static class AquariumSaves
    {
        public static SavePipeline<AquariumSave> CreatePipeline(IClock clock)
        {
            return new SavePipeline<AquariumSave>(
                new JsonSaveSerializer<AquariumSave>(),
                AquariumMigrations.CreateMigrator(),
                new AquariumSanitizer(clock));
        }

        public static SaveSession<AquariumSave> CreateSession(
            ILocalSaveStore store, SavePipeline<AquariumSave> pipeline, IClock clock)
        {
            return new SaveSession<AquariumSave>(
                store, pipeline, AquariumSave.CreateNew, clock, save => save.HasStarted());
        }
    }
}
