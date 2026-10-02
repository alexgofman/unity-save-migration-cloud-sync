namespace SaveSync
{
    public enum LocalReadStatus
    {
        Ok,

        /// <summary>No save has ever been completed on this device.</summary>
        NotFound,

        /// <summary>A save is present but no copy of it passes its integrity check.</summary>
        Corrupt,

        /// <summary>The storage could not be read. The save itself may be fine.</summary>
        ReadFailed
    }

    /// <summary>Which copy of the save a read was served from.</summary>
    public enum LocalCopy
    {
        None,

        /// <summary>The save file itself: the normal case.</summary>
        Main,

        /// <summary>
        /// A complete save that had been written but not yet moved into place when the
        /// process stopped. It is the newest copy.
        /// </summary>
        Pending,

        /// <summary>The previous save, used because the newest one failed its integrity check.</summary>
        Backup
    }

    public sealed class LocalReadResult
    {
        private LocalReadResult(LocalReadStatus status, byte[] payload, long savedAtUtcSeconds, LocalCopy source, string error)
        {
            Status = status;
            Payload = payload;
            SavedAtUtcSeconds = savedAtUtcSeconds;
            Source = source;
            Error = error;
        }

        public LocalReadStatus Status { get; }

        public byte[] Payload { get; }

        /// <summary>The timestamp stored with the payload.</summary>
        public long SavedAtUtcSeconds { get; }

        public LocalCopy Source { get; }

        public string Error { get; }

        public static LocalReadResult Ok(byte[] payload, long savedAtUtcSeconds, LocalCopy source = LocalCopy.Main)
        {
            return new LocalReadResult(LocalReadStatus.Ok, payload, savedAtUtcSeconds, source, null);
        }

        public static LocalReadResult NotFound()
        {
            return new LocalReadResult(LocalReadStatus.NotFound, null, 0, LocalCopy.None, null);
        }

        public static LocalReadResult Corrupt(string error)
        {
            return new LocalReadResult(LocalReadStatus.Corrupt, null, 0, LocalCopy.None, error);
        }

        public static LocalReadResult ReadFailed(string error)
        {
            return new LocalReadResult(LocalReadStatus.ReadFailed, null, 0, LocalCopy.None, error);
        }
    }

    public readonly struct LocalWriteResult
    {
        private LocalWriteResult(bool isOk, string error)
        {
            IsOk = isOk;
            Error = error;
        }

        public bool IsOk { get; }

        public string Error { get; }

        public static LocalWriteResult Ok()
        {
            return new LocalWriteResult(true, null);
        }

        public static LocalWriteResult Failed(string error)
        {
            return new LocalWriteResult(false, error);
        }
    }
}
