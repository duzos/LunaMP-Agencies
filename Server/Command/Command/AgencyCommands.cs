using LmpCommon.Message.Types;
using Server.Agency;
using Server.Command.Command.Base;
using Server.Log;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

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
            var matches = AgencyStore.Agencies.Values.Where(a =>
                a.Id.ToString("N").StartsWith(token, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            agency = matches.Length == 1 ? matches[0] : null;
            return agency != null;
        }
    }

    public static class LaunchSiteCommandArguments
    {
        public static bool TryParse(string text, out string[] arguments)
        {
            var result = new List<string>();
            var token = new StringBuilder();
            var quoted = false;
            var started = false;
            text = text ?? string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                if (character == '\\' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    token.Append('"'); i++; started = true;
                }
                else if (character == '"') { quoted = !quoted; started = true; }
                else if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (started) { result.Add(token.ToString()); token.Clear(); started = false; }
                }
                else { token.Append(character); started = true; }
            }
            if (started) result.Add(token.ToString());
            arguments = result.ToArray();
            return !quoted;
        }

        public static bool ExecuteMutation(string text, bool remove)
        {
            if (!TryParse(text, out var arguments) || arguments.Length != 2 || !AgencyCmdHelpers.TryResolveAgency(arguments[0], out var agency))
            {
                LunaLog.Error("Usage: /" + (remove ? "unassignlaunchsite" : "assignlaunchsite") + " <agency-name|id> <exact-site-id>. Quote names containing spaces; short IDs must be unique.");
                return false;
            }
            var result = remove ? AgencyLaunchSiteStore.Unassign(agency.Id, arguments[1]) : AgencyLaunchSiteStore.Assign(agency.Id, arguments[1]);
            LunaLog.Normal(result.Message);
            return result.Success;
        }
    }

    public class AssignLaunchSiteCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs) => LaunchSiteCommandArguments.ExecuteMutation(commandArgs, remove: false);
    }

    public class UnassignLaunchSiteCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs) => LaunchSiteCommandArguments.ExecuteMutation(commandArgs, remove: true);
    }

    public class ListLaunchSitesCommand : SimpleCommand
    {
        public override bool Execute(string commandArgs)
        {
            if (!LaunchSiteCommandArguments.TryParse(commandArgs, out var arguments) || arguments.Length > 1)
            {
                LunaLog.Error("Usage: /listlaunchsites [agency-name|id]. Quote names containing spaces.");
                return false;
            }
            Agency.Agency agency = null;
            if (arguments.Length == 1 && !AgencyCmdHelpers.TryResolveAgency(arguments[0], out agency))
            {
                LunaLog.Error("Agency not found or short ID is ambiguous.");
                return false;
            }
            var snapshot = AgencyLaunchSiteStore.GetSnapshot();
            var entries = snapshot.Assignments.Where(p => agency == null || p.Value == agency.Id).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
            LunaLog.Normal($"== Launch-site assignments ({entries.Length}, revision {snapshot.Revision}) ==");
            foreach (var entry in entries) LunaLog.Normal($"  '{entry.Key}' -> '{AgencySystem.GetAgency(entry.Value)?.Name}' ({entry.Value})");
            return true;
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
