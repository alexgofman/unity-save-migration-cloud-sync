using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SaveSync.Tests
{
    /// <summary>
    /// A cloud service that lives in memory. By default every request completes at once;
    /// with <see cref="HoldRequests"/> a request stays pending until the test releases it,
    /// which is how in-flight, slow and never-answered requests are staged.
    /// </summary>
    internal sealed class FakeCloudBackend : ICloudSaveBackend
    {
        private readonly FakeClock _clock;
        private readonly List<HeldRequest> _held = new List<HeldRequest>();

        public FakeCloudBackend(FakeClock clock)
        {
            _clock = clock;
        }

        public bool IsSignedIn { get; set; } = true;

        // What the service holds.
        public byte[] StoredPayload { get; private set; }

        public long StoredAtUtcSeconds { get; private set; }

        // How the service behaves.
        public bool HoldRequests { get; set; }

        /// <summary>
        /// A held request normally ends as cancelled when its token is cancelled. With this
        /// set it ignores the token, like a request that can no longer be recalled and still
        /// reaches the service.
        /// </summary>
        public bool IgnoreCancellation { get; set; }

        public int FailNextUploads { get; set; }

        public int FailNextInfoRequests { get; set; }

        public int FailNextDownloads { get; set; }

        public bool ThrowOnUpload { get; set; }

        // What the test can observe.
        public int InfoRequests { get; private set; }

        public int UploadsStarted { get; private set; }

        public int Downloads { get; private set; }

        /// <summary>
        /// Requests that told the caller what the cloud holds: an answered info request, or
        /// a download that either delivered the save or reported that there is none.
        /// </summary>
        public int LooksAnswered { get; private set; }

        public CancellationToken LastToken { get; private set; }

        /// <summary>Requests that were started and have neither answered nor been cancelled.</summary>
        public int RequestsInFlight
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _held.Count; i++)
                {
                    if (_held[i].IsPending()) count++;
                }

                return count;
            }
        }

        /// <summary>Called with the payload at the moment an upload request arrives.</summary>
        public Action<byte[]> UploadArrived { get; set; }

        public void Seed(byte[] payload, long storedAtUtcSeconds)
        {
            StoredPayload = payload;
            StoredAtUtcSeconds = storedAtUtcSeconds;
        }

        /// <summary>
        /// Lets the oldest request that is still waiting finish, with whatever outcome is
        /// configured at this moment.
        /// </summary>
        public void ReleaseNext()
        {
            while (_held.Count > 0)
            {
                HeldRequest next = _held[0];
                _held.RemoveAt(0);
                if (!next.IsPending()) continue;

                next.Release();
                return;
            }

            throw new InvalidOperationException("No request is waiting to be released.");
        }

        public Task<CloudResult<CloudSaveInfo>> GetInfoAsync(CancellationToken cancellationToken)
        {
            InfoRequests++;
            return Respond(cancellationToken, () =>
            {
                if (FailNextInfoRequests > 0)
                {
                    FailNextInfoRequests--;
                    return CloudResult<CloudSaveInfo>.Fail(CloudError.Network, "simulated outage");
                }

                LooksAnswered++;
                return CloudResult<CloudSaveInfo>.Ok(CurrentInfo());
            });
        }

        public Task<CloudResult<CloudSaveSnapshot>> DownloadAsync(CancellationToken cancellationToken)
        {
            Downloads++;
            return Respond(cancellationToken, () =>
            {
                if (FailNextDownloads > 0)
                {
                    FailNextDownloads--;
                    return CloudResult<CloudSaveSnapshot>.Fail(CloudError.Network, "simulated outage");
                }

                LooksAnswered++;
                return StoredPayload == null
                    ? CloudResult<CloudSaveSnapshot>.Fail(CloudError.NotFound, "no save stored")
                    : CloudResult<CloudSaveSnapshot>.Ok(new CloudSaveSnapshot((byte[])StoredPayload.Clone(), CurrentInfo()));
            });
        }

        public Task<CloudResult<CloudSaveInfo>> UploadAsync(byte[] payload, CancellationToken cancellationToken)
        {
            UploadsStarted++;
            UploadArrived?.Invoke(payload);
            if (ThrowOnUpload) throw new InvalidOperationException("simulated backend bug");

            return Respond(cancellationToken, () =>
            {
                if (FailNextUploads > 0)
                {
                    FailNextUploads--;
                    return CloudResult<CloudSaveInfo>.Fail(CloudError.Network, "simulated outage");
                }

                StoredPayload = payload;
                StoredAtUtcSeconds = _clock.UtcNowSeconds;
                return CloudResult<CloudSaveInfo>.Ok(CurrentInfo());
            });
        }

        public Task<CloudResult<bool>> DeleteAsync(CancellationToken cancellationToken)
        {
            return Respond(cancellationToken, () =>
            {
                StoredPayload = null;
                StoredAtUtcSeconds = 0;
                return CloudResult<bool>.Ok(true);
            });
        }

        private CloudSaveInfo CurrentInfo()
        {
            return StoredPayload == null
                ? CloudSaveInfo.None
                : new CloudSaveInfo(true, StoredAtUtcSeconds, StoredPayload.Length);
        }

        private Task<CloudResult<T>> Respond<T>(CancellationToken cancellationToken, Func<CloudResult<T>> outcome)
        {
            LastToken = cancellationToken;
            if (!HoldRequests) return Task.FromResult(outcome());

            var pending = new TaskCompletionSource<CloudResult<T>>();
            if (!IgnoreCancellation)
            {
                cancellationToken.Register(
                    () => pending.TrySetResult(CloudResult<T>.Fail(CloudError.Cancelled, "cancelled by the caller")));
            }

            _held.Add(new HeldRequest(
                () => !pending.Task.IsCompleted,
                () => pending.TrySetResult(outcome())));
            return pending.Task;
        }

        private sealed class HeldRequest
        {
            public HeldRequest(Func<bool> isPending, Action release)
            {
                IsPending = isPending;
                Release = release;
            }

            public Func<bool> IsPending { get; }

            public Action Release { get; }
        }
    }
}
