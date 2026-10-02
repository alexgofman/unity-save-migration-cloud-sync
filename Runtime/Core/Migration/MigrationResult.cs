using System;

namespace SaveSync
{
    public enum MigrationStatus
    {
        /// <summary>The state was already at the client's schema version. Nothing ran.</summary>
        UpToDate,

        /// <summary>One or more steps ran and the state is now at the client's schema version.</summary>
        Migrated,

        /// <summary>The state was written by a newer client. Nothing was changed.</summary>
        NewerThanClient,

        /// <summary>The recorded version is negative, which no client ever writes.</summary>
        InvalidVersion,

        /// <summary>A step threw. The state stays at the last version that completed.</summary>
        StepFailed
    }

    /// <summary>
    /// What <see cref="SaveMigrator{TState}.Migrate"/> did to a state.
    /// </summary>
    public sealed class MigrationResult
    {
        private MigrationResult(
            MigrationStatus status, int fromVersion, int reachedVersion, int clientVersion,
            string failedStep, Exception error)
        {
            Status = status;
            FromVersion = fromVersion;
            ReachedVersion = reachedVersion;
            ClientVersion = clientVersion;
            FailedStep = failedStep;
            Error = error;
        }

        public MigrationStatus Status { get; }

        /// <summary>The version the state recorded before migration.</summary>
        public int FromVersion { get; }

        /// <summary>The version the state records now.</summary>
        public int ReachedVersion { get; }

        /// <summary>The schema version this client reads and writes.</summary>
        public int ClientVersion { get; }

        /// <summary>Description of the step that threw, when a step failed.</summary>
        public string FailedStep { get; }

        public Exception Error { get; }

        /// <summary>How many steps completed.</summary>
        public int StepsApplied => ReachedVersion > FromVersion ? ReachedVersion - FromVersion : 0;

        /// <summary>True when the state is at the client's version and may be used.</summary>
        public bool IsUsable => Status == MigrationStatus.UpToDate || Status == MigrationStatus.Migrated;

        public override string ToString()
        {
            switch (Status)
            {
                case MigrationStatus.UpToDate:
                    return "schema v" + ClientVersion + " (up to date)";
                case MigrationStatus.Migrated:
                    return "schema v" + FromVersion + " -> v" + ReachedVersion;
                case MigrationStatus.NewerThanClient:
                    return "schema v" + FromVersion + " is newer than this client (v" + ClientVersion + ")";
                case MigrationStatus.InvalidVersion:
                    return "schema v" + FromVersion + " is not a valid version";
                default:
                    return "step '" + FailedStep + "' failed at schema v" + ReachedVersion + ": " + Error?.Message;
            }
        }

        internal static MigrationResult UpToDate(int version)
        {
            return new MigrationResult(MigrationStatus.UpToDate, version, version, version, null, null);
        }

        internal static MigrationResult Migrated(int fromVersion, int clientVersion)
        {
            return new MigrationResult(MigrationStatus.Migrated, fromVersion, clientVersion, clientVersion, null, null);
        }

        internal static MigrationResult NewerThanClient(int fromVersion, int clientVersion)
        {
            return new MigrationResult(MigrationStatus.NewerThanClient, fromVersion, fromVersion, clientVersion, null, null);
        }

        internal static MigrationResult InvalidVersion(int fromVersion, int clientVersion)
        {
            return new MigrationResult(MigrationStatus.InvalidVersion, fromVersion, fromVersion, clientVersion, null, null);
        }

        internal static MigrationResult StepFailed(
            int fromVersion, int reachedVersion, int clientVersion, string failedStep, Exception error)
        {
            return new MigrationResult(
                MigrationStatus.StepFailed, fromVersion, reachedVersion, clientVersion, failedStep, error);
        }
    }
}
