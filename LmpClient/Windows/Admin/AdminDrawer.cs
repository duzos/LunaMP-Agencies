using LmpClient.Localization;
using LmpClient.Systems.Admin;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpClient.Systems.Status;
using LmpCommon.Message.Types;
using System;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Admin
{
    public partial class AdminWindow
    {
        // Sub-tabs: 0 = Players (existing), 1 = Agencies (new).
        private static int _adminTab;
        private static readonly string[] AdminTabLabels = { "Players", "Agencies" };

        // Agencies tab state.
        private static Guid _selectedAgencyId;
        private static string _cheatFundsText = "1000000";
        private static string _cheatScienceText = "500";
        private static string _cheatRepText = "100";
        private static string _cheatTechText = "";
        private static string _cheatContractText = "";
        private static string _adminRenameText = "";
        private static Vector2 _adminAgencyScrollPos;

        protected override void DrawWindowContent(int windowId)
        {
            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationContainer.AdminWindowText.Password);
            AdminSystem.Singleton.AdminPassword = GUILayout.PasswordField(AdminSystem.Singleton.AdminPassword, '*', 30, GUILayout.Width(200)); // Max 32 characters
            GUILayout.EndHorizontal();
            GUILayout.Space(5);

            GUI.enabled = !string.IsNullOrEmpty(AdminSystem.Singleton.AdminPassword);

            _adminTab = GUILayout.Toolbar(_adminTab, AdminTabLabels);
            GUILayout.Space(4);

            if (_adminTab == 0) DrawPlayersTab();
            else DrawAgenciesTab();

            GUI.enabled = true;
            GUILayout.EndVertical();
        }

        private void DrawPlayersTab()
        {
            ScrollPos = GUILayout.BeginScrollView(ScrollPos);
            foreach (var player in StatusSystem.Singleton.PlayerStatusList.Keys)
            {
                if (player == SettingsSystem.CurrentSettings.PlayerName) continue;
                DrawPlayerLine(player);
            }
            GUILayout.EndScrollView();
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(DekesslerBigIcon))
            {
                AdminSystem.Singleton.MessageSender.SendDekesslerMsg();
            }
            if (GUILayout.Button(NukeBigIcon))
            {
                AdminSystem.Singleton.MessageSender.SendNukeMsg();
            }
            if (GUILayout.Button(RestartServerIcon))
            {
                AdminSystem.Singleton.MessageSender.SendServerRestartMsg();
            }
            GUILayout.EndHorizontal();
        }

        private static void DrawAgenciesTab()
        {
            GUILayout.Label("Agencies on this server:");
            _adminAgencyScrollPos = GUILayout.BeginScrollView(_adminAgencyScrollPos, GUILayout.Height(140));
            foreach (var a in AgencySystem.Singleton.KnownAgencies.Values
                         .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Toggle(_selectedAgencyId == a.Id, $"{a.Name}  [{a.MemberUniqueIds?.Length ?? 0} members, {(a.IsSolo ? "solo" : "public")}]"))
                {
                    _selectedAgencyId = a.Id;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            if (_selectedAgencyId == Guid.Empty || !AgencySystem.Singleton.KnownAgencies.TryGetValue(_selectedAgencyId, out var sel))
            {
                GUILayout.Label("Select an agency above to inspect or cheat.");
                return;
            }

            GUILayout.Label($"Selected: {sel.Name}");
            GUILayout.Label($"Funds={sel.Funds:N0}  Sci={sel.Science:N1}  Rep={sel.Reputation:N1}  Tech={sel.UnlockedTechCount}");

            // Membership / ownership actions
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Delete (force)") && IsPasswordSet())
                SendAdmin(AgencyAdminOp.Delete, sel.Id, "", 0);
            _adminRenameText = GUILayout.TextField(_adminRenameText ?? string.Empty, GUILayout.Width(160));
            if (GUILayout.Button("Rename") && IsPasswordSet() && !string.IsNullOrWhiteSpace(_adminRenameText))
            {
                SendAdmin(AgencyAdminOp.Rename, sel.Id, _adminRenameText.Trim(), 0);
                _adminRenameText = "";
            }
            GUILayout.EndHorizontal();

            // Move players or force-set an owner for the selected agency.
            GUILayout.Space(4);
            GUILayout.Label("Move / set owner for " + sel.Name + ":");
            foreach (var other in AgencySystem.Singleton.KnownAgencies.Values)
            {
                if (other.MemberUniqueIds == null) continue;
                for (int i = 0; i < other.MemberUniqueIds.Length; i++)
                {
                    var uid = other.MemberUniqueIds[i];
                    var name = i < (other.MemberDisplayNames?.Length ?? 0)
                        ? other.MemberDisplayNames[i] : uid;
                    var isCurrentOwner = uid == sel.OwnerUniqueId;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"  {name}  (from '{other.Name}')" + (isCurrentOwner ? "  [owner]" : ""));
                    GUI.enabled = IsPasswordSet() && other.Id != sel.Id;
                    if (GUILayout.Button("Move here", GUILayout.Width(100)))
                        SendAdmin(AgencyAdminOp.MoveMember, sel.Id, uid, 0);
                    GUI.enabled = IsPasswordSet() && !isCurrentOwner;
                    if (GUILayout.Button("Set owner", GUILayout.Width(100)))
                        SendAdmin(AgencyAdminOp.SetOwner, sel.Id, uid, 0);
                    GUI.enabled = !string.IsNullOrEmpty(AdminSystem.Singleton.AdminPassword);
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(6);
            GUILayout.Label("=== Cheats ===");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Funds", GUILayout.Width(60));
            _cheatFundsText = GUILayout.TextField(_cheatFundsText ?? string.Empty, GUILayout.Width(120));
            if (GUILayout.Button("Set funds") && IsPasswordSet() && double.TryParse(_cheatFundsText, out var f))
                SendAdmin(AgencyAdminOp.SetFunds, sel.Id, "", f);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Science", GUILayout.Width(60));
            _cheatScienceText = GUILayout.TextField(_cheatScienceText ?? string.Empty, GUILayout.Width(120));
            if (GUILayout.Button("Set science") && IsPasswordSet() && double.TryParse(_cheatScienceText, out var s))
                SendAdmin(AgencyAdminOp.SetScience, sel.Id, "", s);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Rep", GUILayout.Width(60));
            _cheatRepText = GUILayout.TextField(_cheatRepText ?? string.Empty, GUILayout.Width(120));
            if (GUILayout.Button("Set rep") && IsPasswordSet() && double.TryParse(_cheatRepText, out var r))
                SendAdmin(AgencyAdminOp.SetReputation, sel.Id, "", r);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Tech id", GUILayout.Width(60));
            _cheatTechText = GUILayout.TextField(_cheatTechText ?? string.Empty, GUILayout.Width(200));
            if (GUILayout.Button("Unlock tech") && IsPasswordSet() && !string.IsNullOrWhiteSpace(_cheatTechText))
                SendAdmin(AgencyAdminOp.UnlockTechNode, sel.Id, _cheatTechText.Trim(), 0);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Contract", GUILayout.Width(60));
            _cheatContractText = GUILayout.TextField(_cheatContractText ?? string.Empty, GUILayout.Width(200));
            if (GUILayout.Button("Complete") && IsPasswordSet() && !string.IsNullOrWhiteSpace(_cheatContractText))
                SendAdmin(AgencyAdminOp.CompleteContract, sel.Id, _cheatContractText.Trim(), 0);
            if (GUILayout.Button("Cancel") && IsPasswordSet() && !string.IsNullOrWhiteSpace(_cheatContractText))
                SendAdmin(AgencyAdminOp.CancelContract, sel.Id, _cheatContractText.Trim(), 0);
            GUILayout.EndHorizontal();
        }

        private static bool IsPasswordSet() => !string.IsNullOrEmpty(AdminSystem.Singleton.AdminPassword);

        private static void SendAdmin(AgencyAdminOp op, Guid id, string s, double n)
        {
            AgencySystem.Singleton.MessageSender.SendAdminOp(AdminSystem.Singleton.AdminPassword, op, id, s, n);
        }

        private static void DrawPlayerLine(string playerName)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(playerName);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(BanIcon))
            {
                _selectedPlayer = playerName;
                _banMode = true;
            }
            if (GUILayout.Button(KickIcon))
            {
                _selectedPlayer = playerName;
                _banMode = false;
            }
            GUILayout.EndHorizontal();
        }

        #region Confirmation Dialog

        public void DrawConfirmationDialog(int windowId)
        {
            //Always draw close button first
            DrawCloseButton(() => { _selectedPlayer = null; _reason = string.Empty; }, _confirmationWindowRect);

            GUILayout.BeginVertical();
            GUI.DragWindow(MoveRect);

            GUILayout.Label(_banMode ? LocalizationContainer.AdminWindowText.BanText : LocalizationContainer.AdminWindowText.KickText, LabelOptions);
            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationContainer.AdminWindowText.Reason, LabelOptions);
            _reason = GUILayout.TextField(_reason, 255, GUILayout.Width(255));
            GUILayout.EndHorizontal();
            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (_banMode)
            {
                if (GUILayout.Button(BanBigIcon, GUILayout.Width(255)))
                {
                    AdminSystem.Singleton.MessageSender.SendBanPlayerMsg(_selectedPlayer, _reason);
                    _selectedPlayer = null;
                    _reason = string.Empty;
                }
            }
            else
            {
                if (GUILayout.Button(KickBigIcon, GUILayout.Width(255)))
                {
                    AdminSystem.Singleton.MessageSender.SendKickPlayerMsg(_selectedPlayer, _reason);
                    _selectedPlayer = null;
                    _reason = string.Empty;
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        #endregion
    }
}
