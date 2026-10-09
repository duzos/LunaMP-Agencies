using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using LmpCommon.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Enums;
using LmpClient.Harmony;
using LmpClient.Extensions;

namespace LmpClient.Systems.Agency
{
    public sealed class ReceivedTradeDesign
    {
        public Guid Id { get; internal set; }
        public Guid SellerAgencyId { get; internal set; }
        public Guid VesselId { get; internal set; }
        public string Name { get; internal set; }
        public string Editor { get; internal set; }
        public string Fingerprint { get; internal set; }
        public string DeliveryStatus { get; internal set; }
        public string LocalPath { get; internal set; }
        public bool Delivered { get; internal set; }
        public TradeEntitlementKind Kind { get; internal set; }
        public bool Redeemed { get; internal set; }
        public bool Reserved { get; internal set; }
        public double PrepaidFunds { get; internal set; }
        /// <summary>Held stock units for this design's fingerprint (from ToolingClient.StockUnits).</summary>
        public int StockUnits { get; internal set; }
    }
    public static class TradeClient
    {
        private static readonly object gate = new object();
        private static TradeOffer[] offers = Array.Empty<TradeOffer>();
        private static TradeEntitlement[] entitlements = Array.Empty<TradeEntitlement>();
        private static readonly Dictionary<Guid, ReceivedTradeDesign> deliveries = new Dictionary<Guid, ReceivedTradeDesign>();
        private static readonly HashSet<Guid> acknowledged = new HashSet<Guid>();
        private static readonly Dictionary<Guid,string> deliveryDiagnostics = new Dictionary<Guid,string>();
        private static bool deliverPending;
        private static Guid launchEntitlement;
        private static readonly Dictionary<Guid,Guid> vesselEntitlements = new Dictionary<Guid,Guid>();
        private static long revision = -1;
        private static Guid agency;
        public static bool Enabled => MainSystem.NetworkState >= ClientState.Handshaking && SettingsSystem.ServerSettings.AgencyTrade;
        public static bool Ready => Enabled && ToolingClient.BalanceReady && agency == AgencySystem.Singleton.MyAgencyId;
        public static string LatestStatus { get; private set; }
        public static string EditorAllowanceStatus { get; private set; }
        private static bool useVoucher = true;
        /// <summary>The "Use free launch voucher" choice. On by default; a matching unspent voucher is applied automatically.</summary>
        public static bool UseVoucher
        {
            get => useVoucher;
            set { if (useVoucher == value) return; useVoucher = value; ToolingClient.RequestQuoteRefresh(); }
        }
        private static bool UsesFunds => SettingsSystem.ServerSettings.GameMode == GameMode.Career;
        /// <summary>The voucher a launch of this quoted design would use, or null. Only applies with tooling on and the toggle on.</summary>
        internal static TradeEntitlement SelectVoucher(ToolingQuote quote)
        {
            if (!Ready || !ToolingClient.Enabled || !useVoucher) return null;
            lock (gate) return TradePolicy.SelectVoucher(entitlements, quote, UsesFunds);
        }
        /// <summary>True when the buyer holds an unspent, unreserved free launch of this exact part list, whatever the toggle says.</summary>
        public static bool HasUnusedVoucher(string fingerprint)
        {
            lock (gate) return entitlements.Any(e => e.Kind == TradeEntitlementKind.SingleLaunch && !e.Redeemed && e.LaunchId == Guid.Empty && e.Fingerprint == fingerprint);
        }
        /// <summary>The StockDesign delivery (bought-stock craft file) for a fingerprint, or null.</summary>
        public static ReceivedTradeDesign StockDesignFor(string fingerprint)
        {
            throw new NotImplementedException("StockDesignFor is implemented by plan 40 slice S3.");
        }
        public static TradeEntitlement VoucherById(Guid id)
        {
            lock (gate) return entitlements.FirstOrDefault(e => e.EntitlementId == id);
        }
        /// <summary>What a single-launch sale of this design would cost the seller's agency now, at the seller's own tooled or untooled rate.</summary>
        public static double EstimatePrepay(ToolingManifest manifest, out double rate, out bool tooled)
        {
            var quote = ToolingClient.StandardQuote(manifest);
            tooled = quote.AlreadyTooled;
            rate = tooled ? ToolingClient.Rates().TooledLaunch : ToolingClient.Rates().UntooledLaunch;
            return !quote.Success || !UsesFunds ? 0 : TradePolicy.PrepaidLaunchCost(quote, rate);
        }
        internal static void Receive(EconomySnapshot snapshot)
        {
            if (snapshot == null) return;
            if (!Enabled || !snapshot.Ready || snapshot.AgencyId != AgencySystem.Singleton.MyAgencyId)
            {
                TraceSnapshot(snapshot, !Enabled ? "disabled" : !snapshot.Ready ? "not-ready" : "agency-mismatch");
                return;
            }
            lock (gate)
            {
                if (agency != snapshot.AgencyId) Clear();
                if (snapshot.Revision < revision) { TraceSnapshot(snapshot, "stale-revision"); return; }
                agency = snapshot.AgencyId; revision = snapshot.Revision;
                offers = snapshot.Offers ?? Array.Empty<TradeOffer>();
                entitlements = snapshot.Entitlements ?? Array.Empty<TradeEntitlement>();
                deliverPending = true;
                TraceSnapshot(snapshot, "accepted");
            }
        }
        private static void TraceSnapshot(EconomySnapshot snapshot, string outcome)
        {
            Diagnostics.PlaytestDiagnostics.Write("client.trade.snapshot", () =>
                $"agency={snapshot.AgencyId} revision={snapshot.Revision} outcome={outcome} offers={string.Join(",", (snapshot.Offers ?? Array.Empty<TradeOffer>()).Select(o => o.OfferId.ToString("N")))}");
        }
        public static IReadOnlyList<TradeOffer> GetOffersSnapshot()
        {
            lock (gate) return offers.Select(o => new TradeOffer { OfferId=o.OfferId, SellerAgencyId=o.SellerAgencyId, BuyerAgencyId=o.BuyerAgencyId,
                VesselId=o.VesselId, Revision=o.Revision, ExpiresUtcTicks=o.ExpiresUtcTicks, Status=o.Status, SellerFunds=o.SellerFunds,
                SellerScience=o.SellerScience, BuyerFunds=o.BuyerFunds, BuyerScience=o.BuyerScience, DesignFingerprint=o.DesignFingerprint,
                BlueprintName=o.BlueprintName, Editor=o.Editor, VesselName=o.VesselName, DesignMode=o.DesignMode, PrepaidLaunchFunds=o.PrepaidLaunchFunds,
                LaunchMultiplier=o.LaunchMultiplier }).ToArray();
        }
        public static IReadOnlyList<ReceivedTradeDesign> GetReceivedDesignsSnapshot()
        {
            // Voucher state changes without a new delivery, so it is read from the live entitlement, not the cached delivery view.
            lock (gate) return deliveries.Values.Select(d =>
            {
                var live = entitlements.FirstOrDefault(e => e.EntitlementId == d.Id);
                return new ReceivedTradeDesign {Id=d.Id,SellerAgencyId=d.SellerAgencyId,VesselId=d.VesselId,
                    Name=d.Name,Editor=d.Editor,Fingerprint=d.Fingerprint,DeliveryStatus=d.DeliveryStatus,LocalPath=d.LocalPath,Delivered=d.Delivered,
                    Kind=live?.Kind??d.Kind,Redeemed=live?.Redeemed??d.Redeemed,Reserved=live!=null && live.LaunchId!=Guid.Empty,PrepaidFunds=live?.PrepaidFunds??d.PrepaidFunds};
            }).ToArray();
        }
        /// <summary>
        /// Whether purchased rights unlock this exact part list. Permanent allowances always count. A free-launch voucher counts only
        /// when it will really be applied: tooling on, toggle on, and either selected for the launch in progress or the one the next launch would use.
        /// </summary>
        internal static bool HasEntitlement(ToolingManifest manifest)
        {
            if (!Ready) return false;
            string fingerprint;
            try { fingerprint = ToolingPolicy.Fingerprint(manifest); }
            catch (ArgumentException) { return false; }
            var pendingVoucher = ToolingClient.PendingVoucher;
            lock (gate)
            {
                if (entitlements.Any(e => e.Kind == TradeEntitlementKind.Permanent && e.Fingerprint == fingerprint)) return true;
                if (!ToolingClient.Enabled || !useVoucher) return false;
                if (ToolingClient.LaunchPending) return pendingVoucher != Guid.Empty && entitlements.Any(e => e.EntitlementId == pendingVoucher && e.Kind == TradeEntitlementKind.SingleLaunch && e.Fingerprint == fingerprint);
                if (!entitlements.Any(e => e.Kind == TradeEntitlementKind.SingleLaunch && !e.Redeemed && e.LaunchId == Guid.Empty && e.Fingerprint == fingerprint)) return false;
            }
            // Pricing is only worth computing once a matching unspent voucher exists.
            return SelectVoucher(ToolingClient.StandardQuote(manifest)) != null;
        }
        public static TradeCommand CaptureCurrentDesign(out ToolingManifest manifest)
        {
            if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch?.ship == null) throw new InvalidOperationException("Open a craft in the editor first.");
            var node = EditorLogic.fetch.ship.SaveShip();
            return CaptureNode(node, node.Serialize(), out manifest);
        }
        public static TradeCommand CaptureBlueprint(string absolutePath, out ToolingManifest manifest)
        {
            absolutePath = ValidateBlueprintPath(absolutePath);
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length <= 0 || info.Length > TradeLimits.MaxBlueprintBytes) throw new InvalidOperationException("Blueprint is missing or too large.");
            var bytes = File.ReadAllBytes(absolutePath);
            return CaptureNode(ConfigNode.Parse(Encoding.UTF8.GetString(bytes)), bytes, out manifest);
        }
        private static string ValidateBlueprintPath(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Choose a saved craft first.");
            var saves = Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath, "saves"));
            var save = Path.GetFullPath(Path.Combine(saves, HighLogic.SaveFolder));
            if (!save.StartsWith(saves + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid save directory.");
            var full = Path.GetFullPath(path);
            var folder = Path.GetDirectoryName(full);
            if (!string.Equals(Path.GetExtension(full), ".craft", StringComparison.OrdinalIgnoreCase) ||
                !new[] { "VAB", "SPH" }.Any(editor => string.Equals(folder, Path.Combine(save, "Ships", editor), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Choose a VAB or SPH craft from the current save.");
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked craft files are unsupported.");
            for (var current = new DirectoryInfo(folder); current != null && current.FullName.Length >= saves.Length; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked craft directories are unsupported.");
            return full;
        }
        private static TradeCommand CaptureNode(ConfigNode node, byte[] bytes, out ToolingManifest manifest)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > TradeLimits.MaxBlueprintBytes) throw new InvalidOperationException("Blueprint is missing or too large.");
            if (node == null || (node.GetValue("type") != "VAB" && node.GetValue("type") != "SPH")) throw new InvalidOperationException("Choose a valid VAB or SPH craft.");
            manifest = ToolingManifestBuilder.FromConfig(node, null);
            return new TradeCommand { BlueprintData=bytes, BlueprintName=node.GetValue("ship") ?? "Purchased craft",
                Editor=node.GetValue("type"), DesignFingerprint=ToolingPolicy.Fingerprint(manifest) };
        }
        public static Guid CreateOffer(TradeCommand draft, ToolingManifest manifest = null)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (draft.OfferId == Guid.Empty) draft.OfferId = Guid.NewGuid();
            if (draft.DesignMode == TradeDesignMode.SingleLaunch)
            {
                // Consistent with the other client-side research checks: the seller must be able to launch the design themselves.
                if (!ToolingClient.Enabled) { LatestStatus = "Single-launch offers need agency tooling."; return Guid.Empty; }
                if (manifest == null) { LatestStatus = "Choose a design to sell one launch of."; return Guid.Empty; }
                if (!AgencyTradeResearch.Validate(manifest, out var reason)) { LatestStatus = "You can only sell a launch you could make yourself. " + reason; return Guid.Empty; }
            }
            return Send(EconomyOperation.TradeCreate, draft, draft.DesignMode == TradeDesignMode.SingleLaunch ? manifest : null);
        }
        public static Guid RespondOffer(Guid id, long expectedRevision, bool accept) => Send(accept ? EconomyOperation.TradeAccept : EconomyOperation.TradeDecline,
            new TradeCommand {OfferId=id, ExpectedRevision=expectedRevision});
        public static Guid CancelOffer(Guid id, long expectedRevision) => Send(EconomyOperation.TradeCancel, new TradeCommand {OfferId=id,ExpectedRevision=expectedRevision});
        private static Guid Send(EconomyOperation operation, TradeCommand command, ToolingManifest manifest = null)
        {
            if (!Ready) {LatestStatus="Waiting for agency trade data."; return Guid.Empty;}
            var copy=new TradeCommand {OfferId=command.OfferId,BuyerAgencyId=command.BuyerAgencyId,VesselId=command.VesselId,EntitlementId=command.EntitlementId,
                ExpectedRevision=command.ExpectedRevision,SellerFunds=command.SellerFunds,SellerScience=command.SellerScience,BuyerFunds=command.BuyerFunds,
                BuyerScience=command.BuyerScience,DesignFingerprint=command.DesignFingerprint,BlueprintName=command.BlueprintName,Editor=command.Editor,
                DesignMode=command.DesignMode,
                BlueprintData=command.BlueprintData==null?Array.Empty<byte>():(byte[])command.BlueprintData.Clone()};
            return ToolingClient.Send(new EconomyCommand {Operation=operation,Trade=copy,Manifest=manifest,ManifestHash=manifest==null?null:ToolingPolicy.ManifestHash(manifest)});
        }
        internal static void HandleResult(EconomyResult result)
        {
            if (!Enabled || result.Operation < EconomyOperation.TradeCreate || result.Operation > EconomyOperation.TradeDelivered) return;
            LatestStatus = result.Reason;
            if (result.RecoveryRequired) ToolingClient.RecoveryDisconnect(result.Reason);
        }
        internal static void Tick()
        {
            if (!Ready) return;
            if (deliverPending)
            {
                deliverPending=false;
                TradeEntitlement[] copy; lock (gate) copy=entitlements;
                foreach (var entitlement in copy) Deliver(entitlement);
            }
            if (HighLogic.LoadedSceneIsEditor && EditorLogic.fetch?.ship != null)
            {
                try { EditorAllowanceStatus = AgencyTradeResearch.ValidateLive(EditorLogic.fetch.ship, out var reason) ? "Current design meets launch research requirements." : reason; }
                catch (Exception e) {EditorAllowanceStatus=e.Message;}
            }
        }
        private static string Hash(byte[] bytes) {using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();}
        private static void Deliver(TradeEntitlement item)
        {
            lock(gate)
                if(deliveries.TryGetValue(item.EntitlementId,out var saved) && saved.Delivered && (item.Delivered || acknowledged.Contains(item.EntitlementId))) return;
            var view = new ReceivedTradeDesign {Id=item.EntitlementId,SellerAgencyId=item.SellerAgencyId,VesselId=item.VesselId,Name=item.BlueprintName,
                Editor=item.Editor,Fingerprint=item.Fingerprint,DeliveryStatus=item.Kind==TradeEntitlementKind.SingleLaunch?"Free launch voucher":"Purchased vessel allowance",
                Kind=item.Kind,Redeemed=item.Redeemed,PrepaidFunds=item.PrepaidFunds};
            try
            {
                if (item.BlueprintData == null || item.BlueprintData.Length == 0) {lock(gate) deliveries[item.EntitlementId]=view; return;}
                if (item.BlueprintData.Length > TradeLimits.MaxBlueprintBytes || Hash(item.BlueprintData)!=item.BlueprintHash || (item.Editor!="VAB" && item.Editor!="SPH"))
                    throw new InvalidOperationException("Received blueprint failed validation.");
                var node=ConfigNode.Parse(Encoding.UTF8.GetString(item.BlueprintData));
                if (ToolingPolicy.Fingerprint(ToolingManifestBuilder.FromConfig(node,null))!=item.Fingerprint) throw new InvalidOperationException("Received blueprint parts do not match its license.");
                var saveRoot=Path.GetFullPath(Path.Combine(KSPUtil.ApplicationRootPath,"saves"));
                var directory=Path.GetFullPath(Path.Combine(saveRoot,HighLogic.SaveFolder,"Ships",item.Editor));
                if (!directory.StartsWith(saveRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid save directory.");
                for(var current=new DirectoryInfo(directory);current!=null && current.FullName.Length>=saveRoot.Length;current=current.Parent)
                    if(current.Exists && (current.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Linked blueprint directories are unsupported.");
                Directory.CreateDirectory(directory);
                var name=new string((item.BlueprintName??"Purchased craft").Where(c=>!Path.GetInvalidFileNameChars().Contains(c) && !char.IsControl(c)).Take(60).ToArray()).Trim().TrimEnd('.');
                if(string.IsNullOrEmpty(name)) name="Purchased craft";
                var path=Path.Combine(directory,"Trade-"+name+"-"+item.EntitlementId.ToString("N")+".craft");
                if(File.Exists(path) && Hash(File.ReadAllBytes(path))!=item.BlueprintHash)
                    path=Path.Combine(directory,"Trade-"+item.EntitlementId.ToString("N")+"-"+item.BlueprintHash+".craft");
                if(File.Exists(path)) {if(Hash(File.ReadAllBytes(path))!=item.BlueprintHash) throw new IOException("Blueprint destination contains different data.");}
                else
                {
                    var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try {using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {stream.Write(item.BlueprintData,0,item.BlueprintData.Length);stream.Flush(true);} File.Move(temporary,path);}
                    finally {if(File.Exists(temporary)) File.Delete(temporary);}
                }
                view.LocalPath=path; view.Delivered=true; view.DeliveryStatus="Saved to "+item.Editor+" craft library";
                if(!item.Delivered && acknowledged.Add(item.EntitlementId)) Send(EconomyOperation.TradeDelivered,new TradeCommand {EntitlementId=item.EntitlementId});
            }
            catch(Exception e) {view.DeliveryStatus="Could not save: "+e.Message;LatestStatus=view.DeliveryStatus;}
            if(!deliveryDiagnostics.TryGetValue(item.EntitlementId,out var previous) || previous!=view.DeliveryStatus)
            {
                deliveryDiagnostics[item.EntitlementId]=view.DeliveryStatus;
                Diagnostics.PlaytestDiagnostics.Write("client.trade.delivery",()=> $"entitlement={item.EntitlementId} saved={view.Delivered} outcome={view.DeliveryStatus}");
            }
            lock(gate) deliveries[item.EntitlementId]=view;
        }
        public static bool LoadDesign(Guid id, bool confirmedReplace)
        {
            if(!Ready) return false;
            ReceivedTradeDesign view; TradeEntitlement license;
            lock(gate) {deliveries.TryGetValue(id,out view);license=entitlements.FirstOrDefault(e=>e.EntitlementId==id);}
            if(view==null || !view.Delivered || license==null) {LatestStatus="Save the received blueprint first.";return false;}
            if(HighLogic.LoadedSceneIsEditor && !confirmedReplace) {LatestStatus="Confirm replacing the current editor craft.";return false;}
            try
            {
                if(Hash(File.ReadAllBytes(view.LocalPath))!=license.BlueprintHash) throw new IOException("Saved blueprint changed. Restore the received copy before loading.");
                if(HighLogic.LoadedSceneIsEditor) EditorLogic.LoadShipFromFile(view.LocalPath);
                else EditorDriver.StartAndLoadVessel(view.LocalPath,view.Editor=="SPH"?EditorFacility.SPH:EditorFacility.VAB);
                return true;
            }
            catch(Exception e) {LatestStatus=e.Message;return false;}
        }
        internal static Guid PrepareLaunch(ToolingManifest manifest)
        {
            var fingerprint=ToolingPolicy.Fingerprint(manifest);
            // A permanent allowance is always valid. Otherwise claim the voucher this launch reserved, which the server accepts for this launch only.
            var voucher=ToolingClient.PendingVoucher;
            lock(gate)
            {
                launchEntitlement=entitlements.FirstOrDefault(e=>e.Kind==TradeEntitlementKind.Permanent && e.Fingerprint==fingerprint)?.EntitlementId
                    ??(voucher==Guid.Empty?Guid.Empty:entitlements.FirstOrDefault(e=>e.EntitlementId==voucher && e.Kind==TradeEntitlementKind.SingleLaunch && e.Fingerprint==fingerprint)?.EntitlementId)??Guid.Empty;
                return launchEntitlement;
            }
        }
        internal static void BindLaunch(Vessel vessel, ShipConstruct ship)
        {
            if(!Enabled || !vessel) return;
            lock(gate) {if(launchEntitlement!=Guid.Empty) vesselEntitlements[vessel.id]=launchEntitlement;launchEntitlement=Guid.Empty;}
        }
        internal static void Registered(Guid vessel) {lock(gate) vesselEntitlements.Remove(vessel);}
        internal static void FillLaunchTail(Guid vessel, LmpCommon.Message.Data.Vessel.VesselProtoMsgData data)
        {
            lock(gate) data.TradeEntitlementId=Enabled && vesselEntitlements.TryGetValue(vessel,out var id)?id:Guid.Empty;
        }
        internal static void Clear()
        {
            lock(gate) {agency=Guid.Empty;revision=-1;offers=Array.Empty<TradeOffer>();entitlements=Array.Empty<TradeEntitlement>();deliveries.Clear();acknowledged.Clear();deliveryDiagnostics.Clear();deliverPending=false;launchEntitlement=Guid.Empty;vesselEntitlements.Clear();}
            LatestStatus=EditorAllowanceStatus=null;
        }
    }
}
