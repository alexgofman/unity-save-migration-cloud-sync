using System;

namespace SaveSync
{
    /// <summary>
    /// Owns the in-memory state and is the only thing that writes it to disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every write goes through the <see cref="SaveGate"/>. The gate opens in exactly four
    /// ways: <see cref="Load"/> read the save, <see cref="Load"/> found that no save exists,
    /// a restored state was installed with <see cref="InstallRestored"/>, or the game reset
    /// on purpose with <see cref="ResetToNew"/> or <see cref="Erase"/>. A load that finds a
    /// save it cannot use leaves the gate closed, so an unreadable file is preserved rather
    /// than replaced by a new game.
    /// </para>
    /// <para>
    /// <see cref="Save"/> never throws for a storage or serialization problem and never
    /// skips silently. Gameplay code calls it from many places and none of them should have
    /// to handle an exception, but each outcome is returned and raised through
    /// <see cref="SaveCompleted"/> so that a save which did not happen is visible.
    /// </para>
    /// <para>
    /// Not thread-safe. Use it from one thread.
    /// </para>
    /// </remarks>
    public sealed class SaveSession<TState> : ISaveSession where TState : class, IVersionedState
    {
        private readonly ILocalSaveStore _store;
        private readonly SavePipeline<TState> _pipeline;
        private readonly Func<TState> _createNew;
        private readonly IClock _clock;
        private readonly Func<TState, bool> _worthPersisting;
        private readonly SaveGate _gate = new SaveGate();

        /// <param name="createNew">Creates the state of a player who has not started yet.</param>
        /// <param name="worthPersisting">
        /// Optional. Answers "has the player begun?" for a state when no save exists yet, so
        /// that opening the game and closing it again does not leave a file behind. Make the
        /// test broad (any sign of progress) rather than a single field. It is never
        /// consulted once a save exists.
        /// </param>
        public SaveSession(
            ILocalSaveStore store,
            SavePipeline<TState> pipeline,
            Func<TState> createNew,
            IClock clock,
            Func<TState, bool> worthPersisting = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
            _createNew = createNew ?? throw new ArgumentNullException(nameof(createNew));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _worthPersisting = worthPersisting;

            State = CreateNewState();
        }

        public event Action<SaveResult> SaveCompleted;

        /// <summary>
        /// The current state. Before a successful <see cref="Load"/> this is a placeholder
        /// that exists so early callers never see null; it cannot be saved.
        /// </summary>
        public TState State { get; private set; }

        public bool IsAuthoritative => _gate.IsOpen;

        public SaveAuthority Authority => _gate.Authority;

        public long LastSavedUtcSeconds { get; private set; }

        /// <summary>
        /// Reads the save from disk through the pipeline and makes it the current state.
        /// </summary>
        public LoadResult<TState> Load()
        {
            _gate.Close();
            LastSavedUtcSeconds = 0;

            LocalReadResult read;
            try
            {
                read = _store.Read();
            }
            catch (Exception exception)
            {
                read = LocalReadResult.ReadFailed(exception.Message);
            }

            switch (read.Status)
            {
                case LocalReadStatus.NotFound:
                    State = CreateNewState();
                    _gate.Open(SaveAuthority.NoSaveOnDisk);
                    return new LoadResult<TState>(LoadStatus.NoSaveFound, State, null, null, LocalCopy.None, null);

                case LocalReadStatus.Corrupt:
                    return Unusable(LoadStatus.Corrupt, null, read.Error);

                case LocalReadStatus.ReadFailed:
                    return Unusable(LoadStatus.ReadFailed, null, read.Error);
            }

            PipelineResult<TState> piped = _pipeline.Read(read.Payload);
            switch (piped.Status)
            {
                case PipelineStatus.Corrupt:
                    return Unusable(LoadStatus.Corrupt, piped.Migration, piped.Error);
                case PipelineStatus.NewerThanClient:
                    return Unusable(LoadStatus.NewerThanClient, piped.Migration, piped.Error);
                case PipelineStatus.MigrationFailed:
                    return Unusable(LoadStatus.MigrationFailed, piped.Migration, piped.Error);
                case PipelineStatus.SanitizerFailed:
                    return Unusable(LoadStatus.SanitizerFailed, piped.Migration, piped.Error);
            }

            State = piped.State;
            LastSavedUtcSeconds = read.SavedAtUtcSeconds;
            _gate.Open(SaveAuthority.LoadedFromDisk);
            return new LoadResult<TState>(LoadStatus.Loaded, State, piped.Migration, piped.Sanitize, read.Source, null);
        }

