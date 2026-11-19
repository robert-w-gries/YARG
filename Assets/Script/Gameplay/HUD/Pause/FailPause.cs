using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Menu.Navigation;
using YARG.Settings;

namespace YARG.Gameplay.HUD
{
    public class FailPause : GenericPause
    {
        [SerializeField]
        private GameObject _separatorObject;

        // This menu has a different navigation scheme. Thus it does not call base.OnEnable().
        protected override void OnEnable()
        {
            if (IsOnlinePlay)
            {
                BuildOnlineOptions();
            }

            HandleNavigationScheme();
        }

        protected override void BuildOnlineOptions()
        {
            base.BuildOnlineOptions();

            // Only "Back to Library" stays. Thus the separators are not necessary.
            _separatorObject.SetActive(false);
            foreach (Transform child in _separatorObject.transform.parent)
            {
                if (child.name.StartsWith("Separator")) child.gameObject.SetActive(false);
            }
        }

        private async void HandleNavigationScheme()
        {
            await UniTask.WaitForSeconds(0.5f, true, cancellationToken: this.GetCancellationTokenOnDestroy());
            if (!isActiveAndEnabled || Navigator.Instance == null)
            {
                return;
            }

            _ = Navigator.Instance.PushScheme(new NavigationScheme(new()
            {
                NavigationScheme.Entry.NavigateSelect,
                NavigationScheme.Entry.NavigateUp,
                NavigationScheme.Entry.NavigateDown,
            }, false));
            HasNavigationScheme = true;
        }
        public void EnableNoFail(bool resume)
        {
            // It feels a bit icky reaching down into the settings like this
            SettingsManager.Settings.NoFail.SetValueWithoutNotify(NoFailMode.On);
            if (resume)
            {
                // UnfailSong already resumes, don't do it twice by calling Back()
                PauseMenuManager.PopAllMenus();
                GameManager.UnfailSong();
            }
            else
            {
                Restart();
            }
        }
    }
}