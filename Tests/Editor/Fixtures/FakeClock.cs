namespace SaveSync.Tests
{
    /// <summary>A clock that only moves when a test moves it.</summary>
    internal sealed class FakeClock : IClock
    {
        public long UtcNowSeconds { get; set; } = 1_700_000_000;

        public double MonotonicSeconds { get; private set; }

        /// <summary>Moves both clocks forward together, as real time does.</summary>
        public void Advance(double seconds)
        {
            MonotonicSeconds += seconds;
            UtcNowSeconds += (long)seconds;
        }
    }
}
