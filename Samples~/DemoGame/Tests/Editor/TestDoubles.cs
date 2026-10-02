namespace SaveSync.Samples.DemoGame.Tests
{
    internal sealed class FixedClock : IClock
    {
        public FixedClock(long utcNowSeconds)
        {
            UtcNowSeconds = utcNowSeconds;
        }

        public long UtcNowSeconds { get; }

        public double MonotonicSeconds => 0;
    }

    internal sealed class MemoryStore : ILocalSaveStore
    {
        private byte[] _payload;
        private long _savedAtUtcSeconds;

        public byte[] Payload => _payload;

        public bool Exists => _payload != null;

        public LocalReadResult Read()
        {
            return _payload == null ? LocalReadResult.NotFound() : LocalReadResult.Ok(_payload, _savedAtUtcSeconds);
        }

        public LocalWriteResult Write(byte[] payload, long savedAtUtcSeconds)
        {
            _payload = payload;
            _savedAtUtcSeconds = savedAtUtcSeconds;
            return LocalWriteResult.Ok();
        }

        public LocalWriteResult Delete()
        {
            _payload = null;
            return LocalWriteResult.Ok();
        }
    }
}
