using System.Threading;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;

namespace SaveSync.PlayFabIntegration
{
    /// <summary>
    /// The PlayFab side of a "delete my account and data" request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A client can remove the cloud save (see <see cref="ICloudSaveBackend.DeleteAsync"/>)
    /// and its own device link. It cannot remove the account itself: that needs a server
    /// call with a title secret, which must never ship in a client. The usual arrangement is
    /// a CloudScript handler that deletes the calling player; a sample handler is included
    /// under <c>Samples~/PlayFabCloudScript</c>.
    /// </para>
    /// <para>
    /// Order matters when both are used: ask the server first, while the session is still
    /// valid, then remove what the client can remove. Every step reports its own outcome so
    /// that a failure in one does not stop the caller from wiping local data.
    /// </para>
    /// </remarks>
    public static class PlayFabAccountDeletion
    {
        /// <summary>Name of the handler in the sample CloudScript.</summary>
        public const string DefaultHandlerName = "RequestAccountDeletion";

        /// <summary>Name of the boolean the sample handler returns.</summary>
        public const string DefaultResultField = "accountDeleted";

        /// <summary>
        /// Asks a CloudScript handler to delete the calling player's account.
        /// </summary>
        /// <returns>
        /// Ok(true) only when the handler ran and reported the deletion. Ok(false) when it
        /// ran and reported that nothing was deleted. A failure when it could not be called,
        /// threw, or returned something else; the caller should then fall back to the
        /// client-side steps and its manual deletion route.
        /// </returns>
        public static async Task<CloudResult<bool>> RequestServerDeleteAsync(
            string handlerName = DefaultHandlerName,
            string resultField = DefaultResultField,
            CancellationToken cancellationToken = default)
        {
            if (!PlayFabClientAPI.IsClientLoggedIn()) return CloudResult<bool>.Fail(CloudError.NotSignedIn);

            var request = new ExecuteCloudScriptRequest { FunctionName = handlerName };
            CloudResult<ExecuteCloudScriptResult> executed =
                await PlayFabCalls.Call<ExecuteCloudScriptRequest, ExecuteCloudScriptResult>(
                    PlayFabClientAPI.ExecuteCloudScript, request, cancellationToken);
            if (!executed.IsOk) return CloudResult<bool>.Fail(executed.Error, executed.Message);

            ScriptExecutionError scriptError = executed.Value.Error;
            if (scriptError != null)
            {
                return CloudResult<bool>.Fail(CloudError.Rejected, scriptError.Error + ": " + scriptError.Message);
            }

            // The field is read by key and type. Matching the result's text for the field
            // name followed by "true" would also accept it inside a nested object or an
            // error string.
            if (!FunctionResultReader.TryGetBoolean(executed.Value.FunctionResult, resultField, out bool deleted))
            {
                return CloudResult<bool>.Fail(
                    CloudError.Unexpected, "The handler did not return a boolean '" + resultField + "'.");
            }

            return CloudResult<bool>.Ok(deleted);
        }

        /// <summary>
        /// Removes the link between this device's custom id and the account, then forgets
        /// the cached session, so the next launch starts as a new player.
        /// </summary>
        public static async Task<CloudResult<bool>> ForgetDeviceAsync(
            string customId, CancellationToken cancellationToken = default)
        {
            CloudResult<UnlinkCustomIDResult> unlinked = PlayFabClientAPI.IsClientLoggedIn()
                ? await PlayFabCalls.Call<UnlinkCustomIDRequest, UnlinkCustomIDResult>(
                    PlayFabClientAPI.UnlinkCustomID, new UnlinkCustomIDRequest { CustomId = customId }, cancellationToken)
                : CloudResult<UnlinkCustomIDResult>.Fail(CloudError.NotSignedIn);

            // The cached credentials go either way: a failed unlink must not leave the
            // device signed in to an account the player asked to leave.
            PlayFabClientAPI.ForgetAllCredentials();

            return unlinked.IsOk
                ? CloudResult<bool>.Ok(true)
                : CloudResult<bool>.Fail(unlinked.Error, unlinked.Message);
        }
    }
}