        /// <summary>
        /// Writes the current state to disk if the gate allows it.
        /// </summary>
        public SaveResult Save()
        {
            SaveResult result = SaveCurrentState();
            SaveCompleted?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Makes a state that came out of <see cref="SavePipeline{TState}.Read"/> (a cloud
        /// restore) the current state and writes it, whether or not a save exists.
        /// </summary>
        /// <remarks>
        /// All or nothing: the state is written first and adopted only if the write
        /// succeeded. A failed install leaves the session exactly as it was, so the game is
        /// never left running on a state the disk does not hold.
        /// </remarks>
        /// <param name="cloudSavedAtUtcSeconds">
        /// The cloud copy's own timestamp, when the service reports one. The restored save is
        /// stamped with it instead of the device clock: at this moment the two copies are the
        /// same save, and saying so with the same timestamp keeps the next comparison from
        /// depending on how far the device clock is from the service's.
        /// </param>
        public SaveResult InstallRestored(TState state, long cloudSavedAtUtcSeconds = 0)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.SchemaVersion != _pipeline.CurrentVersion)
            {
                throw new ArgumentException(
                    "The state is at schema v" + state.SchemaVersion + " but this client writes v" +
                    _pipeline.CurrentVersion + ". Pass downloaded data through SavePipeline.Read first.",
                    nameof(state));
            }

            return Replace(state, SaveAuthority.RestoredFromCloud, cloudSavedAtUtcSeconds);
        }

        /// <summary>
        /// Moves the timestamp of the save on disk forward without changing its content.
        /// Does nothing when the save is already stamped that late.
        /// </summary>
        /// <remarks>
        /// For the moment an upload has landed. The service stamps the cloud copy with its
        /// own clock at that moment, which can be well after the local save it carries was
        /// written (the upload waited, was retried, or the device was offline). Unless the
        /// local timestamp follows, this device would later read its own upload as a newer
        /// save from somewhere else.
        /// </remarks>
        /// <returns>True when the timestamp on disk was changed.</returns>
        public bool RaiseTimestamp(long utcSeconds)
        {
            if (!_gate.IsOpen || utcSeconds <= LastSavedUtcSeconds) return false;

            try
            {
                LocalReadResult read = _store.Read();
                if (read.Status != LocalReadStatus.Ok) return false;
                if (!_store.Write(read.Payload, utcSeconds).IsOk) return false;
            }
            catch (Exception)
            {
                // Best effort. The save itself is untouched; only its timestamp stays old.
                return false;
            }

            LastSavedUtcSeconds = utcSeconds;
            return true;
        }

