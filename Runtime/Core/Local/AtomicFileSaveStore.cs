using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SaveSync
{
    /// <summary>
    /// <see cref="ILocalSaveStore"/> on plain files, written so that a process killed at any
    /// instant leaves a complete save on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A save is never written into the file that holds the current one. Writing in place
    /// starts by truncating that file, so a kill during the write leaves a short file, and a
    /// short file that parses to "nothing" is indistinguishable from a new player.
    /// </para>
    /// <para>
    /// The write sequence, with <c>S</c> the save file:
    /// </para>
    /// <list type="number">
    /// <item>Write the complete new save to <c>S.tmp</c> and flush it to the device.</item>
    /// <item>Move <c>S</c> to <c>S.bak</c>, replacing the older backup.</item>
    /// <item>Move <c>S.tmp</c> to <c>S</c>.</item>
    /// </list>
    /// <para>
    /// Whichever step the process stops in, at least one of the three files is a complete
    /// save, and <see cref="Read"/> finds it: a complete <c>S.tmp</c> is the newest (it was
    /// finished but not yet moved), then <c>S</c>, then <c>S.bak</c>. A partial <c>S.tmp</c>
    /// fails its check and is ignored. Each file starts with one header line holding the
    /// save timestamp, the payload length and a checksum, so "complete" is something the
    /// reader verifies rather than assumes.
    /// </para>
    /// <para>
    /// The timestamp lives in that header on purpose. It is the value the cloud comparison
    /// depends on, and keeping it in a separate store with its own flush schedule means a
    /// kill can leave a new save next to an old timestamp. Written this way, the two cannot
    /// disagree.
    /// </para>
    /// <para>
    /// The checksum detects truncation and accidental damage. It is not a signature and
    /// offers no protection against deliberate edits.
    /// </para>
    /// </remarks>
    public class AtomicFileSaveStore : ILocalSaveStore
    {
        private const string Magic = "SAVESYNC";
        private const int FormatVersion = 1;
        private const int HeaderFields = 5;
        private const int MaxHeaderLength = 128;
        private const byte HeaderEnd = (byte)'\n';

        private readonly string _directory;
        private readonly string _mainPath;
        private readonly string _pendingPath;
        private readonly string _backupPath;
        private readonly bool _flushToDisk;

        /// <param name="directory">Folder that holds the save. Created on first write.</param>
        /// <param name="fileName">File name without extension or folder.</param>
        /// <param name="flushToDisk">
        /// Ask the operating system to put each save on the device before the files are
        /// swapped. Turning it off makes saves cheaper, but then neither the newest save nor
        /// the backup behind it is known to have reached the device: after a power loss or an
        /// operating-system crash both can fail their check, and the save is then reported as
        /// corrupt. A crash or kill of the application alone is safe either way, because the
        /// operating system still holds what was written.
        /// </param>
        public AtomicFileSaveStore(string directory, string fileName, bool flushToDisk = true)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
            if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName)
            {
                throw new ArgumentException("The file name must be a plain name without a folder.", nameof(fileName));
            }

            _directory = directory;
            _mainPath = Path.Combine(directory, fileName + ".sav");
            _pendingPath = _mainPath + ".tmp";
            _backupPath = _mainPath + ".bak";
            _flushToDisk = flushToDisk;
        }

        /// <summary>Path of the save file, for diagnostics.</summary>
        public string FilePath => _mainPath;

        public bool Exists
        {
            get
            {
                try
                {
                    // A pending file on its own counts only when it is complete: a partial
                    // one is the trace of a first save that never finished, and treating it
                    // as a save would lock a new player out of saving.
                    return File.Exists(_mainPath) || File.Exists(_backupPath) || TryReadCopy(_pendingPath, out _, out _);
                }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    // Unknown is treated as present: the caller must not assume a clean slate.
                    return true;
                }
            }
        }

        public LocalReadResult Read()
        {
            try
            {
                if (TryReadCopy(_pendingPath, out byte[] payload, out long savedAt))
                {
                    return LocalReadResult.Ok(payload, savedAt, LocalCopy.Pending);
                }

                if (TryReadCopy(_mainPath, out payload, out savedAt))
                {
                    return LocalReadResult.Ok(payload, savedAt, LocalCopy.Main);
                }

                if (TryReadCopy(_backupPath, out payload, out savedAt))
                {
                    return LocalReadResult.Ok(payload, savedAt, LocalCopy.Backup);
                }

                if (File.Exists(_mainPath) || File.Exists(_backupPath))
                {
                    return LocalReadResult.Corrupt("A save is present but no copy of it passes its integrity check.");
                }

                return LocalReadResult.NotFound();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                // Not being able to read is not the same as the save being damaged. Falling
                // back to an older copy here would silently roll the player back.
                return LocalReadResult.ReadFailed(exception.Message);
            }
        }

        public LocalWriteResult Write(byte[] payload, long savedAtUtcSeconds)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            try
            {
                Directory.CreateDirectory(_directory);
                FinishInterruptedSwap();
                WriteCopy(_pendingPath, payload, savedAtUtcSeconds);
                MovePendingIntoPlace();
                return LocalWriteResult.Ok();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return LocalWriteResult.Failed(exception.Message);
            }
        }

        public LocalWriteResult Delete()
        {
            try
            {
                if (!Directory.Exists(_directory)) return LocalWriteResult.Ok();

                // Oldest copy first. Read prefers the newest copy that is complete, so if
                // this stops halfway, what is left is the save the player had or nothing.
                // Deleting in the other order would leave the backup behind, and the next
                // launch would bring back an older save than the one that was deleted.
                DeleteCopy(_backupPath);
                DeleteCopy(_mainPath);
                DeleteCopy(_pendingPath);
                return LocalWriteResult.Ok();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                return LocalWriteResult.Failed(exception.Message);
            }
        }

        /// <summary>
        /// Removes one file of the save. Separate so that a derived store can do more than
        /// unlink it, and so that tests can stop a delete partway.
        /// </summary>
        protected virtual void DeleteCopy(string path)
        {
            File.Delete(path);
        }

        // A pending file left by an earlier run is either a finished save that was never
        // moved into place, which is the newest copy and must not be overwritten by the
        // write that is about to start, or a partial one, which is discarded.
        private void FinishInterruptedSwap()
        {
            if (!File.Exists(_pendingPath)) return;

            if (TryReadCopy(_pendingPath, out _, out _))
            {
                MovePendingIntoPlace();
            }
            else
            {
                File.Delete(_pendingPath);
            }
        }

        private void MovePendingIntoPlace()
        {
            if (File.Exists(_mainPath))
            {
                File.Delete(_backupPath);
                File.Move(_mainPath, _backupPath);
            }

            File.Move(_pendingPath, _mainPath);
        }

        private void WriteCopy(string path, byte[] payload, long savedAtUtcSeconds)
        {
            string headerLine = string.Format(
                CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} {4:x16}\n",
                Magic, FormatVersion, savedAtUtcSeconds, payload.Length, Checksum(payload));
            byte[] header = Encoding.ASCII.GetBytes(headerLine);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(header, 0, header.Length);
                stream.Write(payload, 0, payload.Length);
                stream.Flush(_flushToDisk);
            }
        }

        private static bool TryReadCopy(string path, out byte[] payload, out long savedAtUtcSeconds)
        {
            payload = null;
            savedAtUtcSeconds = 0;
            if (!File.Exists(path)) return false;

            byte[] bytes = File.ReadAllBytes(path);
            int headerEnd = Array.IndexOf(bytes, HeaderEnd, 0, Math.Min(bytes.Length, MaxHeaderLength));
            if (headerEnd < 0) return false;

            string[] fields = Encoding.ASCII.GetString(bytes, 0, headerEnd).Split(' ');
            if (fields.Length != HeaderFields || fields[0] != Magic) return false;

            CultureInfo invariant = CultureInfo.InvariantCulture;
            if (!int.TryParse(fields[1], NumberStyles.None, invariant, out int format) || format != FormatVersion) return false;
            if (!long.TryParse(fields[2], NumberStyles.AllowLeadingSign, invariant, out long savedAt)) return false;
            if (!int.TryParse(fields[3], NumberStyles.None, invariant, out int length)) return false;
            if (!ulong.TryParse(fields[4], NumberStyles.AllowHexSpecifier, invariant, out ulong checksum)) return false;

            int payloadStart = headerEnd + 1;
            if (bytes.Length - payloadStart != length) return false;

            var body = new byte[length];
            Buffer.BlockCopy(bytes, payloadStart, body, 0, length);
            if (Checksum(body) != checksum) return false;

            payload = body;
            savedAtUtcSeconds = savedAt;
            return true;
        }

        // FNV-1a, 64 bit.
        private static ulong Checksum(byte[] data)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                for (int i = 0; i < data.Length; i++)
                {
                    hash ^= data[i];
                    hash *= 1099511628211UL;
                }

                return hash;
            }
        }

        private static bool IsStorageFailure(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException;
        }
    }
}
