using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace SaveSync
{
    /// <summary>
    /// Json.NET serializer for save states, configured so that a state survives a round trip
    /// unchanged, including properties with private setters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The settings are deliberately the same for writing and reading and for local and
    /// cloud use. A read path with its own, slightly different settings is how a restore can
    /// lose data that a normal load keeps.
    /// </para>
    /// <para>
    /// The serializer is built with <see cref="JsonSerializer.Create(JsonSerializerSettings)"/>,
    /// which ignores <c>JsonConvert.DefaultSettings</c>, so a global default installed
    /// elsewhere in the application cannot change how saves are read.
    /// </para>
    /// <para>
    /// One Json.NET behaviour to keep in mind when writing state classes: a collection
    /// property that already holds an instance is filled in place, so items given in a
    /// property initializer are kept and the saved items are added after them. Initialize
    /// collections empty and add starting content in code.
    /// </para>
    /// </remarks>
    public sealed class JsonSaveSerializer<TState> : ISaveSerializer<TState>
    {
        private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);

        private readonly JsonSerializer _serializer;

        public JsonSaveSerializer() : this(CreateDefaultSettings())
        {
        }

        public JsonSaveSerializer(JsonSerializerSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _serializer = JsonSerializer.Create(settings);
        }

        /// <summary>
        /// The settings used by the parameterless constructor. Start from these when a game
        /// needs converters of its own, so the private-setter resolver is not lost.
        /// </summary>
        public static JsonSerializerSettings CreateDefaultSettings()
        {
            return new JsonSerializerSettings
            {
                ContractResolver = new PrivateSetterContractResolver(),
                Formatting = Formatting.None,

                // Type names in the payload would let a tampered save pick the types to
                // instantiate. A save file is untrusted input.
                TypeNameHandling = TypeNameHandling.None,

                // Bytes after the root object mean the payload is not what was written.
                CheckAdditionalContent = true
            };
        }

        public byte[] Serialize(TState state)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new JsonTextWriter(new StreamWriter(stream, Utf8WithoutBom)))
                {
                    _serializer.Serialize(writer, state, typeof(TState));
                }

                return stream.ToArray();
            }
        }

        public TState Deserialize(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            using (var reader = new JsonTextReader(new StreamReader(new MemoryStream(payload), Utf8WithoutBom)))
            {
                return _serializer.Deserialize<TState>(reader);
            }
        }
    }
}
