using System;

namespace SaveSync
{
    /// <summary>
    /// Why the in-memory state is allowed to overwrite the save on disk.
    /// </summary>
    public enum SaveAuthority
    {
        /// <summary>
        /// The state is a placeholder. It has not been reconciled with the disk and must not
        /// be written.
        /// </summary>
        None,

        /// <summary>A load found no save on this device, so there is nothing to overwrite.</summary>
        NoSaveOnDisk,

        /// <summary>The state was read from the save on disk.</summary>
        LoadedFromDisk,

        /// <summary>The state was downloaded and installed at the player's request.</summary>
        RestoredFromCloud,

        /// <summary>The game deliberately replaced the save with a new one.</summary>
        ExplicitReset
    }

    public enum SaveDecision
    {
        Write,

        /// <summary>The state is not authoritative. Nothing may be written.</summary>
        RefuseNotAuthoritative,

        /// <summary>There is no save yet and the state holds nothing worth creating one for.</summary>
        SkipNothingToPersist
    }

    /// <summary>
    /// The authoritative-state invariant: the in-memory state may be written to disk only
    /// once it is known to supersede what is on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A state object exists from the moment the session is constructed, so that code which
    /// runs early never meets a null. Until the save has been loaded that object is a
    /// placeholder. Anything that saves during start-up, before the load has run, would
    /// replace the player's file with an empty state, and nothing about the call looks
    /// wrong. The gate turns "has this state been reconciled with the disk?" into a fact
    /// that is set in a handful of places and checked on every write.
    /// </para>
    /// <para>
    /// The gate depends on where the state came from, never on what is in it. A content
    /// check such as "the player has chosen a name" is only a proxy for that question, and
    /// it fails in both directions: any code path that legitimately clears the field turns
    /// every later save into a silent no-op, and a placeholder that happens to satisfy the
    /// check is written over real progress.
    /// </para>
    /// <para>
    /// A second, separate rule is applied here too: do not create a save file for a player
    /// who has not started yet. It applies only while no file exists, so it can delay the
    /// first save but can never stop an existing save from being updated.
    /// </para>
    /// </remarks>
    public sealed class SaveGate
    {
        public SaveAuthority Authority { get; private set; }

        public bool IsOpen => Authority != SaveAuthority.None;

        public void Open(SaveAuthority authority)
        {
            if (authority == SaveAuthority.None)
            {
                throw new ArgumentException("Opening the gate needs a reason.", nameof(authority));
            }

            Authority = authority;
        }

        public void Close()
        {
            Authority = SaveAuthority.None;
        }

        /// <param name="saveExistsOnDisk">A save is present on disk, readable or not.</param>
        /// <param name="stateIsWorthPersisting">
        /// The game's own answer to "has the player begun?". Only consulted when no save
        /// exists.
        /// </param>
        public SaveDecision Decide(bool saveExistsOnDisk, bool stateIsWorthPersisting)
        {
            if (!IsOpen) return SaveDecision.RefuseNotAuthoritative;
            if (!saveExistsOnDisk && !stateIsWorthPersisting) return SaveDecision.SkipNothingToPersist;
            return SaveDecision.Write;
        }
    }
}
