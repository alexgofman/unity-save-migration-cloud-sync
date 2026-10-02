using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace SaveSync
{
    /// <summary>
    /// Makes properties with a non-public setter writable during deserialization.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Save classes usually expose <c>{ get; private set; }</c> and change through methods.
    /// Json.NET serializes such a property but, by default, will not assign it when reading.
    /// What it does instead is easy to misread as working: a property that already holds an
    /// object or a collection is filled in place, so lists and nested objects come back. A
    /// scalar cannot be filled in place, so every number, string and flag behind a private
    /// setter silently keeps its default.
    /// </para>
    /// <para>
    /// The result is a state that looks plausible (the collections are there) with its
    /// balances and counters reset to zero. No exception is thrown and nothing is logged.
    /// This resolver marks those properties writable so that what is written is what is
    /// read. Serialized output is unchanged.
    /// </para>
    /// </remarks>
    public sealed class PrivateSetterContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            JsonProperty property = base.CreateProperty(member, memberSerialization);
            if (!property.Writable && member is PropertyInfo info && info.GetSetMethod(nonPublic: true) != null)
            {
                property.Writable = true;
            }

            return property;
        }
    }
}
