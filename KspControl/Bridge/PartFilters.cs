namespace KspControl.Bridge
{
    /// <summary>Pure predicates over primitives so they can be unit-tested without Unity or KSP assemblies.</summary>
    public static class PartFilters
    {
        public static bool IsBuildable(bool categoryNone, string techRequired, bool techHidden) =>
            !categoryNone && !techHidden && !string.IsNullOrEmpty(techRequired) && techRequired != "Unresearcheable";
    }
}
