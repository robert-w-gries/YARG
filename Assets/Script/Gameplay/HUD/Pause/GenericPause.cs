using System;
using YARG.Core.Input;
using YARG.Core.Logging;
using YARG.Menu.Navigation;
using YARG.Menu.Persistent;
using YARG.Replays;

namespace YARG.Gameplay.HUD
{
    public class GenericPause : GameplayBehaviour
    {
        protected bool HasNavigationScheme { get; set; }
        protected override bool DisableUntilSongStarts => false;

        protected PauseMenuManager PauseMenuManager { get; private set; }

        protected override void GameplayAwake()
        {
            PauseMenuManager = FindAnyObjectByType<PauseMenuManager>();
        }

        /// <summary>
        /// Options that would leave friends behind in online play: practice, skipping to the next song, and no
        /// fail (the band's meter is shared). Restart stays: it restarts the song for the whole band.
        /// </summary>
        private static readonly string[] OnlineHiddenOptions =
        {
            nameof(TogglePractice), nameof(FailPause.EnableNoFail),
            nameof(QuickSettings.ToggleNoFail), nameof(SetlistPause.Skip),
        };

        protected bool IsOnlinePlay => GameManager != null && GameManager.IsOnlinePlay;

        /// <summary>
        /// Set up this menu for online play. This method hides the items in <see cref="OnlineHiddenOptions"/>.
        /// <see cref="OnEnable"/> calls it only in online play.
        /// </summary>
        protected virtual void BuildOnlineOptions()
        {
            foreach (var button in GetComponentsInChildren<NavigatableButton>(true))
            {
                if (!button.CallsAnyMethod(OnlineHiddenOptions)) continue;

                button.gameObject.SetActive(false);
                button.NavigationGroup?.RemoveNavigatable(button);
            }
        }

        protected virtual void OnEnable()
        {
            if (IsOnlinePlay)
            {
                BuildOnlineOptions();
            }

            if (Navigator.Instance == null)
            {
                return;
            }

            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                new NavigationScheme.Entry(MenuAction.Red, "Menu.Common.Back", Back),
                new NavigationScheme.Entry(MenuAction.Start, "Menu.Pause.Generic.Resume", Back, hide: true),
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            }, false));
            HasNavigationScheme = true;
        }

        protected virtual void OnDisable()
        {
            if (HasNavigationScheme)
            {
                if (Navigator.Instance != null)
                {
                    Navigator.Instance.PopScheme();
                }

                HasNavigationScheme = false;
            }
        }

        public virtual void Back()
        {
            PauseMenuManager.PopAllMenusWithResume();
        }

        public virtual void Restart()
        {
            // Online play: the whole band restarts (after a vote, for a joiner). The menu stays open until the
            // song loads again.
            if (IsOnlinePlay && GameManager.RequestOnlineRestart())
            {
                return;
            }

            PauseMenuManager.Restart();
        }

        public void TogglePractice()
        {
            GlobalVariables.State.IsPractice = !GlobalVariables.State.IsPractice;
            GlobalVariables.State.SavedInputTime = GameManager.InputTime;
            PauseMenuManager.Restart();
        }

        public void SaveReplay()
        {
            bool succeeded = false;
            try
            {
                succeeded = GameManager.SaveReplay(GameManager.InputTime, ReplayContainer.ReplayDirectory) != null;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to save replay mid-song");
            }

            if (succeeded)
            {
                DialogManager.Instance.ShowMessage("Replay Saved",
                    "The replay was successfully saved mid-song. This replay can be accessed in the " +
                    "\"Imported Songs\" tab in the \"History\" menu.");
            }
            else
            {
                DialogManager.Instance.ShowMessage("Failed to Save Replay",
                    "The replay was unable to be saved mid-song. This could be because the replay only had bots " +
                    "or an error occurred. Please check the logs for more info.");
            }
        }

        public void BackToLibrary()
        {
            // Online play: if this game is the host and friends still play, the host selects if the friends
            // also leave
            if (IsOnlinePlay && GameManager.OfferLeaveWithParty())
            {
                return;
            }

            PauseMenuManager.Quit();
        }

        public void OpenQuickSettings()
        {
            PauseMenuManager.PushMenu(PauseMenuManager.Menu.QuickSettings);
        }
    }
}
