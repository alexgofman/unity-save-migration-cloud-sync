namespace SaveSync
{
    public enum SaveStatus
    {
        Saved,

        /// <summary>No save exists yet and the state holds nothing worth creating one for.</summary>
        SkippedNothingToPersist,

        /// <summary>
        /// The state has not been reconciled with the disk (see <see cref="SaveGate"/>).
        /// Nothing was written and the save on disk is intact.
        /// </summary>
        RefusedNotAuthoritative,

        /// <summary>The state could not be serialized. Nothing was written.</summary>
        SerializationFailed,

        /// <summary>The storage rejected the write. The previous save is intact.</summary>
        WriteFailed
    }

    /// <summary>
    /// Outcome of a save. A save that did not happen always says so: there is no outcome in
    /// which nothing is written and nothing is reported.
    /// </summary>
    public readonly struct SaveResult
    {
        private SaveResult(SaveStatus status, long savedAtUtcSeconds, int sizeBytes, string error)
        {
            Status = status;
            SavedAtUtcSeconds = savedAtUtcSeconds;
            SizeBytes = sizeBytes;
            Error = error;
        }

        public SaveStatus Status { get; }

        /// <summary>The timestamp stored with the save. Zero unless <see cref="IsSaved"/>.</summary>
        public long SavedAtUtcSeconds { get; }

        public int SizeBytes { get; }

        public string Error { get; }

        public bool IsSaved => Status == SaveStatus.Saved;

        /// <summary>True for the outcomes that mean something is broken and should be reported.</summary>
        public bool IsFailure => Status == SaveStatus.SerializationFailed || Status == SaveStatus.WriteFailed;

        public override string ToString()
        {
            return Error == null ? Status.ToString() : Status + ": " + Error;
        }

        internal static SaveResult Saved(long savedAtUtcSeconds, int sizeBytes)
        {
            return new SaveResult(SaveStatus.Saved, savedAtUtcSeconds, sizeBytes, null);
        }

        internal static SaveResult NotWritten(SaveStatus status, string error)
        {
            return new SaveResult(status, 0, 0, error);
        }
    }
}
