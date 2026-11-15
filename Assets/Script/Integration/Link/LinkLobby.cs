using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YARG.Core;
using YARG.Integration.Link.Session;
using YARG.Localization;
using YARG.Menu.Data;
using YARG.Menu.Dialogs;
using YARG.Menu.Persistent;

namespace YARG.Integration.Link
{
    /// <summary>
    /// The online lobby: host or join a session by invite code, see who's in it (their parts, whether they're
    /// ready, how good the connection is), copy the invite code, remove members, leave.
    /// </summary>
    /// <remarks>
    /// Built from the existing dialogs, so it needs no prefabs of its own. Opened from "Online" in the main menu.
    /// There's no separate "start" button: anyone picks a song as usual, and everyone in the menu is asked to
    /// pick their part for it.
    /// </remarks>
    public static class LinkLobby
    {
        /// <summary>How often the open lobby checks for changes.</summary>
        private const float REFRESH_INTERVAL = 0.5f;

        /// <summary>Longest text from the clipboard that we'll offer to join with.</summary>
        private const int MAX_TICKET_LENGTH = 4096;

        private static ListDialog _dialog;
        private static string     _shown;
        private static float      _nextRefresh;

        private static LinkController Controller => LinkController.Instance;

        public static bool IsOpen => _dialog != null && _dialog.IsOpen;

        public static void Show()
        {
            var dialogs = DialogManager.Instance;
            if (dialogs == null || dialogs.IsDialogShowing || Controller == null) return;

            _dialog = dialogs.ShowList(Title());
            _shown = null;
            Fill();
        }

        public static void CloseIfOpen()
        {
            if (!IsOpen) return;

            DialogManager.Instance.ClearDialog();
            _dialog = null;
        }

