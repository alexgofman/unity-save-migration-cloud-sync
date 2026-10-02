using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace SaveSync.Tests
{
    /// <summary>
    /// The store against a real folder. "Killed here" is staged by putting the files into
    /// the state the write sequence would have left behind at that point.
    /// </summary>
    public class AtomicFileSaveStoreTests
    {
        private static readonly byte[] SaveA = Encoding.UTF8.GetBytes("{\"save\":\"A\"}");
        private static readonly byte[] SaveB = Encoding.UTF8.GetBytes("{\"save\":\"B, the newer one\"}");
        private static readonly byte[] SaveC = Encoding.UTF8.GetBytes("{\"save\":\"C\"}");

        private string _root;
        private AtomicFileSaveStore _store;

        private string MainPath => _store.FilePath;

        private string PendingPath => _store.FilePath + ".tmp";

        private string BackupPath => _store.FilePath + ".bak";

        [SetUp]
        public void CreateFolder()
        {
            _root = Path.Combine(Path.GetTempPath(), "savesync-tests-" + Guid.NewGuid().ToString("N"));
            _store = new AtomicFileSaveStore(Path.Combine(_root, "saves"), "slot");
        }

        [TearDown]
        public void RemoveFolder()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void Write_ThenRead_ReturnsThePayloadAndItsTimestamp()
        {
            Assert.That(_store.Write(SaveA, 111).IsOk, Is.True);

            LocalReadResult read = _store.Read();

            Assert.That(read.Status, Is.EqualTo(LocalReadStatus.Ok));
            Assert.That(read.Payload, Is.EqualTo(SaveA));
            Assert.That(read.SavedAtUtcSeconds, Is.EqualTo(111));
            Assert.That(read.Source, Is.EqualTo(LocalCopy.Main));
        }

        [Test]
        public void NothingOnDisk_IsNotFound()
        {
            Assert.That(_store.Exists, Is.False);
            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.NotFound));
        }

        [Test]
        public void TheTimestamp_IsOnDiskAsSoonAsTheSaveIs()
        {
            // No flush call exists that could be skipped: a second store opened on the same
            // folder, as after a kill and relaunch, reads the timestamp of the last save.
            _store.Write(SaveA, 111);
            _store.Write(SaveB, 222);

            var afterRelaunch = new AtomicFileSaveStore(Path.Combine(_root, "saves"), "slot");

            Assert.That(afterRelaunch.Read().SavedAtUtcSeconds, Is.EqualTo(222));
        }

        [Test]
        public void EachWrite_KeepsThePreviousSaveAsBackup_AndLeavesNoPendingFile()
        {
            _store.Write(SaveA, 111);
            _store.Write(SaveB, 222);

            Assert.That(File.Exists(BackupPath), Is.True);
            Assert.That(File.Exists(PendingPath), Is.False);
            Assert.That(_store.Read().Payload, Is.EqualTo(SaveB));
        }

        [Test]
        public void TruncatedSave_FallsBackToThePreviousOne()
        {
            _store.Write(SaveA, 111);
            _store.Write(SaveB, 222);
            Truncate(MainPath);

            LocalReadResult read = _store.Read();

            Assert.That(read.Status, Is.EqualTo(LocalReadStatus.Ok));
            Assert.That(read.Source, Is.EqualTo(LocalCopy.Backup));
            Assert.That(read.Payload, Is.EqualTo(SaveA));
            Assert.That(read.SavedAtUtcSeconds, Is.EqualTo(111));
        }

        [Test]
        public void TruncatedSave_WithNoOtherCopy_IsCorrupt_NotMissing()
        {
            // The distinction that matters: "not found" starts a new game, "corrupt" must not.
            _store.Write(SaveA, 111);
            Truncate(MainPath);

            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.Corrupt));
            Assert.That(_store.Exists, Is.True);
        }

        [Test]
        public void SaveMissingItsLastBytes_IsDetectedByTheLength()
        {
            // The header is intact here; only the end of the payload did not make it.
            _store.Write(SaveB, 222);
            byte[] bytes = File.ReadAllBytes(MainPath);
            var shortened = new byte[bytes.Length - 3];
            Array.Copy(bytes, shortened, shortened.Length);
            File.WriteAllBytes(MainPath, shortened);

            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.Corrupt));
        }

        [Test]
        public void SaveWithBytesAppended_IsDetectedByTheLength()
        {
            _store.Write(SaveB, 222);
            using (FileStream stream = File.Open(MainPath, FileMode.Append))
            {
                stream.Write(SaveA, 0, SaveA.Length);
            }

            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.Corrupt));
        }

        [Test]
        public void EmptyFile_IsCorrupt_NotMissing()
        {
            // What writing in place leaves behind when the process dies right after the file
            // was opened for writing.
            _store.Write(SaveA, 111);
            File.WriteAllBytes(MainPath, new byte[0]);

            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.Corrupt));
        }

        [Test]
        public void DamagedByte_IsDetectedByTheChecksum()
        {
            _store.Write(SaveA, 111);
            byte[] bytes = File.ReadAllBytes(MainPath);
            bytes[bytes.Length - 2] ^= 0x20;
            File.WriteAllBytes(MainPath, bytes);

            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.Corrupt));
        }

        [Test]
        public void KilledDuringTheWrite_PartialPendingFileIsIgnored()
        {
            _store.Write(SaveA, 111);
            File.WriteAllBytes(PendingPath, FirstHalf(CompleteFile(SaveB, 222)));

            LocalReadResult read = _store.Read();

            Assert.That(read.Source, Is.EqualTo(LocalCopy.Main));
            Assert.That(read.Payload, Is.EqualTo(SaveA));
        }

        [Test]
        public void KilledDuringTheFirstEverWrite_IsNotFound_AndSavingStillWorks()
        {
            // A partial pending file alone must not count as a save: it would be reported as
            // corrupt and lock a new player out of ever saving.
            Directory.CreateDirectory(Path.GetDirectoryName(PendingPath));
            File.WriteAllBytes(PendingPath, FirstHalf(CompleteFile(SaveA, 111)));

            Assert.That(_store.Exists, Is.False);
            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.NotFound));

            Assert.That(_store.Write(SaveB, 222).IsOk, Is.True);
            Assert.That(_store.Read().Payload, Is.EqualTo(SaveB));
        }

        [Test]
        public void KilledBeforeTheSwap_CompletePendingFileIsTheNewestSave()
        {
            _store.Write(SaveA, 111);
            File.WriteAllBytes(PendingPath, CompleteFile(SaveB, 222));

            LocalReadResult read = _store.Read();

            Assert.That(read.Source, Is.EqualTo(LocalCopy.Pending));
            Assert.That(read.Payload, Is.EqualTo(SaveB));
            Assert.That(read.SavedAtUtcSeconds, Is.EqualTo(222));
        }

        [Test]
        public void KilledBetweenTheTwoMoves_IsRecovered()
        {
            // The old save has been moved to the backup and the new one is not in place yet.
            _store.Write(SaveA, 111);
            File.Move(MainPath, BackupPath);
            File.WriteAllBytes(PendingPath, CompleteFile(SaveB, 222));

            LocalReadResult read = _store.Read();

            Assert.That(read.Status, Is.EqualTo(LocalReadStatus.Ok));
            Assert.That(read.Payload, Is.EqualTo(SaveB));
            Assert.That(_store.Exists, Is.True);
        }

        [Test]
        public void NextWrite_FinishesAnInterruptedSwap_InsteadOfOverwritingIt()
        {
            // Without this, the write that follows a recovered launch would truncate the only
            // copy of the newest save while writing its successor.
            _store.Write(SaveA, 111);
            File.WriteAllBytes(PendingPath, CompleteFile(SaveB, 222));

            _store.Write(SaveC, 333);

            Assert.That(_store.Read().Payload, Is.EqualTo(SaveC));
            Truncate(MainPath);
            Assert.That(_store.Read().Payload, Is.EqualTo(SaveB), "the recovered save became the backup");
        }

        [Test]
        public void Delete_RemovesEveryCopy()
        {
            _store.Write(SaveA, 111);
            _store.Write(SaveB, 222);
            File.WriteAllBytes(PendingPath, CompleteFile(SaveC, 333));

            Assert.That(_store.Delete().IsOk, Is.True);

            Assert.That(_store.Exists, Is.False);
            Assert.That(_store.Read().Status, Is.EqualTo(LocalReadStatus.NotFound));
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void DeleteThatStopsPartway_NeverBringsBackAnOlderSave(int filesRemovedBeforeItStops)
        {
            // Two saves, so that a backup exists. If the save file went first and the delete
            // then stopped, the next read would be served the backup: the save before the one
            // the player asked to delete.
            var store = new StoreWhoseDeleteStops(Path.Combine(_root, "saves"), "slot", filesRemovedBeforeItStops);
            store.Write(SaveA, 111);
            store.Write(SaveB, 222);

            LocalWriteResult deleted = store.Delete();
            LocalReadResult left = store.Read();

            Assert.That(deleted.IsOk, Is.False);
            Assert.That(left.Status, Is.Not.EqualTo(LocalReadStatus.Corrupt));
            if (left.Status == LocalReadStatus.Ok)
            {
                Assert.That(left.Payload, Is.EqualTo(SaveB), "what is left is the save that was there, not the one before it");
            }
        }

        [Test]
        public void Delete_BeforeAnythingWasWritten_Succeeds()
        {
            Assert.That(_store.Delete().IsOk, Is.True);
        }

        [Test]
        public void Write_ThatCannotReachTheFolder_ReportsFailure_AndDoesNotThrow()
        {
            // A file sits where the save folder should be.
            Directory.CreateDirectory(_root);
            string occupied = Path.Combine(_root, "saves-as-a-file");
            File.WriteAllText(occupied, "not a folder");
            var store = new AtomicFileSaveStore(occupied, "slot");

            LocalWriteResult result = store.Write(SaveA, 111);

            Assert.That(result.IsOk, Is.False);
            Assert.That(result.Error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void FileName_MustNotContainAFolder()
        {
            Assert.Throws<ArgumentException>(() => new AtomicFileSaveStore(_root, Path.Combine("nested", "slot")));
            Assert.Throws<ArgumentException>(() => new AtomicFileSaveStore(_root, string.Empty));
        }

        /// <summary>A store whose delete fails when it reaches the given file, as a kill would stop it.</summary>
        private sealed class StoreWhoseDeleteStops : AtomicFileSaveStore
        {
            private readonly int _stopsAt;
            private int _attempts;

            public StoreWhoseDeleteStops(string directory, string fileName, int stopsAt)
                : base(directory, fileName)
            {
                _stopsAt = stopsAt;
            }

            protected override void DeleteCopy(string path)
            {
                _attempts++;
                if (_attempts == _stopsAt) throw new IOException("stopped here");
                base.DeleteCopy(path);
            }
        }

        /// <summary>The exact bytes the store would put on disk for this save.</summary>
        private byte[] CompleteFile(byte[] payload, long savedAtUtcSeconds)
        {
            var scratch = new AtomicFileSaveStore(Path.Combine(_root, "scratch-" + Guid.NewGuid().ToString("N")), "slot");
            Assert.That(scratch.Write(payload, savedAtUtcSeconds).IsOk, Is.True);
            return File.ReadAllBytes(scratch.FilePath);
        }

        private static byte[] FirstHalf(byte[] bytes)
        {
            var half = new byte[bytes.Length / 2];
            Array.Copy(bytes, half, half.Length);
            return half;
        }

        private static void Truncate(string path)
        {
            File.WriteAllBytes(path, FirstHalf(File.ReadAllBytes(path)));
        }
    }
}
