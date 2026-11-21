using System;
using System.Collections.Generic;
using YARG.Integration.Link.Transport;

namespace YARG.Integration.Link.Session
{
    public enum LinkRole
    {
        /// <summary>Accepts joiners, coordinates synced starts, and relays game data between members.</summary>
        Host,

        /// <summary>Connected to a host; follows its lead.</summary>
        Join,
    }

    /// <summary>Another game in the session.</summary>
    public sealed class LinkMember
    {
        public string Id     { get; }
        public string Name   { get; internal set; }
        public bool   IsHost { get; internal set; }

        /// <summary>See <see cref="SceneMessage.Scene"/>.</summary>
        public string Scene { get; internal set; } = LinkSession.SCENE_MENU;

        /// <summary>What the member is doing, as they report it. See <see cref="StateMessage.Status"/>.</summary>
        public string Status { get; internal set; }

        /// <summary>The member's players, as they report them.</summary>
        public IReadOnlyList<PlayerInfo> Players { get; internal set; } = Array.Empty<PlayerInfo>();

        /// <summary>Once the member has left: why, if known.</summary>
        public string LeftReason { get; internal set; }

        /// <summary>
        /// Once the member has left: whether the connection was lost rather than closed on purpose. Only the
        /// host knows; for joiners it's always <c>false</c>.
        /// </summary>
        public bool ConnectionLost { get; internal set; }

        /// <summary>The direct connection to this member, if we have one (the host has one to everyone).</summary>
        internal PeerId? Peer { get; set; }

        internal LinkMember(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public override string ToString() => $"{Name} ({Id})";
    }
}
