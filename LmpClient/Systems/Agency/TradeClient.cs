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
        internal static void Receive(EconomySnapshot snapshot)
        {
            if (!Enabled || snapshot == null || !snapshot.Ready || snapshot.AgencyId != AgencySystem.Singleton.MyAgencyId) return;
            lock (gate)
            {
                if (agency != snapshot.AgencyId) Clear();
                if (snapshot.Revision < revision) return;
                agency = snapshot.AgencyId; revision = snapshot.Revision;
                offers = snapshot.Offers ?? Array.Empty<TradeOffer>();
                entitlements = snapshot.Entitlements ?? Array.Empty<TradeEntitlement>();
                deliverPending = true;
            }
        }
        public static IReadOnlyList<TradeOffer> GetOffersSnapshot()
        {
            lock (gate) return offers.Select(o => new TradeOffer { OfferId=o.OfferId, SellerAgencyId=o.SellerAgencyId, BuyerAgencyId=o.BuyerAgencyId,
                VesselId=o.VesselId, Revision=o.Revision, ExpiresUtcTicks=o.ExpiresUtcTicks, Status=o.Status, SellerFunds=o.SellerFunds,
                SellerScience=o.SellerScience, BuyerFunds=o.BuyerFunds, BuyerScience=o.BuyerScience, DesignFingerprint=o.DesignFingerprint,
                BlueprintName=o.BlueprintName, Editor=o.Editor, VesselName=o.VesselName }).ToArray();
        }
        public static IReadOnlyList<ReceivedTradeDesign> GetReceivedDesignsSnapshot()
        {
            lock (gate) return deliveries.Values.Select(d => new ReceivedTradeDesign {Id=d.Id,SellerAgencyId=d.SellerAgencyId,VesselId=d.VesselId,
                Name=d.Name,Editor=d.Editor,Fingerprint=d.Fingerprint,DeliveryStatus=d.DeliveryStatus,LocalPath=d.LocalPath,Delivered=d.Delivered}).ToArray();
        }
        internal static bool HasEntitlement(ToolingManifest manifest)
        {
            if (!Ready) return false;
            lock (gate) return TradePolicy.CanUseEntitlement(manifest, entitlements.Select(e => e.Fingerprint));
        }
        public static TradeCommand CaptureCurrentDesign()
        {
            if (!HighLogic.LoadedSceneIsEditor || EditorLogic.fetch?.ship == null) throw new InvalidOperationException("Open a craft in the editor first.");
            var node = EditorLogic.fetch.ship.SaveShip();
            return CaptureNode(node, Encoding.UTF8.GetBytes(node.ToString()));
        }
        public static TradeCommand CaptureBlueprint(string absolutePath)
        {
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.Length <= 0 || info.Length > TradeLimits.MaxBlueprintBytes) throw new InvalidOperationException("Blueprint is missing or too large.");
            var bytes = File.ReadAllBytes(absolutePath);
            return CaptureNode(ConfigNode.Parse(Encoding.UTF8.GetString(bytes)), bytes);
        }
        private static TradeCommand CaptureNode(ConfigNode node, byte[] bytes)
        {
            var manifest = ToolingManifestBuilder.FromConfig(node, null);
            return new TradeCommand { BlueprintData=bytes, BlueprintName=node.GetValue("ship") ?? "Purchased craft",
                Editor=node.GetValue("type"), DesignFingerprint=ToolingPolicy.Fingerprint(manifest) };
        }
        public static Guid CreateOffer(TradeCommand draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            if (draft.OfferId == Guid.Empty) draft.OfferId = Guid.NewGuid();
            return Send(EconomyOperation.TradeCreate, draft);
        }
        public static Guid RespondOffer(Guid id, long expectedRevision, bool accept) => Send(accept ? EconomyOperation.TradeAccept : EconomyOperation.TradeDecline,
            new TradeCommand {OfferId=id, ExpectedRevision=expectedRevision});
        public static Guid CancelOffer(Guid id, long expectedRevision) => Send(EconomyOperation.TradeCancel, new TradeCommand {OfferId=id,ExpectedRevision=expectedRevision});
        private static Guid Send(EconomyOperation operation, TradeCommand command)
        {
            if (!Ready) {LatestStatus="Waiting for agency trade data."; return Guid.Empty;}
            var copy=new TradeCommand {OfferId=command.OfferId,BuyerAgencyId=command.BuyerAgencyId,VesselId=command.VesselId,EntitlementId=command.EntitlementId,
                ExpectedRevision=command.ExpectedRevision,SellerFunds=command.SellerFunds,SellerScience=command.SellerScience,BuyerFunds=command.BuyerFunds,
                BuyerScience=command.BuyerScience,DesignFingerprint=command.DesignFingerprint,BlueprintName=command.BlueprintName,Editor=command.Editor,
                BlueprintData=command.BlueprintData==null?Array.Empty<byte>():(byte[])command.BlueprintData.Clone()};
            return ToolingClient.Send(new EconomyCommand {Operation=operation,Trade=copy});
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
                Editor=item.Editor,Fingerprint=item.Fingerprint,DeliveryStatus="Purchased vessel allowance"};
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
            lock(gate) {launchEntitlement=entitlements.FirstOrDefault(e=>e.Fingerprint==fingerprint)?.EntitlementId??Guid.Empty;return launchEntitlement;}
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
