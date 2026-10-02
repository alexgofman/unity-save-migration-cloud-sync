using System;
using System.Threading.Tasks;

namespace SaveSync
{
    /// <summary>
    /// The cloud backup of one save, without the state type: what a host component needs in
    /// order to drive <see cref="CloudSyncCoordinator{TState}"/>.
    /// </summary>
    /// <remarks>
    /// Everything happens on the thread that calls in. Results, both the events and the
    /// tasks returned here, are delivered from inside <see cref="Tick"/>,
    /// <see cref="FlushNow"/> or the call itself. Nothing completes unless
    /// <see cref="Tick"/> is called regularly.
    /// </remarks>
    public interface ICloudSync
    {
        /// <summary>Raised after each upload attempt.</summary>
        event Action<PushReport> PushCompleted;

        /// <summary>
        /// Raised when a check finds the cloud save newer than the local one. Uploads are
        /// already on hold when this fires; answer with <see cref="RestoreAsync"/> or
        /// <see cref="KeepLocal"/>.
        /// </summary>
        event Action<CloudCheckResult> CloudNewerDetected;

        /// <summary>True while a local save has not been confirmed in the cloud.</summary>
        bool HasUnsyncedChanges { get; }

        PushHold Hold { get; }

        /// <summary>
        /// Advances the coordinator: notices finished and overdue requests and starts due
        /// work. Cheap; call it every frame.
        /// </summary>
        void Tick();

        /// <summary>
        /// Starts the pending upload now instead of waiting for the push interval. For the
        /// moment the application is paused or closed. Best effort: it starts the request
        /// and returns.
        /// </summary>
        void FlushNow();

        /// <summary>Compares the cloud save with the local one.</summary>
        Task<CloudCheckResult> CheckCloudAsync();

        /// <summary>
        /// Downloads the cloud save, runs it through the same pipeline as a local load and
        /// makes it the local save.
        /// </summary>
        Task<RestoreResult> RestoreAsync();

        /// <summary>
        /// Resolves a pending conflict in favour of the local save, which will then replace
        /// the cloud copy.
        /// </summary>
        void KeepLocal();

        /// <summary>
        /// Stops uploads, abandoning one that is in flight, until <see cref="Resume"/>. Use it
        /// around anything that changes which cloud save the backend points at, such as
        /// signing in to another account.
        /// </summary>
        void Suspend();

        /// <summary>Lifts <see cref="Suspend"/>. The cloud is checked again before the next upload.</summary>
        void Resume();
    }
}
