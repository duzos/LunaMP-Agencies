using System;
using System.Collections.Generic;
using System.Linq;
namespace KspControl.EditorModel
{
    /// <summary>Separate necessary import gate; does not prove collision clearance, symmetry or flight stability.</summary>
    public static class StackGeometryValidator
    {
        public static IReadOnlyList<GraphIssue> Validate(CraftGraph graph,IReadOnlyDictionary<string,PartDefinition> catalog,double positionToleranceMetres=0.001,double opposingCosineTolerance=0.0001)
        {
            if(!Vector.Finite(positionToleranceMetres)||positionToleranceMetres<=0||positionToleranceMetres>0.1||!Vector.Finite(opposingCosineTolerance)||opposingCosineTolerance<=0||opposingCosineTolerance>0.01) throw new ArgumentException("invalid_geometry_tolerance");
            var issues=GraphValidator.Validate(graph,catalog).ToList();
            if(issues.Count!=0) return issues;
            // Caller owns this detached graph during validation. No game objects or callbacks are consulted.
            var parts=graph.Parts.ToDictionary(p=>p.Id,StringComparer.Ordinal);
            foreach(var child in graph.Parts)
            {
                if(child.Id==graph.RootId||child.Attachment!=AttachmentKind.Stack) continue;
                var parent=parts[child.ParentId];
                var parentNode=catalog[parent.Definition].Nodes.Single(n=>n.Name==child.ParentNode);
                var childNode=catalog[child.Definition].Nodes.Single(n=>n.Name==child.ChildNode);
                try
                {
                    var expected=AttachmentGeometry.StackPosition(parent.Position,parent.Rotation,parentNode.Position,child.Rotation,childNode.Position);
                    var difference=new Vector(expected.X-child.Position.X,expected.Y-child.Position.Y,expected.Z-child.Position.Z);
                    if(difference.LengthSquared>positionToleranceMetres*positionToleranceMetres) issues.Add(new GraphIssue("stack_nodes_misaligned",child.Id));
                    var a=AttachmentGeometry.Rotate(parentNode.Orientation,parent.Rotation);
                    var b=AttachmentGeometry.Rotate(childNode.Orientation,child.Rotation);
                    var cosine=(a.X*b.X+a.Y*b.Y+a.Z*b.Z)/Math.Sqrt(a.LengthSquared*b.LengthSquared);
                    if(!Vector.Finite(cosine)||cosine > -1+opposingCosineTolerance) issues.Add(new GraphIssue("stack_normals_not_opposed",child.Id));
                }
                catch(ArgumentException) { issues.Add(new GraphIssue("stack_geometry_out_of_bounds",child.Id)); }
            }
            return issues;
        }
    }
}
