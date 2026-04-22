namespace LmpCommon.Message.Types
{
    /// <summary>
    /// Operations the admin UI or admin CLI can dispatch via AgencyMessageType.CliAdminOp.
    /// Mirrors the AgencyAdminOpMsgData payload.
    /// </summary>
    public enum AgencyAdminOp : byte
    {
        Delete = 0,
        Rename = 1,
        MoveMember = 2,
        RemoveMember = 3,
        TransferOwner = 4,
        /// <summary>
        /// Admin-forced owner change. Unlike <see cref="TransferOwner"/>, this
        /// ops bypasses the "target must already be a member" rule — if the
        /// target isn't a member the server auto-joins them first.
        /// </summary>
        SetOwner = 5,
        SetFunds = 10,
        SetScience = 11,
        SetReputation = 12,
        UnlockTechNode = 20,
        GrantAllTech = 21,
        CompleteContract = 30,
        CancelContract = 31,
    }
}
