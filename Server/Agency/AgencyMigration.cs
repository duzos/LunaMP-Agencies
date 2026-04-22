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
        /// Scenario module names that should be managed per-agency rather than
        /// globally. Other modules (Kerbals, warp, etc.) stay global.
        /// </summary>
        public static readonly string[] AgencyScopedModuleNames =
        {
            "Funding",
            "Reputation",
            "ResearchAndDevelopment",
            "ContractSystem",
            "ScenarioContractEvents",
            "ScenarioUpgradeableFacilities",
            "StrategySystem",
        };
    }
}
