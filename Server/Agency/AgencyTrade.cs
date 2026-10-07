using LmpCommon.Agency;
using LunaConfigNode.CfgNode;
using Server.Client;
using Server.System;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Server.Agency
{
    public sealed class StoredTradeOffer
    {
        public TradeOffer Offer;
        public long VesselRevision;
        public string SellerOwner, BuyerOwner;
        public ToolingDesign Design;
        public byte[] Blueprint = Array.Empty<byte>();
        public string BlueprintHash;
        public string VesselFingerprint;
        public Guid SessionId;
        public long CreatedSequence;
    }

    public static partial class AgencyEconomyStore
    {
        private static bool IsTradeOperation(EconomyOperation operation) => operation >= EconomyOperation.TradeCreate && operation <= EconomyOperation.TradeDelivered;

        private static void ValidateTrade(EconomyDocument document)
        {
            if (document.TradeOffers == null || document.Entitlements == null || document.TradeOffers.Count > 1024) throw new InvalidDataException("Invalid trade registry.");
            foreach (var row in document.TradeOffers.Values)
            {
                if (row?.Offer == null || row.Offer.OfferId == Guid.Empty || !TradePolicy.ValidateAmounts(row.Offer.SellerFunds, row.Offer.SellerScience, row.Offer.BuyerFunds, row.Offer.BuyerScience) || row.Blueprint == null || row.Blueprint.Length > TradeLimits.MaxBlueprintBytes) throw new InvalidDataException("Invalid trade offer.");
                if (row.Blueprint.Length > 0 && Hash(row.Blueprint) != row.BlueprintHash) throw new InvalidDataException("Trade blueprint checksum mismatch.");
                if (!Enum.IsDefined(typeof(TradeDesignMode), row.Offer.DesignMode) || !ToolingPolicy.FiniteNonNegative(row.Offer.PrepaidLaunchFunds) || !ToolingPolicy.FiniteNonNegative(row.Offer.LaunchMultiplier)) throw new InvalidDataException("Invalid trade design terms.");
                if (row.Offer.DesignMode == TradeDesignMode.SingleLaunch && (string.IsNullOrEmpty(row.Offer.DesignFingerprint) || row.Blueprint.Length == 0)) throw new InvalidDataException("Invalid single-launch offer.");
            }
            foreach (var received in document.Entitlements.Values)
            {
                if (received == null || received.Count > TradeLimits.MaxEntitlements || received.Sum(e => (long)e.BlueprintData.Length) > TradeLimits.MaxAgencyBlueprintBytes) throw new InvalidDataException("Purchased-design storage limit reached.");
                foreach (var entitlement in received)
                {
                    if (entitlement.EntitlementId == Guid.Empty || entitlement.BlueprintData.Length > 0 && Hash(entitlement.BlueprintData) != entitlement.BlueprintHash) throw new InvalidDataException("Invalid purchased design.");
                    if (!Enum.IsDefined(typeof(TradeEntitlementKind), entitlement.Kind) || !ToolingPolicy.FiniteNonNegative(entitlement.PrepaidFunds) || !ToolingPolicy.FiniteNonNegative(entitlement.LaunchMultiplier)) throw new InvalidDataException("Invalid purchased design terms.");
                    if (entitlement.Kind == TradeEntitlementKind.SingleLaunch && (string.IsNullOrEmpty(entitlement.Fingerprint) || entitlement.Redeemed && entitlement.LaunchId == Guid.Empty)) throw new InvalidDataException("Invalid single-launch voucher.");
                }
            }
        }

        private static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..") throw new ArgumentException("Choose a safe blueprint name of at most 80 characters.");
            return name.Trim();
        }

        private static ToolingManifest BlueprintManifest(byte[] bytes, string editor)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > TradeLimits.MaxBlueprintBytes || editor != "VAB" && editor != "SPH") throw new ArgumentException("A bounded VAB or SPH craft blueprint is required.");
            var node = new ConfigNode(new UTF8Encoding(false, true).GetString(bytes));
            var type = node.GetValue("type")?.Value;
            if (type != editor) throw new ArgumentException("Blueprint editor type does not match.");
            var parts = node.GetNodes("PART").Select(p => p.Value.GetValue("part")?.Value).ToArray();
            if (parts.Length == 0 || parts.Length > ToolingPolicy.MaxParts || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Blueprint contains invalid physical parts.");
            var manifest = new ToolingManifest { Parts = parts.Select(value =>
            {
                var separator = value.LastIndexOf('_');
                if (separator < 1 || !uint.TryParse(value.Substring(separator + 1), out _)) throw new ArgumentException("Blueprint part identity is invalid.");
                return new ToolingPart { Name = value.Substring(0, separator), UnitCost = 0 };
            }).ToArray() };
            ToolingPolicy.Validate(manifest);
            return manifest;
        }

        private static string VesselFingerprint(Guid vesselId)
        {
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var vessel)) throw new InvalidOperationException("Offered craft no longer exists.");
            return ToolingPolicy.Fingerprint(new ToolingManifest { Parts = vessel.Parts.GetAllValues().Select(p => new ToolingPart { Name = p.Fields.GetSingle("name")?.Value, UnitCost = 0 }).ToArray() });
        }

        private static void RequireAgencyOwner(Guid agencyId, ClientStructure client)
        {
            if (!AgencyStore.Agencies.TryGetValue(agencyId, out var agency) || client.AgencyId != agencyId || agency.OwnerUniqueId != client.UniqueIdentifier) throw new InvalidOperationException("Only the agency owner can make this trade decision.");
        }

        private static void ValidateOfferedTitle(StoredTradeOffer saved, OwnershipDocument ownership)
        {
            var offer = saved.Offer;
            if (offer.VesselId == Guid.Empty) return;
            if (!VesselStoreSystem.VesselExists(offer.VesselId) || !ownership.Records.TryGetValue(offer.VesselId, out var title) || title.OwnerAgencyId != offer.SellerAgencyId || title.Revision != saved.VesselRevision || ownership.Absorbed.Contains(offer.VesselId) || ownership.PendingSplits.ContainsKey(offer.VesselId) || ownership.PendingSplits.Values.Any(p => p.ParentId == offer.VesselId)) throw new InvalidOperationException("Offered craft title changed.");
            if (ownership.Constituents.TryGetValue(offer.VesselId, out var constituents) && constituents.Any(c => c.Ownership.OwnerAgencyId != offer.SellerAgencyId)) throw new InvalidOperationException("Undock visiting craft before trading.");
            if (VesselFingerprint(offer.VesselId) != saved.VesselFingerprint) throw new InvalidOperationException("Offered craft physical design changed.");
        }

        private static bool TradeNeedsMaintenance(EconomyDocument document)
        {
            return document.TradeOffers.Values.Count(o => o.Offer.Status != TradeOfferStatus.Open) > 256 || document.TradeOffers.Values.Any(o => o.Offer.Status == TradeOfferStatus.Open && (o.Offer.ExpiresUtcTicks <= UtcNow().Ticks || !AgencyStore.Agencies.TryGetValue(o.Offer.SellerAgencyId, out var seller) || !AgencyStore.Agencies.TryGetValue(o.Offer.BuyerAgencyId, out var buyer) || seller.OwnerUniqueId != o.SellerOwner || buyer.OwnerUniqueId != o.BuyerOwner));
        }

        private static void PruneTrade(EconomyDocument document)
        {
            foreach (var stored in document.TradeOffers.Values)
            {
                if (stored.Offer.Status != TradeOfferStatus.Open) continue;
                if (stored.Offer.ExpiresUtcTicks <= UtcNow().Ticks) { stored.Offer.Status = TradeOfferStatus.Expired; stored.Offer.Revision++; }
                else if (!AgencyStore.Agencies.TryGetValue(stored.Offer.SellerAgencyId, out var seller) || !AgencyStore.Agencies.TryGetValue(stored.Offer.BuyerAgencyId, out var buyer) || seller.OwnerUniqueId != stored.SellerOwner || buyer.OwnerUniqueId != stored.BuyerOwner) { stored.Offer.Status = TradeOfferStatus.Invalidated; stored.Offer.Revision++; }
            }
            // An old accept never creates an offer. Retired IDs fail closed, while the
            // connection sequence watermark also rejects evicted request retries.
            foreach (var id in document.TradeOffers.Where(p => p.Value.Offer.Status != TradeOfferStatus.Open).OrderByDescending(p => p.Value.Offer.ExpiresUtcTicks).Skip(256).Select(p => p.Key).ToArray()) document.TradeOffers.Remove(id);
        }

        private static void InvalidateChangedOffers(EconomyDocument document, OwnershipDocument ownership)
        {
            foreach (var saved in document.TradeOffers.Values.Where(o => o.Offer.Status == TradeOfferStatus.Open && o.Offer.VesselId != Guid.Empty))
            {
                try { ValidateOfferedTitle(saved, ownership); }
                catch (InvalidOperationException) { saved.Offer.Status = TradeOfferStatus.Invalidated; saved.Offer.Revision++; }
            }
        }

        private static void AddEntitlement(EconomyDocument candidate, Guid agencyId, TradeEntitlement entitlement)
        {
            if (!candidate.Entitlements.TryGetValue(agencyId, out var entries)) candidate.Entitlements[agencyId] = entries = new List<TradeEntitlement>();
            // Each purchased launch is its own voucher; only permanent allowances collapse into one.
            if (entitlement.Kind == TradeEntitlementKind.Permanent && entries.Any(e => e.Kind == TradeEntitlementKind.Permanent && e.Fingerprint == entitlement.Fingerprint && e.BlueprintHash == entitlement.BlueprintHash && e.VesselId == entitlement.VesselId)) return;
            entries.Add(entitlement);
        }

        private static void ApplyTrade(EconomyDocument candidate, ClientStructure client, EconomyCommand command, EconomyResult result)
        {
            if (!TradeEnabled || command.Trade == null) throw new InvalidOperationException("Agency trade is unavailable.");
            var request = command.Trade;
            PruneTrade(candidate);
            if (command.Operation == EconomyOperation.TradeDelivered)
            {
                if (!candidate.Entitlements.TryGetValue(client.AgencyId, out var entries)) throw new InvalidOperationException("Purchased design not found.");
                var entry = entries.SingleOrDefault(e => e.EntitlementId == request.EntitlementId) ?? throw new InvalidOperationException("Purchased design not found.");
                entry.Delivered = true;
                return;
            }
            if (command.Operation == EconomyOperation.TradeCreate)
            {
                RequireAgencyOwner(client.AgencyId, client);
                if (request.OfferId == Guid.Empty || candidate.TradeOffers.ContainsKey(request.OfferId) || request.BuyerAgencyId == client.AgencyId || !AgencyStore.Agencies.TryGetValue(request.BuyerAgencyId, out var buyer)) throw new InvalidOperationException("Choose a different existing buyer and unused offer ID.");
                if (candidate.TradeOffers.Values.Count(o => o.Offer.Status == TradeOfferStatus.Open) >= 768) throw new InvalidOperationException("Server offer storage is full; close an existing offer first.");
                if (candidate.TradeOffers.Values.Count(o => o.Offer.Status == TradeOfferStatus.Open && (o.Offer.SellerAgencyId == client.AgencyId || o.Offer.BuyerAgencyId == request.BuyerAgencyId)) >= TradeLimits.MaxOpenOffers) throw new InvalidOperationException("Too many open offers; close an existing offer first.");
                if (!TradePolicy.ValidateAmounts(request.SellerFunds, request.SellerScience, request.BuyerFunds, request.BuyerScience)) throw new ArgumentException("Invalid trade currency amounts.");
                var seller = AgencyStore.Agencies[client.AgencyId];
                if (!Enum.IsDefined(typeof(TradeDesignMode), request.DesignMode)) throw new ArgumentException("Unsupported design mode.");
                var singleLaunch = request.DesignMode == TradeDesignMode.SingleLaunch;
                if (singleLaunch && (!ToolingEnabled || string.IsNullOrEmpty(request.DesignFingerprint))) throw new InvalidOperationException(ToolingEnabled ? "A single-launch offer needs a design." : "Single-launch offers need agency tooling.");
                var offer = new TradeOffer { DesignMode = request.DesignMode, OfferId = request.OfferId, SellerAgencyId = client.AgencyId, BuyerAgencyId = request.BuyerAgencyId, VesselId = request.VesselId, Revision = 1, ExpiresUtcTicks = UtcNow().AddHours(24).Ticks, SellerFunds = request.SellerFunds, SellerScience = request.SellerScience, BuyerFunds = request.BuyerFunds, BuyerScience = request.BuyerScience, DesignFingerprint = request.DesignFingerprint };
                var stored = new StoredTradeOffer { Offer = offer, SellerOwner = seller.OwnerUniqueId, BuyerOwner = buyer.OwnerUniqueId, SessionId = command.SessionId, CreatedSequence = command.Sequence };
                if (!string.IsNullOrEmpty(request.DesignFingerprint))
                {
                    var blueprintFingerprint = ToolingPolicy.Fingerprint(BlueprintManifest(request.BlueprintData, request.Editor));
                    if (singleLaunch)
                    {
                        // The seller needs no tooling: the manifest only prices the launch the seller prepays.
                        if (command.Manifest == null || ToolingPolicy.ManifestHash(command.Manifest) != command.ManifestHash || ToolingPolicy.Fingerprint(command.Manifest) != request.DesignFingerprint || blueprintFingerprint != request.DesignFingerprint) throw new ArgumentException("Blueprint does not match the priced launch manifest.");
                        var quote = Quote(Agency(candidate, client.AgencyId), command.Manifest, command.ManifestHash);
                        offer.LaunchMultiplier = quote.AlreadyTooled ? Rates().TooledLaunch : Rates().UntooledLaunch;
                        offer.PrepaidLaunchFunds = UsesFunds ? TradePolicy.PrepaidLaunchCost(quote, offer.LaunchMultiplier) : 0;
                    }
                    else
                    {
                        stored.Design = Copy(Agency(candidate, client.AgencyId).Designs.SingleOrDefault(d => d.Fingerprint == request.DesignFingerprint) ?? throw new InvalidOperationException("Only an existing tooled design can be sold."));
                        if (blueprintFingerprint != request.DesignFingerprint) throw new ArgumentException("Blueprint does not match the offered tooling.");
                    }
                    stored.Blueprint = Copy(request.BlueprintData);
                    stored.BlueprintHash = Hash(stored.Blueprint);
                    offer.BlueprintName = SafeName(request.BlueprintName);
                    offer.Editor = request.Editor;
                }
                if (offer.VesselId != Guid.Empty)
                {
                    var title = AgencyVesselMap.Get(offer.VesselId) ?? throw new InvalidOperationException("Craft has no owner.");
                    stored.VesselRevision = title.Revision;
                    stored.VesselFingerprint = VesselFingerprint(offer.VesselId);
                    offer.VesselName = VesselStoreSystem.CurrentVessels[offer.VesselId].Fields.GetSingle("name")?.Value;
                    ValidateOfferedTitle(stored, AgencyVesselMap.ExportDocument());
                }
                if (offer.VesselId == Guid.Empty && stored.Design == null && stored.Blueprint.Length == 0 && offer.SellerFunds + offer.SellerScience + offer.BuyerFunds + offer.BuyerScience == 0) throw new ArgumentException("Offer has no assets or currencies.");
                candidate.TradeOffers[offer.OfferId] = stored;
                result.TradeOfferId = offer.OfferId;
                return;
            }
            if (!candidate.TradeOffers.TryGetValue(request.OfferId, out var saved)) throw new InvalidOperationException("Offer no longer exists.");
            var trade = saved.Offer;
            result.TradeOfferId = trade.OfferId;
            if (command.Operation == EconomyOperation.TradeAccept && trade.Status == TradeOfferStatus.Accepted)
            {
                RequireAgencyOwner(trade.BuyerAgencyId, client);
                return;
            }
            if (trade.Status != TradeOfferStatus.Open || request.ExpectedRevision != trade.Revision) throw new InvalidOperationException("Offer changed or closed; refresh before confirming.");
            RequireAgencyOwner(command.Operation == EconomyOperation.TradeCancel ? trade.SellerAgencyId : trade.BuyerAgencyId, client);
            if (command.Operation == EconomyOperation.TradeDecline || command.Operation == EconomyOperation.TradeCancel)
            {
                trade.Status = command.Operation == EconomyOperation.TradeCancel ? TradeOfferStatus.Cancelled : TradeOfferStatus.Declined;
                trade.Revision++;
                return;
            }
            if (command.Operation != EconomyOperation.TradeAccept) throw new ArgumentException("Unsupported trade decision.");
            if (!AgencyStore.Agencies.TryGetValue(trade.SellerAgencyId, out var sellerAgency) || !AgencyStore.Agencies.TryGetValue(trade.BuyerAgencyId, out var buyerAgency) || sellerAgency.OwnerUniqueId != saved.SellerOwner || buyerAgency.OwnerUniqueId != saved.BuyerOwner) throw new InvalidOperationException("Agency ownership changed; create a new offer.");
            if (trade.DesignMode == TradeDesignMode.SingleLaunch && !ToolingEnabled) throw new InvalidOperationException("Single-launch designs need agency tooling.");
            var ownership = AgencyVesselMap.ExportDocument();
            ValidateOfferedTitle(saved, ownership);
            var sellerBalance = Agency(candidate, trade.SellerAgencyId);
            var buyerBalance = Agency(candidate, trade.BuyerAgencyId);
            var settled = TradePolicy.Settle(sellerBalance.Funds, sellerBalance.Science, buyerBalance.Funds, buyerBalance.Science, trade.SellerFunds, trade.SellerScience, trade.BuyerFunds, trade.BuyerScience);
            sellerBalance.Funds = settled.SellerFunds; sellerBalance.Science = settled.SellerScience;
            buyerBalance.Funds = settled.BuyerFunds; buyerBalance.Science = settled.BuyerScience;
            if (trade.DesignMode == TradeDesignMode.SingleLaunch)
            {
                // The seller builds to order: the prepayment is taken now, after the price has been received.
                if (UsesFunds && sellerBalance.Funds < trade.PrepaidLaunchFunds) throw new InvalidOperationException("Seller cannot prepay the launch.");
                Charge(sellerBalance, trade.PrepaidLaunchFunds);
                AddEntitlement(candidate, trade.BuyerAgencyId, new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = trade.SellerAgencyId, Fingerprint = trade.DesignFingerprint, BlueprintName = trade.BlueprintName, Editor = trade.Editor, BlueprintHash = saved.BlueprintHash, BlueprintData = Copy(saved.Blueprint), Kind = TradeEntitlementKind.SingleLaunch, PrepaidFunds = UsesFunds ? trade.PrepaidLaunchFunds : 0, LaunchMultiplier = trade.LaunchMultiplier });
            }
            else if (saved.Design != null)
            {
                if (!sellerBalance.Designs.Any(d => d.Fingerprint == saved.Design.Fingerprint)) throw new InvalidOperationException("Offered tooling no longer exists.");
                if (!buyerBalance.Designs.Any(d => d.Fingerprint == saved.Design.Fingerprint)) buyerBalance.Designs.Add(Copy(saved.Design));
                AddEntitlement(candidate, trade.BuyerAgencyId, new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = trade.SellerAgencyId, Fingerprint = saved.Design.Fingerprint, BlueprintName = trade.BlueprintName, Editor = trade.Editor, BlueprintHash = saved.BlueprintHash, BlueprintData = Copy(saved.Blueprint) });
            }
            if (trade.VesselId != Guid.Empty)
            {
                ownership.Revision++;
                var title = ownership.Records[trade.VesselId];
                title.OwnerAgencyId = trade.BuyerAgencyId; title.CoOwnerAgencyIds = Array.Empty<Guid>(); title.DockingPolicy = VesselDockingPolicy.Nobody; title.Revision = ownership.Revision;
                if (ownership.Constituents.TryGetValue(trade.VesselId, out var constituents)) foreach (var constituent in constituents) { constituent.Ownership.OwnerAgencyId = trade.BuyerAgencyId; constituent.Ownership.CoOwnerAgencyIds = Array.Empty<Guid>(); constituent.Ownership.DockingPolicy = VesselDockingPolicy.Nobody; constituent.Ownership.Revision = ownership.Revision; }
                if (candidate.Vessels.TryGetValue(trade.VesselId, out var paid)) foreach (var part in paid.Parts) if (candidate.Launches.TryGetValue(part.LaunchId, out var launch)) launch.ExternallySettled = true;
                AddEntitlement(candidate, trade.BuyerAgencyId, new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = trade.SellerAgencyId, VesselId = trade.VesselId, Fingerprint = saved.VesselFingerprint, BlueprintName = trade.VesselName });
                candidate.Journal = new EconomyVesselJournal { OwnershipAfter = ownership };
                InvalidateChangedOffers(candidate, ownership);
            }
            trade.Status = TradeOfferStatus.Accepted;
            trade.Revision++;
        }

        private static TradeOffer[] TradeOffersFor(EconomyDocument document, Guid agencyId)
        {
            if (!TradeEnabled) return Array.Empty<TradeOffer>();
            return document.TradeOffers.Values.Where(o => o.Offer.SellerAgencyId == agencyId || o.Offer.BuyerAgencyId == agencyId).Select(o =>
            {
                var offer = Copy(o.Offer);
                if (offer.Status == TradeOfferStatus.Open && offer.ExpiresUtcTicks <= UtcNow().Ticks) { offer.Status = TradeOfferStatus.Expired; offer.Revision++; }
                return offer;
            }).ToArray();
        }

        public static bool ValidateTradeEntitlement(Guid agencyId, Guid entitlementId, global::Server.System.Vessel.Classes.Vessel vessel, Guid launchId = default(Guid))
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (entitlementId == Guid.Empty) return true;
                if (!TradeEnabled || !Ready || !_document.Entitlements.TryGetValue(agencyId, out var entitlements)) return false;
                var manifest = new ToolingManifest { Parts = vessel.Parts.GetAllValues().Select(p => new ToolingPart { Name = p.Fields.GetSingle("name")?.Value }).ToArray() };
                // A voucher grants research for the one launch that reserved it, and only while tooling gameplay is on.
                var allowed = entitlements.Where(e => e.EntitlementId == entitlementId && (e.Kind == TradeEntitlementKind.Permanent || ToolingEnabled && launchId != Guid.Empty && !e.Redeemed && e.LaunchId == launchId));
                return TradePolicy.CanUseEntitlement(manifest, allowed.Select(e => e.Fingerprint));
            }
        }
    }
}
