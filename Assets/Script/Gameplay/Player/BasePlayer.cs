using System;
using System.Collections.Generic;
using PlasticBand.Haptics;
using UnityEngine;
using UnityEngine.InputSystem;
using YARG.Core.Audio;
using YARG.Core.Chart;
using YARG.Core.Engine;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Core.Replays;
using YARG.Gameplay.HUD;
using YARG.Helpers.Extensions;
using YARG.Helpers.UI;
using YARG.Input;
using YARG.Player;
using YARG.Settings;

namespace YARG.Gameplay.Player
{
    public abstract class BasePlayer : GameplayBehaviour
    {
        public int HighwayIndex { get; private set; }

        public YargPlayer Player { get; private set; }

        public float NoteSpeed
        {
            get
            {
                float noteSpeed = Player.Profile.NoteSpeed * _noteSpeedDifficultyScale;

                // If we're in a replay, don't change the note speed (it should be like a video
                // slowing down/speeding up). The actual song speed should be taken into account though,
                // which is saved in the engine parameter override.
                if (Player.IsReplay)
                {
                    return noteSpeed / (float) Player.EngineParameterOverride.SongSpeed;
                }

                if (GameManager.IsPractice && GameManager.SongSpeed < 1)
                {
                    return noteSpeed;
                }

                return noteSpeed / GameManager.SongSpeed;
            }
        }

        /// <summary>
        /// The player's input calibration, in seconds.
        /// </summary>
        /// <remarks>
        /// Be aware that this value is negated!
        /// Positive calibration settings will result in a negative number here.
        /// </remarks>
        public double InputCalibration => -Player.Profile.InputCalibrationSeconds;

        public abstract BaseEngine BaseEngine { get; }

        public BaseStats BaseStats => BaseEngine.BaseStats;
        public BaseEngineParameters BaseParameters => BaseEngine.BaseParameters;

        /// <summary>
        /// <p> Star thresholds, from 1 to 5 stars, then gold stars. </p>
        /// <p> These values represent multiples of the score if you were to FC, hold all sustains fully, hit no dynamics, and use no star power. </p>
        /// <p> Multiplying these by the max multiplier of the instrument will also roughly give you the average multiplier needed for that star. </p>
        /// </summary>
        protected abstract float[] StarMultiplierThresholds { get; set; }

        /// <summary>
        /// Multiples of the maximum points it is possible to get from a solo, which is then added to each star point threshold (1 to 5, then gold stars).
        /// <seealso cref="StarMultiplierThresholds"/>
        /// </summary>
        protected readonly float[] SoloBonusStarMultiplierThresholds = {
            0.05f, 0.1f, 0.2f, 0.35f, 0.65f, 0.95f
        };

        public abstract bool ShouldUpdateInputsOnResume { get; }

        public HitWindowSettings HitWindow { get; protected set; }

        public float Stars => BaseStats.Stars;

        public int Score => BaseStats.TotalScore;
        public int BandBonusScore => BaseStats.BandBonusScore;
        public int Combo => BaseStats.Combo;
        public int NotesHit => BaseStats.NotesHit;

        public int TotalNotes { get; protected set; }

        public bool IsFc { get; protected set; }

        public int? LastHighScore { get; private set; }

        public IReadOnlyList<GameInput> ReplayInputs => _replayInputs.AsReadOnly();

        private Dictionary<int, GameInput> LastInputs { get; } = new();
        private Dictionary<int, GameInput> InputsToSendOnResume { get; } = new();

        protected SongChart Chart { get; private set; }

        protected SyncTrack SyncTrack { get; private set; }

        protected bool IsInitialized { get; private set; }

        protected List<ISantrollerHaptics> SantrollerHaptics { get; private set; } = new();

        protected BaseInputViewer InputViewer { get; private set; }

        protected int  LastCombo;
        protected bool IsStemMuted;

        private List<GameInput> _replayInputs;

        private int _replayInputIndex;

        private float _noteSpeedDifficultyScale;

        public EngineManager.EngineContainer EngineContainer;

        protected bool PlayerHasFailed;

        public bool IsActive => Player.IsActive;
        public bool IsBot    => Player.Profile.IsBot;

        /// <summary>
        /// Whether this player mirrors a friend's player over the online link.
        /// </summary>
        public bool IsRemote => Player.IsRemote;

        /// <summary>
        /// How far behind the song this player's engine runs, in seconds. Always 0 except for remote players,
        /// whose inputs arrive a network delay late. Their track is still drawn in time with the song, so their
        /// hits show up this late.
        /// </summary>
        public double EngineLag { get; private set; }

        /// <summary>
        /// The visual time this player's track is drawn at. Use this instead of
        /// <see cref="GameManager.VisualTime"/> for anything positioned on the track.
        /// </summary>
        public double PlayerVisualTime => GameManager.VisualTime;

