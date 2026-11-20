using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.Serialization;
using UnityEngine.UI;
using YARG.Core.Engine;
using YARG.Core.Game;
using YARG.Core.Logging;
using YARG.Helpers.Extensions;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public enum NoFailMode
    {
        Off,
        On,
        NoMeter
    }
    public class FailMeter : MonoBehaviour
    {
        /// <summary>
        /// One player's icon on the meter.
        /// </summary>
        public sealed class Marker
        {
            public string SpriteName;
            public Color  Color = Color.white;

            /// <summary>The player's meter value, or <c>null</c> to hide their icon.</summary>
            public Func<float?> Happiness;

            /// <summary>Whether the player is in star power, for the meter's glow.</summary>
            public Func<bool> IsStarPowerActive;
        }

        [SerializeField]
        private GameObject _meterContainer;
        [FormerlySerializedAs("Slider")]
        [SerializeField]
        private Slider _bandSlider;
        [FormerlySerializedAs("FillImage")]
        [SerializeField]
        private Image _fillImage;
        [SerializeField]
        private Slider _sliderPrefab;
        [SerializeField]
        private Slider _needlePrefab;
        [SerializeField]
        private RectTransform _sliderContainer;
        [SerializeField]
        private Image _meterSpGlow;

        private Slider[]  _playerSliders;
        private Slider[]  _needleSliders;
        private Tweener[] _playerHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _needleHappinessTweeners = Array.Empty<Tweener>();
        private Tweener[] _xposTweeners            = Array.Empty<Tweener>();
        private Tweener   _meterRedTweener;
        private Tweener   _meterYellowTweener;
        private Tweener   _meterGreenTweener;
        private Tweener   _bandFillTweener;
        private Tweener   _meterPositionTweener;
        private Tweener   _meterGlowFillTweener;
        private Sequence  _meterGlowPulseSequence;
        private float[]   _previousPlayerHappiness;
        private float?[]  _currentHappiness;
        private float     _previousBandHappiness;
        private bool      _sp;

        private MeterColor _previousMeterColor;

        private Vector2[] _xPosVectors;

        private const float OFFSCREEN_Y = -400f;

        /// <summary>
        /// Move the meter out of view when this song has no meter. This method reads
        /// <see cref="GameManager.NoFailMode"/>, not the setting, because online play ignores the setting.
        /// <see cref="GameManager.NoFailMode"/> is correct only after the load. Thus the GameManager calls this
        /// method when it initializes the meter. Awake is too early.
        /// </summary>
        private void HideIfNoMeter(GameManager gameManager)
        {
            if (gameManager.NoFailMode == NoFailMode.NoMeter)
            {
                _meterContainer.transform.position = new Vector3(
                    _meterContainer.transform.position.x,
                    OFFSCREEN_Y,
                    _meterContainer.transform.position.z);
            }
        }

        // TODO: Should probably make a more specific class we can reference here
        private EngineManager _engineManager;
        private GameManager _gameManager;

        private List<Marker> _players;

        // Allows some overlap
        private const float HAPPINESS_COLLISION_RANGE = 0.06f;
        private const float SPRITE_OVERLAP_OFFSET     = 28f;
        private const float SPRITE_INITIAL_OFFSET     = 42f;

        /// <summary>The band's meter, which in an online session can be the host's.</summary>
        private float BandHappiness => _gameManager.BandHappiness;

        // GameManager will have to initialize us
        public void Initialize(EngineManager engineManager, GameManager gameManager)
        {
            _gameManager = gameManager;
            _engineManager = engineManager;
            HideIfNoMeter(gameManager);
            // In an online session that we aren't hosting, the host's meter decides who's shown and where
            _players = gameManager.OnlineBandMeterMarkers() ?? BuildLocalMarkers(engineManager, gameManager);

            _playerSliders = new Slider[_players.Count];
            _needleSliders = new Slider[_players.Count];
            _xposTweeners = new Tweener[_players.Count];
            _playerHappinessTweeners = new Tweener[_players.Count];
            _needleHappinessTweeners = new Tweener[_players.Count];
            _previousPlayerHappiness = new float[_players.Count];
            _currentHappiness = new float?[_players.Count];
            _xPosVectors = new Vector2[_players.Count];

            // Cache tweens for later use
            _meterRedTweener = _fillImage.DOColor(ColorProfile.DefaultRed.ToUnityColor(), 0.25f).
                SetLoops(-1, LoopType.Yoyo).
                SetEase(Ease.InOutSine).
                SetAutoKill(false).
                Pause().
                SetLink(_fillImage.gameObject);

            _meterYellowTweener = _fillImage.DOColor(ColorProfile.DefaultYellow.ToUnityColor(), 0.25f).
                SetAutoKill(false).
                Pause().
                SetLink(_fillImage.gameObject);

            _meterGreenTweener = _fillImage.DOColor(ColorProfile.DefaultGreen.ToUnityColor(), 0.25f).
                SetAutoKill(false).
                Pause().
                SetLink(_fillImage.gameObject);

            // 0.8f is an arbitrary placeholder
            _bandFillTweener = _fillImage.DOFillAmount(0.8f, 0.125f).
                SetAutoKill(false).
                SetLink(_fillImage.gameObject);

            // This is set up to move the container offscreen, but may later be used to move it back on
            _meterPositionTweener = _meterContainer.transform.DOMoveY(OFFSCREEN_Y, 0.5f).
                SetAutoKill(false).
                Pause().
                SetLink(_meterContainer);

            // These values are unimportant, since they get changed in Update anyway
            _meterGlowFillTweener = _meterSpGlow.DOFillAmount(1f, 0.8f)
                .Pause()
                .SetLink(_meterSpGlow.gameObject)
                .SetAutoKill(false);

            _meterGlowPulseSequence = DOTween.Sequence()
                .Append(_meterSpGlow.transform.DOScaleX(0.9f, 0.7f))
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo)
                .Pause()
                .SetLink(_meterSpGlow.gameObject)
                .SetAutoKill(false);

            // attach the slider instances to the scene and apply the correct icon
            for (int i = _players.Count - 1; i >= 0; i--)
            {
                _playerSliders[i] = Instantiate(_sliderPrefab, _sliderContainer);
                _needleSliders[i] = Instantiate(_needlePrefab, _sliderContainer);
                // y value is ignored, so it is ok that it is zero here
                var xOffset = SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * i);
                _xPosVectors[i] = new Vector2(xOffset, 0);

                _xposTweeners[i] = _playerSliders[i].handleRect.DOAnchorPosX(_xPosVectors[i].x, 0.125f).SetAutoKill(false).SetLink(_playerSliders[i].gameObject);
                _needleSliders[i].handleRect.DOAnchorPosX(SPRITE_INITIAL_OFFSET, 0.125f).SetAutoKill(false).SetLink(_needleSliders[i].gameObject);

                var handleImage = _playerSliders[i].handleRect.GetComponentInChildren<Image>();
                var spriteName = _players[i].SpriteName;

                // If the sprite name is null or empty, the icon keeps its default image.
                if (!string.IsNullOrEmpty(spriteName))
                {
                    handleImage.sprite = Addressables.LoadAssetAsync<Sprite>(spriteName).WaitForCompletion();
                }
                handleImage.color = _players[i].Color;

                _playerSliders[i].value = 0.01f;
                _needleSliders[i].value = 0.01f;
                _playerSliders[i].gameObject.SetActive(true);
                _needleSliders[i].gameObject.SetActive(true);

                // Cached for reuse because starting a new tween generates garbage
                // A null value means that the icon is hidden. Update() hides the icon on the first frame. Thus
                // this value is only the start value of the tween. The value of an icon never fails the song.
                // Only the meter of the band can fail the song.
                float happiness = _players[i].Happiness() ?? 0f;
                _playerHappinessTweeners[i] = _playerSliders[i].DOValue(happiness, 0.5f).SetAutoKill(false).SetLink(_playerSliders[i].gameObject);
                _needleHappinessTweeners[i] = _needleSliders[i].DOValue(happiness, 0.5f).SetAutoKill(false).SetLink(_needleSliders[i].gameObject);
                _previousPlayerHappiness[i] = happiness;
            }

            YargLogger.LogDebug("Initialized fail meter");
        }

        /// <summary>This game's own meter: one icon per engine in <paramref name="engineManager"/>.</summary>
        private static List<Marker> BuildLocalMarkers(EngineManager engineManager, GameManager gameManager)
        {
            var markers = new List<Marker>();
            var engines = engineManager.Engines;
            for (int i = 0; i < engines.Count; i++)
            {
                var container = engines[i];
                markers.Add(new Marker
                {
                    SpriteName = container.GetInstrumentSprite(gameManager.Song),
                    Color = gameManager.Players[i].Player.GetGameplayIconColor(),
                    Happiness = () => engineManager.IsRegistered(container) ? container.Happiness : null,
                    IsStarPowerActive = () => container.BaseEngine.BaseStats.IsStarPowerActive,
                });
            }
            return markers;
        }

        // Update is called once per frame
        private void Update()
        {
            // Don't crash the whole game if we didn't get initialized and still manage to somehow become active
            if (_engineManager == null)
            {
                return;
            }

            // No need for any of this if we're paused anyway
            if (_gameManager.Paused)
            {
                return;
            }

            if (_previousBandHappiness != BandHappiness)
            {
                UpdateMeterFill();
            }

            for (var i = 0; i < _players.Count; i++)
            {
                _currentHappiness[i] = _players[i].Happiness();
            }

            bool anyPlayerInSp = false;
            for (var i = _players.Count - 1; i >= 0; i--)
            {
                if (_currentHappiness[i] is not { } playerHappiness)
                {
                    HidePlayerMeter(i);
                    continue;
                }

                ShowPlayerMeter(i);

                if (_players[i].IsStarPowerActive())
                {
                    anyPlayerInSp = true;
                }
                int overlap = 0;
                // Check if we will overlap another icon
                for (var j = i; j >= 0; j--)
                {
                    if (j == i || _currentHappiness[j] is not { } otherPlayerHappiness)
                    {
                        // Ignore self
                        continue;
                    }

                    if (Math.Abs(playerHappiness - otherPlayerHappiness) < HAPPINESS_COLLISION_RANGE)
                    {
                        overlap++;
                    }
                }

                // The extra SPRITE_INITIAL_OFFSET is to get the whole group a bit farther from the meter itself
                var xOffset = SPRITE_INITIAL_OFFSET + (SPRITE_OVERLAP_OFFSET * overlap);
                if (!Mathf.Approximately(_xPosVectors[i].x, xOffset))
                {
                    _xPosVectors[i].x = xOffset;
                    _xposTweeners[i].ChangeEndValue(_xPosVectors[i], 0.125f, true).Play();
                }

                // This we can not do if the current player's happiness hasn't changed
                if (_previousPlayerHappiness[i] != playerHappiness)
                {
                    _playerHappinessTweeners[i].ChangeValues(_playerSliders[i].value, playerHappiness, 0.1f);
                    _needleHappinessTweeners[i].ChangeValues(_needleSliders[i].value, playerHappiness, 0.1f);

                    // Not sure if strictly necessary, but it seems like good practice to not try to play a playing tween
                    if (_playerHappinessTweeners[i].IsComplete())
                    {
                        _playerHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _playerHappinessTweeners[i].Restart();
                    }

                    if (_needleHappinessTweeners[i].IsComplete())
                    {
                        _needleHappinessTweeners[i].Play();
                    }
                    else
                    {
                        _needleHappinessTweeners[i].Restart();
                    }
                }

                _previousPlayerHappiness[i] = playerHappiness;
            }

            UpdateMeterGlow(anyPlayerInSp);
        }

        private void UpdateMeterFill()
        {
            var happiness = BandHappiness;

            var currentColor = GetMeterColor(happiness);
            if (currentColor != _previousMeterColor)
            {
                ApplyColor(currentColor);
                _previousMeterColor = currentColor;
            }

            _bandFillTweener.ChangeValues(_fillImage.fillAmount, happiness).Play();

            _previousBandHappiness = BandHappiness;
        }

        private void UpdateMeterGlow(bool anyPlayerInSp)
        {
            // Happiness 0 = Glow Fill 0.05
            // Happiness 1 = Glow Fill 0.95
            // Everything else needs to be scaled between those two.
            float glowHeight = BandHappiness switch
            {
                < 0 => 0f,
                > 1 => 1f,
                _   => Mathf.Lerp(0.05f, 0.95f, BandHappiness)
            };

            if (anyPlayerInSp && !_sp)
            {
                _meterGlowFillTweener.ChangeValues(_meterSpGlow.fillAmount, glowHeight, (0.8f * BandHappiness));
                _meterGlowFillTweener.Restart();
            }
            else if (!anyPlayerInSp && _sp)
            {
                _meterGlowFillTweener.ChangeValues(_meterSpGlow.fillAmount, 0f, (0.8f * BandHappiness));
                _meterGlowFillTweener.Restart();
            }
            else if (anyPlayerInSp && !_meterGlowFillTweener.IsPlaying())
            {
                var delta = Mathf.Max(Math.Abs(glowHeight - _meterSpGlow.fillAmount), 0.05f);
                _meterGlowFillTweener.ChangeValues(_meterSpGlow.fillAmount, glowHeight, 0.8f * delta);
                _meterGlowFillTweener.Restart();
            }
            _sp = anyPlayerInSp;
        }

        private void ApplyColor(MeterColor color)
        {
            if (_meterRedTweener.active)
            {
                _meterRedTweener.Pause();
            }

            if (_meterYellowTweener.active)
            {
                _meterYellowTweener.Pause();
            }

            if (_meterGreenTweener.active)
            {
                _meterGreenTweener.Pause();
            }

            switch (color)
            {
                case MeterColor.Red:
                    _fillImage.color = Color.black;
                    _meterRedTweener.Restart();
                    break;
                case MeterColor.Yellow:
                    _meterYellowTweener.Restart();
                    break;
                case MeterColor.Green:
                    _meterGreenTweener.Restart();
                    break;
            }
        }

        public void SetActive(bool active)
        {
            if (active)
            {
                // Move onscreen
                _meterPositionTweener.PlayBackwards();
            }

            if (!active)
            {
                // Move offscreen
                _meterPositionTweener.PlayForward();
            }
        }

        /// <summary>
        /// Show an icon again. Online, a friend's icon can come back, for example when the host's meter
        /// starts reporting them.
        /// </summary>
        private void ShowPlayerMeter(int index)
        {
            if (!_playerSliders[index].gameObject.activeSelf)
            {
                _playerSliders[index].gameObject.SetActive(true);
                _needleSliders[index].gameObject.SetActive(true);
                _xposTweeners[index].Play();

                // Move the icon to its current value, even if that's where it was hidden
                _previousPlayerHappiness[index] = float.NaN;
            }
        }

        private void HidePlayerMeter(int index)
        {
            if (_playerSliders[index].gameObject.activeSelf)
            {
                _playerSliders[index].gameObject.SetActive(false);
                _needleSliders[index].gameObject.SetActive(false);
                _playerHappinessTweeners[index].Pause();
                _needleHappinessTweeners[index].Pause();
                _xposTweeners[index].Pause();
            }
        }

        private static MeterColor GetMeterColor(float happiness) =>
            happiness switch
            {
                < 0.333f => MeterColor.Red,
                < 0.666f => MeterColor.Yellow,
                _        => MeterColor.Green
            };

        private void OnDestroy()
        {
            _meterRedTweener?.Kill();
            _meterYellowTweener?.Kill();
            _meterGreenTweener?.Kill();
            _bandFillTweener?.Kill();
            _meterPositionTweener?.Kill();
            _meterGlowFillTweener?.Kill();
            _meterGlowPulseSequence?.Kill();

            if (_xposTweeners != null)
            {
                foreach (var tween in _xposTweeners)
                {
                    tween?.Kill();
                }
            }

            if (_needleSliders != null)
            {
                foreach (var slider in _needleSliders)
                {
                    if (slider != null && slider.handleRect != null)
                    {
                        slider.handleRect.DOKill();
                    }
                }
            }

            if (_playerHappinessTweeners != null)
            {
                foreach (var tween in _playerHappinessTweeners)
                {
                    tween?.Kill();
                }
            }

            if (_needleHappinessTweeners != null)
            {
                foreach (var tween in _needleHappinessTweeners)
                {
                    tween?.Kill();
                }
            }
        }

        private enum MeterColor
        {
            Red,
            Yellow,
            Green
        }
    }
    internal static class FailMeterExtensions
    {
        public static bool IsRegistered(this EngineManager engineManager, EngineManager.EngineContainer player) =>
            engineManager.Engines.Contains(player);
    }
}
