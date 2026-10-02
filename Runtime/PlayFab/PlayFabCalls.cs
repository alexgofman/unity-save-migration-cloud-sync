using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayFab;

namespace SaveSync.PlayFabIntegration
{
    /// <summary>
    /// Adapts the callback style of the PlayFab SDK to tasks that carry a
    /// <see cref="CloudResult{T}"/>.
    /// </summary>
    /// <remarks>
    /// The SDK cannot recall a request once it has been sent. Cancelling the token therefore
    /// only stops the wait: the task completes as cancelled and a late answer is dropped.
    /// The SDK raises its callbacks on the main thread, so code awaiting these tasks from
    /// the main thread continues there.
    /// </remarks>
    internal static class PlayFabCalls
    {
        internal delegate void Api<TRequest, TResult>(
            TRequest request,
            Action<TResult> onResult,
            Action<PlayFabError> onError,
            object customData,
            Dictionary<string, string> extraHeaders);

        internal static Task<CloudResult<TResult>> Call<TRequest, TResult>(
            Api<TRequest, TResult> api, TRequest request, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<CloudResult<TResult>>();
            if (cancellationToken.IsCancellationRequested)
            {
                completion.SetResult(CloudResult<TResult>.Fail(CloudError.Cancelled, "Cancelled before the request was sent."));
                return completion.Task;
            }

            CancellationTokenRegistration registration = cancellationToken.Register(
                () => completion.TrySetResult(CloudResult<TResult>.Fail(CloudError.Cancelled, "Cancelled while waiting for the service.")));

            try
            {
                api(
                    request,
                    result =>
                    {
                        registration.Dispose();
                        completion.TrySetResult(CloudResult<TResult>.Ok(result));
                    },
                    error =>
                    {
                        registration.Dispose();
                        completion.TrySetResult(CloudResult<TResult>.Fail(Classify(error), Describe(error)));
                    },
                    null,
                    null);
            }
            catch (PlayFabException exception)
            {
                // The SDK throws at once, instead of calling the error callback, when the
                // call cannot even be attempted: no session, or no title configured.
                registration.Dispose();
                completion.TrySetResult(CloudResult<TResult>.Fail(Classify(exception.Code), exception.Message));
            }
            catch (Exception exception)
            {
                registration.Dispose();
                completion.TrySetResult(CloudResult<TResult>.Fail(CloudError.Unexpected, exception.Message));
            }

            return completion.Task;
        }

        private static CloudError Classify(PlayFabExceptionCode code)
        {
            switch (code)
            {
                case PlayFabExceptionCode.TitleNotSet:
                case PlayFabExceptionCode.DeveloperKeyNotSet:
                case PlayFabExceptionCode.BuildError:
                    return CloudError.NotConfigured;
                default:
                    return CloudError.NotSignedIn;
            }
        }

        private static CloudError Classify(PlayFabError error)
        {
            switch (error.Error)
            {
                case PlayFabErrorCode.ConnectionError:
                case PlayFabErrorCode.ServiceUnavailable:
                    return CloudError.Network;
                case PlayFabErrorCode.NotAuthenticated:
                case PlayFabErrorCode.NotAuthorized:
                    return CloudError.NotSignedIn;
                case PlayFabErrorCode.FileNotFound:
                    return CloudError.NotFound;
                default:
                    return CloudError.Rejected;
            }
        }

        private static string Describe(PlayFabError error)
        {
            return error.Error + " (HTTP " + error.HttpCode + "): " + error.ErrorMessage;
        }
    }
}
