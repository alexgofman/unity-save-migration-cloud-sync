namespace SaveSync
{
    /// <summary>
    /// Turns a state into bytes and back. One instance is shared by the local store and the
    /// cloud path, so both directions of both paths use exactly the same settings.
    /// </summary>
    public interface ISaveSerializer<TState>
    {
        byte[] Serialize(TState state);

        /// <summary>
        /// May throw on malformed input and may return null; callers treat both as a corrupt
        /// save.
        /// </summary>
        TState Deserialize(byte[] payload);
    }
}
