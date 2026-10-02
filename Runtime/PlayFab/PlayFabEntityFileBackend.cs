using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayFab;
using PlayFab.ClientModels;
using UnityEngine;
using UnityEngine.Networking;
using DataModels = PlayFab.DataModels;

namespace SaveSync.PlayFabIntegration
{
    /// <summary>
    /// <see cref="ICloudSaveBackend"/> on PlayFab Entity Files: one file per player, attached
    /// to the player's title entity.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Entity Files are used because a save can outgrow the size limit of a player-data value.
    /// An upload takes three requests: ask the service for an upload URL, send the bytes to
    /// that URL, then tell the service the upload is complete. Only the last one replaces
    /// the stored file, so a failure before it leaves the previous upload in place.
    /// </para>
    /// <para>
    /// An upload that stops between the first and the last request leaves a pending
    /// operation on the file, and the service refuses to start another one until it is
    /// aborted. <see cref="UploadAsync"/> therefore aborts and retries once when the first
    /// request is refused, and aborts after a failed second or third request. Without that,
    /// one interrupted upload would block every later one.
    /// </para>
    /// <para>
    /// The title id is read from the PlayFab SDK settings. This class never sets one.
    /// </para>
    /// <para>
    /// The SDK and UnityWebRequest both report on the main thread, so call this class from
    /// the main thread and results arrive there.
    /// </para>
    /// </remarks>
    public sealed class PlayFabEntityFileBackend : ICloudSaveBackend
    {
        // The upload URL points at blob storage, which rejects a PUT without this header.
        private const string BlobTypeHeader = "x-ms-blob-type";
        private const string BlobTypeValue = "BlockBlob";

        private readonly string _fileName;
        private readonly List<string> _fileNames;
        private readonly int _httpTimeoutSeconds;

        private DataModels.EntityKey _entity;

        /// <param name="fileName">
        /// Name of the file on the player's entity. Letters, digits and <c>( ) _ - .</c> only.
        /// </param>
        /// <param name="httpTimeoutSeconds">Limit for the blob transfer itself.</param>
        public PlayFabEntityFileBackend(string fileName = "save", int httpTimeoutSeconds = 20)
        {
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("A file name is required.", nameof(fileName));

            _fileName = fileName;
            _fileNames = new List<string> { fileName };
            _httpTimeoutSeconds = Math.Max(1, httpTimeoutSeconds);
        }

        public bool IsSignedIn => _entity != null;

        /// <summary>The PlayFab player id of the signed-in account, once signed in.</summary>
        public string PlayerId { get; private set; }

        /// <summary>
        /// Signs in with a custom id, creating the account on first use, and keeps the
        /// entity the file requests need.
        /// </summary>
        /// <param name="customId">
        /// An identifier that is stable for this installation, for example
        /// <c>SystemInfo.deviceUniqueIdentifier</c>. An account reached this way is tied to
        /// the device; reaching it from another device needs a second sign-in method linked
        /// to the same account.
        /// </param>
        public async Task<CloudResult<string>> SignInWithCustomIdAsync(
            string customId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(customId))
            {
                return CloudResult<string>.Fail(CloudError.NotConfigured, "A custom id is required.");
            }

            if (string.IsNullOrEmpty(PlayFabSettings.staticSettings.TitleId))
            {
                return CloudResult<string>.Fail(
                    CloudError.NotConfigured,
                    "No PlayFab title id is configured. Set it in the PlayFab SDK settings of the project.");
            }

            var request = new LoginWithCustomIDRequest { CustomId = customId, CreateAccount = true };
            CloudResult<LoginResult> login = await PlayFabCalls.Call<LoginWithCustomIDRequest, LoginResult>(
                PlayFabClientAPI.LoginWithCustomID, request, cancellationToken);
            if (!login.IsOk) return CloudResult<string>.Fail(login.Error, login.Message);

            // The login returns a client-model entity key and the file API takes a
            // data-model one. They carry the same two fields.
            EntityKey entity = login.Value.EntityToken?.Entity;
            if (entity == null)
            {
                return CloudResult<string>.Fail(CloudError.Rejected, "The login returned no entity.");
            }

            UseEntity(entity.Id, entity.Type);
            PlayerId = login.Value.PlayFabId;
            return CloudResult<string>.Ok(PlayerId);
        }

        /// <summary>
        /// For games that sign in to PlayFab elsewhere: hands over the entity whose file
        /// this backend should use.
        /// </summary>
        public void UseEntity(string entityId, string entityType)
        {
            if (string.IsNullOrEmpty(entityId)) throw new ArgumentException("An entity id is required.", nameof(entityId));

            _entity = new DataModels.EntityKey { Id = entityId, Type = entityType };
        }

