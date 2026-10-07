using LmpClient.Base;
using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using LmpCommon.Enums;
using System;
using System.Globalization;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    /// <summary>Always-visible editor quote, with an optional compact summary.</summary>
    public sealed class ToolingEditorWindow : Window<ToolingEditorWindow>
    {
        private bool compact;
        private string confirmFingerprint;
        private double confirmCost;
        private GUIStyle text, heading, button;
        private Vector2 scroll;
        public override bool Display
        {
            get => HighLogic.LoadedSceneIsEditor && (ToolingClient.Enabled || TradeClient.Enabled) && MainSystem.NetworkState >= ClientState.Running && MainSystem.ToolbarShowGui && SettingsSystem.CurrentSettings.DisclaimerAccepted;
            set { compact = !value; }
        }
        public override void SetStyles()
        {
            WindowRect = new Rect(Mathf.Max(8, Screen.width - 360), 90, Mathf.Min(340, Screen.width - 16), 360);
            MoveRect = new Rect(0, 0, int.MaxValue, TitleHeight);
        }
        protected override void DrawGui()
        {
            if (text == null)
            {
                text = new GUIStyle(GUI.skin.label) { wordWrap = true };
                heading = new GUIStyle(text) { fontStyle = FontStyle.Bold, fontSize = 14 };
                button = new GUIStyle(GUI.skin.button) { wordWrap = true };
            }
            WindowRect.width = Mathf.Min(340, Screen.width - 16);
            WindowRect.height = Mathf.Min(compact ? (ToolingClient.EditorVoucher != null ? 150 : 112) : 420, Screen.height - 32);
            WindowRect = FixWindowPos(GUILayout.Window(6812 + MainSystem.WindowOffset, WindowRect, DrawContent, "Agency design",
                GUILayout.Width(WindowRect.width), GUILayout.Height(WindowRect.height)));
        }
        protected override void OnCloseButton() { compact = true; confirmFingerprint = null; }
        protected override void DrawWindowContent(int id)
        {
            GUI.DragWindow(MoveRect);
            if (TradeClient.Enabled) GUILayout.Label(TradeClient.EditorAllowanceStatus ?? "Checking purchased-design allowance...", text);
            if (!ToolingClient.Enabled) return;
            var quote = ToolingClient.EditorQuote;
            if (!LmpClient.Harmony.AgencyTooling.Ready)
                GUILayout.Label("Launch hooks unavailable: " + LmpClient.Harmony.AgencyTooling.DiagnosticReason, text);
            var usesFunds = SettingsSystem.ServerSettings.GameMode == GameMode.Career;
            if (!usesFunds) GUILayout.Label("No funds are charged in this game mode.", text);
            if (!ToolingClient.Ready || quote == null)
            {
                GUILayout.Label("Waiting for agency pricing…", text);
                GUILayout.Label(ToolingClient.LatestStatus ?? string.Empty, text);
                return;
            }
            if (!quote.Success) { GUILayout.Label(quote.Reason, text); return; }
            GUILayout.Label("Launch  " + Money(quote.LaunchCost), heading);
            var voucher = ToolingClient.EditorVoucher;
            if (voucher != null)
                GUILayout.Label("One free launch from " + SellerName(voucher.SellerAgencyId) + " applies: you pay " + Money(quote.LaunchCost) + " (inventory/extra only).", text);
            if (compact)
            {
                if (GUILayout.Button("Show tooling details", button)) compact = false;
                return;
            }
            scroll = GUILayout.BeginScrollView(scroll);
            if (TradeClient.Ready && TradeClient.HasUnusedVoucher(quote.Fingerprint))
            {
                TradeClient.UseVoucher = GUILayout.Toggle(TradeClient.UseVoucher, "Use free launch voucher", button);
                if (!TradeClient.UseVoucher) GUILayout.Label("The voucher stays unspent. This launch is charged the normal price.", text);
            }
            GUILayout.Label(quote.AlreadyTooled ? "This exact design is tooled for your agency." : "This design is not tooled. Launching it as is costs " + Multiplier(ToolingClient.Rates().UntooledLaunch) + " its part price (science parts and inventory stay 1x), or you can purchase tooling once.", text);
            CostRow("Science parts · full price", quote.ScienceCost);
            CostRow("Inventory · full price", quote.CargoCost);
            if (!quote.AlreadyTooled)
            {
                GUILayout.Space(6);
                GUILayout.Label("Tool this design  " + Money(quote.ToolingCost), heading);
                GUILayout.Label("Launch after tooling  " + Money(ToolingPolicy.LaunchCost(quote.ScienceCost, quote.CargoCost, quote.NonScienceCost, true, ToolingClient.Rates())), text);
                GUILayout.Label("Tooling discounts future launches of this exact part list. Layout changes are fine; changing parts needs new tooling.", text);
                foreach (var match in quote.Matches)
                    GUILayout.Label(match.Count + " × existing subassembly · combine fee " + Money(match.CombineCost), text);
                if (confirmFingerprint != null && (confirmFingerprint != quote.Fingerprint || confirmCost != quote.ToolingCost)) confirmFingerprint = null;
                if (confirmFingerprint == null)
                {
                    if (GUILayout.Button("Purchase tooling…", button)) { confirmFingerprint = quote.Fingerprint; confirmCost = quote.ToolingCost; }
                }
                else
                {
                    GUILayout.Label(usesFunds ? "Spend " + Money(confirmCost) + " from agency funds?" : "Register this tooling for your agency at no charge?", text);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Confirm", button)) { ToolingClient.PurchaseTooling(); confirmFingerprint = null; }
                    if (GUILayout.Button("Cancel", button)) confirmFingerprint = null;
                    GUILayout.EndHorizontal();
                }
            }
            else confirmFingerprint = null;
            GUILayout.Space(6);
            GUILayout.Label(ToolingClient.LatestStatus ?? "Prices are confirmed by the server before launch.", text);
            GUILayout.EndScrollView();
            if (GUILayout.Button("Collapse", button)) compact = true;
        }
        private static string SellerName(Guid id) => AgencySystem.Singleton.KnownAgencies.TryGetValue(id, out var agency) ? agency.Name : id.ToString("N").Substring(0, 8);
        private static string Money(double value) => value.ToString("N1") + " funds";
        private static string Multiplier(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        private void CostRow(string label, double amount)
        {
            GUILayout.Label(label + "  " + Money(amount), text, GUILayout.Width(Mathf.Max(180, WindowRect.width - 50)));
        }
        public override void CheckWindowLock()
        {
            var point = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (Display && WindowRect.Contains(point))
            {
                if (!IsWindowLocked) { InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, "LMP_Tooling"); IsWindowLocked = true; }
            }
            else RemoveWindowLock();
        }
        public override void RemoveWindowLock()
        {
            if (!IsWindowLocked) return;
            InputLockManager.RemoveControlLock("LMP_Tooling"); IsWindowLocked = false;
        }
    }
}
