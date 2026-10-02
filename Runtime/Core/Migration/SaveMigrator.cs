using System;
using System.Collections.Generic;

namespace SaveSync
{
    /// <summary>
    /// Brings a save from the schema version it was written with up to the version this
    /// client understands, one registered step at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules the migrator enforces:
    /// </para>
    /// <list type="bullet">
    /// <item>Steps are registered in order with no gaps: the step from version N is the N-th
    /// one added. The client's schema version is therefore simply the number of steps, and
    /// cannot drift from the code that implements it.</item>
    /// <item>The migrator writes the version, after a step returns. A step cannot forget to
    /// bump it and cannot skip ahead.</item>
    /// <item>A save from a newer client is refused, untouched. An older client would drop the
    /// fields it does not know and write the save back still stamped with the newer version,
    /// so the newer client would later skip the very migrations that could have repaired
    /// it.</item>
    /// <item>A step that throws stops the chain. The state keeps the last version that
    /// completed, and the caller must not use or persist it.</item>
    /// </list>
    /// <para>
    /// The rule a step author has to keep: every step is idempotent. Guard on the data the
    /// step changes, never on the version number. A step can meet data that is already in the
    /// new shape: a save produced by current code but stamped with an older version, a chain
    /// that failed halfway and runs again on the next launch, or a backup taken in between.
    /// Running the step again must then change nothing.
    /// </para>
    /// </remarks>
    public sealed class SaveMigrator<TState> where TState : class, IVersionedState
    {
        private readonly List<MigrationStep<TState>> _steps = new List<MigrationStep<TState>>();

        /// <summary>The schema version this client reads and writes.</summary>
        public int CurrentVersion => _steps.Count;

        public IReadOnlyList<MigrationStep<TState>> Steps => _steps;

        /// <summary>
        /// Registers the step that takes a state from <paramref name="fromVersion"/> to the
        /// next version.
        /// </summary>
        /// <exception cref="ArgumentException">The step is out of order or leaves a gap.</exception>
        public SaveMigrator<TState> AddStep(int fromVersion, string description, Action<TState> apply)
        {
            if (apply == null) throw new ArgumentNullException(nameof(apply));
            if (fromVersion != _steps.Count)
            {
                throw new ArgumentException(
                    "Migration steps must be added in order without gaps: expected the step from version " +
                    _steps.Count + ", got the step from version " + fromVersion + ".",
                    nameof(fromVersion));
            }

            _steps.Add(new MigrationStep<TState>(fromVersion, description, apply));
            return this;
        }

        /// <summary>
        /// Runs every step the state has not been through yet. Never throws for a failing
        /// step; the outcome is in the result.
        /// </summary>
        public MigrationResult Migrate(TState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            int from = state.SchemaVersion;
            if (from < 0) return MigrationResult.InvalidVersion(from, CurrentVersion);
            if (from > CurrentVersion) return MigrationResult.NewerThanClient(from, CurrentVersion);
            if (from == CurrentVersion) return MigrationResult.UpToDate(from);

            for (int version = from; version < CurrentVersion; version++)
            {
                MigrationStep<TState> step = _steps[version];
                try
                {
                    step.Apply(state);
                }
                catch (Exception exception)
                {
                    // The failed step may have changed part of the state. That is safe only
                    // because steps are idempotent: the next attempt starts from this version
                    // again and the completed part is a no-op.
                    state.SchemaVersion = version;
                    return MigrationResult.StepFailed(from, version, CurrentVersion, step.Description, exception);
                }

                state.SchemaVersion = version + 1;
            }

            return MigrationResult.Migrated(from, CurrentVersion);
        }
    }
}
