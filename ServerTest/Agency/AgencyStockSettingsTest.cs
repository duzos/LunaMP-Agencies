using LmpCommon.Agency;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Server.Agency;
using Server.Settings;
using Server.Settings.Definition;
using Server.Settings.Structures;
using System;

namespace ServerTest.Agency
{
    [TestClass, DoNotParallelize]
    public class AgencyStockSettingsTest
    {
        [TestMethod]
        public void ServerSettingsDefaultToTheShippedStockRates()
        {
            var settings = new GeneralSettingsDefinition();
            Assert.AreEqual(StockDefaults.MaxDiscount, settings.StockMaxDiscount);
            Assert.AreEqual(StockDefaults.FullDiscountUnits, settings.StockFullDiscountUnits);
        }

        [DataTestMethod]
        [DataRow(.5, 10)]
        [DataRow(-.1, 10)]
        [DataRow(double.NaN, 10)]
        [DataRow(.3, 1)]
        [DataRow(.3, 1001)]
        public void InvalidStockSettingsResetBothValuesInMemory(double discount, int units)
        {
            var settings = GeneralSettings.SettingsStore;
            var oldDiscount = settings.StockMaxDiscount; var oldUnits = settings.StockFullDiscountUnits;
            try
            {
                settings.StockMaxDiscount = discount; settings.StockFullDiscountUnits = units;
                SettingsHandler.ValidateStockSettings();
                Assert.AreEqual(StockDefaults.MaxDiscount, settings.StockMaxDiscount);
                Assert.AreEqual(StockDefaults.FullDiscountUnits, settings.StockFullDiscountUnits);
            }
            finally { settings.StockMaxDiscount = oldDiscount; settings.StockFullDiscountUnits = oldUnits; }
        }

        [TestMethod]
        public void ValidStockSettingsAreKept()
        {
            var settings = GeneralSettings.SettingsStore;
            var oldDiscount = settings.StockMaxDiscount; var oldUnits = settings.StockFullDiscountUnits;
            try
            {
                settings.StockMaxDiscount = .45; settings.StockFullDiscountUnits = 50;
                SettingsHandler.ValidateStockSettings();
                Assert.AreEqual(.45, settings.StockMaxDiscount);
                Assert.AreEqual(50, settings.StockFullDiscountUnits);
            }
            finally { settings.StockMaxDiscount = oldDiscount; settings.StockFullDiscountUnits = oldUnits; }
        }

        [TestMethod]
        public void VersionTwoRowsLoadWithEmptyStockState()
        {
            Assert.AreEqual(3, EconomyDocument.CurrentVersion);
            var agency = JsonConvert.DeserializeObject<EconomyAgency>("{\"Funds\":10.0,\"Science\":1.0,\"Designs\":[]}");
            Assert.IsNotNull(agency.Stock); Assert.AreEqual(0, agency.Stock.Count);
            Assert.IsNotNull(agency.Blueprints); Assert.AreEqual(0, agency.Blueprints.Count);
            var launch = JsonConvert.DeserializeObject<EconomyLaunch>("{\"LaunchId\":\"" + Guid.NewGuid() + "\"}");
            Assert.IsNull(launch.Stock);
            var offer = JsonConvert.DeserializeObject<StoredTradeOffer>("{\"Offer\":{}}");
            Assert.IsNotNull(offer.Escrow); Assert.AreEqual(0, offer.Escrow.Count);
        }
    }
}
