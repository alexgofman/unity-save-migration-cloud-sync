using System;

namespace SaveSync
{
    /// <summary>
    /// The part of <see cref="SaveSession{TState}"/> that does not depend on the state type,
    /// for hosts and tools that only need to trigger and observe saves.
    /// </summary>
    public interface ISaveSession
    {
        /// <summary>True once the in-memory state may be written to disk.</summary>
        bool IsAuthoritative { get; }

        /// <summary>
        /// Timestamp stored with the save on disk, as Unix seconds (UTC). Zero when there is
        /// no usable save.
        /// </summary>
        long LastSavedUtcSeconds { get; }

        /// <summary>
        /// Raised after every call to <see cref="Save"/>, whatever the outcome. This is the
        /// one place where "the state changed and was persisted" is known, so cloud sync and
        /// telemetry both hang off it.
        /// </summary>
        event Action<SaveResult> SaveCompleted;

        SaveResult Save();
    }
}
