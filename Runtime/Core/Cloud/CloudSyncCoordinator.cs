using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SaveSync
{
    /// <summary>
    /// Keeps a cloud copy of the local save: uploads after local saves, notices when the
    /// cloud copy is newer, and restores it on request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local save is the source of truth and the cloud copy is a backup of it. Nothing
    /// here blocks or fails gameplay: a save marks the state dirty and returns, and every
    /// network outcome is reported through an event or a result.
    /// </para>
    /// <para>
    /// Upload rules:
    /// </para>
    /// <list type="bullet">
    /// <item>One request at a time. An upload never overlaps another upload, a check or a
    /// download.</item>
    /// <item>A burst of saves becomes one upload. The first save starts a countdown of
    /// <see cref="CloudSyncOptions.MinPushIntervalSeconds"/>; saves during the countdown or
    /// during an upload only set the dirty flag, and the countdown or the upload picks it up
    /// when it ends. No second timer is ever started.</item>
    /// <item>Dirty means "not confirmed in the cloud". The flag is cleared when an upload
    /// starts, because the save read from disk at that moment is what goes up, and set
    /// again if that upload fails or times out. A failed upload is therefore retried on its
    /// own, with a growing delay, without waiting for the next save.</item>
    /// <item>A request that does not finish within
    /// <see cref="CloudSyncOptions.RequestTimeoutSeconds"/> is cancelled and treated as
    /// failed. Without that, one request that never completes would block every later
    /// upload for the rest of the session.</item>
    /// <item>Nothing is uploaded before the cloud has been looked at once. Until the
    /// coordinator knows what is in the cloud it cannot know whether an upload would destroy
    /// something newer.</item>
    /// <item>While it is undecided which copy wins (the cloud copy is newer, or a restore
    /// was asked for and failed) uploads are on hold. Otherwise the countdown would upload
    /// the local save over the cloud copy while the player is still reading the prompt, and
    /// choosing to restore would then hand back the local save.</item>
    /// <item>What is uploaded is the save as it is on disk, and only when the state is
    /// authoritative (see <see cref="SaveGate"/>). A placeholder, unsaved changes, or the
    /// empty state left after the local save was erased never reach the cloud.</item>
    /// </list>
    /// <para>
    /// "Newer" is decided by comparing two wall-clock timestamps with a tolerance; see
    /// <see cref="CloudSyncOptions.ClockSkewToleranceSeconds"/>. This is last-write-wins and
    /// does not merge.
    /// </para>
    /// <para>
    /// The coordinator is driven by <see cref="Tick"/> and uses no timers or threads of its
    /// own. All state changes happen inside calls made by the host, so it behaves the same
    /// whatever thread backend tasks complete on, and tests can drive it step by step with
    /// a fake clock. Not thread-safe: call it from one thread.
    /// </para>
    /// </remarks>
    public sealed class CloudSyncCoordinator<TState> : ICloudSync, IDisposable
        where TState : class, IVersionedState
    {
        private readonly SaveSession<TState> _session;
        private readonly SavePipeline<TState> _pipeline;
        private readonly ICloudSaveBackend _backend;
        private readonly IClock _clock;
        private readonly CloudSyncOptions _options;
        private readonly RequestSlot _slot;

        private readonly List<TaskCompletionSource<CloudCheckResult>> _checkWaiters =
            new List<TaskCompletionSource<CloudCheckResult>>();

        private TaskCompletionSource<RestoreResult> _restoreWaiter;

        private bool _dirty;
        private bool _pushScheduled;
        private double _pushDueAt;
        private int _consecutiveFailures;
        private bool _cloudChecked;
        private PushHold _hold;

        private bool _disposed;

        /// <param name="pipeline">
        /// The same instance the session uses, so that a restore reads a save exactly the
        /// way a local load does.
        /// </param>
        public CloudSyncCoordinator(
            SaveSession<TState> session,
            SavePipeline<TState> pipeline,
            ICloudSaveBackend backend,
            IClock clock,
            CloudSyncOptions options = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _options = options != null ? options.Copy() : new CloudSyncOptions();
            _options.Validate();

            _slot = new RequestSlot(_clock, _options.RequestTimeoutSeconds);
            _session.SaveCompleted += OnSaveCompleted;
        }

        public event Action<PushReport> PushCompleted;

        public event Action<CloudCheckResult> CloudNewerDetected;

        public bool HasUnsyncedChanges => _dirty;

        public PushHold Hold => _hold;

        public bool IsUploadInFlight => _slot.Kind == RequestKind.Upload;

        public bool IsRequestInFlight => !_slot.IsFree;

        /// <summary>Upload attempts that have failed in a row.</summary>
        public int ConsecutiveFailures => _consecutiveFailures;

        private bool IsPushDue => _pushScheduled && _clock.MonotonicSeconds >= _pushDueAt;

        public void Tick()
        {
            if (_disposed) return;

            _slot.TryFinish();
            StartPendingWork();
        }

        public void FlushNow()
        {
            if (_disposed) return;

            _slot.TryFinish();

            // An upload already in flight carries an earlier save. What was saved since stays
            // dirty and goes up after it; two uploads never overlap.
            if (!_slot.IsFree) return;

            // No blind upload: without a completed check the cloud copy might be newer.
            if (!CanPush() || !_cloudChecked) return;

            StartUpload();
        }

        public Task<CloudCheckResult> CheckCloudAsync()
        {
            if (_disposed)
            {
                return Task.FromResult(CloudCheckResult.Unavailable(CloudError.Cancelled, "The coordinator has been disposed."));
            }

            if (!_backend.IsSignedIn)
            {
                return Task.FromResult(CloudCheckResult.Unavailable(CloudError.NotSignedIn, null));
            }

            // A check that is already in flight answers this caller too. Behind an upload or
            // a download the check waits for the slot and is started from Tick.
            var waiter = new TaskCompletionSource<CloudCheckResult>();
            _checkWaiters.Add(waiter);
            if (_slot.IsFree) StartCheck();
            return waiter.Task;
        }

        public Task<RestoreResult> RestoreAsync()
        {
            if (_disposed)
            {
                return Task.FromResult(RestoreResult.NotRestored(
                    RestoreStatus.Unavailable, "The coordinator has been disposed.", CloudError.Cancelled));
            }

            if (_restoreWaiter != null) return _restoreWaiter.Task;

            if (!_backend.IsSignedIn)
            {
                return Task.FromResult(RestoreResult.NotRestored(RestoreStatus.Unavailable, null, CloudError.NotSignedIn));
            }

            // Whatever is in flight gives way. In particular an upload must not be allowed to
            // finish: it would replace the copy the player has just asked to get back. This
            // is as good as the backend's cancellation: a request that can no longer be
            // recalled may still land.
            RequestKind interrupted = _slot.Abandon();
            if (interrupted == RequestKind.Upload) _dirty = true;

            var waiter = new TaskCompletionSource<RestoreResult>();
            _restoreWaiter = waiter;
            _slot.Begin<CloudSaveSnapshot>(
                RequestKind.Download,
                token => _backend.DownloadAsync(token),
                (download, timedOut) => OnDownloadFinished(download));

            if (interrupted == RequestKind.Check)
            {
                CompleteCheckWaiters(CloudCheckResult.Unavailable(CloudError.Cancelled, "Superseded by a restore."));
            }
            else if (interrupted == RequestKind.Upload)
            {
                PushCompleted?.Invoke(PushReport.NotUploaded(
                    PushOutcome.Cancelled, CloudError.Cancelled, "Superseded by a restore.", _consecutiveFailures, 0));
            }

            return waiter.Task;
        }

        public void KeepLocal()
        {
            if ((_hold & PushHold.ConflictPending) == 0) return;

            _hold &= ~PushHold.ConflictPending;

            // The local save is the one the player kept, so it replaces the cloud copy at the
            // next upload. Once that upload has landed the local timestamp follows the cloud
            // one, which is what stops the same question being asked on the next launch.
            _dirty = true;
            EnsurePushScheduled(_options.MinPushIntervalSeconds);
        }

        public void Suspend()
        {
            if (_disposed) return;

            _hold |= PushHold.Suspended;
            if (_slot.Kind != RequestKind.Upload) return;

            _slot.Abandon();
            _dirty = true;
            PushCompleted?.Invoke(PushReport.NotUploaded(
                PushOutcome.Cancelled, CloudError.Cancelled, "Suspended.", _consecutiveFailures, 0));
        }

        public void Resume()
        {
            if ((_hold & PushHold.Suspended) == 0) return;

            _hold &= ~PushHold.Suspended;

            // The cloud copy may have changed while uploads were suspended, or the backend
            // may now point at a different player's save. Look before uploading over it, and
            // do not trust a look that was started before now: it is given up, and anyone
            // waiting for it gets the answer of a new one.
            _cloudChecked = false;
            if (_slot.Kind == RequestKind.Check) _slot.Abandon();
            EnsurePushScheduled(0);
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _session.SaveCompleted -= OnSaveCompleted;
            _slot.Abandon();

            CompleteCheckWaiters(CloudCheckResult.Unavailable(CloudError.Cancelled, "The coordinator has been disposed."));

            TaskCompletionSource<RestoreResult> waiter = _restoreWaiter;
            _restoreWaiter = null;
            waiter?.TrySetResult(RestoreResult.NotRestored(
                RestoreStatus.Unavailable, "The coordinator has been disposed.", CloudError.Cancelled));
        }

        private void OnSaveCompleted(SaveResult result)
        {
            if (_disposed || !result.IsSaved) return;

            _dirty = true;
            EnsurePushScheduled(_options.MinPushIntervalSeconds);
        }

        // Starts the countdown unless something that will pick the dirty flag up is already
        // under way: a countdown that is running, or an upload that will look at the flag
        // when it finishes.
        private void EnsurePushScheduled(double delaySeconds)
        {
            if (!_dirty || _pushScheduled || _slot.Kind == RequestKind.Upload) return;
            SchedulePush(delaySeconds);
        }

        private void SchedulePush(double delaySeconds)
        {
            _pushScheduled = true;
            _pushDueAt = _clock.MonotonicSeconds + delaySeconds;
        }

        private bool CanPush()
        {
            // The cloud copy is a backup of a save that exists on this device. A state that
            // was never loaded, or one that has not been written to disk (a new player, or
            // the state left after the local save was erased), is not uploaded.
            return _dirty
                   && _hold == PushHold.None
                   && _backend.IsSignedIn
                   && _session.IsAuthoritative
                   && _session.LastSavedUtcSeconds > 0;
        }

        private void StartPendingWork()
        {
            if (!_slot.IsFree) return;

            if (_checkWaiters.Count > 0)
            {
                if (_backend.IsSignedIn)
                {
                    StartCheck();
                }
                else
                {
                    CompleteCheckWaiters(CloudCheckResult.Unavailable(CloudError.NotSignedIn, null));
                }

                return;
            }

            // A push that is due but cannot start (on hold, signed out, state not loaded)
            // simply stays due and is looked at again on the next tick.
            if (!IsPushDue || !CanPush()) return;

            if (_cloudChecked)
            {
                StartUpload();
            }
            else
            {
                StartCheck();
            }
        }

        private void StartCheck()
        {
            _slot.Begin<CloudSaveInfo>(
                RequestKind.Check,
                token => _backend.GetInfoAsync(token),
                (info, timedOut) => OnCheckFinished(info));
        }

        private void StartUpload()
        {
            // What goes up is the save as it is on disk, not the state in memory. The cloud
            // copy is therefore always, byte for byte, a save this device wrote first, and
            // nothing half-changed or unsaved can reach it.
            if (!_session.TryReadSavedPayload(out byte[] payload))
            {
                _consecutiveFailures++;
                double retryIn = RetryDelaySeconds();
                SchedulePush(retryIn);
                PushCompleted?.Invoke(PushReport.NotUploaded(
                    PushOutcome.Failed, CloudError.Unexpected,
                    "The local save could not be read for upload.", _consecutiveFailures, retryIn));
                return;
            }

            // The save just read is what goes up. Anything saved from here on is a new
            // change and sets the flag again.
            _dirty = false;
            _pushScheduled = false;
            _slot.Begin<CloudSaveInfo>(
                RequestKind.Upload,
                token => _backend.UploadAsync(payload, token),
                (uploaded, timedOut) => OnUploadFinished(uploaded, payload.Length, timedOut));
        }

        private void OnCheckFinished(CloudResult<CloudSaveInfo> result)
        {
            CloudCheckResult check;
            if (result.IsOk)
            {
                _cloudChecked = true;
                check = Compare(result.Value);
                if (check.Status == CloudCheckStatus.CloudIsNewer)
                {
                    _hold |= PushHold.ConflictPending;
                }
                else if (_session.IsAuthoritative && _session.LastSavedUtcSeconds > result.Value.LastModifiedUtcSeconds)
                {
                    // The local save is later than anything in the cloud: an earlier session
                    // ended before its upload did. The dirty flag does not survive a restart,
                    // so this is where that progress is picked up again instead of waiting
                    // for the next save.
                    _dirty = true;
                    EnsurePushScheduled(_options.MinPushIntervalSeconds);
                }
            }
            else
            {
                check = CloudCheckResult.Unavailable(result.Error, result.Message);

                // This check was standing between a due upload and the cloud. Retry it the
                // way a failed upload is retried.
                if (_dirty && IsPushDue)
                {
                    _consecutiveFailures++;
                    SchedulePush(RetryDelaySeconds());
                }
            }

            CompleteCheckWaiters(check);
            if (check.Status == CloudCheckStatus.CloudIsNewer) CloudNewerDetected?.Invoke(check);
        }

        private CloudCheckResult Compare(CloudSaveInfo cloud)
        {
            // A local save that could not be loaded counts as no local save: whatever is in
            // the cloud is then the only usable copy.
            long local = _session.IsAuthoritative ? _session.LastSavedUtcSeconds : 0;

            if (!cloud.Exists) return CloudCheckResult.NoCloudSave(local);

            return cloud.LastModifiedUtcSeconds > local + _options.ClockSkewToleranceSeconds
                ? CloudCheckResult.CloudIsNewer(cloud.LastModifiedUtcSeconds, local)
                : CloudCheckResult.LocalIsCurrent(cloud.LastModifiedUtcSeconds, local);
        }

        private void OnUploadFinished(CloudResult<CloudSaveInfo> result, int sizeBytes, bool timedOut)
        {
            PushReport report;
            if (result.IsOk)
            {
                _consecutiveFailures = 0;
                FollowTheCloudTimestamp(result.Value);

                // Saves that arrived while the upload was in flight are not in it. They get
                // one follow-up upload, a full interval later.
                EnsurePushScheduled(_options.MinPushIntervalSeconds);
                report = PushReport.Uploaded(sizeBytes);
            }
            else
            {
                // The save never reached the cloud, so what it carried is still unsynced.
                // Without this line a failed upload is forgotten until the next local save
                // happens to set the flag again.
                _dirty = true;
                _consecutiveFailures++;
                double retryIn = RetryDelaySeconds();
                SchedulePush(retryIn);
                report = PushReport.NotUploaded(
                    timedOut ? PushOutcome.TimedOut : PushOutcome.Failed,
                    result.Error, result.Message, _consecutiveFailures, retryIn);
            }

            PushCompleted?.Invoke(report);
        }

        // The service stamps the cloud copy when the upload lands. After a retry or a long
        // wait that can be far later than the local save the upload carried, and on the
        // next launch this device would read its own upload as a newer save. So when the
        // cloud timestamp has run ahead by more than half the tolerance, the local
        // timestamp is moved up to it. Closer than that, the comparison is safe as it is and
        // the extra disk write is not worth it.
        private void FollowTheCloudTimestamp(CloudSaveInfo uploaded)
        {
            if (!uploaded.Exists) return;

            long lead = uploaded.LastModifiedUtcSeconds - _session.LastSavedUtcSeconds;
            if (lead > _options.ClockSkewToleranceSeconds / 2)
            {
                _session.RaiseTimestamp(uploaded.LastModifiedUtcSeconds);
            }
        }

        private void OnDownloadFinished(CloudResult<CloudSaveSnapshot> download)
        {
            RestoreResult restore = ApplyDownload(download);
            switch (restore.Status)
            {
                case RestoreStatus.Restored:
                    // The local save is now the cloud copy (migrated or corrected on the way
                    // in, if it needed that; the next ordinary save carries those changes
                    // up). Nothing to upload and nothing left to decide. This also clears the
                    // dirty flag that the install itself, and any save made while the
                    // download ran, has set.
                    _hold &= ~PushHold.ConflictPending;
                    _dirty = false;
                    _pushScheduled = false;
                    _cloudChecked = true;
                    _consecutiveFailures = 0;
                    break;

                case RestoreStatus.NoCloudSave:
                    // There is nothing in the cloud to protect, so local changes may go up.
                    _hold &= ~PushHold.ConflictPending;
                    _cloudChecked = true;
                    EnsurePushScheduled(0);
                    break;

                default:
                    // The player asked for the cloud copy and did not get it. An upload now
                    // would destroy that copy, so a failed restore never falls through to
                    // "local wins": uploads wait for another restore or for KeepLocal.
                    _hold |= PushHold.ConflictPending;
                    break;
            }

            TaskCompletionSource<RestoreResult> waiter = _restoreWaiter;
            _restoreWaiter = null;
            waiter?.TrySetResult(restore);
        }

        private RestoreResult ApplyDownload(CloudResult<CloudSaveSnapshot> download)
        {
            if (!download.IsOk)
            {
                return download.Error == CloudError.NotFound
                    ? RestoreResult.NotRestored(RestoreStatus.NoCloudSave, download.Message, download.Error)
                    : RestoreResult.NotRestored(RestoreStatus.DownloadFailed, download.Message, download.Error);
            }

            // The same pipeline a local load uses, on the same instance: same serializer
            // settings, same version check, same migrations, same sanitizer.
            PipelineResult<TState> read = _pipeline.Read(download.Value.Payload);
            switch (read.Status)
            {
                case PipelineStatus.Corrupt:
                    return RestoreResult.NotRestored(RestoreStatus.Corrupt, read.Error, CloudError.None, read.Migration);
                case PipelineStatus.NewerThanClient:
                    return RestoreResult.NotRestored(RestoreStatus.NewerThanClient, read.Error, CloudError.None, read.Migration);
                case PipelineStatus.MigrationFailed:
                    return RestoreResult.NotRestored(RestoreStatus.MigrationFailed, read.Error, CloudError.None, read.Migration);
                case PipelineStatus.SanitizerFailed:
                    return RestoreResult.NotRestored(RestoreStatus.SanitizerFailed, read.Error, CloudError.None, read.Migration);
            }

            SaveResult written = _session.InstallRestored(read.State, download.Value.Info.LastModifiedUtcSeconds);
            return written.IsSaved
                ? RestoreResult.Restored(read.Migration, read.Sanitize)
                : RestoreResult.NotRestored(RestoreStatus.LocalWriteFailed, written.Error, CloudError.None, read.Migration);
        }

        private void CompleteCheckWaiters(CloudCheckResult result)
        {
            if (_checkWaiters.Count == 0) return;

            TaskCompletionSource<CloudCheckResult>[] waiters = _checkWaiters.ToArray();
            _checkWaiters.Clear();
            for (int i = 0; i < waiters.Length; i++)
            {
                waiters[i].TrySetResult(result);
            }
        }

        private double RetryDelaySeconds()
        {
            int doublings = Math.Min(Math.Max(_consecutiveFailures, 1) - 1, 30);
            double delay = _options.RetryBaseDelaySeconds * Math.Pow(2, doublings);
            return Math.Min(delay, _options.RetryMaxDelaySeconds);
        }
    }
}