        /// <summary>
        /// How far past the strike line (in track units) this player's notes need to stay before they're
        /// removed, so a remote player's late hits still find them. Capped, so a stalled connection doesn't
        /// keep every passed note around.
        /// </summary>
        public float LateHitAllowance => (float) (Math.Min(EngineLag, MAX_LATE_HIT_SECONDS) * NoteSpeed);

        private const double MAX_LATE_HIT_SECONDS = 1.0;

        protected override void GameplayAwake()
        {
            _replayInputs = new List<GameInput>();

            // TODO: Couldn't there be more than one input viewer?
            //  We were using FindObjectOfType<BaseInputViewer> before anyway, so we're no worse off in that respect
            InputViewer = FindFirstObjectByType<BaseInputViewer>();

            IsFc = true;
        }

        private void Update()
        {
            //Ensure hud elements get repositioned on screen size change
            if (ScreenSizeDetector.HasScreenSizeChanged)
            {
                UpdateVisuals(PlayerVisualTime);
            }
        }

        protected void Start()
        {
            if (Player.Bindings is not null)
            {
                SantrollerHaptics = Player.Bindings.GetDevicesByType<ISantrollerHaptics>();
            }

            if (!Player.IsReplay)
            {
                SubscribeToInputEvents();
            }
        }

        protected void Initialize(int index, YargPlayer player, SongChart chart, int? lastHighScore)
        {
            if (IsInitialized)
            {
                return;
            }

            HighwayIndex = index;
            Player = player;

            if (!Player.IsReplay)
            {
                Player.MenuInput += OnMenuInput;
            }

            Chart = chart;
            SyncTrack = chart.SyncTrack;

            LastHighScore = lastHighScore;

            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();

            if (Player.IsReplay && GameManager.ReplayInfo != null)
            {
                _replayInputs = new List<GameInput>(GameManager.ReplayData.Frames[player.ReplayIndex].Inputs);
                YargLogger.LogFormatDebug("Initialized replay inputs with {0} inputs", _replayInputs.Count);
            }

            if (InputViewer != null && !player.IsRemote)
            {
                InputViewer.SetColors(player.ColorProfile);
                InputViewer.ResetButtons();
            }

            IsInitialized = true;
        }

        public virtual void GameplayUpdate()
        {
            if (!GameManager.Started || GameManager.Paused)
            {
                return;
            }

            if (IsRemote)
            {
                RemoteGameplayUpdate();
                return;
            }

            if (!GameManager.Rewinding)
            {
                UpdateInputs(GameManager.InputTime);
            }

            UpdateVisuals(GameManager.VisualTime);
        }

        private void RemoteGameplayUpdate()
        {
            var feed = Player.RemoteFeed;
            feed.DrainInto(_replayInputs);

            // Only advance the engine as far as the friend's game has sent inputs for,
            // so it never misses a note whose input is still in flight
            double inputTime = GameManager.InputTime;
            if (!GameManager.Rewinding && feed.TryGetPlaybackTime(inputTime, out double time))
            {
                EngineLag = inputTime - time;
                UpdateInputs(time);
            }

            UpdateVisuals(PlayerVisualTime);
        }

        protected abstract void UpdateVisuals(double visualTime);
        protected abstract void ResetVisuals();
        public abstract void Rewind(double visualTime);
        public abstract void PostRewind(double visualTime);

        protected virtual void ResetDifficulty(double time)
        {
            _noteSpeedDifficultyScale = Player.Profile.CurrentDifficulty.NoteSpeedScale();
        }

        public virtual void ResetPracticeSection()
        {
            LastCombo = 0;

            IsFc = true;

            ResetVisuals();
        }

        public abstract void SetPracticeSection(uint start, uint end);

        // TODO Make this more generic
        public abstract void SetStemMuteState(bool muted);

        public virtual void SetStarPowerFX(bool active)
        {
            GameManager.ChangeStemReverbState(SongStem.Song, active);
        }

        public virtual void SetReplayTime(double time)
        {
            IsFc = true;

            _replayInputIndex = BaseEngine.ProcessUpToTime(time, ReplayInputs);

            SetStemMuteState(false);

            ResetVisuals();
            UpdateVisuals(time);
        }

        protected override void GameplayDestroy()
        {
            if (!Player.IsReplay)
            {
                Player.MenuInput -= OnMenuInput;
                UnsubscribeFromInputEvents();
            }

            FinishDestruction();
        }

        protected virtual void FinishDestruction()
        {
        }

        protected virtual void UpdateInputs(double time)
        {
            if (!IsActive)
            {
                return;
            }

            // Apply input offset
            // Video offset is already accounted for
            time += InputCalibration;

            if ((Player.IsReplay && GameManager.ReplayInfo != null) || IsRemote)
            {
                while (_replayInputIndex < ReplayInputs.Count)
                {
                    var input = ReplayInputs[_replayInputIndex];

                    // Current input does not meet the time requirement
                    if (time < input.Time)
                    {
                        break;
                    }

                    BaseEngine.QueueInput(ref input);
                    OnInputQueued(input);

                    _replayInputIndex++;
                }
            }

            BaseEngine.Update(time);
        }

