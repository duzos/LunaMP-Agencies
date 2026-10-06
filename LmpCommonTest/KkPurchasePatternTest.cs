using System;
using System.Collections.Generic;
using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using I = LmpCommon.Agency.KkPurchasePattern.Instruction;
using K = LmpCommon.Agency.KkPurchasePattern.Kind;

namespace LmpCommonTest
{
    [TestClass]
    public class KkPurchasePatternTest
    {
        private static List<I> Button(string label) => new List<I> { new I(K.Label, label), new I(K.Other), new I(K.Button), new I(K.FalseBranch) };
        [TestMethod]
        public void MatchesOnlyExpectedPurchaseCalls()
        {
            var il = Button("Open Base for \n");
            il.AddRange(Button("unrelated"));
            il.AddRange(Button("Close Base for \n"));
            CollectionAssert.AreEqual(new[] { 2, 10 }, KkPurchasePattern.Match(il, "Open Base for \n", "Close Base for \n"));
            CollectionAssert.AreEqual(new[] { 2 }, KkPurchasePattern.Match(Button("Open Base for "), "Open Base for "));
        }
        [TestMethod]
        public void RefusesMissingDuplicateWrongOverloadAndUnguardedButtons()
        {
            Assert.ThrowsException<InvalidOperationException>(() => KkPurchasePattern.Match(Button("other"), "open"));
            var duplicate = Button("open"); duplicate.AddRange(Button("open"));
            Assert.ThrowsException<InvalidOperationException>(() => KkPurchasePattern.Match(duplicate, "open"));
            var wrong = Button("open"); wrong[2] = new I(K.OtherButton);
            Assert.ThrowsException<InvalidOperationException>(() => KkPurchasePattern.Match(wrong, "open"));
            var unguarded = Button("open"); unguarded[3] = new I(K.Other);
            Assert.ThrowsException<InvalidOperationException>(() => KkPurchasePattern.Match(unguarded, "open"));
        }
        [TestMethod]
        public void SaveScopeRestoresAfterNestedException()
        {
            Assert.IsFalse(KkCareerSaveScope.Active);
            using (KkCareerSaveScope.Enter())
            {
                try { using (KkCareerSaveScope.Enter()) { throw new Exception(); } } catch (Exception) { }
                Assert.IsTrue(KkCareerSaveScope.Active);
            }
            Assert.IsFalse(KkCareerSaveScope.Active);
        }
    }
}
