using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Types;
using Server.Client;
using Server.Log;

namespace Server.Agency
{
    /// <summary>
    /// Dispatch admin cheat/management operations for agencies.
    /// Invoked by <see cref="Message.AgencyMsgReader"/> after the admin
    /// password check, and also by the server console commands so the
    /// two entrypoints share identical semantics.
    /// </summary>
    public static class AdminOps
    {
        public static (bool Success, string Message) Dispatch(ClientStructure actor, AgencyAdminOpMsgData data)
        {
            var actorLabel = actor != null ? $"{actor.PlayerName}({actor.UniqueIdentifier})" : "console";
            LunaLog.Info($"[Agency][Admin] Op={data.Op} target={data.TargetAgencyId} string='{data.StringArg}' num={data.NumericArg} actor={actorLabel}");

            switch (data.Op)
            {
                case AgencyAdminOp.Delete:
                    return AgencySystem.DeleteAgency(data.TargetAgencyId, actor?.UniqueIdentifier ?? "console", isAdmin: true, force: true);

                case AgencyAdminOp.Rename:
                    return AgencySystem.RenameAgency(data.TargetAgencyId, actor?.UniqueIdentifier ?? "console", data.StringArg, isAdmin: true);

                case AgencyAdminOp.MoveMember:
                    return AgencySystem.AdminMoveMember(data.StringArg, data.TargetAgencyId);

                case AgencyAdminOp.RemoveMember:
                    return AgencySystem.KickMember(data.TargetAgencyId, actor?.UniqueIdentifier ?? "console", data.StringArg, isAdmin: true);

                case AgencyAdminOp.TransferOwner:
                    return AgencySystem.TransferOwner(data.TargetAgencyId, actor?.UniqueIdentifier ?? "console", data.StringArg, isAdmin: true);

                case AgencyAdminOp.SetOwner:
                    return AgencySystem.AdminSetOwner(data.TargetAgencyId, data.StringArg);

                case AgencyAdminOp.SetFunds:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        AgencySystem.SetAgencyFunds(a, data.NumericArg, "admin set");
                        AgencyScenarioUpdater.WriteFunds(a.Id, data.NumericArg);
                        AgencyFanout.PushFundsToMembers(a, data.NumericArg, "admin cheat");
                        return (true, $"Funds set to {data.NumericArg} for '{a.Name}'.");
                    }

                case AgencyAdminOp.SetScience:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        var v = (float)data.NumericArg;
                        AgencySystem.SetAgencyScience(a, v, "admin set");
                        AgencyScenarioUpdater.WriteScience(a.Id, v);
                        AgencyFanout.PushScienceToMembers(a, v);
                        return (true, $"Science set to {v} for '{a.Name}'.");
                    }

                case AgencyAdminOp.SetReputation:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        var v = (float)data.NumericArg;
                        AgencySystem.SetAgencyReputation(a, v, "admin set");
                        AgencyScenarioUpdater.WriteReputation(a.Id, v);
                        AgencyFanout.PushReputationToMembers(a, v);
                        return (true, $"Reputation set to {v} for '{a.Name}'.");
                    }

                case AgencyAdminOp.UnlockTechNode:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        var ok = AgencyScenarioUpdater.ForceUnlockTech(a.Id, data.StringArg);
                        if (ok)
                        {
                            AgencySystem.IncrementUnlockedTech(a, 1);
                            LunaLog.Info($"[Agency][Admin] UnlockTechNode agency='{a.Name}' node='{data.StringArg}'");
                            return (true, $"Tech '{data.StringArg}' unlocked for '{a.Name}'. Members must reconnect to see it in the R&D UI.");
                        }
                        return (false, $"Tech '{data.StringArg}' already unlocked or not found.");
                    }

                case AgencyAdminOp.GrantAllTech:
                    return (false, "GrantAllTech requires the full tech tree list, not implemented in MVP. Use UnlockTechNode repeatedly or unlock via CLI.");

                case AgencyAdminOp.CompleteContract:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        var ok = AgencyScenarioUpdater.ForceCompleteContract(a.Id, data.StringArg);
                        return ok ? (true, $"Contract {data.StringArg} marked completed.") : (false, "Contract not found.");
                    }

                case AgencyAdminOp.CancelContract:
                    {
                        var a = AgencySystem.GetAgency(data.TargetAgencyId);
                        if (a == null) return (false, "Agency not found.");
                        var ok = AgencyScenarioUpdater.ForceCancelContract(a.Id, data.StringArg);
                        return ok ? (true, $"Contract {data.StringArg} cancelled.") : (false, "Contract not found.");
                    }

                default:
                    return (false, $"Unknown admin op {data.Op}.");
            }
        }
    }
}
