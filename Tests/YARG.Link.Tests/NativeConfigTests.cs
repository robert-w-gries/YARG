using System;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    /// <summary>The JSON text that <see cref="NativeLinkConfig"/> gives to <c>yl_create</c>.</summary>
    public class NativeConfigTests
    {
        [Test]
        public void StringsAreEscaped()
        {
            var config = new NativeLinkConfig
            {
                BindAddress = "127.0.0.1:0",
                LogFilter = "a\"b\\c\nd\u0001",
            };

            var json = JObject.Parse(config.ToJson());

            Assert.That((string) json["bind_addr"], Is.EqualTo("127.0.0.1:0"));
            Assert.That((string) json["log_filter"], Is.EqualTo("a\"b\\c\nd\u0001"));
        }

        [Test]
        public void ValuesHaveTheTypesTheLibraryReads()
        {
            var config = new NativeLinkConfig
            {
                Relay = false,
                AddressLookup = false,
                TicketAddresses = true,
                ConnectTimeout = TimeSpan.FromSeconds(1.5),
                Accept = true,
                MaxPeers = 12,
            };

            var json = JObject.Parse(config.ToJson());

            Assert.That(json["relay"]!.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That((bool) json["relay"], Is.False);
            Assert.That((bool) json["address_lookup"], Is.False);
            Assert.That((bool) json["ticket_addrs"], Is.True);
            Assert.That(json["connect_timeout_ms"]!.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That((long) json["connect_timeout_ms"], Is.EqualTo(1500));
            Assert.That((bool) json["accept"], Is.True);
            Assert.That((int) json["max_peers"], Is.EqualTo(12));
        }

        [Test]
        public void NullValuesAreLeftOut()
        {
            var json = JObject.Parse(new NativeLinkConfig().ToJson());

            Assert.That(json.ContainsKey("bind_addr"), Is.False);
            Assert.That(json.ContainsKey("log_filter"), Is.False);
        }
    }
}
