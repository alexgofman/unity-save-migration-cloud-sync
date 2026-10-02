namespace SaveSync
{
    /// <summary>
    /// Last line of defence over a state that has just been read: clamps values that no
    /// legitimate game could have produced and repairs missing parts.
    /// </summary>
    /// <remarks>
    /// The sanitizer runs after migration, so it only ever sees the current schema, and
    /// before the state becomes visible to the game, so no system observes an unsanitized
    /// value. It runs for every source: a file on disk and a download from the cloud are
    /// equally untrusted. Anything outside the bounds is treated as corruption, tampering or
    /// a migration bug and is corrected, and every correction is written to the report so an
    /// odd save can be diagnosed afterwards.
    /// </remarks>
    public interface ISanitizer<TState>
    {
        void Sanitize(TState state, SanitizeReport report);
    }
}
