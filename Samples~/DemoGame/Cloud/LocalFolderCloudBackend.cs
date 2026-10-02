using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SaveSync.Samples.DemoGame
{
    /// <summary>
    /// A stand-in for a cloud service: the "cloud save" is one file in a folder. It lets the
    /// demo run, and the restore and conflict flows be tried, without an account anywhere.
    /// Point two installations at the same folder to play two devices.
    /// </summary>
    /// <remarks>
    /// Not a backend for a shipped game. It also shows the minimum a real one has to do:
    /// report failures as results, replace the stored save as a whole, and report the time
    /// the service stored it.
    /// </remarks>
    public sealed class LocalFolderCloudBackend : ICloudSaveBackend
    {
        private readonly string _folder;
        private readonly string _path;

        public LocalFolderCloudBackend(string folder)
        {
            if (string.IsNullOrEmpty(folder)) throw new ArgumentException("A folder is required.", nameof(folder));

            _folder = folder;
            _path = Path.Combine(folder, "cloud-save.bin");
        }

        public bool IsSignedIn => true;

        public Task<CloudResult<CloudSaveInfo>> GetInfoAsync(CancellationToken cancellationToken)
        {
            return Run(() => CloudResult<CloudSaveInfo>.Ok(ReadInfo()));
        }

        public Task<CloudResult<CloudSaveSnapshot>> DownloadAsync(CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                CloudSaveInfo info = ReadInfo();
                return info.Exists
                    ? CloudResult<CloudSaveSnapshot>.Ok(new CloudSaveSnapshot(File.ReadAllBytes(_path), info))
                    : CloudResult<CloudSaveSnapshot>.Fail(CloudError.NotFound, "No cloud save in " + _folder);
            });
        }

        public Task<CloudResult<CloudSaveInfo>> UploadAsync(byte[] payload, CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                // Written beside the stored file and moved over it, so a failed upload leaves
                // the previous one in place, as a real service does.
                Directory.CreateDirectory(_folder);
                string incoming = _path + ".incoming";
                File.WriteAllBytes(incoming, payload);
                File.Delete(_path);
                File.Move(incoming, _path);
                return CloudResult<CloudSaveInfo>.Ok(ReadInfo());
            });
        }

        public Task<CloudResult<bool>> DeleteAsync(CancellationToken cancellationToken)
        {
            return Run(() =>
            {
                if (File.Exists(_path)) File.Delete(_path);
                return CloudResult<bool>.Ok(true);
            });
        }

        private CloudSaveInfo ReadInfo()
        {
            var file = new FileInfo(_path);
            if (!file.Exists) return CloudSaveInfo.None;

            long storedAt = new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds();
            return new CloudSaveInfo(true, storedAt, file.Length);
        }

        private static Task<CloudResult<T>> Run<T>(Func<CloudResult<T>> request)
        {
            try
            {
                return Task.FromResult(request());
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                return Task.FromResult(CloudResult<T>.Fail(CloudError.Network, exception.Message));
            }
        }
    }
}
