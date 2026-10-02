using System;

namespace SaveSync
{
    /// <summary>
    /// Timing of the cloud backup. The defaults are a starting point, not measured values;
    /// tune them for the game's save size and save frequency. A coordinator takes a copy
    /// when it is constructed, so later changes to the instance do not affect it.
    /// </summary>
    public sealed class CloudSyncOptions
    {
        /// <summary>
        /// How long a burst of saves is collected before one upload carries all of them.
        /// Saves made while an upload is running wait this long again after it has finished.
        /// </summary>
        public double MinPushIntervalSeconds { get; set; } = 30;

        /// <summary>
        /// How long any single backend request may take before the coordinator stops waiting,
        /// cancels it and treats it as failed.
        /// </summary>
        public double RequestTimeoutSeconds { get; set; } = 20;

        /// <summary>Delay before the first retry of a failed upload. Doubles on each further failure.</summary>
        public double RetryBaseDelaySeconds { get; set; } = 10;

        /// <summary>Upper bound for the retry delay.</summary>
        public double RetryMaxDelaySeconds { get; set; } = 300;

        /// <summary>
        /// How much later than the local save the cloud save must be before it counts as
        /// newer.
        /// </summary>
        /// <remarks>
        /// The two timestamps come from different clocks and different moments: the local one
        /// is the device clock when the state was saved, the cloud one is the service clock
        /// when an upload landed. For a device's own upload the second trails the first by up
        /// to the push interval (the upload waited) plus the request timeout (the upload
        /// ran). The tolerance has to cover that gap, plus clock drift, or a device would see
        /// its own upload as a newer save from somewhere else. <see cref="Validate"/> enforces
        /// the first part.
        /// </remarks>
        public long ClockSkewToleranceSeconds { get; set; } = 90;

        internal CloudSyncOptions Copy()
        {
            return (CloudSyncOptions)MemberwiseClone();
        }

        /// <exception cref="ArgumentOutOfRangeException">A value is out of range or the values contradict each other.</exception>
        public void Validate()
        {
            if (MinPushIntervalSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MinPushIntervalSeconds), "Must not be negative.");
            }

            if (RequestTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(RequestTimeoutSeconds), "Must be positive.");
            }

            if (RetryBaseDelaySeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(RetryBaseDelaySeconds), "Must be positive.");
            }

            if (RetryMaxDelaySeconds < RetryBaseDelaySeconds)
            {
                throw new ArgumentOutOfRangeException(nameof(RetryMaxDelaySeconds), "Must not be smaller than the base delay.");
            }

            if (ClockSkewToleranceSeconds < MinPushIntervalSeconds + RequestTimeoutSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ClockSkewToleranceSeconds),
                    "Must be at least the push interval plus the request timeout, otherwise a device can mistake its own upload for a newer save.");
            }
        }
    }
}
