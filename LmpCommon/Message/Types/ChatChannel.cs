namespace LmpCommon.Message.Types
{
    /// <summary>
    /// Chat scope for ChatMsgData. Global preserves legacy behavior;
    /// Agency restricts delivery to online members of the sender's agency.
    /// </summary>
    public enum ChatChannel : byte
    {
        Global = 0,
        Agency = 1,
    }
}
