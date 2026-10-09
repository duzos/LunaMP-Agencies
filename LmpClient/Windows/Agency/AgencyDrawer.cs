using LmpClient.Systems.Agency;
using LmpCommon.Agency;
using System;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static int _tab;
        private static readonly string[] TabLabels = { "Mine", "Browse", "Create", "Leaderboard", "Vessels", "CommNet", "Trade", "Visibility", "Designs" };
        private static string _createName = "";
        private static string _renameName = "";
        private static string _lastStatusMessage = "";
        private static Vector2 _scrollPos;
        private static Vector2 _mineContentScroll;

        // Leaderboard tab state.
        private static int _leaderboardSort; // 0=firsts 1=funds 2=science 3=vessels
        private static readonly string[] _leaderboardSortLabels =
            { "Firsts", "Funds", "Science", "Vessels" };
        private static Vector2 _leaderboardScrollPos;

        protected override void DrawWindowContent(int windowId)
        {
            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            DrainStatusQueue();
            DrawStatusBanner();

            _tab = GUILayout.SelectionGrid(_tab, TabLabels, Screen.width < 620 ? 3 : 4);
            GUILayout.Space(6);

            switch (_tab)
            {
                case 0:
                    _mineContentScroll = GUILayout.BeginScrollView(_mineContentScroll);
                    DrawMineTab();
                    GUILayout.EndScrollView();
                    break;
                case 1: DrawBrowseTab(); break;
                case 2: DrawCreateTab(); break;
                case 3: DrawLeaderboardTab(); break;
                case 4: DrawVesselsTab(); break;
                case 5: DrawCommNetTab(); break;
                case 6: DrawTradeTab(); break;
                case 7: DrawVisibilityTab(); break;
                case 8: DrawDesignsTab(); break;
            }

            GUILayout.EndVertical();
        }

        private static void DrainStatusQueue()
        {
            while (AgencySystem.Singleton.PendingServerMessages.TryDequeue(out var m))
            {
                _lastStatusMessage = m;
            }
        }

        private static void DrawStatusBanner()
        {
            if (string.IsNullOrEmpty(_lastStatusMessage)) return;
            GUILayout.BeginHorizontal();
            GUILayout.Label(_lastStatusMessage);
            if (GUILayout.Button("x", GUILayout.Width(24))) _lastStatusMessage = "";
            GUILayout.EndHorizontal();
        }

        private static void DrawMineTab()
        {
            var mine = AgencySystem.Singleton.GetMyAgency();
            if (mine == null)
            {
                GUILayout.Label("No agency assigned yet.");
                return;
            }

            DrawIdentityLabel(mine.Id, $"Agency: {mine.Name}");
            DrawIdentityEditor(mine);
            GUILayout.Label($"Owner:  {mine.OwnerDisplayName}");
            GUILayout.Label($"Members: {mine.MemberDisplayNames?.Length ?? 0}");
            GUILayout.Label($"Funds:     {mine.Funds:N0}");
            GUILayout.Label($"Science:   {mine.Science:N1}");
            GUILayout.Label($"Reputation:{mine.Reputation:N1}");
            GUILayout.Label($"Tech unlocked: {mine.UnlockedTechCount}");
            GUILayout.Label($"Solo agency:   {(mine.IsSolo ? "yes (auto)" : "no")}");
            DrawLaunchSitesSummary(mine.Id);

            GUILayout.Space(8);
            GUILayout.Label("Members:");
            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(140));
            if (mine.MemberDisplayNames != null)
            {
                for (int i = 0; i < mine.MemberDisplayNames.Length; i++)
                {
                    var name = mine.MemberDisplayNames[i];
                    var uid = mine.MemberUniqueIds != null && i < mine.MemberUniqueIds.Length
                        ? mine.MemberUniqueIds[i] : "";

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(name);
                    if (AgencySystem.Singleton.AmIOwnerOfMine() && uid != mine.OwnerUniqueId)
                    {
                        if (GUILayout.Button("Kick", GUILayout.Width(60)))
                            AgencySystem.Singleton.MessageSender.SendKick(mine.Id, uid);
                        if (GUILayout.Button("Transfer", GUILayout.Width(80)))
                            AgencySystem.Singleton.MessageSender.SendTransferOwner(mine.Id, uid);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();

            if (AgencySystem.Singleton.AmIOwnerOfMine() && !mine.IsSolo)
            {
                GUILayout.Space(8);
                GUILayout.Label("Pending join requests:");
                JoinRequestInfo[] requests;
                lock (AgencySystem.Singleton.RequestsLock)
                    requests = AgencySystem.Singleton.PendingIncomingRequests
                        .Where(r => r.AgencyId == mine.Id).ToArray();

                if (requests.Length == 0)
                {
                    GUILayout.Label("(none)");
                }
                else
                {
                    foreach (var r in requests)
                    {
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(r.PlayerDisplayName);
                        if (GUILayout.Button("Approve", GUILayout.Width(80)))
                            AgencySystem.Singleton.MessageSender.SendApproveJoin(mine.Id, r.PlayerUniqueId);
                        if (GUILayout.Button("Reject", GUILayout.Width(80)))
                            AgencySystem.Singleton.MessageSender.SendRejectJoin(mine.Id, r.PlayerUniqueId);
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label("Rename agency:");
                GUILayout.BeginHorizontal();
                _renameName = GUILayout.TextField(_renameName ?? string.Empty);
                if (GUILayout.Button("Rename", GUILayout.Width(80)) && !string.IsNullOrWhiteSpace(_renameName))
                {
                    AgencySystem.Singleton.MessageSender.SendRename(mine.Id, _renameName.Trim());
                    _renameName = "";
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(8);
            if (!mine.IsSolo)
            {
                if (GUILayout.Button("Leave agency"))
                    AgencySystem.Singleton.MessageSender.SendLeave();
            }

            DrawTransferSection(mine);
        }

        // Transfer UI state — kept at file scope so the drawer doesn't reset
        // the selection every frame.
        private static int _transferTargetIdx;
        private static string _transferAmountText = "1000";
        private static LmpCommon.Message.Types.AgencyAdminOp _unused; // ensure using directive sticks

        private static void DrawTransferSection(LmpCommon.Agency.AgencyInfo mine)
        {
            GUILayout.Space(10);
            GUILayout.Label("=== Send resources to another agency ===");

            var targets = AgencySystem.Singleton.KnownAgencies.Values
                .Where(a => a.Id != mine.Id && !a.IsSolo)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (targets.Length == 0)
            {
                GUILayout.Label("(no other agencies available)");
                return;
            }

            var labels = targets.Select(a => a.Name).ToArray();
            if (_transferTargetIdx >= labels.Length) _transferTargetIdx = 0;
            _transferTargetIdx = GUILayout.SelectionGrid(_transferTargetIdx, labels, System.Math.Min(labels.Length, 3));

            GUILayout.BeginHorizontal();
            GUILayout.Label("Amount:", GUILayout.Width(60));
            _transferAmountText = GUILayout.TextField(_transferAmountText ?? string.Empty, GUILayout.Width(120));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Send Funds"))
            {
                if (double.TryParse(_transferAmountText, out var amt) && amt > 0)
                    AgencySystem.Singleton.MessageSender.SendTransferResources(
                        mine.Id, targets[_transferTargetIdx].Id,
                        LmpCommon.Agency.ResourceKind.Funds, amt);
            }
            if (GUILayout.Button("Send Science"))
            {
                if (double.TryParse(_transferAmountText, out var amt) && amt > 0)
                    AgencySystem.Singleton.MessageSender.SendTransferResources(
                        mine.Id, targets[_transferTargetIdx].Id,
                        LmpCommon.Agency.ResourceKind.Science, amt);
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawBrowseTab()
        {
            GUILayout.Label("All agencies on this server (solo agencies hidden):");

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            var list = AgencySystem.Singleton.KnownAgencies.Values
                .Where(a => !a.IsSolo)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (list.Length == 0)
            {
                GUILayout.Label("(none)");
            }
            else
            {
                foreach (var a in list)
                {
                    GUILayout.BeginHorizontal();
                    DrawIdentityLabel(a.Id, $"{a.Name} ({a.MemberUniqueIds?.Length ?? 0} members)");
                    GUILayout.FlexibleSpace();
                    if (a.Id == AgencySystem.Singleton.MyAgencyId)
                    {
                        GUILayout.Label("[you]");
                    }
                    else
                    {
                        if (GUILayout.Button("Request to join", GUILayout.Width(140)))
                            AgencySystem.Singleton.MessageSender.SendJoinRequest(a.Id);
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
        }

        private static void DrawCreateTab()
        {
            GUILayout.Label("Create a new agency. You will automatically become its owner.");
            GUILayout.Label("If you are currently in another agency, you will leave it.");
            GUILayout.Space(4);
            GUILayout.Label("Agency name:");
            _createName = GUILayout.TextField(_createName ?? string.Empty);

            GUILayout.Space(4);
            if (GUILayout.Button("Create"))
            {
                if (!string.IsNullOrWhiteSpace(_createName))
                {
                    AgencySystem.Singleton.MessageSender.SendCreate(_createName.Trim());
                    _createName = "";
                }
                else
                {
                    _lastStatusMessage = "Name cannot be empty.";
                }
            }
        }

    }
}
