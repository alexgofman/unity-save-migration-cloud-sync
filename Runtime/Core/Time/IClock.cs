namespace SaveSync
{
    /// <summary>
    /// The two notions of time the save system needs, behind one seam so tests can drive both.
    /// </summary>
    public interface IClock
    {
        /// <summary>
        /// Wall-clock time as Unix seconds (UTC). Used only to stamp saves, because a stamp has
        /// to be comparable with a timestamp produced by another machine.
        /// </summary>
        long UtcNowSeconds { get; }

        /// <summary>
        /// Seconds on a clock that never jumps. Used for throttling, back-off and timeouts, so
        /// that changing the device time cannot stall an upload or fire one early.
        /// </summary>
        double MonotonicSeconds { get; }
    }
}
