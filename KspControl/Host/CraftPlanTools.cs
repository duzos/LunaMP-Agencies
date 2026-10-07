using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using KspControl.Contracts;
using ModelContextProtocol.Server;
namespace KspControl.Host;
/// <summary>
/// craft_plan: read-only, no lease. The graph is validated on the host, catalog data for the parts it names is fetched from the bridge
/// (parts.construction_catalog, 32 names per call), and the pure planner runs here. The catalog fetch is folded into this tool on purpose:
/// a plan is only meaningful against one coherent catalog read, and part_definition already exposes single-part node data for graph authoring.
/// </summary>
[McpServerToolType]
public sealed class CraftPlanTools(ICatalogFetcher fetcher)
{
 [McpServerTool, Description("Plan a rocket from a part graph without changing the game. graph is a JSON object (at most 256 KiB, 1..250 parts): {name, facility:\"VAB\", root, parts:[{id, part, parent, parentNode, node, surface:{heightOffset, angleDegrees}, symmetry, stage}]}. The root part has no parent; stack parts name parentNode (node id on the parent) and node (node id on the part); radial parts use surface instead and may use symmetry 2..8. Free transforms are not accepted. Returns ok, issues[] (unknown_part, part_locked, part_not_buildable, unsupported_staging_topology and geometry issues), planHash and catalogHash (use planHash as expectedPlanHash when applying), topology T1, T2 or T3, every part with computed transforms (part-local metres from the root) and staging fields, and symmetry groups. catalog.unverified lists parts without live construction evidence for information only; they still plan. Surface placements are provisional (calibratedAtApply). Part names are the internal names from the parts tool. Invalid arguments return invalid_argument. Requires a connected game bridge to read part nodes.")]
 public Task<string> CraftPlan(
  [Description("Graph JSON object as a string, at most 262144 bytes, 1..250 parts.")] [StringLength(ConstructionLimits.MaxGraphBytes)] string graph,
  CancellationToken cancellationToken=default) => CraftPlanService.PlanAsync(graph,fetcher,cancellationToken);
}
