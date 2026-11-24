using System;

namespace YARG.Integration.Link.Session
{
    public sealed class LinkSessionOptions
    {
        /// <summary>The game build. Members must match exactly.</summary>
        /// <remarks>
        /// <para>
        /// The check is necessary. Each game runs a mirror of each friend's engine from that friend's inputs and
        /// replay data. The message formats, the replay format and the engine rules can change between builds.
        /// Thus a different build can mirror a friend's play incorrectly, without an error. The hello refuses
        /// such a member before this can occur.
        /// </para>
        /// <para>
        /// <c>LinkController</c> sets this to <c>GlobalVariables.Instance.CurrentVersion</c>, the value
        /// that <c>GameRecord.GameVersion</c> also records. The session does not read that value itself, because
        /// <see cref="LinkSession"/> does not use Unity, and the tests run it outside Unity.
        /// </para>
        /// </remarks>
        public string GameVersion = "dev";

        /// <summary>Shown to other members.</summary>
        public string Name = "Player";

        /// <summary>Identifies this game to the other members for the lifetime of the session.</summary>
        public string MemberId = Guid.NewGuid().ToString("N");

        /// <summary>Lead time between "everyone is ready" and playback starting (host only).</summary>
        public double StartLeadSeconds = 2.0;

        /// <summary>Stop waiting for members who haven't reported ready after this long (host only).</summary>
        public double StartTimeoutSeconds = 90.0;

        /// <summary>
        /// Host only: a joiner's restart vote that not everyone in its song agreed to within this long ends
        /// without a restart.
        /// </summary>
        public double RestartVoteSeconds = 30.0;

        /// <summary>Host only: a joiner can't open a restart vote within this long of opening its last one.</summary>
        public double RestartCooldownSeconds = 30.0;

        /// <summary>Drop connections that haven't said hello within this long.</summary>
        public double HelloTimeoutSeconds = 10.0;

        /// <summary>
        /// Host only: the most joiners at once. The host is not a joiner, thus a full session has
        /// <see cref="MaxMembers"/> + 1 members. More joiners are refused with <see cref="ByeCodes.FULL"/>.
        /// This limit is for the network: the host sends the inputs of each member to all other joiners. The
        /// limit for players is <see cref="LinkLimits.MAX_SESSION_PLAYERS"/>.
        /// </summary>
        public int MaxMembers = 8;

        /// <summary>Host only: connections that haven't said hello yet. More are dropped straight away.</summary>
        public int MaxUnadmittedPeers = 4;

        /// <summary>Messages larger than this are neither sent nor accepted; a peer that sends one is dropped.</summary>
        public int MaxMessageBytes = LinkLimits.MAX_MESSAGE_BYTES;

        /// <summary>
        /// What one joiner may send the host, on average, in messages and bytes per second. Inputs go out at
        /// up to 120 Hz, so these leave about twice the room a real game needs. A host may send each joiner
        /// <see cref="MaxMembers"/> + 1 times as much, since it passes everyone's data on.
        /// </summary>
        public double MessagesPerSecond = 250;
        public double BytesPerSecond    = 512 * 1024;

        /// <summary>How far over the average rates a peer may go for a moment (a song setup, say).</summary>
        public double BurstSeconds = 4.0;

        /// <summary>Transport events handled per <see cref="LinkSession.Update"/>, so a flood can't stall a frame.</summary>
        public int MaxEventsPerUpdate = 4096;
    }
}
