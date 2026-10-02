namespace SaveSync
{
    public enum LoadStatus
    {
        /// <summary>The save was read, migrated and sanitized.</summary>
        Loaded,

        /// <summary>No save on this device. The session holds a new state.</summary>
        NoSaveFound,

        /// <summary>A save is present but cannot be read.</summary>
        Corrupt,

        /// <summary>The save was written by a newer client.</summary>
        NewerThanClient,

        MigrationFailed,

        SanitizerFailed,

        /// <summary>The storage could not be read. The save itself may be fine.</summary>
        ReadFailed
    }

    /// <summary>
    /// Outcome of <see cref="SaveSession{TState}.Load"/>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="LoadStatus.Loaded"/> and <see cref="LoadStatus.NoSaveFound"/> leave
    /// the session able to save. Every other status means a save exists that this client
    /// could not use; the session keeps it untouched and refuses to write until the game
    /// decides what to do (restore from the cloud, or reset on purpose).
    /// </remarks>
    public sealed class LoadResult<TState>
    {
        internal LoadResult(
            LoadStatus status, TState state, MigrationResult migration, SanitizeReport sanitize,
            LocalCopy source, string error)
        {
            Status = status;
            State = state;
            Migration = migration;
            Sanitize = sanitize;
            Source = source;
            Error = error;
        }

        public LoadStatus Status { get; }

        /// <summary>The session's state after the load. A placeholder unless <see cref="IsUsable"/>.</summary>
        public TState State { get; }

        public MigrationResult Migration { get; }

        public SanitizeReport Sanitize { get; }

        /// <summary>
        /// Which copy was read. <see cref="LocalCopy.Backup"/> means the newest save was
        /// damaged and the previous one was used.
        /// </summary>
        public LocalCopy Source { get; }

        public string Error { get; }

        public bool IsUsable => Status == LoadStatus.Loaded || Status == LoadStatus.NoSaveFound;
    }
}
