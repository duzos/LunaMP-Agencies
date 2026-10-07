using System;
using System.Globalization;
using System.Linq;
using LmpClient.Systems.Agency;
using LmpCommon.Agency;
using UnityEngine;
using KSP.UI.Screens;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static int tradeTab;
        private static Guid tradeBuyer, tradeVessel, tradeConfirm, tradeLoadConfirm, tradeAgency;
        private static long tradeConfirmRevision;
        private static string tradeSearch = "", tradeError, tradeDraftVesselName;
        private static string giveFunds = "0", giveScience = "0", receiveFunds = "0", receiveScience = "0";
        private static int tradeDesignSource, tradeDesignMode;
        private static ToolingManifest tradeInfoManifest, tradeDraftManifest;
        private static string tradeInfoKey, tradeInfoError;
        private static float tradeInfoRefresh;
        private static double tradeDraftPrepay, tradeDraftRate;
        private static bool tradeDraftTooled;
        private static string tradeSave, tradeBlueprintPath, tradeBlueprintLabel;
        private static CraftBrowserDialog tradeBrowser;
        private static Vector2 tradeScroll, tradeBuyerScroll, tradeVesselScroll;
        private static TradeCommand tradeDraft;
        private static GUIStyle tradeText, tradeHeading, tradeButton;

        private static void DrawTradeTab()
        {
            if (tradeText == null)
            {
                tradeText = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false };
                tradeHeading = new GUIStyle(tradeText) { fontStyle = FontStyle.Bold };
                tradeButton = new GUIStyle(GUI.skin.button) { wordWrap = true, richText = false };
            }
            if (!TradeClient.Enabled) { GUILayout.Label("Trading is disabled on this server.", tradeText); return; }
            if (tradeAgency != AgencySystem.Singleton.MyAgencyId || tradeSave != HighLogic.SaveFolder)
            {
                tradeAgency = AgencySystem.Singleton.MyAgencyId;
                tradeSave = HighLogic.SaveFolder;
                tradeBuyer = tradeVessel = tradeConfirm = tradeLoadConfirm = Guid.Empty;
                tradeDraft = null; tradeError = null;
                tradeDesignSource = tradeDesignMode = 0; tradeBlueprintPath = tradeBlueprintLabel = null;
                tradeInfoManifest = tradeDraftManifest = null; tradeInfoKey = tradeInfoError = null;
                if (tradeBrowser != null) tradeBrowser.Dismiss();
                tradeBrowser = null;
            }
            if (!TradeClient.Ready) { GUILayout.Label("Syncing your agency's offers and purchased designs...", tradeText); return; }
            tradeTab = GUILayout.Toolbar(tradeTab, new[] { "Offers", "New offer", "Received designs" });
            if (!string.IsNullOrEmpty(TradeClient.LatestStatus)) GUILayout.Label(TradeClient.LatestStatus, tradeText);
            if (!string.IsNullOrEmpty(tradeError)) GUILayout.Label(tradeError, tradeText);
            tradeScroll = GUILayout.BeginScrollView(tradeScroll);
            if (tradeTab == 0) DrawTradeOffers();
            else if (tradeTab == 1) DrawNewTrade();
            else DrawReceivedTrades();
            GUILayout.EndScrollView();
        }

        private static string TradeAgencyName(Guid id) => AgencySystem.Singleton.KnownAgencies.TryGetValue(id, out var agency) ? agency.Name : id.ToString("N").Substring(0, 8);
        private static string Funds(double value) => value.ToString("N1", CultureInfo.CurrentCulture) + " funds";
        private static void DrawTradeTerms(double funds, double science) => GUILayout.Label("Funds " + funds.ToString("R", CultureInfo.CurrentCulture) + "  |  Science " + science.ToString("R", CultureInfo.CurrentCulture), tradeText);

        private static void DrawTradeOffers()
        {
            var offers = TradeClient.GetOffersSnapshot().OrderByDescending(o => o.Status == TradeOfferStatus.Open).ThenByDescending(o => o.Revision).ToArray();
            if (offers.Length == 0) GUILayout.Label("No offers yet. Create an offer for another agency to exchange craft, designs, funds or science.", tradeText);
            foreach (var offer in offers)
            {
                var incoming = offer.BuyerAgencyId == tradeAgency;
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label((incoming ? "From " : "To ") + TradeAgencyName(incoming ? offer.SellerAgencyId : offer.BuyerAgencyId) + " · " + offer.Status, tradeHeading);
                if (offer.VesselId != Guid.Empty) GUILayout.Label("Craft: " + offer.VesselName, tradeText);
                if (!string.IsNullOrEmpty(offer.DesignFingerprint))
                {
                    if (offer.DesignMode == TradeDesignMode.SingleLaunch)
                    {
                        GUILayout.Label("One free launch of: " + offer.BlueprintName + " (" + offer.Editor + ")", tradeText);
                        GUILayout.Label(incoming ? "The seller prepays " + Funds(offer.PrepaidLaunchFunds) + " when you accept. At launch you pay only inventory and any part cost above that prepayment (for example extra fuel). No tooling is included." :
                            "Your agency prepays " + Funds(offer.PrepaidLaunchFunds) + " when the buyer accepts. No tooling is included.", tradeText);
                    }
                    else GUILayout.Label("Tooled design: " + offer.BlueprintName + " (" + offer.Editor + ")", tradeText);
                }
                GUILayout.Label("Your agency gives", tradeHeading);
                DrawTradeTerms(incoming ? offer.BuyerFunds : offer.SellerFunds, incoming ? offer.BuyerScience : offer.SellerScience);
                GUILayout.Label("Your agency receives" + (incoming && (offer.VesselId != Guid.Empty || !string.IsNullOrEmpty(offer.DesignFingerprint)) ? " the items above, plus" : ""), tradeHeading);
                DrawTradeTerms(incoming ? offer.SellerFunds : offer.BuyerFunds, incoming ? offer.SellerScience : offer.BuyerScience);
                if (offer.Status == TradeOfferStatus.Open)
                {
                    GUILayout.Label("Expires " + new DateTime(offer.ExpiresUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g"), tradeText);
                    if (AgencySystem.Singleton.AmIOwnerOfMine())
                    {
                        if (incoming)
                        {
                            var confirming = tradeConfirm == offer.OfferId && tradeConfirmRevision == offer.Revision;
                            if (confirming) GUILayout.Label("Confirm this exchange? The server will transfer both sides together." + (offer.DesignMode == TradeDesignMode.SingleLaunch && !string.IsNullOrEmpty(offer.DesignFingerprint) ? " The seller's prepayment of " + Funds(offer.PrepaidLaunchFunds) + " is taken at the same moment." : ""), tradeText);
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button(confirming ? "Confirm exchange" : "Review and accept", tradeButton))
                            {
                                if (confirming) { TradeClient.RespondOffer(offer.OfferId, offer.Revision, true); tradeConfirm = Guid.Empty; }
                                else { tradeConfirm = offer.OfferId; tradeConfirmRevision = offer.Revision; }
                            }
                            if (GUILayout.Button(confirming ? "Back" : "Decline", tradeButton))
                            {
                                if (confirming) tradeConfirm = Guid.Empty;
                                else TradeClient.RespondOffer(offer.OfferId, offer.Revision, false);
                            }
                            GUILayout.EndHorizontal();
                        }
                        else if (GUILayout.Button("Withdraw offer", tradeButton)) TradeClient.CancelOffer(offer.OfferId, offer.Revision);
                    }
                    else GUILayout.Label("Your agency owner manages offers.", tradeText);
                }
                GUILayout.EndVertical();
            }
        }

        private static void DrawNewTrade()
        {
            if (!AgencySystem.Singleton.AmIOwnerOfMine()) { GUILayout.Label("Only your agency owner can create an offer.", tradeText); return; }
            if (tradeDraft != null)
            {
                GUILayout.Label("Review offer to " + TradeAgencyName(tradeDraft.BuyerAgencyId), tradeHeading);
                if (tradeDraft.VesselId != Guid.Empty) GUILayout.Label("Transfers ownership of " + tradeDraftVesselName + ".", tradeText);
                if (tradeDraft.BlueprintData.Length > 0)
                {
                    if (tradeDraft.DesignMode == TradeDesignMode.SingleLaunch)
                    {
                        GUILayout.Label("Includes a copy of " + tradeDraft.BlueprintName + " (" + tradeDraft.Editor + ") and one free launch. No tooling is included.", tradeText);
                        GUILayout.Label("Your agency prepays about " + Funds(tradeDraftPrepay) + " when the buyer accepts, at your " + (tradeDraftTooled ? "tooled" : "untooled") + " rate of " + tradeDraftRate.ToString("0.##", CultureInfo.InvariantCulture) + "x. The buyer pays only inventory and any extra part cost.", tradeText);
                    }
                    else GUILayout.Label("Includes tooling and a copy of " + tradeDraft.BlueprintName + " (" + tradeDraft.Editor + "): unlimited launches at the tooled price. You keep your own tooling.", tradeText);
                }
                GUILayout.Label("Your agency gives", tradeHeading); DrawTradeTerms(tradeDraft.SellerFunds, tradeDraft.SellerScience);
                GUILayout.Label("Your agency receives", tradeHeading); DrawTradeTerms(tradeDraft.BuyerFunds, tradeDraft.BuyerScience);
                GUILayout.Label("Nothing transfers until the other agency's owner accepts. Balances and ownership are checked again then.", tradeText);
                if (GUILayout.Button("Send offer", tradeButton)) { TradeClient.CreateOffer(tradeDraft, tradeDraftManifest); tradeDraft = null; tradeDraftManifest = null; tradeTab = 0; }
                if (GUILayout.Button("Back to edit", tradeButton)) tradeDraft = null;
                return;
            }
            GUILayout.Label("1. Choose the receiving agency", tradeHeading);
            tradeSearch = GUILayout.TextField(tradeSearch, 80);
            tradeBuyerScroll = GUILayout.BeginScrollView(tradeBuyerScroll, GUILayout.Height(90));
            foreach (var agency in AgencySystem.Singleton.KnownAgencies.Values.Where(a => a.Id != tradeAgency && (a.Name ?? "").IndexOf(tradeSearch, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(a => a.Name))
                if (GUILayout.Toggle(tradeBuyer == agency.Id, agency.Name, tradeButton)) tradeBuyer = agency.Id;
            GUILayout.EndScrollView();
            GUILayout.Label("2. Choose items to give (optional)", tradeHeading);
            if (GUILayout.Toggle(tradeVessel == Guid.Empty, "No craft transfer", tradeButton)) tradeVessel = Guid.Empty;
            tradeVesselScroll = GUILayout.BeginScrollView(tradeVesselScroll, GUILayout.Height(80));
            if (FlightGlobals.Vessels != null)
                foreach (var vessel in FlightGlobals.Vessels.Where(v => v != null && AgencySystem.Singleton.GetVesselAgency(v.id) == tradeAgency).OrderBy(v => v.vesselName))
                    if (GUILayout.Toggle(tradeVessel == vessel.id, vessel.vesselName, tradeButton)) tradeVessel = vessel.id;
            GUILayout.EndScrollView();
            GUILayout.Label("Design", tradeHeading);
            if (!ToolingClient.Enabled)
            {
                tradeDesignSource = 0;
                GUILayout.Label("Selling a design needs agency tooling, which is off on this server.", tradeText);
            }
            else
            {
                if (GUILayout.Toggle(tradeDesignSource == 0, "No design", tradeButton)) tradeDesignSource = 0;
                if (HighLogic.LoadedSceneIsEditor)
                {
                    if (GUILayout.Toggle(tradeDesignSource == 1, "Current editor craft", tradeButton)) tradeDesignSource = 1;
                }
                else if (tradeDesignSource == 1) tradeDesignSource = 0;
                if (GUILayout.Toggle(tradeDesignSource == 2, "Saved craft", tradeButton)) tradeDesignSource = 2;
                if (tradeDesignSource == 2)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Choose VAB craft", tradeButton)) BrowseTradeBlueprint(EditorFacility.VAB);
                    if (GUILayout.Button("Choose SPH craft", tradeButton)) BrowseTradeBlueprint(EditorFacility.SPH);
                    GUILayout.EndHorizontal();
                    GUILayout.Label(tradeBlueprintLabel ?? "Choose a craft from this save's library.", tradeText);
                }
                RefreshTradeDesignInfo();
                if (tradeDesignSource != 0) DrawTradeDesignMode();
            }
            GUILayout.Label("3. Set both sides of the exchange", tradeHeading);
            GUILayout.Label("Your agency gives", tradeText); AmountInputs(ref giveFunds, ref giveScience);
            GUILayout.Label("The other agency gives", tradeText); AmountInputs(ref receiveFunds, ref receiveScience);
            var enabled = GUI.enabled; GUI.enabled = enabled && tradeBuyer != Guid.Empty;
            if (GUILayout.Button("Review offer", tradeButton))
            {
                try
                {
                    ToolingManifest manifest = null;
                    var draft = tradeDesignSource == 1 ? TradeClient.CaptureCurrentDesign(out manifest) : tradeDesignSource == 2 ? TradeClient.CaptureBlueprint(tradeBlueprintPath, out manifest) : new TradeCommand();
                    if (tradeDesignSource != 0)
                    {
                        draft.DesignMode = (TradeDesignMode)tradeDesignMode;
                        if (draft.DesignMode == TradeDesignMode.ToolingAndDesign && !ToolingClient.HasTooling(draft.DesignFingerprint)) throw new InvalidOperationException("Tool this design in the editor first, or sell one free launch instead.");
                        tradeDraftPrepay = TradeClient.EstimatePrepay(manifest, out tradeDraftRate, out tradeDraftTooled);
                    }
                    tradeDraftManifest = manifest;
                    draft.BuyerAgencyId = tradeBuyer; draft.VesselId = tradeVessel;
                    draft.SellerFunds = ParseTradeAmount(giveFunds); draft.SellerScience = ParseTradeAmount(giveScience);
                    draft.BuyerFunds = ParseTradeAmount(receiveFunds); draft.BuyerScience = ParseTradeAmount(receiveScience);
                    tradeDraftVesselName = FlightGlobals.Vessels?.FirstOrDefault(v => v != null && v.id == tradeVessel)?.vesselName ?? tradeVessel.ToString();
                    tradeDraft = draft; tradeError = null;
                }
                catch (Exception e) { tradeError = e.Message; }
            }
            GUI.enabled = enabled;
        }

        private static void RefreshTradeDesignInfo()
        {
            if (tradeDesignSource == 0) { tradeInfoManifest = null; tradeInfoError = null; tradeInfoKey = null; return; }
            var key = tradeDesignSource == 1 ? "editor" : tradeBlueprintPath ?? "";
            // The editor craft is re-read about once a second; a saved craft only when the choice changes.
            if (key == tradeInfoKey && (tradeDesignSource == 2 || Time.realtimeSinceStartup < tradeInfoRefresh)) return;
            tradeInfoKey = key; tradeInfoRefresh = Time.realtimeSinceStartup + 1f;
            try
            {
                if (tradeDesignSource == 2 && string.IsNullOrEmpty(tradeBlueprintPath)) { tradeInfoManifest = null; tradeInfoError = "Choose a craft first."; return; }
                ToolingManifest manifest;
                if (tradeDesignSource == 1) TradeClient.CaptureCurrentDesign(out manifest); else TradeClient.CaptureBlueprint(tradeBlueprintPath, out manifest);
                tradeInfoManifest = manifest; tradeInfoError = null;
            }
            catch (Exception e) { tradeInfoManifest = null; tradeInfoError = e.Message; }
        }

        private static void DrawTradeDesignMode()
        {
            GUILayout.Label("What the buyer gets", tradeHeading);
            if (tradeInfoManifest == null) { GUILayout.Label(tradeInfoError ?? "Choose a craft first.", tradeText); return; }
            var tooled = ToolingClient.HasTooling(ToolingPolicy.Fingerprint(tradeInfoManifest));
            var prepay = TradeClient.EstimatePrepay(tradeInfoManifest, out var rate, out _);
            if (!tooled) tradeDesignMode = (int)TradeDesignMode.SingleLaunch;
            var enabled = GUI.enabled;
            GUI.enabled = enabled && tooled;
            if (GUILayout.Toggle(tradeDesignMode == (int)TradeDesignMode.ToolingAndDesign, "Design + tooling: unlimited launches at the tooled price (you keep your tooling)", tradeButton)) tradeDesignMode = (int)TradeDesignMode.ToolingAndDesign;
            GUI.enabled = enabled;
            if (!tooled) GUILayout.Label("Tool this design in the editor first to sell tooling.", tradeText);
            if (GUILayout.Toggle(tradeDesignMode == (int)TradeDesignMode.SingleLaunch, "Design + one free launch (no tooling): you prepay " + Funds(prepay) + " now at your " + (tooled ? "tooled" : "untooled") + " rate (" + rate.ToString("0.##", CultureInfo.InvariantCulture) + "x)", tradeButton)) tradeDesignMode = (int)TradeDesignMode.SingleLaunch;
        }

        private static void BrowseTradeBlueprint(EditorFacility facility)
        {
            if (tradeBrowser != null) return;
            var selectedAgency = tradeAgency;
            var selectedSave = tradeSave;
            try
            {
                tradeBrowser = CraftBrowserDialog.Spawn(facility, selectedSave,
                    (CraftBrowserDialog.SelectFileCallback)((path, loadType) =>
                    {
                        tradeBrowser = null;
                        if (!TradeClient.Ready || selectedAgency != AgencySystem.Singleton.MyAgencyId || selectedSave != HighLogic.SaveFolder) return;
                        try
                        {
                            var design = TradeClient.CaptureBlueprint(path, out _);
                            tradeBlueprintPath = path;
                            tradeBlueprintLabel = design.BlueprintName + " (" + design.Editor + ")";
                            tradeDesignSource = 2;
                            tradeError = null;
                        }
                        catch (Exception e) { tradeError = e.Message; }
                    }), () => { tradeBrowser = null; }, false);
            }
            catch (Exception e) { tradeBrowser = null; tradeError = "Could not open craft library: " + e.Message; }
        }

        private static void AmountInputs(ref string funds, ref string science)
        {
            GUILayout.BeginHorizontal(); GUILayout.Label("Funds", GUILayout.Width(48)); funds = GUILayout.TextField(funds, 20);
            GUILayout.Label("Science", GUILayout.Width(58)); science = GUILayout.TextField(science, 20); GUILayout.EndHorizontal();
        }
        private static double ParseTradeAmount(string text)
        {
            if (!double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !ToolingPolicy.FiniteNonNegative(value))
                throw new ArgumentException("Enter nonnegative funds and science using a decimal point, for example 100 or 12.5.");
            return value;
        }
        private static void DrawReceivedTrades()
        {
            var designs = TradeClient.GetReceivedDesignsSnapshot();
            if (designs.Count == 0) GUILayout.Label("Purchased designs will appear here. Tooling plus design gives unlimited launches at the tooled price. One free launch is spent by your next launch of the exact part list, which then behaves like any untooled design. Adding or removing parts changes the design.", tradeText);
            foreach (var design in designs)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(design.Name, tradeHeading);
                GUILayout.Label("From " + TradeAgencyName(design.SellerAgencyId) + " · " + design.Editor, tradeText);
                if (design.Kind == TradeEntitlementKind.SingleLaunch)
                    GUILayout.Label("One free launch: " + (design.Redeemed ? "Used" : design.Reserved ? "In progress" : "Available") + " (seller prepaid " + Funds(design.PrepaidFunds) + ")", tradeText);
                else if (design.VesselId == Guid.Empty && !string.IsNullOrEmpty(design.LocalPath)) GUILayout.Label("Tooling + design (unlimited)", tradeText);
                GUILayout.Label(design.DeliveryStatus, tradeText);
                if (!string.IsNullOrEmpty(design.LocalPath))
                {
                    var confirming = tradeLoadConfirm == design.Id;
                    if (confirming) GUILayout.Label("Loading replaces the current editor craft. Save any changes you want to keep first.", tradeText);
                    if (GUILayout.Button(confirming ? "Replace and load design" : "Load in editor", tradeButton))
                    {
                        if (confirming) { TradeClient.LoadDesign(design.Id, true); tradeLoadConfirm = Guid.Empty; }
                        else tradeLoadConfirm = design.Id;
                    }
                    if (confirming && GUILayout.Button("Cancel", tradeButton)) tradeLoadConfirm = Guid.Empty;
                }
                GUILayout.EndVertical();
            }
        }
    }
}
