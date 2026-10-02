namespace SaveSync
{
    /// <summary>
    /// Durable storage for one save on this device. The store deals in bytes and a
    /// timestamp; it knows nothing about the state type.
    /// </summary>
    /// <remarks>
    /// Two properties are required of an implementation, because the rest of the package
    /// relies on them:
    /// <list type="bullet">
    /// <item>A write either replaces the save completely or leaves the previous one
    /// readable. A process that is killed mid-write must not leave a half-written
    /// save.</item>
    /// <item>The timestamp is stored by the same write as the payload, so it can never be
    /// older than the save it describes.</item>
    /// </list>
    /// </remarks>
    public interface ILocalSaveStore
    {
        /// <summary>
        /// True when a save is present on disk, whether or not it can be read.
        /// </summary>
        bool Exists { get; }

        LocalReadResult Read();

        LocalWriteResult Write(byte[] payload, long savedAtUtcSeconds);

        /// <summary>
        /// Removes every copy of the save. If it is interrupted, what is left must not be an
        /// older save than the one that was there.
        /// </summary>
        LocalWriteResult Delete();
    }
}
