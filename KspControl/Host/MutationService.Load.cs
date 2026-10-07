using System.Collections.Concurrent;
using KspControl.Contracts;
using KspControl.EditorModel;
using Newtonsoft.Json.Linq;
namespace KspControl.Host;

/// <summary>editor_load_craft on the journaled mutation path (plan R3-section 4). It shares the replace-craft effect with an apply: both replace the editor craft.</summary>
public sealed partial class MutationService
{
 private readonly ConcurrentDictionary<string,string> operationLabels=new(StringComparer.Ordinal);

 /// <summary>The operation name an indeterminate envelope reports: the journal stores the effect, which an apply and a load share.</summary>
 private string OperationLabel(Job job)
 {
  if(job.Operation=="editor.restore_snapshot") return "restore_snapshot";
  return operationLabels.TryGetValue(job.RequestId,out var label) ? label : "apply_craft";
 }

 public Task<string> LoadAsync(string requestId,string leaseId,string expectedRevision,string facility,string fileName,string expectedSha256,bool allowUpgrade,CancellationToken cancellationToken)
 {
  var bad=MutationArguments.Common(requestId,leaseId,expectedRevision) ?? LoadArguments.File(facility,fileName,expectedSha256);
  if(bad!=null) return Task.FromResult(CraftPlanService.Invalid(bad));
  var args=new JObject { ["requestId"]=requestId,["facility"]=facility,["fileName"]=fileName,["expectedSha256"]=expectedSha256,["allowUpgrade"]=allowUpgrade,["expectedRevision"]=expectedRevision };
  operationLabels[requestId]="load_craft";
  return RunAsync(OperationEffects.ReplaceCraft,EditorOperations.LoadCraft,requestId,leaseId.ToLowerInvariant(),args,OperationLimits.WaitSecondsMax,cancellationToken);
 }
}

/// <summary>The argument rules of editor_load_craft, returning an invalid_argument detail or null.</summary>
public static class LoadArguments
{
 public static string? File(string? facility,string? fileName,string? expectedSha256)
 {
  if(!LoadLimits.IsFacility(facility)) return "facility must be VAB or SPH";
  const string extension=CraftPaths.CraftExtension;
  if(string.IsNullOrEmpty(fileName) || fileName.Length>LoadLimits.FileNameMax || !fileName.EndsWith(extension,StringComparison.OrdinalIgnoreCase) || !CraftPaths.IsName(fileName[..^extension.Length]))
   return "fileName must be a plain file name ending in .craft, as listed by craft_list, with no folder part";
  return LoadLimits.IsSha256(expectedSha256) ? null : "expectedSha256 must be 64 lower-case hex characters (the sha256 of the file)";
 }
}
