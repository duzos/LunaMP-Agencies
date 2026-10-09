using LmpCommon.Agency;
using LunaConfigNode.CfgNode;
using Server.Client;
using Server.Log;
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
        /// <summary>Stock offers only: the seller's units held while the offer is open. Empty once the offer closes.</summary>
        public List<DesignStockLot> Escrow = new List<DesignStockLot>();
    }

    public static partial class AgencyEconomyStore
    {
        /// <summary>
        /// The one exit for an open offer: sets the status, bumps the revision and returns any escrow to the seller's stock. With
        /// <paramref name="strict"/> false (the live server) a missing seller row drops the units with a log line; with it true (the downgrade tool) that is an error.
        /// </summary>
        private static void CloseOffer(EconomyDocument document, StoredTradeOffer stored, TradeOfferStatus status, bool strict)
        {
            stored.Offer.Status = status;
            stored.Offer.Revision++;
            var escrow = stored.Escrow;
            stored.Escrow = new List<DesignStockLot>();
            if (escrow == null || escrow.Count == 0) return;
            var sellerId = stored.Offer.SellerAgencyId;
            if (!document.Agencies.TryGetValue(sellerId, out var seller))
            {
                if (strict) throw new InvalidDataException("Offer " + stored.Offer.OfferId + " holds escrowed stock but its seller " + sellerId + " has no economy row.");
                if (!AgencyStore.Agencies.TryGetValue(sellerId, out var source))
                {
                    // The agency was deleted; nobody is left to return the units to.
                    LunaLog.Warning($"[Economy] Discarded {escrow.Sum(l => l.Units)} escrowed stock units of {stored.Offer.DesignFingerprint} from offer {stored.Offer.OfferId}: seller agency {sellerId} no longer exists.");
                    return;
                }
                document.Agencies[sellerId] = seller = new EconomyAgency { Funds = source.Funds, Science = source.Science };
            }
            // Each escrow row came out of this seller's stock, so returning it never adds a lot slot (R1.1).
            foreach (var lot in escrow) StockPolicy.Merge(seller.Stock, lot);
        }

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
                ValidateStockOffer(row);
            }
            foreach (var received in document.Entitlements.Values)
            {
                if (received == null || received.Count > TradeLimits.MaxEntitlements || received.Sum(e => (long)e.BlueprintData.Length) > TradeLimits.MaxAgencyBlueprintBytes) throw new InvalidDataException("Purchased-design storage limit reached.");
                foreach (var entitlement in received)
                {
                    if (entitlement.EntitlementId == Guid.Empty || entitlement.BlueprintData.Length > 0 && Hash(entitlement.BlueprintData) != entitlement.BlueprintHash) throw new InvalidDataException("Invalid purchased design.");
                    if (!Enum.IsDefined(typeof(TradeEntitlementKind), entitlement.Kind) || !ToolingPolicy.FiniteNonNegative(entitlement.PrepaidFunds) || !ToolingPolicy.FiniteNonNegative(entitlement.LaunchMultiplier)) throw new InvalidDataException("Invalid purchased design terms.");
                    if (entitlement.Kind == TradeEntitlementKind.SingleLaunch && (string.IsNullOrEmpty(entitlement.Fingerprint) || entitlement.Redeemed && entitlement.LaunchId == Guid.Empty)) throw new InvalidDataException("Invalid single-launch voucher.");
                    if (entitlement.Kind == TradeEntitlementKind.StockDesign && (string.IsNullOrEmpty(entitlement.Fingerprint) || entitlement.Redeemed || entitlement.LaunchId != Guid.Empty || entitlement.PrepaidFunds != 0)) throw new InvalidDataException("Invalid stock design.");
                }
            }
        }

        /// <summary>Offer-level stock invariants: open Stock offers escrow exactly their units of their design; every other offer escrows nothing.</summary>
        private static void ValidateStockOffer(StoredTradeOffer row)
        {
            var offer = row.Offer;
            if (row.Escrow == null || !ToolingPolicy.FiniteNonNegative(offer.StockPrepaidTotal) || offer.StockUnits < 0) throw new InvalidDataException("Invalid stock offer.");
            if (offer.DesignMode != TradeDesignMode.Stock)
            {
                if (row.Escrow.Count > 0 || offer.StockUnits != 0 || offer.StockPrepaidTotal != 0) throw new InvalidDataException("Only a stock offer can escrow stock.");
                return;
            }
            if (string.IsNullOrEmpty(offer.DesignFingerprint) || row.Blueprint.Length == 0 || offer.StockUnits < 1 || offer.StockUnits > StockDefaults.MaxHeldUnits || row.Design != null) throw new InvalidDataException("Invalid stock offer.");
            if (offer.Status != TradeOfferStatus.Open)
            {
                if (row.Escrow.Count > 0) throw new InvalidDataException("A closed stock offer still holds escrow.");
                return;
            }
            foreach (var lot in row.Escrow)
                if (lot == null || lot.LotId == Guid.Empty || lot.Fingerprint != offer.DesignFingerprint || lot.Units < 1 || lot.Units > StockDefaults.MaxHeldUnits || !ToolingPolicy.FiniteNonNegative(lot.PrepaidPerUnit) || !ToolingPolicy.FiniteNonNegative(lot.LaunchMultiplier))
                    throw new InvalidDataException("Invalid escrowed stock.");
            if (row.Escrow.Sum(l => (long)l.Units) != offer.StockUnits) throw new InvalidDataException("Escrowed stock does not match the offer.");
            var escrowed = row.Escrow.Sum(l => l.Units * l.PrepaidPerUnit);
            if (Math.Abs(escrowed - offer.StockPrepaidTotal) > 1e-6 * Math.Max(1, escrowed)) throw new InvalidDataException("Escrowed stock value does not match the offer.");
        }

        /// <summary>
        /// Drops StockDesign entitlements nothing needs any more: delivered, nothing retains the design for the holder
        /// (<see cref="StockDesignRetainable"/>), and no open incoming Stock offer of that design. The craft file stays on the client's disk.
        /// Called from RetireTerminalLaunches.
        /// </summary>
        private static void RetireStockDesigns(EconomyDocument document)
        {
            foreach (var entry in document.Entitlements)
                entry.Value.RemoveAll(e => e.Kind == TradeEntitlementKind.StockDesign && e.Delivered && !StockDesignRetainable(document, entry.Key, e.Fingerprint)
                    && !document.TradeOffers.Values.Any(o => o.Offer.Status == TradeOfferStatus.Open && o.Offer.DesignMode == TradeDesignMode.Stock && o.Offer.BuyerAgencyId == entry.Key && o.Offer.DesignFingerprint == e.Fingerprint));
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

        /// <summary>
        /// Which part names are science parts, for craft files and stored vessels that carry names only. The server has no part database, so it uses
        /// what clients declared: a name classified by one of the <paramref name="context"/> manifests (the craft being priced, or the design it
        /// claims to be) takes that classification; any other name is a science part when any manifest stored in <paramref name="document"/> declares it so.
        /// </summary>
        internal static Func<string, bool> ScienceClassifier(EconomyDocument document, params ToolingManifest[] context)
        {
            var declared = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var manifest in context ?? Array.Empty<ToolingManifest>())
            {
                if (manifest?.Parts == null) continue;
                try { ToolingPolicy.Validate(manifest); }
                catch (ArgumentException) { continue; }
                foreach (var part in manifest.Parts) if (!declared.ContainsKey(part.Name)) declared[part.Name] = part.IsScience;
            }
            var known = new HashSet<string>(StringComparer.Ordinal);
            void Add(ToolingManifest manifest)
            {
                if (manifest?.Parts == null) return;
                foreach (var part in manifest.Parts) if (part != null && part.IsScience && part.Name != null) known.Add(part.Name);
            }
            if (document != null)
            {
                foreach (var agency in document.Agencies.Values) if (agency?.Designs != null) foreach (var design in agency.Designs) Add(design?.Manifest);
                foreach (var launch in document.Launches.Values) Add(launch?.Manifest);
                foreach (var offer in document.TradeOffers.Values) Add(offer?.Design?.Manifest);
            }
            return name => name != null && (declared.TryGetValue(name, out var science) ? science : known.Contains(name));
        }

        private static ToolingManifest BlueprintManifest(byte[] bytes, string editor, Func<string, bool> isScience = null)
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
                var name = value.Substring(0, separator);
                return new ToolingPart { Name = name, UnitCost = 0, IsScience = isScience != null && isScience(name) };
            }).ToArray() };
            ToolingPolicy.Validate(manifest);
            return manifest;
        }

        private static string VesselFingerprint(Guid vesselId)
        {
            if (!VesselStoreSystem.CurrentVessels.TryGetValue(vesselId, out var vessel)) throw new InvalidOperationException("Offered craft no longer exists.");
            // A change check on the offered vessel itself, not a design identity: every part counts, science included, as it always has.
            return ToolingPolicy.LegacyFingerprint(new ToolingManifest { Parts = vessel.Parts.GetAllValues().Select(p => new ToolingPart { Name = p.Fields.GetSingle("name")?.Value, UnitCost = 0 }).ToArray() });
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
                if (stored.Offer.ExpiresUtcTicks <= UtcNow().Ticks) CloseOffer(document, stored, TradeOfferStatus.Expired, false);
                else if (!AgencyStore.Agencies.TryGetValue(stored.Offer.SellerAgencyId, out var seller) || !AgencyStore.Agencies.TryGetValue(stored.Offer.BuyerAgencyId, out var buyer) || seller.OwnerUniqueId != stored.SellerOwner || buyer.OwnerUniqueId != stored.BuyerOwner) CloseOffer(document, stored, TradeOfferStatus.Invalidated, false);
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
                catch (InvalidOperationException) { CloseOffer(document, saved, TradeOfferStatus.Invalidated, false); }
            }
        }

        private static void AddEntitlement(EconomyDocument candidate, Guid agencyId, TradeEntitlement entitlement)
        {
            if (!candidate.Entitlements.TryGetValue(agencyId, out var entries)) candidate.Entitlements[agencyId] = entries = new List<TradeEntitlement>();
            // Each purchased launch is its own voucher; only permanent allowances and stock craft files collapse into one.
            if (entitlement.Kind == TradeEntitlementKind.Permanent && entries.Any(e => e.Kind == TradeEntitlementKind.Permanent && e.Fingerprint == entitlement.Fingerprint && e.BlueprintHash == entitlement.BlueprintHash && e.VesselId == entitlement.VesselId)) return;
            if (entitlement.Kind == TradeEntitlementKind.StockDesign && entries.Any(e => e.Kind == TradeEntitlementKind.StockDesign && e.Fingerprint == entitlement.Fingerprint && e.BlueprintHash == entitlement.BlueprintHash)) return;
            entries.Add(entitlement);
        }

        /// <param name="newFiles">Execute's list of blueprint files written in this transaction, so a failed Commit can delete them; null leaves them to the Load cleanup.</param>
        private static void ApplyTrade(EconomyDocument candidate, ClientStructure client, EconomyCommand command, EconomyResult result, List<string> newFiles = null)
        {
            // With trade off only the seller withdrawing their own open Stock offer is still allowed (checked below), so escrow is never stranded.
            if (command.Trade == null || !TradeEnabled && command.Operation != EconomyOperation.TradeCancel) throw new InvalidOperationException("Agency trade is unavailable.");
            var request = command.Trade;
            PruneTrade(candidate);
            if (command.Operation == EconomyOperation.TradeDelivered)
            {
                if (!candidate.Entitlements.TryGetValue(client.AgencyId, out var entries)) throw new InvalidOperationException("Purchased design not found.");
                var entry = entries.SingleOrDefault(e => e.EntitlementId == request.EntitlementId) ?? throw new InvalidOperationException("Purchased design not found.");
                entry.Delivered = true;
                // The client has written the stock craft file; keep only its hash so repeat purchases don't fill the purchased-design storage.
                if (entry.Kind == TradeEntitlementKind.StockDesign) entry.BlueprintData = Array.Empty<byte>();
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
                switch (request.DesignMode)
                {
                    case TradeDesignMode.SingleLaunch:
                        if (!ToolingEnabled || string.IsNullOrEmpty(request.DesignFingerprint)) throw new InvalidOperationException(ToolingEnabled ? "A single-launch offer needs a design." : "Single-launch offers need agency tooling.");
                        break;
                    case TradeDesignMode.Stock:
                        if (!ToolingEnabled || string.IsNullOrEmpty(request.DesignFingerprint)) throw new InvalidOperationException(ToolingEnabled ? "A stock offer needs a design." : "Stock offers need agency tooling.");
                        if (request.StockUnits < 1 || request.StockUnits > StockDefaults.MaxHeldUnits) throw new ArgumentException("Choose between 1 and " + StockDefaults.MaxHeldUnits + " units to sell.");
                        break;
                    case TradeDesignMode.ToolingAndDesign:
                        break;
                    default: throw new ArgumentException("Unsupported design mode.");
                }
                var offer = new TradeOffer { DesignMode = request.DesignMode, OfferId = request.OfferId, SellerAgencyId = client.AgencyId, BuyerAgencyId = request.BuyerAgencyId, VesselId = request.VesselId, Revision = 1, ExpiresUtcTicks = UtcNow().AddHours(24).Ticks, SellerFunds = request.SellerFunds, SellerScience = request.SellerScience, BuyerFunds = request.BuyerFunds, BuyerScience = request.BuyerScience, DesignFingerprint = request.DesignFingerprint };
                var stored = new StoredTradeOffer { Offer = offer, SellerOwner = seller.OwnerUniqueId, BuyerOwner = buyer.OwnerUniqueId, SessionId = command.SessionId, CreatedSequence = command.Sequence };
                if (!string.IsNullOrEmpty(request.DesignFingerprint))
                {
                    var sellerDesign = Agency(candidate, client.AgencyId).Designs.FirstOrDefault(d => d.Fingerprint == request.DesignFingerprint);
                    var blueprintFingerprint = ToolingPolicy.Fingerprint(BlueprintManifest(request.BlueprintData, request.Editor, ScienceClassifier(candidate, command.Manifest, sellerDesign?.Manifest)));
                    switch (request.DesignMode)
                    {
                        case TradeDesignMode.SingleLaunch:
                            // The seller needs no tooling: the manifest only prices the launch the seller prepays.
                            if (command.Manifest == null || ToolingPolicy.ManifestHash(command.Manifest) != command.ManifestHash || ToolingPolicy.Fingerprint(command.Manifest) != request.DesignFingerprint || blueprintFingerprint != request.DesignFingerprint) throw new ArgumentException("Blueprint does not match the priced launch manifest.");
                            var quote = Quote(Agency(candidate, client.AgencyId), command.Manifest, command.ManifestHash);
                            offer.LaunchMultiplier = quote.AlreadyTooled ? Rates().TooledLaunch : Rates().UntooledLaunch;
                            offer.PrepaidLaunchFunds = UsesFunds ? TradePolicy.PrepaidLaunchCost(quote, offer.LaunchMultiplier) : 0;
                            break;
                        case TradeDesignMode.ToolingAndDesign:
                            stored.Design = Copy(Agency(candidate, client.AgencyId).Designs.SingleOrDefault(d => d.Fingerprint == request.DesignFingerprint) ?? throw new InvalidOperationException("Only an existing tooled design can be sold."));
                            if (blueprintFingerprint != request.DesignFingerprint) throw new ArgumentException("Blueprint does not match the offered tooling.");
                            break;
                        case TradeDesignMode.Stock:
                            // No tooling needed: a reseller of bought stock offers the craft file it received. Stock never sets stored.Design.
                            if (blueprintFingerprint != request.DesignFingerprint) throw new ArgumentException("Blueprint does not match the offered stock.");
                            EscrowStock(candidate, client.AgencyId, stored, request.StockUnits);
                            break;
                        default: throw new ArgumentException("Unsupported design mode.");
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
                if (offer.VesselId == Guid.Empty && stored.Design == null && stored.Blueprint.Length == 0 && stored.Escrow.Count == 0 && offer.SellerFunds + offer.SellerScience + offer.BuyerFunds + offer.BuyerScience == 0) throw new ArgumentException("Offer has no assets or currencies.");
                candidate.TradeOffers[offer.OfferId] = stored;
                result.TradeOfferId = offer.OfferId;
                return;
            }
            if (!candidate.TradeOffers.TryGetValue(request.OfferId, out var saved)) throw new InvalidOperationException("Offer no longer exists.");
            var trade = saved.Offer;
            if (!TradeEnabled && !IsOwnStockOffer(trade, client.AgencyId)) throw new InvalidOperationException("Agency trade is unavailable.");
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
                CloseOffer(candidate, saved, command.Operation == EconomyOperation.TradeCancel ? TradeOfferStatus.Cancelled : TradeOfferStatus.Declined, false);
                return;
            }
            if (command.Operation != EconomyOperation.TradeAccept) throw new ArgumentException("Unsupported trade decision.");
            if (!AgencyStore.Agencies.TryGetValue(trade.SellerAgencyId, out var sellerAgency) || !AgencyStore.Agencies.TryGetValue(trade.BuyerAgencyId, out var buyerAgency) || sellerAgency.OwnerUniqueId != saved.SellerOwner || buyerAgency.OwnerUniqueId != saved.BuyerOwner) throw new InvalidOperationException("Agency ownership changed; create a new offer.");
            if ((trade.DesignMode == TradeDesignMode.SingleLaunch || trade.DesignMode == TradeDesignMode.Stock) && !ToolingEnabled) throw new InvalidOperationException(trade.DesignMode == TradeDesignMode.Stock ? "Stock offers need agency tooling." : "Single-launch designs need agency tooling.");
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
            else if (trade.DesignMode == TradeDesignMode.ToolingAndDesign && saved.Design != null)
            {
                if (!sellerBalance.Designs.Any(d => d.Fingerprint == saved.Design.Fingerprint)) throw new InvalidOperationException("Offered tooling no longer exists.");
                if (!buyerBalance.Designs.Any(d => d.Fingerprint == saved.Design.Fingerprint)) buyerBalance.Designs.Add(Copy(saved.Design));
                AddEntitlement(candidate, trade.BuyerAgencyId, new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = trade.SellerAgencyId, Fingerprint = saved.Design.Fingerprint, BlueprintName = trade.BlueprintName, Editor = trade.Editor, BlueprintHash = saved.BlueprintHash, BlueprintData = Copy(saved.Blueprint) });
            }
            else if (trade.DesignMode == TradeDesignMode.Stock)
            {
                TransferEscrow(candidate, saved, buyerBalance);
                AddEntitlement(candidate, trade.BuyerAgencyId, new TradeEntitlement { EntitlementId = Guid.NewGuid(), SellerAgencyId = trade.SellerAgencyId, Fingerprint = trade.DesignFingerprint, BlueprintName = trade.BlueprintName, Editor = trade.Editor, BlueprintHash = saved.BlueprintHash, BlueprintData = Copy(saved.Blueprint), Kind = TradeEntitlementKind.StockDesign });
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
            // Last, after everything that can refuse the accept (R2.2): a refused accept never writes a blueprint file.
            if (trade.DesignMode == TradeDesignMode.ToolingAndDesign && saved.Design != null) CopyToolingBlueprint(candidate, saved, newFiles);
            trade.Status = TradeOfferStatus.Accepted;
            trade.Revision++;
        }

        /// <summary>
        /// Moves <paramref name="units"/> of the offer's design from the seller's stock into the offer's escrow, oldest first, with fresh LotIds.
        /// In Career only funds-built units can be sold. Fails, with nothing committed, when the seller is short or the take needs a 65th lot slot.
        /// </summary>
        private static void EscrowStock(EconomyDocument candidate, Guid sellerId, StoredTradeOffer stored, int units)
        {
            var offer = stored.Offer;
            var seller = Agency(candidate, sellerId);
            var fingerprint = offer.DesignFingerprint;
            if (UsesFunds)
            {
                var all = seller.Stock.Where(l => l.Fingerprint == fingerprint).Sum(l => (long)l.Units);
                var fundsBuilt = seller.Stock.Where(l => l.Fingerprint == fingerprint && l.FundsBuilt).Sum(l => (long)l.Units);
                if (fundsBuilt < units && all >= units) throw new InvalidOperationException((all - fundsBuilt) + " of these units were built without funds and can't be sold in Career.");
            }
            stored.Escrow = StockPolicy.Take(seller.Stock, fingerprint, units, UsesFunds).ToList();
            offer.StockUnits = units;
            offer.StockPrepaidTotal = stored.Escrow.Sum(l => l.Units * l.PrepaidPerUnit);
            if (!ToolingPolicy.FiniteNonNegative(offer.StockPrepaidTotal)) throw new ArgumentException("Stock value exceeds the supported range.");
            // The offer is not in the document yet, so its escrow rows are added to the count here.
            if (StockLotSlots(candidate, sellerId) + stored.Escrow.Count > StockDefaults.MaxLots) throw new InvalidOperationException("Too many stock batches; launch or sell some stock first.");
        }

        /// <summary>Accept of a Stock offer: the escrowed units join the buyer's stock with fresh LotIds and unchanged terms, sourced from the seller.</summary>
        private static void TransferEscrow(EconomyDocument candidate, StoredTradeOffer saved, EconomyAgency buyer)
        {
            var trade = saved.Offer;
            if (UsesFunds && saved.Escrow.Any(l => !l.FundsBuilt)) throw new InvalidOperationException("These units were built without funds and can't be sold in Career.");
            if (StockHeld(candidate, trade.BuyerAgencyId, trade.DesignFingerprint) + trade.StockUnits > StockDefaults.MaxHeldUnits) throw new InvalidOperationException("Buyer cannot hold more of this design.");
            foreach (var lot in saved.Escrow)
                StockPolicy.Merge(buyer.Stock, new DesignStockLot { LotId = Guid.NewGuid(), Fingerprint = lot.Fingerprint, Units = lot.Units, PrepaidPerUnit = lot.PrepaidPerUnit, LaunchMultiplier = lot.LaunchMultiplier, BuilderAgencyId = lot.BuilderAgencyId, SourceAgencyId = trade.SellerAgencyId, FundsBuilt = lot.FundsBuilt, CreatedUtcTicks = lot.CreatedUtcTicks });
            saved.Escrow = new List<DesignStockLot>();
            if (StockLotSlots(candidate, trade.BuyerAgencyId) > StockDefaults.MaxLots) throw new InvalidOperationException("Buyer has too many stock batches; the buyer must launch or sell some stock first.");
        }

        /// <summary>ToolingAndDesign accept: gives the buyer the offer's craft as its tooling blueprint when it has none. Any storage miss skips; it never fails the accept.</summary>
        private static void CopyToolingBlueprint(EconomyDocument candidate, StoredTradeOffer saved, List<string> newFiles)
        {
            var trade = saved.Offer;
            var buyer = Agency(candidate, trade.BuyerAgencyId);
            if (saved.Blueprint.Length == 0 || buyer.Blueprints.ContainsKey(saved.Design.Fingerprint)) return;
            try
            {
                if (!TryStoreBlueprint(candidate, trade.BuyerAgencyId, saved.Design.Fingerprint, saved.Blueprint, trade.Editor, trade.BlueprintName, newFiles ?? new List<string>(), ScienceClassifier(candidate, saved.Design.Manifest), out var reason))
                    LunaLog.Debug($"[Economy] Bought tooling {saved.Design.Fingerprint} kept without a saved craft for {trade.BuyerAgencyId}: {reason}");
            }
            catch (Exception e)
            {
                // TryStoreBlueprint never throws by contract (every storage miss is a skip); this is defence in depth so no storage fault can refuse a paid trade.
                LunaLog.Warning($"[Economy] Bought tooling {saved.Design.Fingerprint} kept without a saved craft for {trade.BuyerAgencyId}: {e.Message}");
            }
        }

        /// <summary>An open Stock offer this agency is selling; the one thing a seller can still see and cancel while trade is switched off.</summary>
        private static bool IsOwnStockOffer(TradeOffer offer, Guid agencyId) => offer.DesignMode == TradeDesignMode.Stock && offer.Status == TradeOfferStatus.Open && offer.SellerAgencyId == agencyId;

        private static TradeOffer[] TradeOffersFor(EconomyDocument document, Guid agencyId)
        {
            var offers = TradeEnabled ? document.TradeOffers.Values.Where(o => o.Offer.SellerAgencyId == agencyId || o.Offer.BuyerAgencyId == agencyId)
                : document.TradeOffers.Values.Where(o => IsOwnStockOffer(o.Offer, agencyId));
            return offers.Select(o =>
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
                if (!Ready) return false;
                var names = new ToolingManifest { Parts = vessel.Parts.GetAllValues().Select(p => new ToolingPart { Name = p.Fields.GetSingle("name")?.Value }).ToArray() };
                EconomyLaunch stockLaunch = null;
                if (launchId != Guid.Empty) _document.Launches.TryGetValue(launchId, out stockLaunch);
                // Science parts are not part of a design: classify the vessel's names (by its own launch manifest when there is one) before matching.
                var manifest = ToolingPolicy.Classify(names, ScienceClassifier(_document, stockLaunch?.Manifest));
                // A stock unit grants research only to the Prepared launch that reserved it, keyed by its lot ID. It needs tooling, not trade.
                if (stockLaunch != null && stockLaunch.Stock != null && stockLaunch.Stock.LotId == entitlementId)
                    return ToolingEnabled && stockLaunch.State == LaunchState.Prepared && stockLaunch.AgencyId == agencyId && TradePolicy.CanUseEntitlement(manifest, new[] { stockLaunch.Stock.Fingerprint });
                if (!TradeEnabled || !_document.Entitlements.TryGetValue(agencyId, out var entitlements)) return false;
                // Permanent: always. SingleLaunch: only for the one launch that reserved it, while tooling gameplay is on. StockDesign: never by itself.
                var allowed = entitlements.Where(e => e.EntitlementId == entitlementId && (e.Kind == TradeEntitlementKind.Permanent || e.Kind == TradeEntitlementKind.SingleLaunch && ToolingEnabled && launchId != Guid.Empty && !e.Redeemed && e.LaunchId == launchId));
                return TradePolicy.CanUseEntitlement(manifest, allowed.Select(e => e.Fingerprint));
            }
        }
    }
}
