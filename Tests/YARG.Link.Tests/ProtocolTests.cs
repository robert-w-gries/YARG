using System;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Integration.Link.Session;

namespace YARG.Link.Tests
{
    public class ProtocolTests
    {
        private static string Json(LinkMessage message) => Encoding.UTF8.GetString(LinkProtocol.Encode(message));
        private static LinkMessage Parse(string json) => LinkProtocol.Decode(Encoding.UTF8.GetBytes(json));

        [Test]
        public void WireShapes()
        {
            Assert.That(Json(new StartMessage { DelayMs = 2000 }), Is.EqualTo(@"{""t"":""start"",""delayMs"":2000}"));
            Assert.That(Json(new LoadSongMessage { Song = new SongInfo("ab", "N", "A") }),
                Is.EqualTo(@"{""t"":""load_song"",""song"":{""hash"":""ab"",""name"":""N"",""artist"":""A""}}"));
            Assert.That(Json(new CancelMessage { Reason = null }), Is.EqualTo(@"{""t"":""cancel""}"));
        }

        [Test]
        public void RoundTrip()
        {
            var hello = (HelloMessage) Parse(Json(new HelloMessage
            {
                Protocol = LinkProtocol.VERSION, Game = "v1", Member = "m", Name = "Rob", Host = true,
            }));
            Assert.That(hello.Protocol, Is.EqualTo(LinkProtocol.VERSION));
            Assert.That(hello.Game, Is.EqualTo("v1"));
            Assert.That(hello.Member, Is.EqualTo("m"));
            Assert.That(hello.Name, Is.EqualTo("Rob"));
            Assert.That(hello.Host, Is.True);

            var game = (GameDataMessage) Parse(Json(new GameDataMessage
            {
                From = "m", Kind = "inputs", Data = new JObject { ["time"] = 1.25, ["SomeKey"] = "kept" },
            }));
            Assert.That(game.Kind, Is.EqualTo("inputs"));
            Assert.That((double) game.Data["time"], Is.EqualTo(1.25));
            // The camelCase naming must not touch game data
            Assert.That((string) game.Data["SomeKey"], Is.EqualTo("kept"));

            var roster = (RosterMessage) Parse(Json(new RosterMessage
            {
                Members = { new MemberInfo { Id = "a", Name = "A", Scene = "menu", Host = true } },
            }));
            Assert.That(roster.Members, Has.Count.EqualTo(1));
            Assert.That(roster.Members[0].Host, Is.True);
        }

        [Test]
        public void UnknownTypeAndExtraFieldsAreIgnored()
        {
            Assert.That(Parse(@"{""t"":""from_the_future"",""x"":1}"), Is.Null);
            var start = (StartMessage) Parse(@"{""t"":""start"",""delayMs"":5,""newField"":true}");
            Assert.That(start.DelayMs, Is.EqualTo(5));
        }

        [Test]
        public void MalformedMessagesThrowFormatException()
        {
            Assert.Throws<FormatException>(() => Parse("not json"));
            Assert.Throws<FormatException>(() => Parse(@"{""delayMs"":5}"));
            Assert.Throws<FormatException>(() => Parse(@"{""t"":""start"",""delayMs"":""soon""}"));
        }
    }
}
