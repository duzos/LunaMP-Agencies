using KspControl.EditorModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace KspControl.EditorModel.Tests;
[TestClass] public class GraphTests
{
 private static Dictionary<string,PartDefinition> Catalog()=>new() {
  ["probe"]=new() { Name="probe",Unlocked=true,ConfigurationVerified=true,Nodes=new(){new(){Name="bottom",Kind=AttachmentKind.Stack,Orientation=new(0,-1,0),Position=new(0,-0.1,0)}} },
  ["tank"]=new() { Name="tank",Unlocked=true,ConfigurationVerified=true,AllowsSurfaceChildren=true,Nodes=new(){new(){Name="top",Kind=AttachmentKind.Stack,Orientation=new(0,1,0),Position=new(0,0.5,0)},new(){Name="bottom",Kind=AttachmentKind.Stack,Orientation=new(0,-1,0),Position=new(0,-0.5,0)}} },
  ["engine"]=new() { Name="engine",Unlocked=true,ConfigurationVerified=true,Nodes=new(){new(){Name="top",Kind=AttachmentKind.Stack,Orientation=new(0,1,0),Position=new(0,0.2,0)}} },
  ["panel"]=new() { Name="panel",Unlocked=true,ConfigurationVerified=true,Nodes=new(){new(){Name="surface",Kind=AttachmentKind.Surface,Orientation=new(1,0,0)}} }
 };
 private static CraftGraph Rocket()=>new() { Name="Original probe",RootId="core",Parts=new() {
  new(){Id="core",Definition="probe"},
  new(){Id="fuel",Definition="tank",ParentId="core",ParentNode="bottom",ChildNode="top",Position=new(0,-0.6,0)},
  new(){Id="motor",Definition="engine",ParentId="fuel",ParentNode="bottom",ChildNode="top",Position=new(0,-1.3,0),Stage=0},
  new(){Id="left",Definition="panel",ParentId="fuel",ChildNode="surface",Attachment=AttachmentKind.Surface,Position=new(-0.5,-0.6,0),SymmetrySet="panels"},
  new(){Id="right",Definition="panel",ParentId="fuel",ChildNode="surface",Attachment=AttachmentKind.Surface,Position=new(0.5,-0.6,0),SymmetrySet="panels"}
 } };
 private static void Has(CraftGraph graph,string code,Dictionary<string,PartDefinition>? catalog=null)=>Assert.IsTrue(GraphValidator.Validate(graph,catalog??Catalog()).Any(i=>i.Code==code),code);
 [TestMethod] public void OriginalStackAndRadialGraphPasses(){Assert.AreEqual(0,GraphValidator.Validate(Rocket(),Catalog()).Count);}
 [TestMethod] public void DuplicateAndMissingHandlesRejected(){var g=Rocket();g.Parts[1].Id="core";Has(g,"duplicate_part_id");g=Rocket();g.Parts[1].ParentId="absent";Has(g,"missing_parent");}
 [TestMethod] public void CyclesRejected(){var g=Rocket();g.Parts[1].ParentId="motor";Has(g,"attachment_cycle");}
 [TestMethod] public void MissingAndOccupiedNodesRejected(){var g=Rocket();g.Parts[2].ChildNode="missing";Has(g,"missing_child_node");g=Rocket();g.Parts[2].ParentId="core";Has(g,"node_occupied");}
 [TestMethod] public void LockedOrUnverifiedPartsRejected(){var c=Catalog();c["tank"].Unlocked=false;Has(Rocket(),"part_locked",c);c["engine"].ConfigurationVerified=false;Has(Rocket(),"configuration_unverified",c);}
 [TestMethod] public void NonfiniteAndInvalidRotationsRejected(){var g=Rocket();g.Parts[0].Position=new(double.NaN,0,0);Has(g,"invalid_transform");g=Rocket();g.Parts[0].Rotation=new(0,0,0,0);Has(g,"invalid_transform");}
 [TestMethod] public void StagingAndSymmetryValidated(){var g=Rocket();g.Parts[2].Stage=-2;Has(g,"invalid_stage");g=Rocket();g.Parts[4].Stage=4;Has(g,"invalid_symmetry_set");}
 [TestMethod] public void SurfaceRuleEnforced(){var c=Catalog();c["tank"].AllowsSurfaceChildren=false;Has(Rocket(),"surface_attachment_denied",c);}
 [TestMethod] public void AttachmentPositionUsesBothRotations(){var identity=new Rotation(0,0,0,1);var rotated=new Rotation(0,0,1,0);var result=AttachmentGeometry.StackPosition(new(5,5,0),rotated,new(0,-1,0),identity,new(0,2,0));Assert.AreEqual(5,result.X,1e-9);Assert.AreEqual(4,result.Y,1e-9);}
 [TestMethod] public void InvalidCatalogEntriesProduceDiagnostics() { var c=Catalog();c["tank"]=null!;Has(Rocket(),"unknown_part",c);c=Catalog();c["tank"].Nodes[0].Orientation=new();Has(Rocket(),"invalid_catalog_nodes",c); }
 [TestMethod] public void ExtremeFiniteGeometryAndOversizeNodeCatalogRejected() { Assert.ThrowsException<ArgumentException>(()=>AttachmentGeometry.Rotate(new(double.MaxValue,0,0),new(0,0,0,1))); var c=Catalog();c["tank"].Nodes.AddRange(Enumerable.Range(0,129).Select(i=>new AttachNodeDefinition {Name="node"+i,Orientation=new(0,1,0)}));Has(Rocket(),"invalid_catalog_nodes",c); }
 [TestMethod] public void ValidStackPassesGeometryGate() { Assert.AreEqual(0,StackGeometryValidator.Validate(Rocket(),Catalog()).Count); }
 [TestMethod] public void MisplacedAndSameFacingStackRejected() { var g=Rocket();g.Parts[1].Position=new(0,-10,0);Assert.IsTrue(StackGeometryValidator.Validate(g,Catalog()).Any(i=>i.Code=="stack_nodes_misaligned"));var c=Catalog();c["tank"].Nodes[0].Orientation=new(0,-1,0);Assert.IsTrue(StackGeometryValidator.Validate(Rocket(),c).Any(i=>i.Code=="stack_normals_not_opposed")); }
 [TestMethod] public void RotatedStackUsesCraftSpaceNodes() { var g=Rocket();g.Parts=g.Parts.Take(3).ToList();foreach(var p in g.Parts) { p.Position=new(-p.Position.X,-p.Position.Y,p.Position.Z);p.Rotation=new(0,0,1,0); } Assert.AreEqual(0,StackGeometryValidator.Validate(g,Catalog()).Count); }
 [TestMethod] public void GeometryTolerancesMustBeBounded() { Assert.ThrowsException<ArgumentException>(()=>StackGeometryValidator.Validate(Rocket(),Catalog(),double.NaN));Assert.ThrowsException<ArgumentException>(()=>StackGeometryValidator.Validate(Rocket(),Catalog(),10)); }
 [TestMethod] public void GeometryRejectsInvalidQuaternion(){Assert.ThrowsException<ArgumentException>(()=>AttachmentGeometry.StackPosition(new(),new(),new(),new(0,0,0,1),new()));}
}



