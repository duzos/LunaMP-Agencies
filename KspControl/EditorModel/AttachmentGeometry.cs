using System;
namespace KspControl.EditorModel
{
    public static class AttachmentGeometry
    {
        private static bool Bounded(Vector value) => value.IsFinite && value.LengthSquared<=1e12;
        // Both rotations must already express the intended craft-space orientation.
        public static Vector StackPosition(Vector parentPosition, Rotation parentRotation, Vector parentNode, Rotation childRotation, Vector childNode)
        {
            if(!Bounded(parentPosition)||!Bounded(parentNode)||!Bounded(childNode)||!parentRotation.IsUnit||!childRotation.IsUnit) throw new ArgumentException("invalid_attachment_transform");
            var a=Rotate(parentNode,parentRotation); var b=Rotate(childNode,childRotation);
            var result=new Vector(parentPosition.X+a.X-b.X,parentPosition.Y+a.Y-b.Y,parentPosition.Z+a.Z-b.Z);
            if(!Bounded(result)) throw new ArgumentException("attachment_transform_out_of_bounds"); return result;
        }
        public static Vector Rotate(Vector v,Rotation q)
        {
            if(!Bounded(v)||!q.IsUnit) throw new ArgumentException("invalid_transform");
            var tx=2*(q.Y*v.Z-q.Z*v.Y);var ty=2*(q.Z*v.X-q.X*v.Z);var tz=2*(q.X*v.Y-q.Y*v.X);
            var result=new Vector(v.X+q.W*tx+q.Y*tz-q.Z*ty,v.Y+q.W*ty+q.Z*tx-q.X*tz,v.Z+q.W*tz+q.X*ty-q.Y*tx);
            if(!Bounded(result)) throw new ArgumentException("rotation_out_of_bounds"); return result;
        }
    }
}

