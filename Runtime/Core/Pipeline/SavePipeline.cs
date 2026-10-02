using System;

namespace SaveSync
{
    /// <summary>
    /// The single path from bytes to a usable state: deserialize, check the version, migrate,
    /// sanitize.
    /// </summary>
    /// <remarks>
    /// A local load and a cloud restore both call <see cref="Read"/> on the same instance. A
    /// downloaded save is no more trustworthy than a file on disk, and a second read path
    /// with its own serializer settings or without the migration step is how the two drift
    /// apart until one of them loses data. Keeping one path makes that impossible to do by
    /// accident.
    /// </remarks>
    public sealed class SavePipeline<TState> where TState : class, IVersionedState
    {
        private readonly ISaveSerializer<TState> _serializer;
        private readonly SaveMigrator<TState> _migrator;
        private readonly ISanitizer<TState> _sanitizer;

        /// <param name="sanitizer">Optional. Without one, migrated states are used as they are.</param>
        public SavePipeline(
            ISaveSerializer<TState> serializer, SaveMigrator<TState> migrator, ISanitizer<TState> sanitizer = null)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
            _sanitizer = sanitizer;
        }

        /// <summary>The schema version this client reads and writes.</summary>
        public int CurrentVersion => _migrator.CurrentVersion;

        /// <summary>
        /// Turns a payload into a state that is safe to hand to the game. Never throws for bad
        /// input; the outcome is in the result.
        /// </summary>
        public PipelineResult<TState> Read(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return Failed(PipelineStatus.Corrupt, null, "The payload is empty.");
            }

            TState state;
            try
            {
                state = _serializer.Deserialize(payload);
            }
            catch (Exception exception)
            {
                return Failed(PipelineStatus.Corrupt, null, "The payload could not be parsed: " + exception.Message);
            }

            if (state == null)
            {
                return Failed(PipelineStatus.Corrupt, null, "The payload parsed to nothing.");
            }

            MigrationResult migration = _migrator.Migrate(state);
            switch (migration.Status)
            {
                case MigrationStatus.NewerThanClient:
                    return Failed(PipelineStatus.NewerThanClient, migration, migration.ToString());
                case MigrationStatus.InvalidVersion:
                    return Failed(PipelineStatus.Corrupt, migration, migration.ToString());
                case MigrationStatus.StepFailed:
                    return Failed(PipelineStatus.MigrationFailed, migration, migration.ToString());
            }

            var report = new SanitizeReport();
            if (_sanitizer != null)
            {
                try
                {
                    _sanitizer.Sanitize(state, report);
                }
                catch (Exception exception)
                {
                    // A half-sanitized state is worse than no state: refuse it and leave the
                    // source untouched so the fault can be fixed and the save read again.
                    return Failed(PipelineStatus.SanitizerFailed, migration, "The sanitizer threw: " + exception.Message);
                }
            }

            return new PipelineResult<TState>(PipelineStatus.Ok, state, migration, report, null);
        }

        /// <summary>Serializes a state. Throws whatever the serializer throws.</summary>
        public byte[] Write(TState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return _serializer.Serialize(state);
        }

        private static PipelineResult<TState> Failed(PipelineStatus status, MigrationResult migration, string error)
        {
            return new PipelineResult<TState>(status, null, migration, null, error);
        }
    }
}
