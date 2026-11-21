using YARG.Core.Song;
using YARG.Integration.Link.Play;
using YARG.Integration.Link.Session;
using YARG.Menu;
using YARG.Menu.Persistent;
using YARG.Song;

namespace YARG.Integration.Link
{
    /// <summary>
    /// A friend picked a song: open difficulty select for it, or decline it, and tell everyone when someone
    /// declines.
    /// </summary>
    /// <remarks>
    /// A request that reaches us on the score screen takes us back to the menu first. The score was saved when the
    /// song ended, so leaving the score screen loses nothing.
    /// </remarks>
    internal sealed class SongRequests
    {
        // Song a friend asked us to load, while we're on the way to difficulty select or still in it
        private string _hash;
        private string _name;
        private SongEntry _entry;

        // Left the score screen for the request, and waiting for the menu to load
        private bool _awaitingMenu;

        /// <summary>We're picking a part for a friend's song.</summary>
        public bool IsChoosing => _hash != null;

        public void Clear()
        {
            _hash = null;
            _name = null;
            _entry = null;
            _awaitingMenu = false;
        }

        public void OnLoadSong(LinkSession session, SongInfo song)
        {
            string hash = song.Hash;
            string name = song.Name ?? "?";

            var scene = GlobalVariables.Instance.CurrentScene;
            bool inMenu = scene == SceneIndex.Menu && MenuManager.Instance != null;
            if (!inMenu && scene != SceneIndex.Score)
            {
                Decline(session, hash, name, DeclinedMessage.BUSY);
                return;
            }

            if (hash.Length != HashWrapper.HASH_SIZE_IN_BYTES * 2 ||
                !SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(hash), out var entries) ||
                entries.Count == 0)
            {
                ToastManager.ToastWarning($"Your friend picked \"{name}\", which isn't in your library. " +
                    "They'll play it without you.");
                Decline(session, hash, name, DeclinedMessage.MISSING);
                return;
            }

            var entry = entries[0];
            ToastManager.ToastInformation($"Your friend picked \"{entry.Name.Original}\"! Choose your part.");

            _hash = hash;
            _name = entry.Name.Original;
            _entry = entry;

            if (inMenu)
            {
                OpenDifficultySelect();
            }
            else
            {
                // On the score screen: Update opens difficulty select once the menu is up
                _awaitingMenu = true;
                GlobalVariables.Instance.LoadScene(SceneIndex.Menu);
            }
        }

        private void OpenDifficultySelect()
        {
            // Set here, not when the request arrives: the score screen resets the state as it unloads
            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.State.CurrentSong = _entry;

            // Reopen difficulty select so it picks up the new song
            if (MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect)
            {
                MenuManager.Instance.PopMenu();
            }
            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);
        }

        /// <summary>
        /// Turn down a friend's song: the host's coordinator stops waiting for us, and everyone hears why.
        /// </summary>
        private static void Decline(LinkSession session, string hash, string name, string reason)
        {
            session.ReportDecline(hash, reason);
            session.Send(new DeclinedMessage { Song = hash, Name = name, Reason = reason });
        }

        public void OnDeclined(LinkMember member, DeclinedMessage message)
        {
            string name = message.Name ?? "the song";
            string text = message.Reason switch
            {
                DeclinedMessage.MISSING => $"{member.Name} doesn't have \"{name}\", so they'll sit this one out",
                DeclinedMessage.BACKED  => $"{member.Name} backed out of \"{name}\"",
                _                       => $"{member.Name} can't play \"{name}\" right now",
            };
            ToastManager.ToastWarning(text);
        }

        /// <summary>
        /// Call every frame. Opens difficulty select once the menu loads after the score screen, and if the player
        /// backs out of a friend's song, declines it so nobody waits on us.
        /// </summary>
        public void Update(LinkSession session)
        {
            if (_hash == null) return;

            if (_awaitingMenu)
            {
                // The score screen is still unloading, or the menu hasn't pushed its first menu yet
                if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MenuManager.Instance == null ||
                    MenuManager.Instance.CurrentMenu == MenuManager.Menu.None)
                {
                    return;
                }

                _awaitingMenu = false;
                OpenDifficultySelect();
                return;
            }

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                // Moved on to gameplay; reporting ready takes it from here
                Clear();
                return;
            }

            if (MenuManager.Instance != null && MenuManager.Instance.CurrentMenu != MenuManager.Menu.DifficultySelect)
            {
                Decline(session, _hash, _name, DeclinedMessage.BACKED);
                Clear();
            }
        }
    }
}
