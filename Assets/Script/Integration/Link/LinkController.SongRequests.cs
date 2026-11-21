using Newtonsoft.Json.Linq;
using YARG.Core.Song;
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
    public partial class LinkController
    {
        // Song a friend asked us to load, while we're still in difficulty select
        private string _remoteRequestHash;
        private string _remoteRequestName;

        private void HandleLoadSong(SongInfo song)
        {
            string hash = song.Hash;
            string name = song.Name ?? "?";

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu || MenuManager.Instance == null)
            {
                Decline(hash, name, DECLINE_BUSY);
                return;
            }

            if (hash.Length != HashWrapper.HASH_SIZE_IN_BYTES * 2 ||
                !SongContainer.SongsByHash.TryGetValue(HashWrapper.FromString(hash), out var entries) ||
                entries.Count == 0)
            {
                ToastManager.ToastWarning($"Your friend picked \"{name}\", which isn't in your library. " +
                    "They'll play it without you.");
                Decline(hash, name, DECLINE_MISSING);
                return;
            }

            var entry = entries[0];
            ToastManager.ToastInformation($"Your friend picked \"{entry.Name.Original}\"! Choose your part.");

            GlobalVariables.State = PersistentState.Default;
            GlobalVariables.State.CurrentSong = entry;

            // Reopen difficulty select so it picks up the new song
            if (MenuManager.Instance.CurrentMenu == MenuManager.Menu.DifficultySelect)
            {
                MenuManager.Instance.PopMenu();
            }
            MenuManager.Instance.PushMenu(MenuManager.Menu.DifficultySelect);

            _remoteRequestHash = hash;
            _remoteRequestName = entry.Name.Original;
        }

        private const string DECLINE_MISSING = "missing";
        private const string DECLINE_BUSY    = "busy";
        private const string DECLINE_BACKED  = "backed out";

        /// <summary>
        /// Turn down a friend's song: the host's coordinator stops waiting for us, and everyone hears why.
        /// </summary>
        private void Decline(string hash, string name, string reason)
        {
            _session.ReportDecline(hash, reason);
            _session.Broadcast(KIND_DECLINED, new JObject
            {
                ["song"] = hash,
                ["name"] = name,
                ["reason"] = reason,
            });
        }

        private void OnFriendDeclined(LinkMember member, JObject data)
        {
            string name = (string) data["name"] ?? "the song";
            string message = (string) data["reason"] switch
            {
                DECLINE_MISSING => $"{member.Name} doesn't have \"{name}\", so they'll sit this one out",
                DECLINE_BACKED  => $"{member.Name} backed out of \"{name}\"",
                _               => $"{member.Name} can't play \"{name}\" right now",
            };
            ToastManager.ToastWarning(message);
        }

        /// <summary>
        /// If the player backs out of a friend's song, decline it so nobody waits on us.
        /// </summary>
        private void CheckRemoteRequestAbandoned()
        {
            if (_remoteRequestHash == null) return;

            if (GlobalVariables.Instance.CurrentScene != SceneIndex.Menu)
            {
                // Moved on to gameplay; reporting ready takes it from here
                _remoteRequestHash = null;
                return;
            }

            if (MenuManager.Instance != null && MenuManager.Instance.CurrentMenu != MenuManager.Menu.DifficultySelect)
            {
                Decline(_remoteRequestHash, _remoteRequestName, DECLINE_BACKED);
                _remoteRequestHash = null;
            }
        }
    }
}