        /// <summary>Stops making requests for the current player.</summary>
        public void SignOut()
        {
            _entity = null;
            PlayerId = null;
        }

        public async Task<CloudResult<CloudSaveInfo>> GetInfoAsync(CancellationToken cancellationToken)
        {
            CloudResult<DataModels.GetFileMetadata> file = await FindFileAsync(cancellationToken);
            if (file.IsOk) return CloudResult<CloudSaveInfo>.Ok(ToInfo(file.Value));

            return file.Error == CloudError.NotFound
                ? CloudResult<CloudSaveInfo>.Ok(CloudSaveInfo.None)
                : CloudResult<CloudSaveInfo>.Fail(file.Error, file.Message);
        }

        public async Task<CloudResult<CloudSaveSnapshot>> DownloadAsync(CancellationToken cancellationToken)
        {
            CloudResult<DataModels.GetFileMetadata> file = await FindFileAsync(cancellationToken);
            if (!file.IsOk) return CloudResult<CloudSaveSnapshot>.Fail(file.Error, file.Message);

            if (string.IsNullOrEmpty(file.Value.DownloadUrl))
            {
                return CloudResult<CloudSaveSnapshot>.Fail(CloudError.Rejected, "The service returned no download URL.");
            }

            CloudResult<byte[]> bytes = await HttpGetAsync(file.Value.DownloadUrl, cancellationToken);
            if (!bytes.IsOk) return CloudResult<CloudSaveSnapshot>.Fail(bytes.Error, bytes.Message);

            return CloudResult<CloudSaveSnapshot>.Ok(new CloudSaveSnapshot(bytes.Value, ToInfo(file.Value)));
        }

        public async Task<CloudResult<CloudSaveInfo>> UploadAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (_entity == null) return CloudResult<CloudSaveInfo>.Fail(CloudError.NotSignedIn);

            CloudResult<DataModels.InitiateFileUploadsResponse> initiated = await InitiateAsync(cancellationToken);
            if (!initiated.IsOk && initiated.Error == CloudError.Rejected)
            {
                // Most likely an earlier upload was interrupted and its operation is still
                // pending on the file. Clear it and ask once more.
                await AbortAsync();
                initiated = await InitiateAsync(cancellationToken);
            }

            if (!initiated.IsOk) return CloudResult<CloudSaveInfo>.Fail(initiated.Error, initiated.Message);

            string uploadUrl = FindUploadUrl(initiated.Value);
            if (string.IsNullOrEmpty(uploadUrl))
            {
                await AbortAsync();
                return CloudResult<CloudSaveInfo>.Fail(CloudError.Rejected, "The service returned no upload URL.");
            }

            CloudResult<bool> sent = await HttpPutAsync(uploadUrl, payload, cancellationToken);
            if (!sent.IsOk)
            {
                await AbortAsync();
                return CloudResult<CloudSaveInfo>.Fail(sent.Error, sent.Message);
            }

            var finalize = new DataModels.FinalizeFileUploadsRequest { Entity = _entity, FileNames = _fileNames };
            CloudResult<DataModels.FinalizeFileUploadsResponse> finalized =
                await PlayFabCalls.Call<DataModels.FinalizeFileUploadsRequest, DataModels.FinalizeFileUploadsResponse>(
                    PlayFabDataAPI.FinalizeFileUploads, finalize, cancellationToken);
            if (!finalized.IsOk)
            {
                await AbortAsync();
                return CloudResult<CloudSaveInfo>.Fail(finalized.Error, finalized.Message);
            }

            DataModels.GetFileMetadata stored = null;
            if (finalized.Value.Metadata != null) finalized.Value.Metadata.TryGetValue(_fileName, out stored);

