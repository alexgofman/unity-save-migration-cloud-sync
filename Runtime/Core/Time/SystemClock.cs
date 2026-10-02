using System;
using System.Diagnostics;

namespace SaveSync
{
    /// <summary>
    /// <see cref="IClock"/> backed by the system UTC clock and a <see cref="Stopwatch"/>.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public long UtcNowSeconds => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        public double MonotonicSeconds => _stopwatch.Elapsed.TotalSeconds;
    }
}
