using LmpCommon.Agency;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Settings.Structures;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// Business logic for agencies. All mutations flow through here so the
    /// invariants ("player is in at most one agency", "owner must be member",
    /// "no duplicate names") stay in one place. Persistence and networking are
    /// triggered here as well so that callers do not have to remember to save
    /// and broadcast.
    ///
    /// <b>No-agency fallback:</b> every authenticated player has an agency.
    /// If the player has no explicit agency, <see cref="EnsureSoloAgency"/>
    /// creates a private <c>solo-&lt;uniqueId&gt;</c> agency with IsSolo=true
    /// and makes them its sole owner. Solo agencies are not listed in the
    /// Browse UI but are otherwise identical to regular agencies. Dissolving
    /// the solo agency happens automatically when the player joins a regular
    /// one (the system cleans up empty agencies).
    /// </summary>
    public static class AgencySystem
    {
        public const string SoloNamePrefix = "Solo-";

        public static Agency GetAgency(Guid id)
        {
            AgencyStore.Agencies.TryGetValue(id, out var a);
            return a;
        }

        public static Agency GetAgencyByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return AgencyStore.Agencies.Values.FirstOrDefault(a =>
                string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        public static Agency GetAgencyForPlayer(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return null;
            return AgencyStore.Agencies.Values.FirstOrDefault(a => a.HasMember(uniqueId));
        }

        public static Agency EnsureSoloAgency(string uniqueId, string displayName)
        {
            if (string.IsNullOrEmpty(uniqueId))
                throw new ArgumentException("uniqueId required", nameof(uniqueId));

            var existing = GetAgencyForPlayer(uniqueId);
            if (existing != null) return existing;

            var agency = new Agency
            {
                Id = Guid.NewGuid(),
                Name = SoloNamePrefix + (string.IsNullOrEmpty(displayName) ? uniqueId : displayName),
                OwnerUniqueId = uniqueId,
                OwnerDisplayName = displayName ?? string.Empty,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                IsSolo = true,
                Funds = GameplaySettings.SettingsStore.StartingFunds,
                Science = GameplaySettings.SettingsStore.StartingScience,
                Reputation = GameplaySettings.SettingsStore.StartingReputation,
            };
            agency.Members.Add(new Agency.Member { UniqueId = uniqueId, DisplayName = displayName ?? string.Empty });

            if (!AgencyStore.Agencies.TryAdd(agency.Id, agency))
                return GetAgencyForPlayer(uniqueId);

            AgencyStore.PersistAgency(agency);
            AgencyScenarioStore.EnsureBaselineForAgency(agency.Id,
                agency.Funds, agency.Science, agency.Reputation);
            AgencyScenarioStore.BackupAgency(agency.Id);

            LunaLog.Info($"[Agency] Created solo agency '{agency.Name}' id={agency.Id} owner={displayName}({uniqueId})");
            AgencyNetwork.BroadcastUpsert(agency);
            return agency;
        }

        public static (bool Success, string Message, Agency Agency) CreateAgency(string name, string ownerUniqueId, string ownerDisplayName)
        {
            if (string.IsNullOrWhiteSpace(name))
                return (false, "Agency name cannot be empty.", null);
            if (name.Length > 48)
                return (false, "Agency name too long (max 48 chars).", null);
            if (name.StartsWith(SoloNamePrefix, StringComparison.OrdinalIgnoreCase))
                return (false, $"'{SoloNamePrefix}' prefix is reserved for solo agencies.", null);
            if (GetAgencyByName(name) != null)
                return (false, $"Agency name '{name}' already in use.", null);
            if (string.IsNullOrEmpty(ownerUniqueId))
                return (false, "Owner identity required.", null);

            // If the owner is already a member of another agency, remove them from it first.
            var existing = GetAgencyForPlayer(ownerUniqueId);
            if (existing != null)
            {
                RemoveMemberInternal(existing, ownerUniqueId, "creating own agency");
            }

            var agency = new Agency
            {
                Id = Guid.NewGuid(),
                Name = name.Trim(),
                OwnerUniqueId = ownerUniqueId,
                OwnerDisplayName = ownerDisplayName ?? string.Empty,
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                IsSolo = false,
                Funds = GameplaySettings.SettingsStore.StartingFunds,
                Science = GameplaySettings.SettingsStore.StartingScience,
                Reputation = GameplaySettings.SettingsStore.StartingReputation,
            };
            agency.Members.Add(new Agency.Member { UniqueId = ownerUniqueId, DisplayName = ownerDisplayName ?? string.Empty });

            if (!AgencyStore.Agencies.TryAdd(agency.Id, agency))
                return (false, "Failed to register agency (id collision).", null);

            AgencyStore.PersistAgency(agency);
            AgencyScenarioStore.EnsureBaselineForAgency(agency.Id, agency.Funds, agency.Science, agency.Reputation);
            AgencyScenarioStore.BackupAgency(agency.Id);

            ApplyClientAgencyAssignment(ownerUniqueId, agency.Id,
                $"Created agency '{agency.Name}'. Reconnecting to load its career state.");

            LunaLog.Info($"[Agency] CreateAgency name='{agency.Name}' id={agency.Id} owner={ownerDisplayName}({ownerUniqueId})");
            AgencyNetwork.BroadcastUpsert(agency);
            return (true, $"Created agency '{agency.Name}'.", agency);
        }

        public static (bool Success, string Message) RenameAgency(Guid agencyId, string actorUniqueId, string newName, bool isAdmin)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (agency.IsSolo) return (false, "Cannot rename a solo agency.");
            if (!isAdmin && !string.Equals(agency.OwnerUniqueId, actorUniqueId, StringComparison.Ordinal))
                return (false, "Only the owner (or an admin) can rename.");
            if (string.IsNullOrWhiteSpace(newName)) return (false, "Name cannot be empty.");
            if (newName.Length > 48) return (false, "Name too long.");
            if (newName.StartsWith(SoloNamePrefix, StringComparison.OrdinalIgnoreCase))
                return (false, $"'{SoloNamePrefix}' prefix is reserved.");

            var conflict = GetAgencyByName(newName);
            if (conflict != null && conflict.Id != agencyId)
                return (false, $"Agency name '{newName}' already in use.");

            var before = agency.Name;
            lock (agency.Lock)
            {
                agency.Name = newName.Trim();
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] Rename id={agencyId} '{before}'->'{agency.Name}' actor={actorUniqueId} admin={isAdmin}");
            AgencyNetwork.BroadcastUpsert(agency);
            return (true, $"Renamed to '{agency.Name}'.");
        }

        public static (bool Success, string Message) DeleteAgency(Guid agencyId, string actorUniqueId, bool isAdmin, bool force)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (!isAdmin) return (false, "Only an admin can delete an agency.");
            lock (agency.Lock)
            {
                if (!force && agency.Members.Count > 1)
                    return (false, "Agency still has members. Use --force to delete anyway.");
            }

            AgencyStore.Agencies.TryRemove(agencyId, out _);
            AgencyScenarioStore.RemoveAgency(agencyId);
            AgencyStore.DeleteAgencyFiles(agencyId);

            // Demote members to solo agencies to preserve invariant.
            string[] orphans;
            lock (agency.Lock)
            {
                orphans = agency.Members.Select(m => m.UniqueId).ToArray();
            }
            foreach (var orphan in orphans)
            {
                var client = AgencyNetwork.GetOnlinePlayerByUniqueId(orphan);
                var display = client?.PlayerName ?? orphan;
                var solo = EnsureSoloAgency(orphan, display);
                if (client != null)
                    client.AgencyId = solo.Id;
            }

            LunaLog.Info($"[Agency] Delete id={agencyId} name='{agency.Name}' by={actorUniqueId} force={force} orphans={orphans.Length}");
            AgencyNetwork.BroadcastDelete(agencyId);
            return (true, $"Deleted agency '{agency.Name}'.");
        }

        public static (bool Success, string Message) PostJoinRequest(Guid agencyId, string playerUniqueId, string playerDisplayName)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (agency.IsSolo) return (false, "Cannot join a solo agency.");
            if (agency.HasMember(playerUniqueId)) return (false, "Already a member.");
            if (agency.HasPendingJoinRequest(playerUniqueId)) return (false, "Join request already pending.");

            JoinRequestInfo request;
            lock (agency.Lock)
            {
                request = new JoinRequestInfo
                {
                    AgencyId = agencyId,
                    PlayerUniqueId = playerUniqueId,
                    PlayerDisplayName = playerDisplayName ?? string.Empty,
                    RequestedUtcTicks = DateTime.UtcNow.Ticks,
                };
                agency.PendingJoinRequests.Add(request);
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] JoinRequestPosted agency='{agency.Name}' id={agencyId} player={playerDisplayName}({playerUniqueId})");
            AgencyNetwork.SendJoinRequestPostedToOwner(agency, request);
            return (true, "Join request submitted.");
        }

        public static (bool Success, string Message) CancelJoinRequest(Guid agencyId, string playerUniqueId)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            bool removed;
            lock (agency.Lock)
            {
                var before = agency.PendingJoinRequests.Count;
                agency.PendingJoinRequests.RemoveAll(r => r.PlayerUniqueId == playerUniqueId);
                removed = agency.PendingJoinRequests.Count < before;
            }
            if (!removed) return (false, "No pending request.");
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] JoinRequestCancelled agency='{agency.Name}' player={playerUniqueId}");
            return (true, "Join request cancelled.");
        }

        public static (bool Success, string Message) ResolveJoinRequest(Guid agencyId, string actorUniqueId, string targetUniqueId, bool approve, bool isAdmin)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (!isAdmin && !string.Equals(agency.OwnerUniqueId, actorUniqueId, StringComparison.Ordinal))
                return (false, "Only the owner (or an admin) can resolve join requests.");

            JoinRequestInfo request;
            lock (agency.Lock)
            {
                request = agency.PendingJoinRequests.FirstOrDefault(r => r.PlayerUniqueId == targetUniqueId);
                if (request == null) return (false, "No such pending request.");
                agency.PendingJoinRequests.Remove(request);
            }

            if (approve)
            {
                var previous = GetAgencyForPlayer(targetUniqueId);
                if (previous != null && previous.Id != agencyId)
                    RemoveMemberInternal(previous, targetUniqueId, "approved into new agency");

                lock (agency.Lock)
                {
                    if (!agency.Members.Any(m => m.UniqueId == targetUniqueId))
                        agency.Members.Add(new Agency.Member { UniqueId = targetUniqueId, DisplayName = request.PlayerDisplayName });
                }

                ApplyClientAgencyAssignment(targetUniqueId, agency.Id,
                    $"You joined '{agency.Name}'. Reconnecting to load its career state.");
            }

            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] JoinRequestResolved agency='{agency.Name}' target={targetUniqueId} approved={approve} actor={actorUniqueId} admin={isAdmin}");
            AgencyNetwork.SendJoinRequestResolvedToPlayer(targetUniqueId, agencyId, approve);
            AgencyNetwork.BroadcastUpsert(agency);
            return (true, approve ? "Request approved." : "Request rejected.");
        }

        public static (bool Success, string Message) LeaveAgency(string playerUniqueId, string playerDisplayName)
        {
            var agency = GetAgencyForPlayer(playerUniqueId);
            if (agency == null) return (false, "Not a member of any agency.");
            if (agency.IsSolo) return (false, "Cannot leave a solo agency.");
            if (string.Equals(agency.OwnerUniqueId, playerUniqueId, StringComparison.Ordinal))
            {
                lock (agency.Lock)
                {
                    if (agency.Members.Count > 1)
                        return (false, "Transfer ownership before leaving, or have an admin delete the agency.");
                }
            }

            RemoveMemberInternal(agency, playerUniqueId, "leave");

            // Drop to solo agency.
            var solo = EnsureSoloAgency(playerUniqueId, playerDisplayName);
            ApplyClientAgencyAssignment(playerUniqueId, solo.Id,
                $"You left '{agency.Name}'. Reconnecting to your solo agency.");

            LunaLog.Info($"[Agency] Leave agency='{agency.Name}' player={playerUniqueId} -> solo={solo.Id}");
            return (true, "Left agency.");
        }

        public static (bool Success, string Message) KickMember(Guid agencyId, string actorUniqueId, string targetUniqueId, bool isAdmin)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (agency.IsSolo) return (false, "Cannot kick from a solo agency.");
            if (!isAdmin && !string.Equals(agency.OwnerUniqueId, actorUniqueId, StringComparison.Ordinal))
                return (false, "Only the owner (or an admin) can kick members.");
            if (string.Equals(agency.OwnerUniqueId, targetUniqueId, StringComparison.Ordinal))
                return (false, "Transfer ownership before kicking the owner.");

            if (!agency.HasMember(targetUniqueId)) return (false, "Not a member.");

            RemoveMemberInternal(agency, targetUniqueId, "kicked");

            var client = AgencyNetwork.GetOnlinePlayerByUniqueId(targetUniqueId);
            var solo = EnsureSoloAgency(targetUniqueId, client?.PlayerName ?? targetUniqueId);
            ApplyClientAgencyAssignment(targetUniqueId, solo.Id,
                $"You were kicked from '{agency.Name}'. Reconnecting to your solo agency.");

            LunaLog.Info($"[Agency] Kick agency='{agency.Name}' target={targetUniqueId} actor={actorUniqueId} admin={isAdmin}");
            return (true, "Member kicked.");
        }

        public static (bool Success, string Message) TransferOwner(Guid agencyId, string actorUniqueId, string newOwnerUniqueId, bool isAdmin)
        {
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (agency.IsSolo) return (false, "Cannot transfer a solo agency.");
            if (!isAdmin && !string.Equals(agency.OwnerUniqueId, actorUniqueId, StringComparison.Ordinal))
                return (false, "Only the owner (or an admin) can transfer.");
            if (!agency.HasMember(newOwnerUniqueId))
                return (false, "Target player must already be a member.");

            string beforeOwner;
            lock (agency.Lock)
            {
                beforeOwner = agency.OwnerUniqueId;
                agency.OwnerUniqueId = newOwnerUniqueId;
                var m = agency.Members.FirstOrDefault(x => x.UniqueId == newOwnerUniqueId);
                if (m != null) agency.OwnerDisplayName = m.DisplayName ?? string.Empty;
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] TransferOwner agency='{agency.Name}' from={beforeOwner} to={newOwnerUniqueId} actor={actorUniqueId} admin={isAdmin}");
            AgencyNetwork.BroadcastUpsert(agency);
            return (true, "Ownership transferred.");
        }

        /// <summary>
        /// Admin: force a player to become the owner of an agency. If the
        /// player isn't currently a member, we auto-join them first. The
        /// displaced previous owner remains a member.
        /// </summary>
        public static (bool Success, string Message) AdminSetOwner(Guid agencyId, string newOwnerUniqueId)
        {
            if (string.IsNullOrEmpty(newOwnerUniqueId)) return (false, "Target player id required.");
            var agency = GetAgency(agencyId);
            if (agency == null) return (false, "Agency not found.");
            if (agency.IsSolo) return (false, "Cannot rewrite a solo agency's owner.");

            // Detach from any other agency first so membership stays unique.
            var previous = GetAgencyForPlayer(newOwnerUniqueId);
            if (previous != null && previous.Id != agencyId)
                RemoveMemberInternal(previous, newOwnerUniqueId, "admin-set-owner of other agency");

            var displayName = AgencyNetwork.GetOnlinePlayerByUniqueId(newOwnerUniqueId)?.PlayerName
                              ?? agency.Members.FirstOrDefault(m => m.UniqueId == newOwnerUniqueId)?.DisplayName
                              ?? newOwnerUniqueId;

            string previousOwner;
            lock (agency.Lock)
            {
                previousOwner = agency.OwnerUniqueId;
                if (!agency.Members.Any(m => m.UniqueId == newOwnerUniqueId))
                    agency.Members.Add(new Agency.Member { UniqueId = newOwnerUniqueId, DisplayName = displayName });
                agency.OwnerUniqueId = newOwnerUniqueId;
                agency.OwnerDisplayName = displayName;
            }
            AgencyStore.PersistAgency(agency);
            ApplyClientAgencyAssignment(newOwnerUniqueId, agency.Id,
                $"An admin made you the owner of '{agency.Name}'. Reconnecting.");

            LunaLog.Info($"[Agency] AdminSetOwner agency='{agency.Name}' previous={previousOwner} new={newOwnerUniqueId}");
            AgencyNetwork.BroadcastUpsert(agency);
            return (true, $"Set owner of '{agency.Name}' to {displayName}.");
        }

        /// <summary>
        /// Transfer <paramref name="amount"/> of <paramref name="kind"/> from
        /// <paramref name="fromAgencyId"/> to <paramref name="toAgencyId"/>.
        /// The requesting player must either be a member of the source agency
        /// (any member) or an admin.
        /// </summary>
        public static (bool Success, string Message) TransferResources(
            Guid fromAgencyId, Guid toAgencyId, ResourceKind kind, double amount,
            string actorUniqueId, bool isAdmin)
        {
            if (amount <= 0) return (false, "Amount must be positive.");
            if (fromAgencyId == toAgencyId) return (false, "Source and destination are the same.");

            var src = GetAgency(fromAgencyId);
            var dst = GetAgency(toAgencyId);
            if (src == null) return (false, "Source agency not found.");
            if (dst == null) return (false, "Destination agency not found.");

            if (!isAdmin && !src.HasMember(actorUniqueId))
                return (false, "You must be a member of the source agency.");

            switch (kind)
            {
                case ResourceKind.Funds:
                    {
                        if (src.Funds < amount) return (false, "Insufficient funds.");
                        var srcBefore = src.Funds;
                        var dstBefore = dst.Funds;
                        SetAgencyFunds(src, srcBefore - amount, $"transfer->{dst.Name}");
                        AgencyScenarioUpdater.WriteFunds(src.Id, src.Funds);
                        AgencyFanout.PushFundsToMembers(src, src.Funds, $"transfer to {dst.Name}");
                        SetAgencyFunds(dst, dstBefore + amount, $"transfer<-{src.Name}");
                        AgencyScenarioUpdater.WriteFunds(dst.Id, dst.Funds);
                        AgencyFanout.PushFundsToMembers(dst, dst.Funds, $"transfer from {src.Name}");
                        LunaLog.Info($"[Agency] Transfer Funds amount={amount} from='{src.Name}' to='{dst.Name}' actor={actorUniqueId}");
                        return (true, $"Sent {amount:N0} funds to '{dst.Name}'.");
                    }
                case ResourceKind.Science:
                    {
                        var fAmount = (float)amount;
                        if (src.Science < fAmount) return (false, "Insufficient science.");
                        var srcBefore = src.Science;
                        var dstBefore = dst.Science;
                        SetAgencyScience(src, srcBefore - fAmount, $"transfer->{dst.Name}");
                        AgencyScenarioUpdater.WriteScience(src.Id, src.Science);
                        AgencyFanout.PushScienceToMembers(src, src.Science);
                        SetAgencyScience(dst, dstBefore + fAmount, $"transfer<-{src.Name}");
                        AgencyScenarioUpdater.WriteScience(dst.Id, dst.Science);
                        AgencyFanout.PushScienceToMembers(dst, dst.Science);
                        LunaLog.Info($"[Agency] Transfer Science amount={fAmount} from='{src.Name}' to='{dst.Name}' actor={actorUniqueId}");
                        return (true, $"Sent {fAmount:N1} science to '{dst.Name}'.");
                    }
                default:
                    return (false, $"Transfer of {kind} is not supported.");
            }
        }

        /// <summary>Admin: force-move a player into a target agency.</summary>
        public static (bool Success, string Message) AdminMoveMember(string targetUniqueId, Guid targetAgencyId)
        {
            if (string.IsNullOrEmpty(targetUniqueId)) return (false, "Target player id required.");
            var target = GetAgency(targetAgencyId);
            if (target == null) return (false, "Target agency not found.");

            var current = GetAgencyForPlayer(targetUniqueId);
            if (current != null && current.Id == targetAgencyId)
                return (true, "Player already in that agency.");

            var displayName = AgencyNetwork.GetOnlinePlayerByUniqueId(targetUniqueId)?.PlayerName
                              ?? current?.Members.FirstOrDefault(m => m.UniqueId == targetUniqueId)?.DisplayName
                              ?? targetUniqueId;

            if (current != null)
            {
                RemoveMemberInternal(current, targetUniqueId, "admin-moved");
            }

            lock (target.Lock)
            {
                if (!target.Members.Any(m => m.UniqueId == targetUniqueId))
                    target.Members.Add(new Agency.Member { UniqueId = targetUniqueId, DisplayName = displayName });
            }
            AgencyStore.PersistAgency(target);
            ApplyClientAgencyAssignment(targetUniqueId, target.Id,
                $"An admin moved you to '{target.Name}'. Reconnecting to load its career state.");

            LunaLog.Info($"[Agency] AdminMoveMember target={targetUniqueId} into='{target.Name}' id={targetAgencyId}");
            AgencyNetwork.BroadcastUpsert(target);
            return (true, $"Moved into '{target.Name}'.");
        }

        public static void SetAgencyFunds(Agency agency, double value, string reason)
        {
            if (agency == null) return;
            double before;
            lock (agency.Lock)
            {
                before = agency.Funds;
                agency.Funds = value;
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] SetFunds agency='{agency.Name}' before={before} after={value} reason={reason}");
            AgencyNetwork.BroadcastUpsert(agency);
        }

        public static void SetAgencyScience(Agency agency, float value, string reason)
        {
            if (agency == null) return;
            float before;
            lock (agency.Lock)
            {
                before = agency.Science;
                agency.Science = value;
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] SetScience agency='{agency.Name}' before={before} after={value} reason={reason}");
            AgencyNetwork.BroadcastUpsert(agency);
        }

        public static void SetAgencyReputation(Agency agency, float value, string reason)
        {
            if (agency == null) return;
            float before;
            lock (agency.Lock)
            {
                before = agency.Reputation;
                agency.Reputation = value;
            }
            AgencyStore.PersistAgency(agency);
            LunaLog.Info($"[Agency] SetReputation agency='{agency.Name}' before={before} after={value} reason={reason}");
            AgencyNetwork.BroadcastUpsert(agency);
        }

        public static void IncrementUnlockedTech(Agency agency, int delta)
        {
            if (agency == null) return;
            lock (agency.Lock)
            {
                agency.UnlockedTechCount = Math.Max(0, agency.UnlockedTechCount + delta);
            }
            AgencyStore.PersistAgency(agency);
            AgencyNetwork.BroadcastUpsert(agency);
        }

        #region Internal helpers

        private static void RemoveMemberInternal(Agency agency, string uniqueId, string reason)
        {
            if (agency == null) return;
            bool wasOwner;
            int remaining;
            lock (agency.Lock)
            {
                agency.Members.RemoveAll(m => m.UniqueId == uniqueId);
                wasOwner = string.Equals(agency.OwnerUniqueId, uniqueId, StringComparison.Ordinal);
                remaining = agency.Members.Count;
                if (wasOwner && remaining > 0)
                {
                    var successor = agency.Members.First();
                    agency.OwnerUniqueId = successor.UniqueId;
                    agency.OwnerDisplayName = successor.DisplayName ?? string.Empty;
                }
                agency.PendingJoinRequests.RemoveAll(r => r.PlayerUniqueId == uniqueId);
            }

            LunaLog.Info($"[Agency] MemberRemoved agency='{agency.Name}' player={uniqueId} reason={reason} remaining={remaining}");

            if (remaining == 0 && agency.IsSolo)
            {
                // Solo agency lost its only member: remove it — it exists
                // only as long as its one owner does.
                AgencyStore.Agencies.TryRemove(agency.Id, out _);
                AgencyScenarioStore.RemoveAgency(agency.Id);
                AgencyStore.DeleteAgencyFiles(agency.Id);
                AgencyNetwork.BroadcastDelete(agency.Id);
                return;
            }

            // Public agencies are NOT auto-deleted when empty. This used to
            // destroy the Default Agency (and its funds/tech) when every
            // member happened to leave for a solo/new agency during testing.
            // Empty public agencies stick around — an admin must run /deleteagency
            // or the admin-UI Delete action to remove them.
            AgencyStore.PersistAgency(agency);
            AgencyNetwork.BroadcastUpsert(agency);
        }

        /// <summary>
        /// Applies the agency id to an online client AND force-disconnects
        /// them with a reason so the client reconnects against the new agency
        /// state. Stock KSP's ScenarioModules cache state too aggressively to
        /// swap funds/sci/tech in-place, so a clean reconnect is the only way
        /// to keep player-visible state in sync with the server.
        ///
        /// Callers should pass a reason that will be displayed to the player
        /// (e.g. "You joined Kerbin Dynamics, reconnecting...").
        /// </summary>
        private static void ApplyClientAgencyAssignment(string uniqueId, Guid agencyId, string reconnectReason)
        {
            var client = AgencyNetwork.GetOnlinePlayerByUniqueId(uniqueId);
            if (client == null) return;

            client.AgencyId = agencyId;
            if (!string.IsNullOrEmpty(reconnectReason))
                AgencyNetwork.KickForAgencyChange(uniqueId, reconnectReason);
        }

        /// <summary>Sets the agency id on an online client without kicking. Used by
        /// <see cref="AssignAgencyOnConnect"/> where the client is still
        /// handshaking and a kick would abort that handshake.</summary>
        private static void ApplyClientAgencyAssignmentSilent(string uniqueId, Guid agencyId)
        {
            var client = AgencyNetwork.GetOnlinePlayerByUniqueId(uniqueId);
            if (client != null) client.AgencyId = agencyId;
        }

        #endregion

        /// <summary>
        /// Public helper used by handshake to assign the agency on connect.
        /// </summary>
        public static void AssignAgencyOnConnect(ClientStructure client)
        {
            if (client == null || string.IsNullOrEmpty(client.UniqueIdentifier)) return;

            var agency = GetAgencyForPlayer(client.UniqueIdentifier)
                         ?? EnsureSoloAgency(client.UniqueIdentifier, client.PlayerName);
            client.AgencyId = agency.Id;

            // Refresh display name if it differs (e.g. player changed name).
            lock (agency.Lock)
            {
                var m = agency.Members.FirstOrDefault(x => x.UniqueId == client.UniqueIdentifier);
                if (m != null && !string.Equals(m.DisplayName, client.PlayerName, StringComparison.Ordinal))
                {
                    m.DisplayName = client.PlayerName ?? string.Empty;
                    if (string.Equals(agency.OwnerUniqueId, client.UniqueIdentifier, StringComparison.Ordinal))
                        agency.OwnerDisplayName = client.PlayerName ?? string.Empty;
                }
            }
            AgencyStore.PersistAgency(agency);
        }

        public static IEnumerable<Agency> GetAll() => AgencyStore.Agencies.Values;
    }
}
