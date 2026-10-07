namespace KspControl.Contracts
{
    /// <summary>
    /// Queued editor operations served on the main-thread observation queue. State and Engineering are read-only; the rest
    /// need a lease and are executed as asynchronous jobs after admission (plan R3-section 6.4).
    /// </summary>
    public static class EditorOperations
    {
        public const string State = "editor.state";
        public const string Engineering = "editor.engineering";
        public const string ApplyCraft = "editor.apply_craft";
        public const string RestoreSnapshot = "editor.restore_snapshot";
        public const string SaveCraft = "editor.save_craft";
        public const string OperationStatus = "editor.operation_status";
        /// <summary>Operations that change the editor. They are reachable only through the host journal.</summary>
        public static readonly string[] Mutations = { ApplyCraft, RestoreSnapshot, SaveCraft };
    }

    /// <summary>Read-only craft-file operations served on the main-thread observation queue.</summary>
    public static class CraftOperations
    {
        /// <summary>The ship files of the current save's Ships folder, with size, write time, hash and ledger ownership.</summary>
        public const string List = "craft.list";
    }
}