        /// <summary>Called every frame by <see cref="LinkController"/>; rebuilds the open lobby when it changes.</summary>
        public static void Refresh()
        {
            if (!IsOpen || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + REFRESH_INTERVAL;

            Fill();
        }

        private static void Fill()
        {
            var session = Controller.CurrentSession;
            var rows = session == null ? new List<Row>() : Rows(session);

            // Only rebuild when something changed, so the list doesn't flicker or lose the mouse
            string shown = Title() + "\n" + string.Join("\n", rows.Select(r => r.Text)) +
                (session == null ? "|" + ClipboardTicket() : "");
            if (shown == _shown) return;
            _shown = shown;

            _dialog.Title.text = Title();
            _dialog.ClearList();
            _dialog.ClearButtons();

            if (session == null)
            {
                FillNoSession();
            }
            else
            {
                FillSession(session, rows);
            }

            _dialog.AddDialogButton("Menu.Common.Close", MenuData.Colors.CancelButton, CloseIfOpen);
        }

        private static string Title()
        {
            var session = Controller.CurrentSession;
            string hint;
            string title;
            if (session == null)
            {
                title = "Online";
                hint = "Play songs together with friends over the internet. One of you hosts, and the others " +
                    "join with the host's invite code.";
            }
            else if (session.Role == LinkRole.Host)
            {
                title = "Online session (hosting)";
                hint = "Friends join with your invite code. Pick a song as usual, and everyone in the menu is " +
                    "asked to pick their part for it.";
            }
            else
            {
                title = "Online session";
                hint = "Pick a song as usual, and everyone in the menu is asked to pick their part for it.";
            }

            return $"{title}\n<size=60%><color=#c0c0c0>{hint}</color></size>";
        }

        private static void FillNoSession()
        {
            _dialog.AddListButton("Host a session", () =>
            {
                Controller.Host();
                _shown = null;
                Fill();
            }, closeOnClick: false);

            _dialog.AddListButton("Join with an invite code...", AskForTicket, closeOnClick: false);

            string clipboard = ClipboardTicket();
            if (clipboard != null)
            {
                _dialog.AddListButton($"Join with the copied invite code <color=#909090>({Shorten(clipboard)})</color>",
                    () => JoinWith(clipboard), closeOnClick: false);
            }
        }

        private static void FillSession(LinkSession session, List<Row> rows)
        {
            foreach (var row in rows)
            {
                var member = row.Member;
                if (member != null && session.Role == LinkRole.Host)
                {
                    _dialog.AddListButton(row.Text, () => ConfirmKick(member), closeOnClick: false);
                }
                else
                {
                    _dialog.AddListButton(row.Text, closeOnClick: false);
                }
            }

            if (session.Role == LinkRole.Host && session.Members.Count > 0)
            {
                _dialog.AddListButton("<color=#909090>Click a friend to remove them from the session.</color>",
                    closeOnClick: false);
            }

            var copy = _dialog.AddDialogButton("Menu.Link.CopyInvite", MenuData.Colors.ConfirmButton, () =>
            {
                Controller.CopyInviteCode();
                ToastManager.ToastSuccess("Copied the invite code");
            });
            copy.gameObject.SetActive(Controller.InviteCode != null);

            _dialog.AddDialogButton(session.Role == LinkRole.Host ? "Menu.Link.EndSession" : "Menu.Link.Leave",
                MenuData.Colors.CancelButton, () =>
                {
                    Controller.Leave();
                    _shown = null;
                    Fill();
                });
        }

        private readonly struct Row
        {
            public readonly string     Text;
            /// <summary>The friend this row is about, or <c>null</c> for us or a status line.</summary>
            public readonly LinkMember Member;

            public Row(string text, LinkMember member)
            {
                Text = text;
                Member = member;
            }
        }

        private static List<Row> Rows(LinkSession session)
        {
            var rows = new List<Row>();

            bool hosting = session.Role == LinkRole.Host;
            rows.Add(new Row(Describe(session.LocalName + " (you)", hosting, session.LocalStatus,
                session.LocalPlayers, null, false), null));

            if (Controller.IsConnecting)
            {
                string problem = Controller.ConnectProblem;
                string text = problem == null
                    ? "Connecting to the host... This can take a few seconds."
                    : $"Can't reach the host ({problem}). Trying again...";
                rows.Add(new Row($"<color=#ffd080>{Escape(text)}</color>", null));
                return rows;
            }

            // The host first, then everyone else by name
            foreach (var member in session.Members.OrderBy(m => !m.IsHost).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                rows.Add(new Row(Describe(member.Name, member.IsHost, member.Status, member.Players,
                    Controller.DescribeConnection(member), Controller.IsConnectionPoor(member)), member));
            }

            if (session.Members.Count == 0 && hosting)
            {
                rows.Add(new Row("<color=#909090>Nobody has joined yet. Send your friends the invite code.</color>", null));
            }

            return rows;
        }

        /// <summary>One lobby line: name, status, parts and connection.</summary>
        private static string Describe(string name, bool host, string status, IReadOnlyList<PlayerInfo> players,
            string connection, bool poorConnection)
        {
            var text = new StringBuilder();
            text.Append("<b>").Append(Escape(name)).Append("</b>");
            if (host) text.Append(" <color=#909090>(host)</color>");

            string statusColor = status == LinkController.STATUS_READY ? "#80ff80" : "#c0c0c0";
            text.Append($"  <color={statusColor}>{LinkController.DescribeStatus(status)}</color>");

            text.Append("  <color=#c0c0c0>");
            if (players == null || players.Count == 0)
            {
                text.Append("No players");
            }
            else
            {
                text.Append(string.Join(", ", players.Select(DescribePart)));
            }
            text.Append("</color>");

            if (!string.IsNullOrEmpty(connection))
            {
                string color = poorConnection ? "#ffb060" : "#909090";
                text.Append($"  <color={color}>{connection}</color>");
            }

            return text.ToString();
        }

        /// <summary>For example "Ann: Guitar (Expert)", or "Bot: Drums (Hard)".</summary>
        private static string DescribePart(PlayerInfo player)
        {
            string instrument = Enum.TryParse(player.Instrument, out Instrument i) ? i.ToLocalizedName() : player.Instrument;
            string difficulty = Enum.TryParse(player.Difficulty, out Difficulty d) ? d.ToLocalizedName() : player.Difficulty;
            string name = player.Bot ? $"{player.Name} (bot)" : player.Name;
            return $"{Escape(name)}: {instrument} ({difficulty})";
        }

        private static void AskForTicket()
        {
            CloseIfOpen();
            DialogManager.Instance.ShowRenameDialog("Paste the invite code", ticket =>
            {
                // The rename dialog is still open here; join once it has closed
                ReopenAfterDialog(() => Controller.Join(ticket)).Forget();
            });
        }

        private static void JoinWith(string ticket)
        {
            Controller.Join(ticket);
            if (IsOpen)
            {
                _shown = null;
                Fill();
            }
        }

        private static void ConfirmKick(LinkMember member)
        {
            CloseIfOpen();

            var dialog = DialogManager.Instance.ShowMessage($"Remove {member.Name}?",
                $"{member.Name} leaves the session. They can join again with the invite code.");
            dialog.ClearButtons();
            dialog.AddDialogButton("Menu.Common.Cancel", MenuData.Colors.CancelButton,
                () => ReopenAfterDialog(null).Forget());
            dialog.AddDialogButton("Menu.Link.Remove", MenuData.Colors.ConfirmButton,
                () => ReopenAfterDialog(() => Controller.Kick(member)).Forget());
        }

        /// <summary>Close the current dialog, do something, then show the lobby again (unless that opened a dialog).</summary>
        private static async UniTaskVoid ReopenAfterDialog(Action action)
        {
            var dialogs = DialogManager.Instance;
            await UniTask.Yield();
            dialogs.ClearDialog();
            await UniTask.Yield();

            action?.Invoke();
            Show();
        }

        /// <summary>The clipboard, if it might be an invite code.</summary>
        private static string ClipboardTicket()
        {
            string text = GUIUtility.systemCopyBuffer?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length < 16 || text.Length > MAX_TICKET_LENGTH) return null;
            if (text.Any(char.IsWhiteSpace)) return null;

            // Don't offer to join our own session
            if (Controller.CurrentSession != null && text == Controller.CurrentSession.LocalTicket) return null;
            if (text == Controller.LastHostedInviteCode) return null;
            return text;
        }

        private static string Shorten(string ticket) =>
            Escape(ticket.Length <= 16 ? ticket : ticket.Substring(0, 8) + "..." + ticket.Substring(ticket.Length - 4));

        private static string Escape(string text) => text?.Replace("<", "<​") ?? "";
    }
}
