using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace SaveSync.Tests
{
    public class FunctionResultReaderTests
    {
        [Test]
        public void ReadsABoolean_FromADictionary()
        {
            var result = new Dictionary<string, object> { { "done", true }, { "note", "ok" } };

            Assert.That(FunctionResultReader.TryGetBoolean(result, "done", out bool value), Is.True);
            Assert.That(value, Is.True);
        }

        [Test]
        public void ReadsABoolean_FromAnUntypedDictionary()
        {
            var result = new Hashtable { { "done", false } };

            Assert.That(FunctionResultReader.TryGetBoolean(result, "done", out bool value), Is.True);
            Assert.That(value, Is.False);
        }

        [Test]
        public void ReadsABoolean_FromJsonText_HoweverItIsFormatted()
        {
            const string spreadOut = "{\n\t\"done\" :\ttrue\n}";

            Assert.That(FunctionResultReader.TryGetBoolean(spreadOut, "done", out bool value), Is.True);
            Assert.That(value, Is.True);
        }

        [Test]
        public void ReadsABoolean_FromAJsonTree()
        {
            JObject result = JObject.Parse("{\"done\":true}");

            Assert.That(FunctionResultReader.TryGetBoolean(result, "done", out bool value), Is.True);
            Assert.That(value, Is.True);
        }

        [Test]
        public void DoesNotAccept_TheSameTextNestedInsideAnotherObject()
        {
            // A substring search for "done":true would say yes here.
            const string nested = "{\"done\":false,\"previous\":{\"done\":true}}";

            Assert.That(FunctionResultReader.TryGetBoolean(nested, "done", out bool value), Is.True);
            Assert.That(value, Is.False);
        }

        [Test]
        public void DoesNotAccept_TheSameTextQuotedInsideAMessage()
        {
            // And here.
            const string quoted = "{\"error\":\"expected \\\"done\\\":true but the account is locked\"}";

            Assert.That(FunctionResultReader.TryGetBoolean(quoted, "done", out _), Is.False);
        }

        [TestCase("{\"done\":\"true\"}")]
        [TestCase("{\"done\":1}")]
        [TestCase("{\"done\":null}")]
        [TestCase("{}")]
        [TestCase("[true]")]
        [TestCase("true")]
        [TestCase("not json")]
        [TestCase("")]
        public void DoesNotAccept_AnythingThatIsNotABooleanUnderThatKey(string json)
        {
            Assert.That(FunctionResultReader.TryGetBoolean(json, "done", out bool value), Is.False);
            Assert.That(value, Is.False);
        }

        [Test]
        public void DoesNotAccept_ANullResultOrKey()
        {
            Assert.That(FunctionResultReader.TryGetBoolean(null, "done", out _), Is.False);
            Assert.That(FunctionResultReader.TryGetBoolean(new Dictionary<string, object>(), null, out _), Is.False);
        }
    }
}
