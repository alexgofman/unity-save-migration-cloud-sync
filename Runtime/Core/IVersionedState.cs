namespace SaveSync
{
    /// <summary>
    /// A save state that records the schema version it was last migrated to.
    /// </summary>
    /// <remarks>
    /// Saves written before versioning existed have no such field, so they deserialize with
    /// the integer default. That is why version 0 means "written before versioning" and the
    /// first migration step always starts from 0.
    /// </remarks>
    public interface IVersionedState
    {
        int SchemaVersion { get; set; }
    }
}
