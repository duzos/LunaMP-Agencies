namespace KspControl.Contracts
{
    /// <summary>Queued, read-only editor observations. Neither takes a lease, and neither changes the game.</summary>
    public static class EditorOperations
    {
        public const string State = "editor.state";
        public const string Engineering = "editor.engineering";
    }
}
