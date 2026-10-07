using System;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LmpCommonTest
{
    [TestClass]
    public class VisibilityContactTrackerTest
    {
        private sealed class Snapshot { public double Position; }
        private static VisibilityContactTracker<Snapshot> Tracker(double classify = 2, double expire = 10, int max = 10000) =>
            new VisibilityContactTracker<Snapshot>(s => new Snapshot { Position = s.Position }, classify, expire, max);
        private static VisibilityContactView<Snapshot> View(VisibilityContactTracker<Snapshot> tracker, double now) => tracker.GetViews(now, 128)[0];

        [TestMethod]
        public void ClassificationCreditsOnlyConsecutiveActualObservationAndReacquisitionKeepsKnowledge()
        {
            var tracker = Tracker(); var vessel = Guid.NewGuid(); var owner = Guid.NewGuid(); var s = new Snapshot();
            tracker.Observe(vessel, owner, 0, s, 0, 0, false);
            var id = View(tracker, 0).ContactId;
            tracker.Observe(vessel, owner, 0, s, 1, 1, false);
            Assert.AreEqual(.5, View(tracker, 1).Progress);
            tracker.Lose(vessel, 1);
            tracker.Observe(vessel, owner, 0, s, 5, 4, false);
            Assert.AreEqual(.5, View(tracker, 5).Progress, "Blind gap cannot earn observation credit.");
            Assert.AreEqual(id, View(tracker, 5).ContactId);
            tracker.Observe(vessel, owner, 0, s, 6, 1, false);
            Assert.AreEqual(ContactIdentificationTier.Classified, View(tracker, 6).Tier);
            Assert.IsFalse(tracker.IsIdentifiedAndDetected(vessel, 6));
            tracker.Observe(vessel, owner, 0, s, 6.5, .5, true);
            Assert.IsTrue(tracker.IsIdentifiedAndDetected(vessel, 6.5));
            tracker.Observe(vessel, owner, 0, s, 7, .5, false);
            Assert.AreEqual(ContactIdentificationTier.Identified, View(tracker, 7).Tier);
        }

        [TestMethod]
        public void FirstSampleLongGapsAndExcessCreditCannotAdvanceProgress()
        {
            var tracker = Tracker(); var vessel = Guid.NewGuid(); var owner = Guid.NewGuid(); var s = new Snapshot();
            tracker.Observe(vessel, owner, 0, s, 0, 100, false);
            tracker.Observe(vessel, owner, 0, s, 2, 2, false);
            tracker.Observe(vessel, owner, 0, s, 2.5, 1, false);
            Assert.AreEqual(0d, View(tracker, 2.5).Progress);
            tracker.LoseAll(2.5);
            tracker.Observe(vessel, owner, 0, s, 3, .5, false);
            Assert.AreEqual(0d, View(tracker, 3).Progress);
        }

        [TestMethod]
        public void LostSnapshotsAreDetachedFadeAndExpireWithoutCurrentPositionReads()
        {
            var tracker = Tracker(); var vessel = Guid.NewGuid(); var owner = Guid.NewGuid(); var s = new Snapshot { Position = 10 };
            tracker.Observe(vessel, owner, 0, s, 0, 0, true);
            var id = View(tracker, 0).ContactId;
            s.Position = 99;
            tracker.Lose(vessel, .1);
            var view = View(tracker, 5);
            Assert.AreEqual(10d, view.Snapshot.Position);
            Assert.AreEqual(.5, view.Fade);
            Assert.IsFalse(view.IsDetected);
            view.Snapshot.Position = 100;
            Assert.AreEqual(10d, View(tracker, 5).Snapshot.Position);
            Assert.AreEqual(0, tracker.GetViews(10, 0).Length);
            Assert.AreEqual(0, tracker.Count, "Expiry is independent of render cap.");
            tracker.Observe(vessel, owner, 0, s, 10, 0, false);
            Assert.AreNotEqual(id, View(tracker, 10).ContactId);
        }

        [TestMethod]
        public void FreshnessStopsNativeDisclosureEvenWithoutLossCallback()
        {
            var tracker = Tracker(); var vessel = Guid.NewGuid(); var owner = Guid.NewGuid();
            tracker.Observe(vessel, owner, 0, new Snapshot(), 0, 0, true);
            Assert.IsTrue(tracker.IsIdentifiedAndDetected(vessel, 1));
            Assert.IsFalse(tracker.IsIdentifiedAndDetected(vessel, 1.01));
            Assert.IsFalse(View(tracker, 1.01).IsDetected);
        }

        [TestMethod]
        public void OwnershipChangesDeletionResetAndCapacityRemovePriorKnowledge()
        {
            var tracker = Tracker(max: 2); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var owner = Guid.NewGuid(); var s = new Snapshot();
            tracker.Observe(a, owner, 0, s, 0, 0, true); var original = View(tracker, 0).ContactId;
            tracker.Observe(a, owner, 1, s, 1, 1, false);
            Assert.AreNotEqual(original, View(tracker, 1).ContactId);
            Assert.AreEqual(ContactIdentificationTier.Anonymous, View(tracker, 1).Tier);
            tracker.Observe(b, owner, 0, s, 2, 0, true);
            tracker.Observe(c, owner, 0, s, 3, 0, true);
            Assert.AreEqual(2, tracker.Count);
            Assert.IsFalse(tracker.IsIdentifiedAndDetected(a, 3));
            tracker.ReconcileOwnership((id, agency, revision) => id == c);
            Assert.AreEqual(1, tracker.Count);
            tracker.Clear(); Assert.AreEqual(0, tracker.Count);
            Assert.IsTrue(tracker.Observe(a, owner, 0, s, 0, 0, false), "New session may restart monotonic origin.");
        }

        [TestMethod]
        public void RenderLimitAppliesBeforeCloningAndSnapshotsAreMostRecentFirst()
        {
            var copies = 0;
            var tracker = new VisibilityContactTracker<Snapshot>(s => { copies++; return new Snapshot { Position = s.Position }; }, 600, 600);
            var owner = Guid.NewGuid();
            for (var i = 0; i < 200; i++) tracker.Observe(Guid.NewGuid(), owner, 0, new Snapshot { Position = i }, i, 0, false);
            copies = 0;
            var views = tracker.GetViews(199, 3);
            Assert.AreEqual(3, copies); Assert.AreEqual(3, views.Length); Assert.AreEqual(199d, views[0].Snapshot.Position);
            copies = 0; Assert.AreEqual(128, tracker.GetViews(199, int.MaxValue).Length); Assert.AreEqual(128, copies);
        }

        [TestMethod]
        public void InvalidOrBackwardObservationCannotMutateKnowledge()
        {
            var tracker = Tracker(); var vessel = Guid.NewGuid(); var owner = Guid.NewGuid(); var s = new Snapshot { Position = 1 };
            tracker.Observe(vessel, owner, 0, s, 5, 0, false);
            foreach (var time in new[] { 4d, -1, double.NaN, double.PositiveInfinity })
                Assert.IsFalse(tracker.Observe(vessel, owner, 0, new Snapshot { Position = 99 }, time, 0, true));
            Assert.IsFalse(tracker.Observe(vessel, owner, 0, s, 5, double.NaN, true));
            Assert.IsFalse(tracker.Observe(Guid.Empty, owner, 0, s, 5, 0, true));
            Assert.AreEqual(1d, View(tracker, 5).Snapshot.Position);
            Assert.AreEqual(ContactIdentificationTier.Anonymous, View(tracker, 5).Tier);
        }
    }
}
