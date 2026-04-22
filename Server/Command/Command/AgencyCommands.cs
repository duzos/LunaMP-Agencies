using LmpCommon.Message.Types;
using Server.Agency;
using Server.Command.Command.Base;
using Server.Log;
using System;
using System.Linq;

namespace Server.Command.Command
{
    /// <summary>
    /// Console commands for agency management and cheats. Each is a
    /// <see cref="SimpleCommand"/> registered in CommandHandler — invoke with
    /// e.g. <c>/listagencies</c>.
    ///
    /// Cheat commands (set funds/science/rep, unlock tech) go through the
    /// same authoritative path as the admin-UI cheat subpanel and network
    /// messages, so console and GUI stay consistent.
    /// </summary>
    public static class AgencyCmdHelpers
    {
        public static bool TryResolveAgency(string token, out Agency.Agency agency)
        {
            agency = null;
            if (string.IsNullOrWhiteSpace(token)) return false;

            if (Guid.TryParse(token, out var id) && AgencyStore.Agencies.TryGetValue(id, out agency))
                return true;

            agency = AgencySystem.GetAgencyByName(token);
            if (agency != null) return true;

            // Match on short N-format ids pasted from logs.
            agency = AgencyStore.Agencies.Values.FirstOrDefault(a =>
                a.Id.ToString("N").StartsWith(token, StringComparison.OrdinalIgnoreCase));
            return agency != null;
        }
    }

