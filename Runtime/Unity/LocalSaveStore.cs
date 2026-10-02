using System.IO;
using UnityEngine;

namespace SaveSync.Unity
{
    /// <summary>
    /// The atomic file store, placed under <c>Application.persistentDataPath</c>.
    /// </summary>
    /// <remarks>
    /// Construct it on the main thread: Unity only answers for the persistent data path
    /// there. After construction the store is plain file I/O.
    /// </remarks>
    public sealed class LocalSaveStore : AtomicFileSaveStore
    {
        public const string DefaultFolder = "Saves";

        /// <param name="fileName">File name without extension, for example "slot1".</param>
        /// <param name="folder">Sub-folder of the persistent data path.</param>
        /// <param name="flushToDisk">See <see cref="AtomicFileSaveStore"/>.</param>
        public LocalSaveStore(string fileName, string folder = DefaultFolder, bool flushToDisk = true)
            : base(Path.Combine(Application.persistentDataPath, folder), fileName, flushToDisk)
        {
        }
    }
}
