using System;
using System.Collections.Generic;
using System.Linq;

namespace LmpCommon.Agency
{
    public enum ContactIdentificationTier { Anonymous, Classified, Identified }

    public static class VisibilityContactSettings
    {
        public const double ClassificationSeconds = 600, IdentificationDistance = 2500, ExpirySeconds = 600;
        public const double ActiveDetectionRangeMultiplier = 3;
        public static double NormalizeClassificationSeconds(double value) => Normalize(value, ClassificationSeconds, 2592000);
        public static double NormalizeIdentificationDistance(double value) => Normalize(value, IdentificationDistance, 1e9);
        public static double NormalizeExpirySeconds(double value) => Normalize(value, ExpirySeconds, 2592000);
        public static double NormalizeActiveDetectionRangeMultiplier(double value) =>
            !double.IsNaN(value) && value >= 1 && value <= 100 ? value : ActiveDetectionRangeMultiplier;
        private static double Normalize(double value, double fallback, double maximum) =>
            !double.IsNaN(value) && value > 0 && value <= maximum ? value : fallback;
    }

    public sealed class VisibilityContactView<TSnapshot>
    {
        public Guid ContactId { get; internal set; }
        public TSnapshot Snapshot { get; internal set; }
        public ContactIdentificationTier Tier { get; internal set; }
        public double Progress { get; internal set; }
        public bool IsDetected { get; internal set; }
        public double AgeSeconds { get; internal set; }
        public double Fade { get; internal set; }
    }

    /// <summary>Session-local knowledge. Times are monotonic real seconds, never universal/game time.</summary>
    public sealed class VisibilityContactTracker<TSnapshot> where TSnapshot : class
    {
        public const double FreshnessSeconds = 1;
        public const int MaximumViews = 128;
        private sealed class Contact
        {
            internal Guid Id, Owner;
            internal long Revision;
            internal TSnapshot Snapshot;
            internal double LastSeen, ObservedSeconds;
            internal bool Detected;
            internal ContactIdentificationTier Tier;
        }
        private readonly Dictionary<Guid, Contact> contacts = new Dictionary<Guid, Contact>();
        private readonly Func<TSnapshot, TSnapshot> clone;
        private readonly double classificationSeconds, expirySeconds;
        private readonly int maximumContacts;
        private double clock = -1;
        private double nextExpirySweep;

        public VisibilityContactTracker(Func<TSnapshot, TSnapshot> clone, double classificationSeconds,
            double expirySeconds, int maximumContacts = 10000)
        {
            this.clone = clone ?? throw new ArgumentNullException(nameof(clone));
            this.classificationSeconds = VisibilityContactSettings.NormalizeClassificationSeconds(classificationSeconds);
            this.expirySeconds = VisibilityContactSettings.NormalizeExpirySeconds(expirySeconds);
            this.maximumContacts = Math.Max(1, Math.Min(10000, maximumContacts));
        }

        public int Count => contacts.Count;
        private bool Advance(double now)
        {
            if (double.IsNaN(now) || double.IsInfinity(now) || now < 0 || now < clock) return false;
            clock = now;
            return true;
        }
        private void Expire(double now, bool force)
        {
            if (!force && now < nextExpirySweep) return;
            nextExpirySweep = now + .5;
            foreach (var id in contacts.Where(p => now - p.Value.LastSeen >= expirySeconds).Select(p => p.Key).ToArray())
                contacts.Remove(id);
        }

