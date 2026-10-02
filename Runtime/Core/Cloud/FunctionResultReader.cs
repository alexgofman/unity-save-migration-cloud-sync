using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SaveSync
{
    /// <summary>
    /// Reads one typed field from the loosely typed result of a server-side function.
    /// </summary>
    /// <remarks>
    /// Service SDKs hand such a result back as an object: a dictionary, a JSON tree, or JSON
    /// text. Searching its text for a fragment such as <c>"done":true</c> looks equivalent
    /// and is not. It also matches the fragment nested inside another object or quoted
    /// inside an error message, and it stops matching when the service formats the JSON
    /// differently. This reader accepts only a boolean stored directly under the given key
    /// of the top-level object.
    /// </remarks>
    public static class FunctionResultReader
    {
        public static bool TryGetBoolean(object functionResult, string field, out bool value)
        {
            value = false;
            if (functionResult == null || string.IsNullOrEmpty(field)) return false;

            if (functionResult is string text)
            {
                try
                {
                    functionResult = JToken.Parse(text);
                }
                catch (JsonException)
                {
                    return false;
                }
            }

            if (functionResult is JObject json)
            {
                return json.TryGetValue(field, out JToken token) && TryAsBoolean(token, out value);
            }

            if (functionResult is IDictionary<string, object> typed)
            {
                return typed.TryGetValue(field, out object raw) && TryAsBoolean(raw, out value);
            }

            if (functionResult is IDictionary untyped)
            {
                return untyped.Contains(field) && TryAsBoolean(untyped[field], out value);
            }

            return false;
        }

        private static bool TryAsBoolean(object raw, out bool value)
        {
            if (raw is bool flag)
            {
                value = flag;
                return true;
            }

            if (raw is JValue jsonValue && jsonValue.Type == JTokenType.Boolean)
            {
                value = (bool)jsonValue.Value;
                return true;
            }

            value = false;
            return false;
        }
    }
}
