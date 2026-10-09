using LmpCommon.Agency;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Agency
{
    public static partial class AgencyEconomyStore
    {
        /// <summary>
        /// Rewrites fingerprints stored by builds before agencies.11, which counted science parts, to the current science-free fingerprint
        /// (<see cref="ToolingPolicy.Fingerprint"/>). Runs on every Load before validation and is idempotent. A design without science parts has the
        /// same fingerprint under both rules, so only records of designs that carry science parts change.
        /// <para>
        /// The old-to-new map is learned from every stored manifest (designs, offered designs, launch manifests), then from stored craft files whose
        /// old fingerprint matches a record no manifest explains. It is global: a part's science category is the same for every agency. Designs of one
        /// agency that now share a fingerprint (the same craft tooled with different science parts) collapse into the first, and their saved craft refs
        /// keep the newest. Stock lots, escrow, offers, vouchers, licences and stock launch terms are re-keyed; their funds are untouched.
        /// </para>
        /// </summary>
        internal static bool MigrateScienceFingerprints(EconomyDocument d)
        {
            if (d?.Agencies == null) return false;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            void Learn(ToolingManifest manifest)
            {
                if (manifest == null) return;
                try
                {
                    var legacy = ToolingPolicy.LegacyFingerprint(manifest);
                    var current = ToolingPolicy.Fingerprint(manifest);
                    if (legacy != current) map[legacy] = current;
                }
                catch (ArgumentException) { }
            }
            foreach (var agency in d.Agencies.Values)
                if (agency?.Designs != null) foreach (var design in agency.Designs) Learn(design?.Manifest);
            foreach (var offer in (d.TradeOffers ?? new Dictionary<Guid, StoredTradeOffer>()).Values) Learn(offer?.Design?.Manifest);
            foreach (var launch in (d.Launches ?? new Dictionary<Guid, EconomyLaunch>()).Values) Learn(launch?.Manifest);

            // Records with no manifest behind them (a bought licence, a resold stock offer) still carry the craft file the fingerprint came from.
            var classifier = ScienceClassifier(d);
            void LearnBlueprint(string fingerprint, byte[] bytes, string editor)
            {
                if (string.IsNullOrEmpty(fingerprint) || map.ContainsKey(fingerprint) || bytes == null || bytes.Length == 0) return;
                try
                {
                    var manifest = BlueprintManifest(bytes, editor, classifier);
                    if (ToolingPolicy.LegacyFingerprint(manifest) == fingerprint && ToolingPolicy.Fingerprint(manifest) != fingerprint) map[fingerprint] = ToolingPolicy.Fingerprint(manifest);
                }
                catch (Exception) { }
            }
            foreach (var offer in (d.TradeOffers ?? new Dictionary<Guid, StoredTradeOffer>()).Values)
                if (offer?.Offer != null) LearnBlueprint(offer.Offer.DesignFingerprint, offer.Blueprint, offer.Offer.Editor);
            foreach (var held in (d.Entitlements ?? new Dictionary<Guid, List<TradeEntitlement>>()).Values)
                if (held != null) foreach (var entitlement in held) if (entitlement != null) LearnBlueprint(entitlement.Fingerprint, entitlement.BlueprintData, entitlement.Editor);
            if (map.Count == 0) return false;

            var changed = false;
            string Map(string fingerprint)
            {
                if (fingerprint == null || !map.TryGetValue(fingerprint, out var current)) return fingerprint;
                changed = true;
                return current;
            }
            void MapDesign(ToolingDesign design)
            {
                // Only a fingerprint that is exactly the old rule's digest of its own manifest is rewritten; anything else is left for Validate to refuse.
                if (design?.Manifest == null) return;
                try
                {
                    if (design.Fingerprint == ToolingPolicy.LegacyFingerprint(design.Manifest)) design.Fingerprint = Map(design.Fingerprint);
                }
                catch (ArgumentException) { }
            }
            void MapLots(IEnumerable<DesignStockLot> lots)
            {
                if (lots == null) return;
                foreach (var lot in lots) if (lot != null) lot.Fingerprint = Map(lot.Fingerprint);
            }

            foreach (var agency in d.Agencies.Values)
            {
                if (agency == null) continue;
                if (agency.Designs != null)
                {
                    foreach (var design in agency.Designs) MapDesign(design);
                    var kept = new List<ToolingDesign>();
                    foreach (var design in agency.Designs)
                    {
                        var first = design == null ? null : kept.FirstOrDefault(k => k != null && k.Fingerprint == design.Fingerprint);
                        if (first == null) { kept.Add(design); continue; }
                        if (string.IsNullOrWhiteSpace(first.Name) && !string.IsNullOrWhiteSpace(design.Name)) first.Name = design.Name;
                        changed = true;
                    }
                    agency.Designs = kept;
                }
                MapLots(agency.Stock);
                if (agency.Blueprints != null && agency.Blueprints.Count > 0)
                {
                    var rekeyed = new Dictionary<string, ToolingBlueprintRef>(StringComparer.Ordinal);
                    foreach (var entry in agency.Blueprints)
                    {
                        var key = Map(entry.Key);
                        if (entry.Value != null) entry.Value.Fingerprint = key;
                        if (rekeyed.TryGetValue(key, out var other) && other != null && (entry.Value == null || other.SavedUtcTicks >= entry.Value.SavedUtcTicks)) { changed = true; continue; }
                        if (rekeyed.ContainsKey(key)) changed = true;
                        rekeyed[key] = entry.Value;
                    }
                    agency.Blueprints = rekeyed;
                }
            }
            foreach (var offer in (d.TradeOffers ?? new Dictionary<Guid, StoredTradeOffer>()).Values)
            {
                if (offer == null) continue;
                if (offer.Offer != null) offer.Offer.DesignFingerprint = Map(offer.Offer.DesignFingerprint);
                MapDesign(offer.Design);
                MapLots(offer.Escrow);
            }
            foreach (var held in (d.Entitlements ?? new Dictionary<Guid, List<TradeEntitlement>>()).Values)
                if (held != null) foreach (var entitlement in held) if (entitlement != null) entitlement.Fingerprint = Map(entitlement.Fingerprint);
            foreach (var launch in (d.Launches ?? new Dictionary<Guid, EconomyLaunch>()).Values)
                if (launch?.Stock != null) launch.Stock.Fingerprint = Map(launch.Stock.Fingerprint);
            return changed;
        }
    }
}
