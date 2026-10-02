using System;

namespace SaveSync
{
    /// <summary>
    /// One schema change: takes a state at <see cref="FromVersion"/> to <see cref="ToVersion"/>.
    /// </summary>
    public sealed class MigrationStep<TState>
    {
        internal MigrationStep(int fromVersion, string description, Action<TState> apply)
        {
            FromVersion = fromVersion;
            Description = description ?? string.Empty;
            Apply = apply;
        }

        public int FromVersion { get; }

        public int ToVersion => FromVersion + 1;

        /// <summary>What the step changes, for logs and failure reports.</summary>
        public string Description { get; }

        /// <summary>
        /// The change itself. It must be idempotent; see <see cref="SaveMigrator{TState}"/>.
        /// </summary>
        public Action<TState> Apply { get; }
    }
}
