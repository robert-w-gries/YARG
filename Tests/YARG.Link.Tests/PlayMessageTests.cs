using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Integration.Link.Transport;

namespace YARG.Link.Tests
{
    public class PlayMessageTests
    {
        private const string SONG = "0123456789abcdef";
        private const string PLAY = "fedcba9876543210";

        private static T RoundTrip<T>(T message) where T : PlayMessage =>
            (T) PlayProtocol.Decode(PlayProtocol.KindOf(message), PlayProtocol.Encode(message));

        [Test]
        public void InputsKeepTheirWireNames()
        {
            var json = PlayProtocol.Encode(new InputsMessage
            {
                Song = SONG, Play = PLAY, Seq = 3, Time = 1.5, Clock = 1.6,
                Players = new List<PlayerInputs>
                {
                    new() { Index = 1, From = 7, Inputs = new byte[16], Score = 100, BandBonus = 5, Combo = 2,
                        Stars = 1.5f, NotesHit = 9, TotalNotes = 10, StarPower = true },
                },
            });

            Assert.That(json.Properties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "song", "play", "seq", "time", "clock", "players" }));
            var player = (JObject) json["players"]![0];
            Assert.That(player.Properties().Select(p => p.Name), Is.EquivalentTo(new[]
                { "i", "from", "inputs", "score", "bonus", "combo", "stars", "notesHit", "totalNotes", "sp" }));
            Assert.That((bool) player["sp"], Is.True);
            Assert.That((string) player["inputs"], Is.EqualTo(Convert.ToBase64String(new byte[16])));
        }

        [Test]
        public void PauseAndResumeKeepTheirWireNames()
        {
            var pause = PlayProtocol.Encode(new PauseMessage { Song = SONG, Play = PLAY, Epoch = 2, Time = 3, Ready = true });
            Assert.That(pause.Properties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "song", "play", "n", "time", "ready" }));

            var resume = PlayProtocol.Encode(new ResumeMessage { Song = SONG, Play = PLAY, Epoch = 2, Time = 3, DelayMs = 1500 });
            Assert.That(resume.Properties().Select(p => p.Name),
                Is.EquivalentTo(new[] { "song", "play", "n", "time", "delayMs" }));
        }

        [Test]
        public void ReadsMessagesInTheFormatThatTheGameSentBefore()
        {
            // As the controller built it by hand before the typed messages
            var data = JObject.Parse(@"{
                ""song"": ""0123456789abcdef"", ""play"": ""fedcba9876543210"", ""seq"": 4, ""time"": 12.5,
                ""clock"": 12.51,
                ""players"": [ { ""i"": 0, ""from"": 3, ""inputs"": """", ""score"": 1200, ""bonus"": 40,
                    ""combo"": 12, ""stars"": 2.5, ""notesHit"": 30, ""totalNotes"": 31 } ]
            }");

            var inputs = (InputsMessage) PlayProtocol.Decode("inputs", data);
            Assert.That(inputs.Seq, Is.EqualTo(4));
            Assert.That(inputs.Clock, Is.EqualTo(12.51));
            var player = inputs.Players.Single();
            Assert.That(player.From, Is.EqualTo(3));
            Assert.That(player.Inputs, Is.Empty);
            Assert.That(player.BandBonus, Is.EqualTo(40));
            Assert.That(player.Stars, Is.EqualTo(2.5f));
        }

        [Test]
        public void MeterKeepsMemberIdsAsTheyAre()
        {
            var meter = new MeterMessage
            {
                Song = SONG, Play = PLAY, Happiness = 0.5f,
                Players = new Dictionary<string, List<float?>> { ["Member-ABC"] = new() { null, 0.25f } },
            };

            var json = PlayProtocol.Encode(meter);
            Assert.That(((JObject) json["players"]!).Properties().Single().Name, Is.EqualTo("Member-ABC"));

            var back = RoundTrip(meter);
            Assert.That(back.Players["Member-ABC"], Is.EqualTo(new float?[] { null, 0.25f }));
            Assert.That(back.Delivery, Is.EqualTo(Delivery.Unreliable));
        }

        [Test]
        public void MeterValuesThatAreNotNumbersAreDropped()
        {
            var data = JObject.Parse(@"{ ""song"": ""s"", ""play"": ""p"", ""happiness"": ""NaN"",
                ""players"": { ""m"": [ ""Infinity"", 2.0, -1.0 ] } }");

            var meter = (MeterMessage) PlayProtocol.Decode("meter", data);
            Assert.That(meter.Happiness, Is.Null);
            Assert.That(meter.Players["m"], Is.EqualTo(new float?[] { null, 1f, 0f }));
        }

        [Test]
        public void UnknownKindsAreIgnored()
        {
            Assert.That(PlayProtocol.Decode("from_a_newer_game", new JObject()), Is.Null);
        }

        [Test]
        public void SongMessagesNeedASongAndAPlay()
        {
            Assert.Throws<FormatException>(() => PlayProtocol.Decode("left", JObject.Parse(@"{ ""song"": ""s"" }")));
            Assert.Throws<FormatException>(() => PlayProtocol.Decode("left", JObject.Parse(@"{ ""play"": ""p"" }")));
            Assert.Throws<FormatException>(() => PlayProtocol.Decode("left",
                new JObject { ["song"] = new string('a', LinkLimits.MAX_ID_LENGTH + 1), ["play"] = "p" }));
            Assert.That(PlayProtocol.Decode("left", JObject.Parse(@"{ ""song"": ""s"", ""play"": ""p"" }")),
                Is.InstanceOf<LeftMessage>());
        }

        [Test]
        public void ValuesOfTheWrongTypeAreAFormatError()
        {
            Assert.Throws<FormatException>(() => PlayProtocol.Decode("pause",
                JObject.Parse(@"{ ""song"": ""s"", ""play"": ""p"", ""n"": ""first"", ""time"": 1 }")));
        }

        [Test]
        public void BadTimesAreAFormatError()
        {
            Assert.Throws<FormatException>(() => RoundTrip(new PauseMessage { Song = SONG, Play = PLAY, Time = 1e9 }));
            Assert.Throws<FormatException>(() => RoundTrip(new PauseMessage { Song = SONG, Play = PLAY, Epoch = -1 }));
            Assert.Throws<FormatException>(() => RoundTrip(new InputsMessage
                { Song = SONG, Play = PLAY, Time = 1e9, Players = new List<PlayerInputs>() }));
        }

        [Test]
        public void ANonsenseClockIsIgnoredNotRefused()
        {
            var inputs = RoundTrip(new InputsMessage
                { Song = SONG, Play = PLAY, Time = 1, Clock = 1e12, Players = new List<PlayerInputs>() });
            Assert.That(inputs.Clock, Is.Null);
        }

        [Test]
        public void SetupsAreLimited()
        {
            var players = Enumerable.Range(0, LinkLimits.MAX_PLAYERS_PER_MEMBER + 1).Select(_ => new SetupPlayer()).ToList();
            Assert.Throws<FormatException>(() => RoundTrip(new SetupMessage
                { Song = SONG, Play = PLAY, Players = players, Replay = new byte[1] }));
            Assert.Throws<FormatException>(() => RoundTrip(new SetupMessage
                { Song = SONG, Play = PLAY, Players = new List<SetupPlayer>() }));

            var setup = RoundTrip(new SetupMessage
            {
                Song = SONG, Play = PLAY, Replay = new byte[] { 1, 2 },
                Players = new List<SetupPlayer> { new() { Name = new string('x', 500), Instrument = "FiveFretGuitar" } },
            });
            Assert.That(setup.Players[0].Name, Has.Length.EqualTo(LinkLimits.MAX_NAME_LENGTH));
            Assert.That(setup.Players[0].Frame, Is.EqualTo(-1), "a player without a frame has no highway");
            Assert.That(setup.Replay, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(setup.Players[0].StarCutoffs, Is.Null, "an older friend sends no star cutoffs");
        }

        [Test]
        public void SetupStarCutoffsMustBeSane()
        {
            var good = new[] { 1, 2, 3, 4, 5, 6 };
            var setup = RoundTrip(new SetupMessage
            {
                Song = SONG, Play = PLAY, Replay = new byte[1],
                Players = new List<SetupPlayer>
                {
                    new() { StarCutoffs = good },
                    new() { StarCutoffs = new[] { 1, 2, 3 } },
                    new() { StarCutoffs = new[] { 1, 2, 3, 4, 5, -6 } },
                },
            });
            Assert.That(setup.Players[0].StarCutoffs, Is.EqualTo(good));
            Assert.That(setup.Players[1].StarCutoffs, Is.Null, "the wrong number of cutoffs");
            Assert.That(setup.Players[2].StarCutoffs, Is.Null, "a negative cutoff");
        }

        [Test]
        public void TooManyInputsAreRefused()
        {
            var bytes = new byte[(InputCodec.MAX_INPUTS_PER_MESSAGE + 1) * InputCodec.INPUT_SIZE];
            Assert.Throws<FormatException>(() => RoundTrip(new InputsMessage
            {
                Song = SONG, Play = PLAY, Time = 1,
                Players = new List<PlayerInputs> { new() { Inputs = bytes } },
            }));
        }

        [Test]
        public void EveryMessageHasAKindThatDecodesBackToIt()
        {
            var types = typeof(PlayMessage).Assembly.GetTypes()
                .Where(t => typeof(PlayMessage).IsAssignableFrom(t) && !t.IsAbstract).ToList();
            Assert.That(types, Has.Count.EqualTo(12));

            foreach (var type in types)
            {
                var message = (PlayMessage) Activator.CreateInstance(type);
                string kind = PlayProtocol.KindOf(message);
                var data = new JObject { ["song"] = "s", ["play"] = "p", ["member"] = "m", ["replay"] = "" };
                if (kind is "setup" or "inputs") data["players"] = new JArray();
                Assert.That(PlayProtocol.Decode(kind, data), Is.InstanceOf(type), kind);
            }
        }
    }

    public class InputCodecTests
    {
        [Test]
        public void RoundTrips()
        {
            var inputs = new[] { new LinkInput(1.25, 3, -7), new LinkInput(2.5, 0, int.MaxValue) };
            var bytes = InputCodec.Encode(inputs, 0, i => i, out int count);

            Assert.That(count, Is.EqualTo(2));
            Assert.That(bytes, Has.Length.EqualTo(2 * InputCodec.INPUT_SIZE));
            Assert.That(InputCodec.Decode(bytes), Is.EqualTo(inputs));
        }

        [Test]
        public void EncodesFromAnIndexAndAtMostAMessageWorth()
        {
            var inputs = Enumerable.Range(0, InputCodec.MAX_INPUTS_PER_MESSAGE + 10)
                .Select(i => new LinkInput(i, i, i)).ToList();

            var first = InputCodec.Encode(inputs, 5, i => i, out int count);
            Assert.That(count, Is.EqualTo(InputCodec.MAX_INPUTS_PER_MESSAGE));
            Assert.That(InputCodec.Decode(first)[0], Is.EqualTo(inputs[5]));

            InputCodec.Encode(inputs, inputs.Count, i => i, out int none);
            Assert.That(none, Is.Zero);
        }

        [Test]
        public void IgnoresAPartialInputAtTheEnd()
        {
            var bytes = InputCodec.Encode(new[] { new LinkInput(1, 2, 3) }, 0, i => i, out _);
            Assert.That(InputCodec.Decode(bytes.Concat(new byte[5]).ToArray()), Has.Count.EqualTo(1));
            Assert.That(InputCodec.Decode(null), Is.Empty);
        }

        [Test]
        public void RefusesNonsenseTimes()
        {
            var bytes = InputCodec.Encode(new[] { new LinkInput(double.NaN, 0, 0) }, 0, i => i, out _);
            Assert.Throws<FormatException>(() => InputCodec.Decode(bytes));
        }
    }
}
