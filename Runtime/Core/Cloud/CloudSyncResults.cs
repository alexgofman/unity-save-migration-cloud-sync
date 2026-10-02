using System;

namespace SaveSync
{
    /// <summary>Why uploads are currently not being started.</summary>
    [Flags]
    public enum PushHold
    {
        None = 0,

        /// <summary>
        /// The cloud copy may be the one to keep (it is newer, or a restore was asked for and
        /// did not complete). Uploads wait for <see cref="ICloudSync.RestoreAsync"/> to
        /// succeed or for <see cref="ICloudSync.KeepLocal"/>.
        /// </summary>
        ConflictPending = 1,

        /// <summary>The game called <see cref="ICloudSync.Suspend"/>.</summary>
        Suspended = 2
    }

    public enum CloudCheckStatus
    {
        /// <summary>The player has no save in the cloud.</summary>
        NoCloudSave,

        /// <summary>The cloud save is not newer than the local one. Nothing to decide.</summary>
        LocalIsCurrent,

        /// <summary>The cloud save is newer than the local one, or there is no usable local save.</summary>
        CloudIsNewer,

        /// <summary>The cloud could not be asked. Nothing is known.</summary>
        Unavailable
    }

    public readonly struct CloudCheckResult
    {
        private CloudCheckResult(
            CloudCheckStatus status, long cloudUtcSeconds, long localUtcSeconds, CloudError error, string message)
        {
            Status = status;
            CloudUtcSeconds = cloudUtcSeconds;
            LocalUtcSeconds = localUtcSeconds;
            Error = error;
            Message = message;
        }

        public CloudCheckStatus Status { get; }

        /// <summary>When the service stored the cloud save, as Unix seconds (UTC). Zero if none.</summary>
        public long CloudUtcSeconds { get; }

        /// <summary>When the local save was written, as Unix seconds (UTC). Zero if none is usable.</summary>
        public long LocalUtcSeconds { get; }

        public CloudError Error { get; }

        public string Message { get; }

        internal static CloudCheckResult NoCloudSave(long localUtcSeconds)
        {
            return new CloudCheckResult(CloudCheckStatus.NoCloudSave, 0, localUtcSeconds, CloudError.None, null);
        }

        internal static CloudCheckResult LocalIsCurrent(long cloudUtcSeconds, long localUtcSeconds)
        {
            return new CloudCheckResult(CloudCheckStatus.LocalIsCurrent, cloudUtcSeconds, localUtcSeconds, CloudError.None, null);
        }

        internal static CloudCheckResult CloudIsNewer(long cloudUtcSeconds, long localUtcSeconds)
        {
            return new CloudCheckResult(CloudCheckStatus.CloudIsNewer, cloudUtcSeconds, localUtcSeconds, CloudError.None, null);
        }

        internal static CloudCheckResult Unavailable(CloudError error, string message)
        {
            return new CloudCheckResult(CloudCheckStatus.Unavailable, 0, 0, error, message);
        }
    }

    public enum RestoreStatus
    {
        /// <summary>The cloud save is now the local save and the current state.</summary>
        Restored,

        /// <summary>There is nothing in the cloud to restore.</summary>
        NoCloudSave,

        DownloadFailed,

        /// <summary>The download could not be parsed.</summary>
        Corrupt,

        /// <summary>The cloud save was written by a newer client. Update the game to restore it.</summary>
        NewerThanClient,

        MigrationFailed,

        SanitizerFailed,

        /// <summary>The download was fine but could not be written to this device.</summary>
        LocalWriteFailed,

        /// <summary>Not signed in, or the coordinator was disposed.</summary>
        Unavailable
    }

    /// <summary>
    /// Outcome of <see cref="ICloudSync.RestoreAsync"/>. For every status except
    /// <see cref="RestoreStatus.Restored"/> the local save and the current state are exactly
    /// as they were.
    /// </summary>
    public sealed class RestoreResult
    {
        private RestoreResult(
            RestoreStatus status, MigrationResult migration, SanitizeReport sanitize, CloudError error, string message)
        {
            Status = status;
            Migration = migration;
            Sanitize = sanitize;
            Error = error;
            Message = message;
        }

        public RestoreStatus Status { get; }

        /// <summary>The migration the downloaded save went through, when it got that far.</summary>
        public MigrationResult Migration { get; }

        public SanitizeReport Sanitize { get; }

        public CloudError Error { get; }

        public string Message { get; }

        public bool IsRestored => Status == RestoreStatus.Restored;

        internal static RestoreResult Restored(MigrationResult migration, SanitizeReport sanitize)
        {
            return new RestoreResult(RestoreStatus.Restored, migration, sanitize, CloudError.None, null);
        }

        internal static RestoreResult NotRestored(
            RestoreStatus status, string message, CloudError error = CloudError.None, MigrationResult migration = null)
        {
            return new RestoreResult(status, migration, null, error, message);
        }
    }

    public enum PushOutcome
    {
        Uploaded,

        Failed,

        /// <summary>The upload did not finish within the request timeout.</summary>
        TimedOut,

        /// <summary>The upload was abandoned because a restore or a suspend took priority.</summary>
        Cancelled
    }

    /// <summary>What happened to one upload attempt, for logs and telemetry.</summary>
    public readonly struct PushReport
    {
        private PushReport(
            PushOutcome outcome, int sizeBytes, CloudError error, string message,
            int consecutiveFailures, double retryInSeconds)
        {
            Outcome = outcome;
            SizeBytes = sizeBytes;
            Error = error;
            Message = message;
            ConsecutiveFailures = consecutiveFailures;
            RetryInSeconds = retryInSeconds;
        }

        public PushOutcome Outcome { get; }

        public int SizeBytes { get; }

        public CloudError Error { get; }

        public string Message { get; }

        /// <summary>Attempts that have failed in a row, including this one.</summary>
        public int ConsecutiveFailures { get; }

        /// <summary>When the next attempt is due. Zero when none is scheduled by this outcome.</summary>
        public double RetryInSeconds { get; }

        public override string ToString()
        {
            if (Outcome == PushOutcome.Uploaded) return "Uploaded " + SizeBytes + " bytes";
            return Outcome + " (" + Error + (Message == null ? string.Empty : ": " + Message) + ")";
        }

        internal static PushReport Uploaded(int sizeBytes)
        {
            return new PushReport(PushOutcome.Uploaded, sizeBytes, CloudError.None, null, 0, 0);
        }

        internal static PushReport NotUploaded(
            PushOutcome outcome, CloudError error, string message, int consecutiveFailures, double retryInSeconds)
        {
            return new PushReport(outcome, 0, error, message, consecutiveFailures, retryInSeconds);
        }
    }
}
