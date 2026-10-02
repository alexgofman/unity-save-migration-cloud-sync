using System;
using System.Threading;
using System.Threading.Tasks;

namespace SaveSync
{
    internal enum RequestKind
    {
        None,
        Check,
        Upload,
        Download
    }

    /// <summary>
    /// The one backend request the coordinator may have in flight, with its deadline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holding at most one request is what keeps an upload from overlapping another upload,
    /// a check or a download. The deadline is what keeps a request that never completes from
    /// occupying the slot for the rest of the session.
    /// </para>
    /// <para>
    /// Nothing here runs on its own. The outcome of a request is only looked at when the
    /// owner calls <see cref="TryFinish"/>, so it is delivered on the owner's thread whatever
    /// thread the backend completed its task on, and a request that was given up can finish
    /// later without anyone hearing about it.
    /// </para>
    /// </remarks>
    internal sealed class RequestSlot
    {
        private readonly IClock _clock;
        private readonly double _timeoutSeconds;

        private Task _request;
        private Action<bool> _finish;
        private CancellationTokenSource _cancellation;
        private double _startedAt;

        public RequestSlot(IClock clock, double timeoutSeconds)
        {
            _clock = clock;
            _timeoutSeconds = timeoutSeconds;
        }

        public RequestKind Kind { get; private set; }

        public bool IsFree => Kind == RequestKind.None;

        /// <summary>
        /// Starts a request. <paramref name="onFinished"/> receives its result, or a timeout
        /// result with the flag set, from a later <see cref="TryFinish"/>.
        /// </summary>
        public void Begin<T>(
            RequestKind kind,
            Func<CancellationToken, Task<CloudResult<T>>> start,
            Action<CloudResult<T>, bool> onFinished)
        {
            if (!IsFree) throw new InvalidOperationException("A request is already in flight.");

            Kind = kind;
            _startedAt = _clock.MonotonicSeconds;
            _cancellation = new CancellationTokenSource();

            Task<CloudResult<T>> request = Start(start, _cancellation.Token);
            _request = request;
            _finish = timedOut => onFinished(timedOut ? TimedOut<T>() : ResultOf(request), timedOut);
        }

        /// <summary>
        /// If the request has completed or run out of time, frees the slot and then reports
        /// the outcome. Returns false while the request is still running.
        /// </summary>
        public bool TryFinish()
        {
            if (IsFree) return false;

            // Completion is looked at before the deadline, so a result that arrived in time
            // is never thrown away just because nobody asked for a while (a paused app).
            bool timedOut;
            if (_request.IsCompleted)
            {
                timedOut = false;
            }
            else if (_clock.MonotonicSeconds - _startedAt >= _timeoutSeconds)
            {
                timedOut = true;
            }
            else
            {
                return false;
            }

            // Free the slot first: the callback raises events, and a listener may start the
            // next request from inside one.
            Action<bool> finish = _finish;
            Release(cancel: timedOut);
            finish(timedOut);
            return true;
        }

        /// <summary>
        /// Gives the request up without reporting anything: cancels it and frees the slot.
        /// </summary>
        /// <returns>What was in flight.</returns>
        public RequestKind Abandon()
        {
            RequestKind kind = Kind;
            if (kind != RequestKind.None) Release(cancel: true);
            return kind;
        }

        private void Release(bool cancel)
        {
            CancellationTokenSource cancellation = _cancellation;
            Task request = _request;

            Kind = RequestKind.None;
            _request = null;
            _finish = null;
            _cancellation = null;

            if (cancel)
            {
                IgnoreLateOutcome(request);
                try
                {
                    cancellation.Cancel();
                }
                catch (AggregateException)
                {
                    // A backend's cancellation callback threw. The request is given up either
                    // way and the owner must keep running.
                }
            }

            cancellation.Dispose();
        }

        // A backend is supposed to report failures through its result. One that throws, or
        // returns no task at all, is turned into a failed result here, so there is a single
        // way of learning that a request did not work.
        private static Task<CloudResult<T>> Start<T>(
            Func<CancellationToken, Task<CloudResult<T>>> start, CancellationToken token)
        {
            try
            {
                return start(token)
                       ?? Task.FromResult(CloudResult<T>.Fail(CloudError.Unexpected, "The backend returned no task."));
            }
            catch (Exception exception)
            {
                return Task.FromResult(CloudResult<T>.Fail(CloudError.Unexpected, exception.Message));
            }
        }

        private static CloudResult<T> ResultOf<T>(Task<CloudResult<T>> request)
        {
            if (request.Status == TaskStatus.RanToCompletion) return request.Result;

            if (request.IsCanceled)
            {
                return CloudResult<T>.Fail(CloudError.Cancelled, "The backend cancelled the request.");
            }

            Exception exception = request.Exception?.GetBaseException();
            return CloudResult<T>.Fail(CloudError.Unexpected, exception?.Message ?? "The backend request faulted.");
        }

        private static CloudResult<T> TimedOut<T>()
        {
            return CloudResult<T>.Fail(CloudError.Timeout, "The request did not finish in time.");
        }

        // A request that was given up may still fail later. Reading its exception keeps that
        // from surfacing as an unobserved task exception.
        private static void IgnoreLateOutcome(Task abandoned)
        {
            abandoned.ContinueWith(
                task => { Exception ignored = task.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
