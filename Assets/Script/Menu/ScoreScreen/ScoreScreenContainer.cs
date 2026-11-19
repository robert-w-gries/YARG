using YARG.Core.Engine;
using YARG.Core.Replays;
using YARG.Player;
using YARG.Replays;

namespace YARG.Menu.ScoreScreen
{
    public struct PlayerScoreCard
    {
        public bool  IsHighScore;
        public bool  IsReplay;

        public YargPlayer Player;
        public BaseStats  Stats;
    }

    public struct ScoreScreenStats
    {
        public PlayerScoreCard[] PlayerScores;

        public int BandStars;
        public int BandScore;

        public double MeanAverageOffset;

        /// <summary>The song was played with no fail on (online play ignores the setting).</summary>
        public bool NoFail;

        /// <summary>True if the song was online play (<see cref="Gameplay.GameManager.IsOnlinePlay"/>).</summary>
        public bool IsOnlinePlay;

#nullable enable
        public ReplayInfo? ReplayInfo;
        public bool? ReplayWasConsistent;
#nullable disable
    }
}
