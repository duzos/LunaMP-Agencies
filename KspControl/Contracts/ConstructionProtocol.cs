namespace KspControl.Contracts
{
    /// <summary>Read-only construction catalog operation served by the bridge on the main-thread observation queue.</summary>
    public static class ConstructionOperations
    {
        public const string Catalog = "parts.construction_catalog";
    }

    /// <summary>Bounds shared by the host craft_plan tool and the bridge catalog operation.</summary>
    public static class ConstructionLimits
    {
        /// <summary>Most part names accepted by one parts.construction_catalog call.</summary>
        public const int MaxCatalogParts = 32;
        public const int MaxPartNameLength = 64;
        /// <summary>craft_plan graph JSON limit in UTF-8 bytes (plan R1-section 4).</summary>
        public const int MaxGraphBytes = 256 * 1024;
        public const int MaxGraphParts = 250;
    }
}
