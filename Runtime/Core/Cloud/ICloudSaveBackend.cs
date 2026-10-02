using System.Threading;
using System.Threading.Tasks;

namespace SaveSync
{
    /// <summary>
    /// What the service knows about the player's cloud save without downloading it.
    /// </summary>
    public readonly struct CloudSaveInfo
    {
        public CloudSaveInfo(bool exists, long lastModifiedUtcSeconds, long sizeBytes)
        {
            Exists = exists;
            LastModifiedUtcSeconds = lastModifiedUtcSeconds;
            SizeBytes = sizeBytes;
        }

        public bool Exists { get; }

        /// <summary>
        /// When the service last stored the save, by the service's clock, as Unix seconds
        /// (UTC).
        /// </summary>
        public long LastModifiedUtcSeconds { get; }

        public long SizeBytes { get; }

        public static CloudSaveInfo None => default(CloudSaveInfo);
    }

    /// <summary>
    /// A downloaded cloud save together with what the service reports about it.
    /// </summary>
    public readonly struct CloudSaveSnapshot
    {
        public CloudSaveSnapshot(byte[] payload, CloudSaveInfo info)
        {
            Payload = payload;
            Info = info;
        }

        public byte[] Payload { get; }

        public CloudSaveInfo Info { get; }
    }

    /// <summary>
    /// Storage for one save blob per signed-in player.
    /// </summary>
    /// <remarks>
    /// Contract for implementations:
    /// <list type="bullet">
    /// <item>Expected failures (offline, signed out, rejected) are returned as a failed
    /// <see cref="CloudResult{T}"/>, not thrown.</item>
    /// <item>A request should stop when its token is cancelled. The coordinator does not
    /// depend on it: it stops waiting on its own deadline and ignores a late result.</item>
    /// <item>An upload either replaces the stored blob completely or leaves the previous
    /// one in place.</item>
    /// </list>
    /// </remarks>
    public interface ICloudSaveBackend
    {
        /// <summary>True while requests can be made on behalf of a player.</summary>
        bool IsSignedIn { get; }

        Task<CloudResult<CloudSaveInfo>> GetInfoAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Downloads the save. Fails with <see cref="CloudError.NotFound"/> when there is none.
        /// </summary>
        Task<CloudResult<CloudSaveSnapshot>> DownloadAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Replaces the save and returns what the service now reports for it.
        /// </summary>
        Task<CloudResult<CloudSaveInfo>> UploadAsync(byte[] payload, CancellationToken cancellationToken);

        /// <summary>Deletes the save. Succeeds when there was nothing to delete.</summary>
        Task<CloudResult<bool>> DeleteAsync(CancellationToken cancellationToken);
    }
}