        public bool Observe(Guid vesselId, Guid ownerId, long ownershipRevision, TSnapshot snapshot,
            double nowSeconds, double detectedSeconds, bool closeIdentified)
        {
            if (vesselId == Guid.Empty || ownerId == Guid.Empty || ownershipRevision < 0 || snapshot == null ||
                double.IsNaN(detectedSeconds) || double.IsInfinity(detectedSeconds) || detectedSeconds < 0 ||
                !Advance(nowSeconds)) return false;
            var copy = clone(snapshot);
            if (copy == null) return false;
            Expire(nowSeconds, false);
            if (!contacts.TryGetValue(vesselId, out var contact) || contact.Owner != ownerId || contact.Revision != ownershipRevision || nowSeconds - contact.LastSeen >= expirySeconds)
            {
                contacts.Remove(vesselId);
                if (contacts.Count >= maximumContacts)
                    contacts.Remove(contacts.OrderBy(p => p.Value.LastSeen).ThenBy(p => p.Value.Id).First().Key);
                contact = new Contact { Id = Guid.NewGuid(), Owner = ownerId, Revision = ownershipRevision, LastSeen = nowSeconds };
                contacts[vesselId] = contact;
            }
            var interval = nowSeconds - contact.LastSeen;
            if (contact.Detected && interval <= FreshnessSeconds && detectedSeconds <= interval)
                contact.ObservedSeconds = Math.Min(classificationSeconds, contact.ObservedSeconds + detectedSeconds);
            contact.LastSeen = nowSeconds;
            contact.Detected = true;
            contact.Snapshot = copy;
            if (closeIdentified) contact.Tier = ContactIdentificationTier.Identified;
            else if (contact.Tier == ContactIdentificationTier.Anonymous && contact.ObservedSeconds >= classificationSeconds)
                contact.Tier = ContactIdentificationTier.Classified;
            return true;
        }

        public void Lose(Guid vesselId, double nowSeconds)
        {
            if (Advance(nowSeconds) && contacts.TryGetValue(vesselId, out var contact)) contact.Detected = false;
        }

        public void LoseAll(double nowSeconds)
        {
            if (!Advance(nowSeconds)) return;
            foreach (var contact in contacts.Values) contact.Detected = false;
        }

        public bool IsIdentifiedAndDetected(Guid vesselId, double nowSeconds) =>
            Advance(nowSeconds) && contacts.TryGetValue(vesselId, out var contact) && contact.Detected &&
            nowSeconds - contact.LastSeen <= FreshnessSeconds && nowSeconds - contact.LastSeen < expirySeconds && contact.Tier == ContactIdentificationTier.Identified;

        public VisibilityContactView<TSnapshot>[] GetViews(double nowSeconds, int maximumViews)
        {
            if (!Advance(nowSeconds)) return Array.Empty<VisibilityContactView<TSnapshot>>();
            Expire(nowSeconds, true);
            // Expiration always covers every contact; expensive detached snapshots only cover the render budget.
            return contacts.Values.OrderByDescending(c => c.LastSeen).ThenBy(c => c.Id)
                .Take(Math.Max(0, Math.Min(MaximumViews, maximumViews))).Select(c =>
                {
                    var age = nowSeconds - c.LastSeen;
                    var detected = c.Detected && age <= FreshnessSeconds;
                    return new VisibilityContactView<TSnapshot> { ContactId = c.Id, Snapshot = clone(c.Snapshot), Tier = c.Tier,
                        Progress = c.Tier == ContactIdentificationTier.Identified ? 1 : c.ObservedSeconds / classificationSeconds,
                        IsDetected = detected, AgeSeconds = age, Fade = detected ? 1 : Math.Max(0, 1 - age / expirySeconds) };
                }).ToArray();
        }

        public void ReconcileOwnership(Func<Guid, Guid, long, bool> stillOwned)
        {
            if (stillOwned == null) throw new ArgumentNullException(nameof(stillOwned));
            foreach (var id in contacts.Where(p => !stillOwned(p.Key, p.Value.Owner, p.Value.Revision)).Select(p => p.Key).ToArray())
                contacts.Remove(id);
        }

        public void Forget(Guid vesselId) { contacts.Remove(vesselId); }

        public void Clear() { contacts.Clear(); clock = -1; nextExpirySweep = 0; }
    }
}
