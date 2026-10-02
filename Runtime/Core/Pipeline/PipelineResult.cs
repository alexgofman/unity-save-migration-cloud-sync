namespace SaveSync
{
    public enum PipelineStatus
    {
        /// <summary>The payload was read, migrated and sanitized. The state may be used.</summary>
        Ok,

        /// <summary>The payload is empty, not parseable, or carries an invalid version.</summary>
        Corrupt,

        /// <summary>The payload was written by a newer client and must not be opened by this one.</summary>
        NewerThanClient,

        /// <summary>A migration step threw.</summary>
        MigrationFailed,

        /// <summary>The sanitizer threw.</summary>
        SanitizerFailed
    }

    /// <summary>
    /// Outcome of <see cref="SavePipeline{TState}.Read"/>. <see cref="State"/> is set only
    /// when the status is <see cref="PipelineStatus.Ok"/>.
    /// </summary>
    public sealed class PipelineResult<TState>
    {
        internal PipelineResult(
            PipelineStatus status, TState state, MigrationResult migration, SanitizeReport sanitize, string error)
        {
            Status = status;
            State = state;
            Migration = migration;
            Sanitize = sanitize;
            Error = error;
        }

        public PipelineStatus Status { get; }

        public TState State { get; }

        /// <summary>Null when the payload could not be parsed at all.</summary>
        public MigrationResult Migration { get; }

        /// <summary>Null unless the sanitizer ran.</summary>
        public SanitizeReport Sanitize { get; }

        public string Error { get; }

        public bool IsOk => Status == PipelineStatus.Ok;
    }
}
