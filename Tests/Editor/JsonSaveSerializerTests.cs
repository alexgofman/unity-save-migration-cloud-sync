using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;

namespace SaveSync.Tests
{
    public class JsonSaveSerializerTests
    {
        [Test]
        public void RoundTrip_KeepsScalarsBehindPrivateSetters()
        {
            var serializer = new JsonSaveSerializer<TestSave>();
            TestSave original = TestSaves.Current(playerName: "Ada", coins: 1234);
            original.Badges.Add("first-steps");

            TestSave copy = serializer.Deserialize(serializer.Serialize(original));

            Assert.That(copy.PlayerName, Is.EqualTo("Ada"), "private setter on the root");
            Assert.That(copy.Purse.Coins, Is.EqualTo(1234), "private setter inside a nested object");
            Assert.That(copy.Badges, Is.EqualTo(new[] { "first-steps" }));
            Assert.That(copy.Lives, Is.EqualTo(original.Lives));
        }

        [Test]
        public void DefaultJsonNetSettings_SilentlyResetThoseScalars()
        {
            // The failure this package's serializer exists to prevent, pinned down as a test.
            // With stock settings the read succeeds, the collection comes back, and every
            // scalar behind a private setter is back at its default.
            TestSave original = TestSaves.Current(playerName: "Ada", coins: 1234);
            original.Badges.Add("first-steps");
            string json = JsonConvert.SerializeObject(original);

            TestSave copy = JsonConvert.DeserializeObject<TestSave>(json);

            Assert.That(json, Does.Contain("\"Coins\":1234"), "the value was written");
            Assert.That(copy.Badges, Is.EqualTo(new[] { "first-steps" }), "collections are filled in place, so the state looks intact");
            Assert.That(copy.Purse.Coins, Is.EqualTo(0), "but the nested scalar is gone");
            Assert.That(copy.PlayerName, Is.EqualTo(string.Empty), "and so is the one on the root");
        }

        [Test]
        public void Output_IsTheSame_WithAndWithoutTheResolver()
        {
            // The resolver only changes what can be written back on read. A save written by
            // either configuration can be read by this serializer.
            TestSave save = TestSaves.Current(coins: 77);

            string stock = JsonConvert.SerializeObject(save);
            string ours = Encoding.UTF8.GetString(new JsonSaveSerializer<TestSave>().Serialize(save));

            Assert.That(ours, Is.EqualTo(stock));
        }

        [Test]
        public void MissingMembers_KeepTheirInitializers()
        {
            // An older save simply lacks members added later. They must come back as the
            // initializer left them, not as null.
            byte[] olderSave = Encoding.UTF8.GetBytes("{\"SchemaVersion\":0}");

            TestSave save = new JsonSaveSerializer<TestSave>().Deserialize(olderSave);

            Assert.That(save.Purse, Is.Not.Null);
            Assert.That(save.Badges, Is.Not.Null);
            Assert.That(save.PlayerName, Is.EqualTo(string.Empty));
        }

        [Test]
        public void UnknownMembers_AreIgnored()
        {
            // Which is exactly why a save from a newer client has to be refused by version:
            // nothing at this level reports that data was dropped.
            byte[] newerSave = Encoding.UTF8.GetBytes("{\"SchemaVersion\":7,\"AddedLater\":{\"Value\":5}}");

            TestSave save = new JsonSaveSerializer<TestSave>().Deserialize(newerSave);

            Assert.That(save.SchemaVersion, Is.EqualTo(7));
        }

        [Test]
        public void TypeNamesInThePayload_AreNotHonoured()
        {
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"$type\":\"System.Diagnostics.Process, System\",\"SchemaVersion\":2}");

            TestSave save = new JsonSaveSerializer<TestSave>().Deserialize(payload);

            Assert.That(save, Is.TypeOf<TestSave>());
        }

        [Test]
        public void ItemsFromAnInitializer_AreKept_WhenACollectionIsFilledInPlace()
        {
            // Documented caveat: initialize collections empty. Json.NET adds the saved items
            // to the instance the initializer created.
            var serializer = new JsonSaveSerializer<Preloaded>();
            var saved = new Preloaded();
            saved.Items.Add("earned");

            Preloaded copy = serializer.Deserialize(serializer.Serialize(saved));

            Assert.That(copy.Items, Is.EqualTo(new[] { "starter", "starter", "earned" }));
        }

        [Test]
        public void GlobalDefaultSettings_DoNotChangeHowSavesAreRead()
        {
            Func<JsonSerializerSettings> before = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
                var serializer = new JsonSaveSerializer<WithNullMember>();

                string json = Encoding.UTF8.GetString(serializer.Serialize(new WithNullMember()));

                Assert.That(json, Is.EqualTo("{\"Note\":null}"));
            }
            finally
            {
                JsonConvert.DefaultSettings = before;
            }
        }

        private sealed class Preloaded
        {
            public List<string> Items { get; private set; } = new List<string> { "starter" };
        }

        private sealed class WithNullMember
        {
            public string Note { get; set; }
        }
    }
}
