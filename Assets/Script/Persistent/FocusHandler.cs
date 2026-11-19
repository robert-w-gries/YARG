using UnityEngine;
using YARG.Core.Audio;
using YARG.Settings;

namespace YARG
{
    /// <summary>
    /// Handles behavior that reacts to the application window gaining or losing focus,
    /// such as muting audio and capping the framerate while unfocused.
    /// </summary>
    public class FocusHandler : MonoSingleton<FocusHandler>
    {
        // Tracks whether audio was muted because the window lost focus,
        // so it can be restored when focus returns.
        private bool _mutedFromFocusLoss;

        // Tracks whether the framerate was capped because the window lost focus,
        // so it can be restored when focus returns.
        private bool _framerateLimitedFromFocusLoss;

        // Unity sends the first OnApplicationFocus before Start. At that time, the settings are not loaded
        // (SettingsManager.Settings is null). This flag records that early event. Update then applies the focus
        // state when the settings are loaded. Thus "Mute on Focus Loss" and "Background FPS Cap" also apply
        // when the game starts without focus.
        private bool _focusChangePending;

        private void Update()
        {
            if (!_focusChangePending || SettingsManager.Settings == null)
            {
                return;
            }

            _focusChangePending = false;
            OnApplicationFocus(Application.isFocused);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (SettingsManager.Settings == null)
            {
                _focusChangePending = true;
                return;
            }

            if (!hasFocus)
            {
                if (SettingsManager.Settings.MuteOnFocusLoss.Value && !_mutedFromFocusLoss)
                {
                    GlobalAudioHandler.SetMasterVolume(0);
                    _mutedFromFocusLoss = true;
                }

                int backgroundFpsCap = SettingsManager.Settings.BackgroundFpsCap.Value;
                if (backgroundFpsCap > 0 && !_framerateLimitedFromFocusLoss)
                {
                    // VSync would otherwise pin the framerate to the monitor's refresh
                    // rate, ignoring targetFrameRate, so disable it while unfocused.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = backgroundFpsCap;
                    _framerateLimitedFromFocusLoss = true;
                }
            }
            else
            {
                if (_mutedFromFocusLoss)
                {
                    GlobalAudioHandler.SetMasterVolume(SettingsManager.Settings.MasterMusicVolume.Value);
                    _mutedFromFocusLoss = false;
                }

                if (_framerateLimitedFromFocusLoss)
                {
                    // Restore from the live settings so changes made while unfocused are respected.
                    QualitySettings.vSyncCount = SettingsManager.Settings.VSync.Value ? 1 : 0;
                    Application.targetFrameRate = SettingsManager.Settings.FpsCap.Value;
                    _framerateLimitedFromFocusLoss = false;
                }
            }
        }
    }
}