        /// <summary>
        /// Reads back the bytes of the save on disk, which is what a cloud backup uploads.
        /// </summary>
        /// <returns>
        /// False when the session is not authoritative or there is no readable save on disk.
        /// </returns>
        public bool TryReadSavedPayload(out byte[] payload)
        {
            payload = null;
            if (!_gate.IsOpen) return false;

            try
            {
                LocalReadResult read = _store.Read();
                if (read.Status != LocalReadStatus.Ok) return false;

                payload = read.Payload;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deliberately replaces the save with a new game. This is the way out when
        /// <see cref="Load"/> reported a save that cannot be used and there is nothing to
        /// restore.
        /// </summary>
        public SaveResult ResetToNew()
        {
            return Replace(CreateNewState(), SaveAuthority.ExplicitReset, 0);
        }

        /// <summary>
        /// Deletes the save from this device and returns the session to a new state, for a
        /// "delete my data" request. The in-memory state is replaced as well, so nothing in
        /// the running session can write the old progress back.
        /// </summary>
        public LocalWriteResult Erase()
        {
            LocalWriteResult deleted;
            try
            {
                deleted = _store.Delete();
            }
            catch (Exception exception)
            {
                deleted = LocalWriteResult.Failed(exception.Message);
            }

            if (!deleted.IsOk) return deleted;

            State = CreateNewState();
            LastSavedUtcSeconds = 0;
            _gate.Open(SaveAuthority.NoSaveOnDisk);
            return deleted;
        }

        private SaveResult Replace(TState state, SaveAuthority authority, long savedAtUtcSeconds)
        {
            SaveResult result = WriteState(state, savedAtUtcSeconds);
            if (result.IsSaved)
            {
                State = state;
                _gate.Open(authority);
            }

            SaveCompleted?.Invoke(result);
            return result;
        }

        private SaveResult SaveCurrentState()
        {
            if (!_gate.IsOpen)
            {
                return SaveResult.NotWritten(
                    SaveStatus.RefusedNotAuthoritative,
                    "The in-memory state has not been loaded from, or reconciled with, the save on disk.");
            }

            bool saveExists;
            try
            {
                saveExists = _store.Exists;
            }
            catch (Exception exception)
            {
                return SaveResult.NotWritten(SaveStatus.WriteFailed, exception.Message);
            }

            if (_gate.Decide(saveExists, saveExists || IsWorthPersisting(State)) == SaveDecision.SkipNothingToPersist)
            {
                return SaveResult.NotWritten(SaveStatus.SkippedNothingToPersist, null);
            }

            return WriteState(State, 0);
        }

        /// <param name="savedAtUtcSeconds">The timestamp to store, or 0 to use the clock.</param>
        private SaveResult WriteState(TState state, long savedAtUtcSeconds)
        {
            byte[] payload;
            try
            {
                payload = _pipeline.Write(state);
            }
            catch (Exception exception)
            {
                return SaveResult.NotWritten(SaveStatus.SerializationFailed, exception.Message);
            }

            long savedAt = savedAtUtcSeconds > 0 ? savedAtUtcSeconds : _clock.UtcNowSeconds;
            LocalWriteResult written;
            try
            {
                written = _store.Write(payload, savedAt);
            }
            catch (Exception exception)
            {
                written = LocalWriteResult.Failed(exception.Message);
            }

            if (!written.IsOk)
            {
                return SaveResult.NotWritten(SaveStatus.WriteFailed, written.Error);
            }

            LastSavedUtcSeconds = savedAt;
            return SaveResult.Saved(savedAt, payload.Length);
        }

        private bool IsWorthPersisting(TState state)
        {
            if (_worthPersisting == null) return true;

            try
            {
                return _worthPersisting(state);
            }
            catch (Exception)
            {
                // The predicate is game code inspecting game data. If it cannot answer, the
                // safe answer is to save: persistence must not depend on it working.
                return true;
            }
        }

        private TState CreateNewState()
        {
            TState state = _createNew();
            if (state == null) throw new InvalidOperationException("The state factory returned null.");

            // A state created by this client is at this client's schema by definition.
            // Stamping it here means a new save is never mistaken for a pre-versioning one.
            state.SchemaVersion = _pipeline.CurrentVersion;
            return state;
        }

        private LoadResult<TState> Unusable(LoadStatus status, MigrationResult migration, string error)
        {
            // Keep a placeholder so callers still have an object to read, and keep the gate
            // closed so that placeholder cannot replace the save that could not be used.
            State = CreateNewState();
            return new LoadResult<TState>(status, State, migration, null, LocalCopy.None, error);
        }
    }
}
