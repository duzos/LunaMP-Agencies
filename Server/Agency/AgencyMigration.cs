using LmpCommon.Enums;
using LunaConfigNode.CfgNode;
using Server.Log;
using Server.Settings.Structures;
using Server.System;
using System;
using System.Globalization;
using System.Linq;

namespace Server.Agency
{
    /// <summary>
    /// One-shot migration that creates a "Default Agency" from pre-existing
    /// global scenario files so that upgrading an in-flight server does not
    /// lose career state.
    ///
    /// Runs at startup after <see cref="AgencyStore.LoadExistingAgencies"/>.
    /// If any agency is already present, migration is a no-op (idempotent).
    /// </summary>
    public static class AgencyMigration
    {
        public const string DefaultAgencyName = "Default Agency";

        public static void RunIfNeeded()
        {
            if (AgencyStore.Agencies.Any())
            {
                LunaLog.Debug("[Agency] Migration skipped: agencies already exist.");
                return;
            }

            // Only migrate career-mode state. Sandbox/Science stay global.
            if (GeneralSettings.SettingsStore.GameMode == GameMode.Sandbox)
            {
                LunaLog.Debug("[Agency] Migration skipped: sandbox game mode.");
                return;
            }

            LunaLog.Info("[Agency] Running first-time migration: creating Default Agency from global scenarios.");

            var agency = new Agency
            {
                Id = Guid.NewGuid(),
                Name = DefaultAgencyName,
                OwnerUniqueId = "server-default",
                OwnerDisplayName = "server",
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
                IsSolo = false,
                Funds = GameplaySettings.SettingsStore.StartingFunds,
                Science = GameplaySettings.SettingsStore.StartingScience,
                Reputation = GameplaySettings.SettingsStore.StartingReputation,
            };
            AgencyStore.Agencies[agency.Id] = agency;

            // Copy the globally loaded scenarios that are agency-scoped into the default agency's dict.
            foreach (var moduleName in AgencyScopedModuleNames)
            {
                if (ScenarioStoreSystem.CurrentScenarios.TryGetValue(moduleName, out var configNode))
                {
                    AgencyScenarioStore.AddOrUpdate(agency.Id, moduleName, configNode);
                }
            }

            AgencyScenarioStore.EnsureBaselineForAgency(agency.Id, agency.Funds, agency.Science, agency.Reputation);

            // Pull the headline values out of the migrated scenario nodes so
            // the Agency entity (used for UI and upsert broadcasts) reflects
            // what the career actually has, not the gameplay defaults.
            SyncEntityFromScenarios(agency);

            AgencyScenarioStore.BackupAgency(agency.Id);
            AgencyStore.PersistAgency(agency);

            LunaLog.Info($"[Agency] Migration complete: Default Agency id={agency.Id} funds={agency.Funds} sci={agency.Science} rep={agency.Reputation}. Existing players will be auto-enrolled into solo agencies on first connect; use the admin console to move them into the Default Agency if desired.");
        }

        /// <summary>
        /// Reads the scalar funds/sci/rep values out of the agency's migrated
        /// scenario nodes and copies them onto the in-memory Agency entity.
        /// </summary>
        public static void SyncEntityFromScenarios(Agency agency)
        {
            if (agency == null) return;

            if (AgencyScenarioStore.TryGet(agency.Id, "Funding", out var funding))
            {
                var v = funding?.GetValue("funds")?.Value;
                if (!string.IsNullOrEmpty(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var funds))
                    agency.Funds = funds;
            }

            if (AgencyScenarioStore.TryGet(agency.Id, "ResearchAndDevelopment", out var rd))
            {
                var v = rd?.GetValue("sci")?.Value;
                if (!string.IsNullOrEmpty(v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var sci))
                    agency.Science = sci;

                // Count unlocked tech nodes too so the UI summary is accurate.
                try { agency.UnlockedTechCount = rd.GetNodes("Tech").Count; } catch { }
            }

            if (AgencyScenarioStore.TryGet(agency.Id, "Reputation", out var reputation))
            {
                var v = reputation?.GetValue("rep")?.Value;
                if (!string.IsNullOrEmpty(v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var rep))
                    agency.Reputation = rep;
            }
        }

        /// <summary>
        /// Modules whose authoritative state lives on the server and is
        /// updated via ShareProgress* delta messages (Funds, Science,
        /// Reputation, contract accept/complete, tech-node unlock). Client
        /// scenario uploads of these modules are dropped — accepting them
        /// would let stale local KSP state (e.g. uninitialised Funding =
        /// 0 during early scene transitions) clobber the server's value
        /// before the deltas catch up.
        /// </summary>
        public static readonly string[] DeltaManagedModules =
        {
            "Funding",
            "Reputation",
            "ResearchAndDevelopment",
            "ContractSystem",
        };

        /// <summary>
        /// Snapshot-managed modules that are ALWAYS per-agency when the
        /// agencies feature is on — they have no ShareProgress delta channel
        /// and their semantics are clearly agency-scoped (per-agency mission
        /// progress, per-agency facility upgrades, per-agency active
        /// strategies).
        /// </summary>
        public static readonly string[] AlwaysSnapshotManagedModules =
        {
            "ScenarioContractEvents",
            "ScenarioUpgradeableFacilities",
            "StrategySystem",
        };

        /// <summary>
        /// Snapshot-managed modules that are per-agency only when their
        /// corresponding config flag is on. If the flag is off the module
        /// stays globally shared as before.
        /// </summary>
        /// <returns>The set of module names that should be treated as
        /// per-agency snapshot-managed for this server's current settings.</returns>
        public static global::System.Collections.Generic.HashSet<string> GetActiveSnapshotManagedModules()
        {
            var set = new global::System.Collections.Generic.HashSet<string>(AlwaysSnapshotManagedModules);
            if (global::Server.Settings.Structures.GeneralSettings.SettingsStore.AgencyScansatPerAgency)
                set.Add("SCANcontroller");
            return set;
        }

        /// <summary>
        /// Convenience: union of delta-managed + currently-active snapshot-
        /// managed module names. Use for "is this module agency-scoped right
        /// now?" tests on the hot path.
        /// </summary>
        public static global::System.Collections.Generic.HashSet<string> GetActiveAgencyScopedModules()
        {
            var set = GetActiveSnapshotManagedModules();
            foreach (var m in DeltaManagedModules) set.Add(m);
            return set;
        }

        /// <summary>
        /// Backwards-compatible static union (delta + always-snapshot, no
        /// flag-gated entries). Existing callers that need a static list at
        /// type-init time use this; runtime callers should use
        /// <see cref="GetActiveAgencyScopedModules"/> instead.
        /// </summary>
        public static readonly string[] AgencyScopedModuleNames =
            DeltaManagedModules.Concat(AlwaysSnapshotManagedModules).ToArray();
    }
}
