using System;
using System.IO;
using System.Linq;
using System.Text;
using LmpCommon.Agency;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Server;
using Newtonsoft.Json;
using Server.Client;
using Server.Context;
using Server.Diagnostics;
using Server.Server;
using Server.Settings.Structures;
using Server.System;

namespace Server.Agency
{
    public sealed class VisibilityDocument
    {
        public int Version = 1;
        public long Revision;
        public VisibilityAgencyGrant[] AgencyGrants = Array.Empty<VisibilityAgencyGrant>();
        public VisibilityCraftOverride[] CraftOverrides = Array.Empty<VisibilityCraftOverride>();
    }

    public static class AgencyVisibilityStore
    {
        private static VisibilityDocument _document = new VisibilityDocument();
        private static string _loadError;
        private static long _snapshotRevision;
        public static Action<string> PersistenceCheckpoint;
        public static bool Enabled => GeneralSettings.SettingsStore.AgencyHideCraft;
        public static string FilePath => Path.Combine(ServerContext.UniverseDirectory, "AgencyVisibility.json");
        public static bool Ready { get { lock (AgencyVesselMap.TransactionGate) return _loadError == null && AgencyVesselMap.Ready && (!AgencyEconomyStore.Enabled || AgencyEconomyStore.Ready); } }

        public static void Load()
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                _document = new VisibilityDocument(); _loadError = null; _snapshotRevision = 0;
                try
                {
                    if (File.Exists(FilePath))
                    {
                        if (new FileInfo(FilePath).Length > AgencyEconomyWire.MaximumPayloadBytes) throw new InvalidDataException("Visibility file exceeds limit.");
                        var saved = JsonConvert.DeserializeObject<VisibilityDocument>(File.ReadAllText(FilePath));
                        Validate(saved); _document = saved;
                    }
                    _snapshotRevision = _document.Revision;
                }
                catch (Exception error) { _loadError = "Visibility preferences unavailable: " + error.GetType().Name; }
            }
        }

        private static void Validate(VisibilityDocument document)
        {
            if (document == null || document.Version != 1) throw new InvalidDataException("Unsupported visibility data.");
            VisibilityLimits.Validate(new VisibilitySnapshot { Revision = document.Revision, AgencyGrants = document.AgencyGrants, CraftOverrides = document.CraftOverrides });
        }

        private static VisibilityEndpoint[] Endpoints()
        {
            return VesselStoreSystem.CurrentVessels.Keys.Where(id => !AgencyVesselMap.IsAbsorbed(id) && !AgencyVesselMap.IsPendingSplit(id) && !VesselContext.RemovedVessels.ContainsKey(id)).OrderBy(id => id).Select(id =>
            {
                var title = AgencyVesselMap.Get(id);
                var owner = title?.OwnerAgencyId ?? Guid.Empty;
                if (!AgencyStore.Agencies.ContainsKey(owner)) owner = Guid.Empty;
                return new VisibilityEndpoint { VesselId = id, OwnerAgencyId = owner, OwnershipRevision = title?.Revision ?? 0 };
            }).ToArray();
        }

        private static VisibilitySnapshot BuildSnapshot(VisibilityDocument document)
        {
            var result = new VisibilitySnapshot { Ready = Ready, Revision = Math.Max(document.Revision, _snapshotRevision) };
            if (!AgencyVesselMap.Ready) return result;
            var endpoints = Endpoints();
            if (endpoints.Length > VisibilityLimits.MaxEndpoints) { result.Ready = false; return result; }
            result.Endpoints = endpoints;
            if (!result.Ready) return result;
            var byId = endpoints.ToDictionary(e => e.VesselId);
            result.AgencyGrants = document.AgencyGrants.Where(g => AgencyStore.Agencies.ContainsKey(g.OwnerAgencyId) && AgencyStore.Agencies.ContainsKey(g.TargetAgencyId)).Select(g => new VisibilityAgencyGrant { OwnerAgencyId = g.OwnerAgencyId, TargetAgencyId = g.TargetAgencyId }).ToArray();
            result.CraftOverrides = document.CraftOverrides.Where(r => AgencyStore.Agencies.ContainsKey(r.TargetAgencyId) && byId.TryGetValue(r.Source.VesselId, out var endpoint) && endpoint.OwnerAgencyId != Guid.Empty && VisibilityLimits.SameStamp(endpoint, r.Source)).Select(r => new VisibilityCraftOverride { Source = r.Source.Copy(), TargetAgencyId = r.TargetAgencyId, Rule = r.Rule }).ToArray();
            return result;
        }

        public static AgencyVisibilitySnapshotMsgData GetSnapshot()
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                var value = BuildSnapshot(_document);
                if (AgencyEconomyWire.Size(value) > AgencyEconomyWire.MaximumPayloadBytes - 1024) value = new VisibilitySnapshot { Ready = false, Revision = value.Revision };
                var message = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyVisibilitySnapshotMsgData>();
                message.Ready = value.Ready; message.Revision = value.Revision; message.Endpoints = value.Endpoints; message.AgencyGrants = value.AgencyGrants; message.CraftOverrides = value.CraftOverrides;
                return message;
            }
        }

        public static (bool Success, string Reason) Mutate(ClientStructure client, AgencyVisibilityCommandMsgData command)
        {
            lock (AgencyVesselMap.TransactionGate)
            {
                if (!Enabled) return (false, "Craft visibility is disabled.");
                if (!Ready) return (false, _loadError ?? "Authoritative craft state is unavailable.");
                if (client == null || !client.Authenticated || client.ConnectionStatus != ConnectionStatus.Connected || !ServerContext.Clients.Values.Any(c => ReferenceEquals(c, client)) || !AgencyStore.Agencies.TryGetValue(client.AgencyId, out var agency) || !agency.HasMember(client.UniqueIdentifier) || agency.OwnerUniqueId != client.UniqueIdentifier) return (false, "Only the owning agency owner can change visibility sharing.");
                if (command == null || command.RequestId == Guid.Empty || command.TargetAgencyId == client.AgencyId || !AgencyStore.Agencies.ContainsKey(command.TargetAgencyId)) return (false, "Choose a different existing target agency.");
                var snapshot = BuildSnapshot(_document);
                if (!snapshot.Ready) return (false, "Visibility endpoint storage limit reached.");
                var grants = snapshot.AgencyGrants.ToList();
                var overrides = snapshot.CraftOverrides.ToList();
                switch (command.Operation)
                {
                    case VisibilityOperation.SetAgencyShare:
                        grants.RemoveAll(g => g.OwnerAgencyId == client.AgencyId && g.TargetAgencyId == command.TargetAgencyId);
                        if (command.Enabled) grants.Add(new VisibilityAgencyGrant { OwnerAgencyId = client.AgencyId, TargetAgencyId = command.TargetAgencyId });
                        break;
                    case VisibilityOperation.SetCraftOverride:
                        var endpoint = snapshot.Endpoints.FirstOrDefault(e => e.VesselId == command.VesselId);
                        if (endpoint == null || endpoint.OwnerAgencyId != client.AgencyId || endpoint.OwnershipRevision != command.ExpectedOwnershipRevision) return (false, "Craft ownership changed; refresh before saving.");
                        if (!Enum.IsDefined(typeof(VisibilityOverride), command.Rule)) return (false, "Unknown visibility override.");
                        overrides.RemoveAll(r => r.Source.VesselId == endpoint.VesselId && r.TargetAgencyId == command.TargetAgencyId);
                        if (command.Rule != VisibilityOverride.Inherit) overrides.Add(new VisibilityCraftOverride { Source = endpoint.Copy(), TargetAgencyId = command.TargetAgencyId, Rule = command.Rule });
                        break;
                    default: return (false, "Unknown visibility operation.");
                }
                var candidate = new VisibilityDocument { Revision = checked(Math.Max(_document.Revision, _snapshotRevision) + 1), AgencyGrants = grants.ToArray(), CraftOverrides = overrides.ToArray() };
                try
                {
                    Validate(candidate);
                    if (AgencyEconomyWire.Size(BuildSnapshot(candidate)) > AgencyEconomyWire.MaximumPayloadBytes - 1024) throw new InvalidDataException("Visibility snapshot storage limit reached.");
                    var json = JsonConvert.SerializeObject(candidate);
                    if (Encoding.UTF8.GetByteCount(json) > AgencyEconomyWire.MaximumPayloadBytes) throw new InvalidDataException("Visibility store is full.");
                    PersistenceCheckpoint?.Invoke("before-document");
                    AgencyVesselMap.AtomicWrite(FilePath, json);
                }
                catch (Exception error) { return (false, "Visibility sharing unchanged: " + error.GetType().Name); }
                _document = candidate; _snapshotRevision = candidate.Revision;
                return (true, "Visibility sharing saved.");
            }
        }

        public static void HandleCommand(ClientStructure client, AgencyVisibilityCommandMsgData command)
        {
            var result = Mutate(client, command);
            PlaytestDiagnostics.Write("visibility.command", () => $"agency={client.AgencyId} vessel={command.VesselId} targetAgency={command.TargetAgencyId} operation={command.Operation} success={result.Success}");
            if (result.Success) Broadcast();
            var reply = ServerContext.ServerMessageFactory.CreateNewMessageData<AgencyVisibilityResultMsgData>();
            reply.RequestId = command.RequestId; reply.Success = result.Success; reply.Reason = result.Reason;
            MessageQueuer.SendToClient<AgencySrvMsg>(client, reply);
        }
        public static void SendTo(ClientStructure client) { if (Enabled) MessageQueuer.SendToClient<AgencySrvMsg>(client, GetSnapshot()); }
        public static void Broadcast()
        {
            if (!Enabled) return;
            AgencyVisibilitySnapshotMsgData snapshot;
            lock (AgencyVesselMap.TransactionGate) { _snapshotRevision = checked(_snapshotRevision + 1); snapshot = GetSnapshot(); }
            MessageQueuer.SendToAllClients<AgencySrvMsg>(snapshot);
        }
    }
}
