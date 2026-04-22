namespace LmpCommon.Agency
{
    /// <summary>
    /// Which career resource is being transferred between agencies.
    /// Intentionally small — reputation transfer tends to be a gameplay-balance
    /// landmine so we leave it out until there's a demand for it.
    /// </summary>
    public enum ResourceKind : byte
    {
        Funds = 0,
        Science = 1,
    }
}