        private void SubscribeToInputEvents()
        {
            Player.Bindings.SubscribeToGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded += OnDeviceAdded;
            Player.Bindings.DeviceRemoved += OnDeviceRemoved;
        }

        private void UnsubscribeFromInputEvents()
        {
            Player.Bindings.UnsubscribeFromGameplayInputs(Player.Profile.GameMode, OnGameInput);

            Player.Bindings.DeviceAdded -= OnDeviceAdded;
            Player.Bindings.DeviceRemoved -= OnDeviceRemoved;
        }

        private void OnDeviceAdded(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Add(haptics);
            }
        }

        private void OnDeviceRemoved(InputDevice device)
        {
            if (device is ISantrollerHaptics haptics)
            {
                SantrollerHaptics.Remove(haptics);
            }

            if (GameManager != null && !GameManager.Paused && SettingsManager.Settings.PauseOnDeviceDisconnect.Value)
            {
                GameManager.SetPaused(true);
            }
        }

        public void SendInputsOnResume()
        {
            foreach (var originalInput in InputsToSendOnResume.Values)
            {
                var input = new GameInput(InputManager.CurrentInputTime, originalInput.Action, originalInput.Integer);
                OnGameInput(ref input);
            }

            InputsToSendOnResume.Clear();
        }

        protected virtual bool IsMenuOpen => false;

        protected virtual void OnMenuInput(YargPlayer player, ref GameInput input)
        {
        }

        protected void OnGameInput(ref GameInput input)
        {
            // Ignore completely if the song hasn't started yet or player failed
            if (!GameManager.Started || PlayerHasFailed || !IsActive)
            {
                YargLogger.LogFormatDebug("Ignoring input: Started: {0} Failed: {1} IsActive: {2}", GameManager.Started, PlayerHasFailed, IsActive);
                return;
            }

            if (IsMenuOpen && !SettingsManager.Settings.PauseOnMenuOpen.Value)
            {
                return;
            }

            // Ignore while paused
            if (GameManager.Paused || GameManager.Rewinding)
            {
                if (!ShouldUpdateInputsOnResume)
                {
                    return;
                }

                if (LastInputs.TryGetValue(input.Action, out var lastInput))
                {
                    if (lastInput.Button != input.Button)
                    {
                        InputsToSendOnResume[input.Action] = input;
                    }
                    else
                    {
                        InputsToSendOnResume.Remove(input.Action);
                    }
                }

                return;
            }

            LastInputs[input.Action] = input;

            double adjustedTime = GameManager.GetInputTime(input.Time);
            // Apply input offset
            adjustedTime += InputCalibration;
            input = new(adjustedTime, input.Action, input.Integer);

            // Allow the input to be explicitly ignored before processing it
            if (InterceptInput(ref input)) return;

            BaseEngine.QueueInput(ref input);
            OnInputQueued(input);
            _replayInputs.Add(input);
        }

        protected virtual void OnStarPowerPhraseHit()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay && !IsRemote)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerAward);
            }
        }

        protected virtual void OnStarPowerReady()
        {
            if (!GameManager.Paused && !GameManager.IsSeekingReplay && !IsRemote)
            {
                GlobalAudioHandler.PlaySoundEffect(SfxSample.StarPowerReady);
            }
        }

        protected virtual void OnStarPowerPhraseMissed()
        {

        }

        protected virtual void OnStarPowerStatus(bool active)
        {
            var deploySample = SfxSample.StarPowerDeploy;
            if (SettingsManager.Settings.UseCrowdCheering.Value &&
                !GlobalVariables.State.CrowdSfxVenueOverride)
            {
                deploySample = SfxSample.StarPowerDeployCrowd;
            }

            if (!GameManager.Paused && !IsRemote)
            {
                GlobalAudioHandler.PlaySoundEffect(active
                    ? deploySample
                    : SfxSample.StarPowerRelease);

                SetStarPowerFX(active);
            }

            GameManager.ChangeStarPowerStatus(active);

            foreach (var haptics in SantrollerHaptics)
            {
                haptics.SetStarPowerActive(active);
            }
        }

        protected abstract bool InterceptInput(ref GameInput input);

        protected virtual void OnInputQueued(GameInput input)
        {
            if (InputViewer != null && !IsRemote)
            {
                InputViewer.OnInput(input);
            }
        }

        protected void OnComboIncrement(int amount)
        {
            GameManager.AddBandCombo(amount);
        }

        protected void OnComboReset()
        {
            GameManager.ResetBandCombo();
        }

        public abstract (ReplayFrame Frame, ReplayStats Stats) ConstructReplayData();
    }
}