            // Without metadata the upload still succeeded; the timestamp is simply unknown.
            return CloudResult<CloudSaveInfo>.Ok(
                stored != null ? ToInfo(stored) : new CloudSaveInfo(true, 0, payload.Length));
        }

        public async Task<CloudResult<bool>> DeleteAsync(CancellationToken cancellationToken)
        {
            if (_entity == null) return CloudResult<bool>.Fail(CloudError.NotSignedIn);

            var request = new DataModels.DeleteFilesRequest { Entity = _entity, FileNames = _fileNames };
            CloudResult<DataModels.DeleteFilesResponse> deleted =
                await PlayFabCalls.Call<DataModels.DeleteFilesRequest, DataModels.DeleteFilesResponse>(
                    PlayFabDataAPI.DeleteFiles, request, cancellationToken);

            if (deleted.IsOk || deleted.Error == CloudError.NotFound) return CloudResult<bool>.Ok(true);
            return CloudResult<bool>.Fail(deleted.Error, deleted.Message);
        }

        private async Task<CloudResult<DataModels.GetFileMetadata>> FindFileAsync(CancellationToken cancellationToken)
        {
            if (_entity == null) return CloudResult<DataModels.GetFileMetadata>.Fail(CloudError.NotSignedIn);

            var request = new DataModels.GetFilesRequest { Entity = _entity };
            CloudResult<DataModels.GetFilesResponse> files =
                await PlayFabCalls.Call<DataModels.GetFilesRequest, DataModels.GetFilesResponse>(
                    PlayFabDataAPI.GetFiles, request, cancellationToken);
            if (!files.IsOk) return CloudResult<DataModels.GetFileMetadata>.Fail(files.Error, files.Message);

            if (files.Value.Metadata != null
                && files.Value.Metadata.TryGetValue(_fileName, out DataModels.GetFileMetadata file)
                && file != null)
            {
                return CloudResult<DataModels.GetFileMetadata>.Ok(file);
            }

            return CloudResult<DataModels.GetFileMetadata>.Fail(CloudError.NotFound, "The player has no cloud save.");
        }

        private Task<CloudResult<DataModels.InitiateFileUploadsResponse>> InitiateAsync(CancellationToken cancellationToken)
        {
            var request = new DataModels.InitiateFileUploadsRequest { Entity = _entity, FileNames = _fileNames };
            return PlayFabCalls.Call<DataModels.InitiateFileUploadsRequest, DataModels.InitiateFileUploadsResponse>(
                PlayFabDataAPI.InitiateFileUploads, request, cancellationToken);
        }

        // Not tied to the caller's token: the abort is the clean-up after a cancelled or
        // failed upload and has to go out precisely when that token is already cancelled.
        private Task<CloudResult<DataModels.AbortFileUploadsResponse>> AbortAsync()
        {
            var request = new DataModels.AbortFileUploadsRequest { Entity = _entity, FileNames = _fileNames };
            return PlayFabCalls.Call<DataModels.AbortFileUploadsRequest, DataModels.AbortFileUploadsResponse>(
                PlayFabDataAPI.AbortFileUploads, request, CancellationToken.None);
        }

        private string FindUploadUrl(DataModels.InitiateFileUploadsResponse initiated)
        {
            if (initiated?.UploadDetails == null) return null;

            for (int i = 0; i < initiated.UploadDetails.Count; i++)
            {
                DataModels.InitiateFileUploadMetadata detail = initiated.UploadDetails[i];
                if (detail != null && detail.FileName == _fileName) return detail.UploadUrl;
            }

            return null;
        }

        private static CloudSaveInfo ToInfo(DataModels.GetFileMetadata file)
        {
            // The service reports UTC without marking the value as such. Setting the kind
            // keeps the conversion from applying the device's time zone.
            DateTime utc = DateTime.SpecifyKind(file.LastModified, DateTimeKind.Utc);
            return new CloudSaveInfo(true, new DateTimeOffset(utc).ToUnixTimeSeconds(), file.Size);
        }

        private async Task<CloudResult<bool>> HttpPutAsync(string url, byte[] body, CancellationToken cancellationToken)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader(BlobTypeHeader, BlobTypeValue);

                CloudResult<bool> sent = await SendAsync(request, cancellationToken);
                return sent;
            }
        }

        private async Task<CloudResult<byte[]>> HttpGetAsync(string url, CancellationToken cancellationToken)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                CloudResult<bool> sent = await SendAsync(request, cancellationToken);
                if (!sent.IsOk) return CloudResult<byte[]>.Fail(sent.Error, sent.Message);

                byte[] data = request.downloadHandler.data;
                return data == null || data.Length == 0
                    ? CloudResult<byte[]>.Fail(CloudError.Rejected, "The download was empty.")
                    : CloudResult<byte[]>.Ok(data);
            }
        }

        private async Task<CloudResult<bool>> SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            request.timeout = _httpTimeoutSeconds;

            // Abort stops the transfer; the awaited operation then completes with an error.
            using (cancellationToken.Register(request.Abort))
            {
                try
                {
                    await Awaitable.FromAsyncOperation(request.SendWebRequest(), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return CloudResult<bool>.Fail(CloudError.Cancelled, "The transfer was cancelled.");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return CloudResult<bool>.Fail(CloudError.Cancelled, "The transfer was cancelled.");
            }

            if (request.result == UnityWebRequest.Result.Success) return CloudResult<bool>.Ok(true);

            CloudError error = request.result == UnityWebRequest.Result.ConnectionError
                ? CloudError.Network
                : CloudError.Rejected;
            return CloudResult<bool>.Fail(error, request.error + " (HTTP " + request.responseCode + ")");
        }
    }
}