    public class ListAgenciesCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var all = AgencyStore.Agencies.Values.OrderBy(a => a.Name).ToArray();
            LunaLog.Normal($"== Agencies ({all.Length}) ==");
            foreach (var a in all)
            {
                var solo = a.IsSolo ? " [solo]" : string.Empty;
                LunaLog.Normal($"  {a.Id.ToString("N").Substring(0, 8)} '{a.Name}'{solo} owner={a.OwnerDisplayName} members={a.Members.Count} funds={a.Funds:N0} sci={a.Science:N1} rep={a.Reputation:N1}");
            }
            return true;
        }
    }

    public class CreateAgencyCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                LunaLog.Error("Usage: /createagency <name> [ownerUniqueId] [ownerDisplayName]");
                return false;
            }
            var parts = commandArgs.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
            var name = parts[0];
            var owner = parts.Length > 1 ? parts[1] : "console";
            var ownerName = parts.Length > 2 ? parts[2] : "console";
            var (ok, msg, _) = AgencySystem.CreateAgency(name, owner, ownerName);
            LunaLog.Normal($"createagency: {msg}");
            return ok;
        }
    }

    public class DeleteAgencyCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                LunaLog.Error("Usage: /deleteagency <name|id> [--force]");
                return false;
            }
            var parts = commandArgs.Split(' ');
            var force = parts.Any(p => p == "--force");
            if (!AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Agency not found.");
                return false;
            }
            var (ok, msg) = AgencySystem.DeleteAgency(agency.Id, "console", isAdmin: true, force);
            LunaLog.Normal($"deleteagency: {msg}");
            return ok;
        }
    }

    public class RenameAgencyCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                LunaLog.Error("Usage: /renameagency <name|id> <newName>");
                return false;
            }
            var parts = commandArgs.Split(new[] { ' ' }, 2);
            if (parts.Length < 2)
            {
                LunaLog.Error("Usage: /renameagency <name|id> <newName>");
                return false;
            }
            if (!AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Agency not found.");
                return false;
            }
            var (ok, msg) = AgencySystem.RenameAgency(agency.Id, "console", parts[1], isAdmin: true);
            LunaLog.Normal($"renameagency: {msg}");
            return ok;
        }
    }

    public class MovePlayerAgencyCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (string.IsNullOrWhiteSpace(commandArgs))
            {
                LunaLog.Error("Usage: /moveplayeragency <playerUniqueId> <agency>");
                return false;
            }
            var parts = commandArgs.Split(' ', 2);
            if (parts.Length < 2 || !AgencyCmdHelpers.TryResolveAgency(parts[1], out var agency))
            {
                LunaLog.Error("Agency not found.");
                return false;
            }
            var (ok, msg) = AgencySystem.AdminMoveMember(parts[0], agency.Id);
            LunaLog.Normal($"moveplayeragency: {msg}");
            return ok;
        }
    }

    public class TransferAgencyOwnerCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2)
            {
                LunaLog.Error("Usage: /transferagencyowner <name|id> <newOwnerUniqueId>");
                return false;
            }
            if (!AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Agency not found.");
                return false;
            }
            var (ok, msg) = AgencySystem.TransferOwner(agency.Id, "console", parts[1], isAdmin: true);
            LunaLog.Normal($"transferagencyowner: {msg}");
            return ok;
        }
    }

    public class SetAgencyFundsCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !double.TryParse(parts[1], out var funds)
                || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /setagencyfunds <name|id> <value>");
                return false;
            }
            AgencySystem.SetAgencyFunds(agency, funds, "console");
            AgencyScenarioUpdater.WriteFunds(agency.Id, funds);
            AgencyFanout.PushFundsToMembers(agency, funds, "console-set");
            LunaLog.Normal($"setagencyfunds '{agency.Name}' = {funds}");
            return true;
        }
    }

    public class SetAgencyScienceCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !float.TryParse(parts[1], out var sci)
                || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /setagencyscience <name|id> <value>");
                return false;
            }
            AgencySystem.SetAgencyScience(agency, sci, "console");
            AgencyScenarioUpdater.WriteScience(agency.Id, sci);
            AgencyFanout.PushScienceToMembers(agency, sci);
            LunaLog.Normal($"setagencyscience '{agency.Name}' = {sci}");
            return true;
        }
    }

    public class SetAgencyReputationCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !float.TryParse(parts[1], out var rep)
                || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /setagencyrep <name|id> <value>");
                return false;
            }
            AgencySystem.SetAgencyReputation(agency, rep, "console");
            AgencyScenarioUpdater.WriteReputation(agency.Id, rep);
            AgencyFanout.PushReputationToMembers(agency, rep);
            LunaLog.Normal($"setagencyrep '{agency.Name}' = {rep}");
            return true;
        }
    }

    public class UnlockAgencyTechCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /unlockagencytech <name|id> <techNodeId>");
                return false;
            }
            if (AgencyScenarioUpdater.ForceUnlockTech(agency.Id, parts[1]))
            {
                AgencySystem.IncrementUnlockedTech(agency, 1);
                LunaLog.Normal($"unlockagencytech: '{parts[1]}' unlocked for '{agency.Name}'. Members must reconnect to see it.");
                return true;
            }
            LunaLog.Error("Tech already unlocked or agency missing R&D scenario.");
            return false;
        }
    }

    public class CompleteAgencyContractCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /completeagencycontract <name|id> <contractGuid>");
                return false;
            }
            var ok = AgencyScenarioUpdater.ForceCompleteContract(agency.Id, parts[1]);
            LunaLog.Normal($"completeagencycontract: {(ok ? "ok" : "not found")}");
            return ok;
        }
    }

    public class CancelAgencyContractCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            var parts = commandArgs?.Split(' ');
            if (parts == null || parts.Length < 2 || !AgencyCmdHelpers.TryResolveAgency(parts[0], out var agency))
            {
                LunaLog.Error("Usage: /cancelagencycontract <name|id> <contractGuid>");
                return false;
            }
            var ok = AgencyScenarioUpdater.ForceCancelContract(agency.Id, parts[1]);
            LunaLog.Normal($"cancelagencycontract: {(ok ? "ok" : "not found")}");
            return ok;
        }
    }

    public class AgencyInfoCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (!AgencyCmdHelpers.TryResolveAgency(commandArgs?.Trim(), out var agency))
            {
                LunaLog.Error("Usage: /agencyinfo <name|id>");
                return false;
            }
            LunaLog.Normal($"=== Agency '{agency.Name}' ===");
            LunaLog.Normal($"  id: {agency.Id}");
            LunaLog.Normal($"  solo: {agency.IsSolo}  created: {new DateTime(agency.CreatedUtcTicks, DateTimeKind.Utc):u}");
            LunaLog.Normal($"  owner: {agency.OwnerDisplayName} ({agency.OwnerUniqueId})");
            LunaLog.Normal($"  funds={agency.Funds:N0}  science={agency.Science:N1}  reputation={agency.Reputation:N1}");
            LunaLog.Normal($"  unlockedTechCount={agency.UnlockedTechCount}");
            LunaLog.Normal($"  members ({agency.Members.Count}):");
            foreach (var m in agency.Members) LunaLog.Normal($"    - {m.DisplayName} ({m.UniqueId})");
            LunaLog.Normal($"  pending join requests ({agency.PendingJoinRequests.Count}):");
            foreach (var r in agency.PendingJoinRequests) LunaLog.Normal($"    - {r.PlayerDisplayName} ({r.PlayerUniqueId})");
            return true;
        }
    }
}
