namespace SaveSync.Tests
{
    /// <summary>
    /// <see cref="ILocalSaveStore"/> without a disk, with switches for the failures a test
    /// wants to provoke.
    /// </summary>
    internal sealed class InMemorySaveStore : ILocalSaveStore
    {
        public byte[] Payload { get; private set; }

        public long SavedAtUtcSeconds { get; private set; }

        public int Writes { get; private set; }

        /// <summary>A save is present but fails its integrity check.</summary>
        public bool Unreadable { get; set; }

        public bool FailWrites { get; set; }

        public bool Exists => Payload != null || Unreadable;

        public void Seed(byte[] payload, long savedAtUtcSeconds)
        {
            Payload = payload;
            SavedAtUtcSeconds = savedAtUtcSeconds;
        }

        public LocalReadResult Read()
        {
            if (Unreadable) return LocalReadResult.Corrupt("simulated damage");
            if (Payload == null) return LocalReadResult.NotFound();
            return LocalReadResult.Ok((byte[])Payload.Clone(), SavedAtUtcSeconds);
        }

        public LocalWriteResult Write(byte[] payload, long savedAtUtcSeconds)
        {
            if (FailWrites) return LocalWriteResult.Failed("simulated full disk");

            Payload = payload;
            SavedAtUtcSeconds = savedAtUtcSeconds;
            Unreadable = false;
            Writes++;
            return LocalWriteResult.Ok();
        }

        public LocalWriteResult Delete()
        {
            Payload = null;
            SavedAtUtcSeconds = 0;
            Unreadable = false;
            return LocalWriteResult.Ok();
        }
    }
}
