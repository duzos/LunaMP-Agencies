using LmpClient.Systems.Agency;
using LmpClient.Systems.SettingsSys;
using LmpCommon.Agency;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LmpClient.Windows.Agency
{
    public partial class AgencyWindow
    {
        private static Guid _selectedVessel;
        private static Guid _accessAgency;
        private static Guid _transferConfirmation;
        private static Guid _deleteConfirmation;
        // A sent deletion stays pending until the server answers (or 30 s pass), so it cannot be pressed twice.
        private static Guid _deleteRequest, _deleteRequestVessel;
        private static DateTime _deleteRequestSince;
        private static string _vesselSearch = string.Empty;
        private static Vector2 _vesselsScroll;
        private static Vector2 _vesselDetailsScroll;
        private static Vector2 _accessAgenciesScroll;
        private static Vector2 _dockRequestsScroll;
        private static GUIStyle _vesselText;
        private static GUIStyle _vesselHeading;
        private static GUIStyle _vesselButton;
        private static readonly string[] DockPolicyLabels = { "Nobody", "Co-owners", "Anyone" };

        public static void ResetVesselOwnershipUi()
        {
            _selectedVessel = _accessAgency = _transferConfirmation = Guid.Empty;
            _deleteConfirmation = _deleteRequest = _deleteRequestVessel = Guid.Empty;
            _vesselSearch = string.Empty;
        }

        private static bool DeleteWaiting(AgencySystem system, Guid vessel)
        {
            if (_deleteRequest == Guid.Empty) return false;
            if (system.LatestOwnershipResult?.RequestId == _deleteRequest || (DateTime.UtcNow - _deleteRequestSince).TotalSeconds > 30)
            { _deleteRequest = _deleteRequestVessel = Guid.Empty; return false; }
            return _deleteRequestVessel == vessel;
        }

        // Called only by the agency system's main-thread notification routine.
        public static void NotifyDockRequest()
        {
            _tab = 4;
            Singleton.DisplayToggle = true;
        }

        private static void DrawVesselsTab()
        {
            if (_vesselText == null)
            {
                _vesselText = new GUIStyle(GUI.skin.label) { wordWrap = true };
                _vesselHeading = new GUIStyle(_vesselText) { fontStyle = FontStyle.Bold };
                _vesselButton = new GUIStyle(GUI.skin.button) { wordWrap = true };
            }
            if (!SettingsSystem.ServerSettings.AgencyVesselOwnership)
            {
                GUILayout.Label("Vessel ownership is disabled on this server.", _vesselText);
                return;
            }
            var system = AgencySystem.Singleton;
            if (!system.OwnershipReady)
            {
                GUILayout.Label("Waiting for vessel permissions from the server. Flight control is locked until they arrive.", _vesselText);
                return;
            }
            var records = system.GetOwnershipSnapshot();
            var names = new Dictionary<Guid, string>();
            if (FlightGlobals.Vessels != null)
                foreach (var vessel in FlightGlobals.Vessels)
                    if (vessel != null && VisibilityClient.CanSee(vessel)) names[vessel.id] = vessel.vesselName ?? ShortVesselId(vessel.id);
            foreach (var id in records.Keys)
                if (!names.ContainsKey(id) && VisibilityClient.CanSee(id)) names[id] = ShortVesselId(id) + " (not loaded)";

            DrawDockRequests(names);
            GUILayout.Label("Craft ownership", _vesselHeading);
            GUILayout.Label("Your agency and co-owners can fly a craft. Other agencies spectate. Claim ownerless craft or manage access below.", _vesselText);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search", GUILayout.Width(48));
            _vesselSearch = GUILayout.TextField(_vesselSearch);
            GUILayout.EndHorizontal();
            var compact = Screen.width < 620;
            if (compact) GUILayout.BeginVertical(); else GUILayout.BeginHorizontal();
            if (compact) GUILayout.BeginVertical(GUI.skin.box, GUILayout.Height(125));
            else GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(210));
            _vesselsScroll = GUILayout.BeginScrollView(_vesselsScroll);
            foreach (var entry in names.OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase))
            {
                records.TryGetValue(entry.Key, out var record);
                var ownerName = OwnershipAgencyName(record?.OwnerAgencyId ?? Guid.Empty);
                if (!string.IsNullOrEmpty(_vesselSearch) && entry.Value.IndexOf(_vesselSearch, StringComparison.OrdinalIgnoreCase) < 0
                    && ownerName.IndexOf(_vesselSearch, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Toggle(_selectedVessel == entry.Key, entry.Value + "\n" + ownerName, _vesselButton) && _selectedVessel != entry.Key)
                {
                    _selectedVessel = entry.Key;
                    _transferConfirmation = Guid.Empty;
                    _deleteConfirmation = Guid.Empty;
                }
            }
            if (names.Count == 0) GUILayout.Label("No craft received yet.", _vesselText);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.BeginVertical(GUI.skin.box);
            _vesselDetailsScroll = GUILayout.BeginScrollView(_vesselDetailsScroll);
            if (_selectedVessel == Guid.Empty || !names.ContainsKey(_selectedVessel))
                GUILayout.Label("Choose a craft to view its ownership and access.", _vesselText);
            else
            {
                records.TryGetValue(_selectedVessel, out var record);
                var owner = record?.OwnerAgencyId ?? Guid.Empty;
                GUILayout.Label(names[_selectedVessel], _vesselHeading);
                GUILayout.Label(OwnershipAgencyName(owner), _vesselText);
                GUILayout.Label(system.CanControlVessel(_selectedVessel) ? "Your agency can fly this craft." : "Your agency can spectate this craft.", _vesselText);
                if (owner == Guid.Empty)
                {
                    GUILayout.Label("The first accepted claim assigns this craft to that agency.", _vesselText);
                    if (GUILayout.Button("Claim for my agency", _vesselButton))
                        system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.Claim, _selectedVessel);
                }
                else
                {
                    GUILayout.Space(6);
                    GUILayout.Label("Co-owners", _vesselHeading);
                    if (record != null)
                    {
                        foreach (var agencyId in record.CoOwnerAgencyIds)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(OwnershipAgencyName(agencyId), _vesselText);
                            if (system.CanManageVessel(_selectedVessel) && GUILayout.Button("Remove", GUILayout.Width(68)))
                                system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.RemoveCoOwner, _selectedVessel, agencyId);
                            GUILayout.EndHorizontal();
                        }
                        if (record.CoOwnerAgencyIds.Count == 0) GUILayout.Label("No co-owners.", _vesselText);
                        GUILayout.Label("When the owning agency is offline: " + DockPolicyLabels[(int)record.DockingPolicy], _vesselText);
                    }
                    if (system.CanManageVessel(_selectedVessel))
                    {
                        GUILayout.Space(6);
                        var deleting = DeleteWaiting(system, _selectedVessel);
                        var deleteEnabled = GUI.enabled;
                        GUI.enabled = deleteEnabled && !deleting;
                        if (GUILayout.Button("Delete craft…", _vesselButton)) _deleteConfirmation = _selectedVessel;
                        GUI.enabled = deleteEnabled;
                        if (deleting) GUILayout.Label("Waiting for server...", _vesselText);
                        else if (_deleteConfirmation == _selectedVessel)
                        {
                            GUILayout.Label("Permanently delete " + names[_selectedVessel] + "? This removes the craft and its cargo with no refund. Recover or remove crew first, then leave flight.", _vesselText);
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button("Confirm deletion", _vesselButton))
                            {
                                _deleteRequest = system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.Delete, _selectedVessel);
                                _deleteRequestVessel = _selectedVessel; _deleteRequestSince = DateTime.UtcNow;
                                _deleteConfirmation = Guid.Empty;
                            }
                            if (GUILayout.Button("Cancel")) _deleteConfirmation = Guid.Empty;
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.Space(6);
                        GUILayout.Label("Offline docking policy", _vesselHeading);
                        var currentPolicy = record == null ? 0 : (int)record.DockingPolicy;
                        var selectedPolicy = GUILayout.Toolbar(currentPolicy, DockPolicyLabels);
                        if (selectedPolicy != currentPolicy)
                            system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.SetDockingPolicy, _selectedVessel, Guid.Empty, (VesselDockingPolicy)selectedPolicy);
                        GUILayout.Label("When your agency is online, foreign docking requests ask for consent.", _vesselText);
                        GUILayout.Space(6);
                        GUILayout.Label("Choose another agency", _vesselHeading);
                        _accessAgenciesScroll = GUILayout.BeginScrollView(_accessAgenciesScroll, GUILayout.Height(95));
                        foreach (var agency in system.KnownAgencies.Values.Where(a => a.Id != owner).OrderBy(a => a.Name))
                            if (GUILayout.Toggle(_accessAgency == agency.Id, agency.Name, _vesselButton) && _accessAgency != agency.Id)
                            { _accessAgency = agency.Id; _transferConfirmation = Guid.Empty; }
                        GUILayout.EndScrollView();
                        var enabled = GUI.enabled;
                        GUI.enabled = enabled && _accessAgency != Guid.Empty && system.KnownAgencies.ContainsKey(_accessAgency) && _accessAgency != owner;
                        if (GUILayout.Button("Add selected agency as co-owner", _vesselButton))
                            system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.AddCoOwner, _selectedVessel, _accessAgency);
                        if (GUILayout.Button("Hand over to selected agency…", _vesselButton)) _transferConfirmation = _accessAgency;
                        GUI.enabled = enabled;
                        if (_transferConfirmation != Guid.Empty)
                        {
                            GUILayout.Label("Transfer to " + OwnershipAgencyName(_transferConfirmation) + "? Existing co-owners will be removed and offline docking set to Nobody. Undock foreign craft first.", _vesselText);
                            GUILayout.BeginHorizontal();
                            if (GUILayout.Button("Confirm handover", _vesselButton))
                            {
                                system.MessageSender.SendOwnershipCommand(VesselOwnershipOperation.Transfer, _selectedVessel, _transferConfirmation);
                                _transferConfirmation = Guid.Empty;
                            }
                            if (GUILayout.Button("Cancel")) _transferConfirmation = Guid.Empty;
                            GUILayout.EndHorizontal();
                        }
                    }
                    else GUILayout.Label("Only the owning agency's owner can change access or hand over this craft.", _vesselText);
                }
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            if (compact) GUILayout.EndVertical(); else GUILayout.EndHorizontal();
            var result = system.LatestOwnershipResult;
            if (result != null) GUILayout.Label("Server: " + result.Reason, _vesselText);
            GUILayout.Label("Changes appear after server confirmation.", _vesselText);
        }

        private static void DrawDockRequests(IReadOnlyDictionary<Guid, string> names)
        {
            var system = AgencySystem.Singleton;
            var requests = system.GetDockRequests().OrderBy(r => r.Status == DockConsentStatus.Pending ? 0 : 1)
                .ThenBy(r => r.ExpiresUtcTicks).ToArray();
            if (requests.Length == 0) return;
            _dockRequestsScroll = GUILayout.BeginScrollView(_dockRequestsScroll, GUILayout.Height(Math.Min(180, requests.Length * 115)));
            foreach (var request in requests)
            {
                var seconds = Math.Max(0, (int)Math.Ceiling((request.ExpiresUtcTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerSecond));
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label("Docking: " + VesselDisplayName(names, request.SourceVesselId) + " → " + VesselDisplayName(names, request.TargetVesselId), _vesselHeading);
                if (request.RequesterAgencyId != system.MyAgencyId && request.Status == DockConsentStatus.Pending && seconds > 0)
                {
                    GUILayout.Label(request.RequesterName + " (" + OwnershipAgencyName(request.RequesterAgencyId) + ") requests one dock. " + seconds + "s remaining. The combined craft keeps the surviving vessel's owner; original ownership returns on undock.", _vesselText);
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Allow once")) system.MessageSender.RespondDock(request.RequestId, true);
                    if (GUILayout.Button("Decline")) system.MessageSender.RespondDock(request.RequestId, false);
                    GUILayout.EndHorizontal();
                }
                else GUILayout.Label(request.Status == DockConsentStatus.Granted && seconds > 0 ? "Permission granted. Continue your approach within " + seconds + "s."
                    : request.Status == DockConsentStatus.Pending && seconds > 0 ? "Waiting for the owning agency's response (" + seconds + "s)."
                    : request.Status + ": " + request.Reason, _vesselText);
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
        }

        private static string OwnershipAgencyName(Guid id) => id == Guid.Empty ? "Ownerless" :
            AgencySystem.Singleton.KnownAgencies.TryGetValue(id, out var agency) ? agency.Name : "Agency " + ShortVesselId(id);
        private static string ShortVesselId(Guid id) => id.ToString("N").Substring(0, 8);
        private static string VesselDisplayName(IReadOnlyDictionary<Guid, string> names, Guid id) => names.TryGetValue(id, out var name) ? name : ShortVesselId(id);
    }
}
