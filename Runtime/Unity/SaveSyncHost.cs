using System;
using System.Threading;
using UnityEngine;

namespace SaveSync.Unity
{
    /// <summary>
    /// Connects a save session and its cloud sync to the Unity player loop: ticks the
    /// coordinator every frame and flushes when the application is paused or closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The component owns nothing. The game builds the session and the coordinator, hands
    /// them over with <see cref="Bind"/>, and disposes the coordinator itself.
    /// </para>
    /// <para>
    /// On pause and on quit the state is first written to disk, and only then is the upload
    /// started. The local write is what protects the last seconds of play: it is
    /// synchronous and complete when the callback returns. The upload is best effort. On
    /// mobile the player loop stops once the application is in the background, and an
    /// upload that needs several round trips usually finishes after the application comes
    /// back, or not at all if it is killed first.
    /// </para>
    /// <para>
    /// The coordinator measures its intervals on a real-time clock, so ticking it once per
    /// frame works at any time scale, including a game paused with a time scale of zero.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("Save Sync/Save Sync Host")]
    public sealed class SaveSyncHost : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Write the state to disk when the application is paused or closed, before the upload is started.")]
        private bool _saveOnPause = true;

        [SerializeField]
        [Tooltip("Write failed saves, refused saves and failed uploads to the console.")]
        private bool _logProblems = true;

        private ISaveSession _session;
        private ICloudSync _sync;
        private bool _pumping;
        private bool _refusalLogged;

        /// <summary>
        /// Starts driving the given session and coordinator. Call it once both exist;
        /// calling it again replaces them.
        /// </summary>
        /// <param name="sync">Optional. Without it the component only saves on pause and quit.</param>
        public void Bind(ISaveSession session, ICloudSync sync = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            Unbind();
            _session = session;
            _sync = sync;
            _refusalLogged = false;
            _session.SaveCompleted += OnSaveCompleted;
            if (_sync != null) _sync.PushCompleted += OnPushCompleted;

            StartPump();
        }

        /// <summary>Stops driving the current session and coordinator.</summary>
        public void Unbind()
        {
            if (_session != null) _session.SaveCompleted -= OnSaveCompleted;
            if (_sync != null) _sync.PushCompleted -= OnPushCompleted;
            _session = null;
            _sync = null;
        }

        /// <summary>
        /// Saves (if enabled) and starts the pending upload. Called automatically on pause
        /// and quit; call it yourself at other moments the player is likely to leave.
        /// </summary>
        public void Flush()
        {
            if (_session == null) return;

            // A session that has not loaded yet has nothing it is allowed to write.
            if (_saveOnPause && _session.IsAuthoritative) _session.Save();

            _sync?.FlushNow();
        }

        private void OnEnable()
        {
            StartPump();
        }

        private void OnDestroy()
        {
            Unbind();
        }

        private void OnApplicationPause(bool pausing)
        {
            if (pausing) Flush();
        }

        private void OnApplicationQuit()
        {
            Flush();
        }

        private void StartPump()
        {
            if (_pumping || !isActiveAndEnabled) return;

            _pumping = true;
            _ = PumpAsync(destroyCancellationToken);
        }

        // One loop for the lifetime of the component. It ends when the component is
        // destroyed, which cancels the token and makes the frame wait throw.
        private async Awaitable PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (isActiveAndEnabled) Tick();
                    await Awaitable.NextFrameAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Destroyed while waiting for the next frame.
            }
            finally
            {
                _pumping = false;
            }
        }

        private void Tick()
        {
            if (_sync == null) return;

            try
            {
                _sync.Tick();
            }
            catch (Exception exception)
            {
                // A listener threw from inside the tick. Report it and keep ticking: one bad
                // handler must not end cloud sync for the session.
                Debug.LogException(exception, this);
            }
        }

        private void OnSaveCompleted(SaveResult result)
        {
            if (!_logProblems) return;

            if (result.IsFailure)
            {
                Debug.LogError("[SaveSync] The save was not written: " + result, this);
            }
            else if (result.Status == SaveStatus.RefusedNotAuthoritative && !_refusalLogged)
            {
                // Once per session, not per call: code that saves before the load has run is
                // refused every time it tries. Said once, it points at the two real causes
                // without flooding the console.
                _refusalLogged = true;
                Debug.LogWarning(
                    "[SaveSync] A save was refused because the session has no authoritative state. Either " +
                    "something saved before SaveSession.Load ran, or Load found a save it could not use and " +
                    "the game has not yet restored or reset. Nothing on disk was changed.", this);
            }
        }

        private void OnPushCompleted(PushReport report)
        {
            if (!_logProblems) return;

            if (report.Outcome == PushOutcome.Failed || report.Outcome == PushOutcome.TimedOut)
            {
                Debug.LogWarning(
                    "[SaveSync] Cloud upload: " + report + ". Next attempt in " + report.RetryInSeconds + " s.", this);
            }
        }
    }
}
