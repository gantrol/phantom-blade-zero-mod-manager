using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;

namespace PhantomBladeMap.Extractor;

// Package-scoped actor/property reader. It skips mesh payloads and retains no
// references after the caller advances to the next streamed cell.
sealed class ArchiveActorReader(IoPackage package, DefaultFileProvider provider, string path)
{
    readonly byte[] bytes = provider.SaveAsset(path);
    readonly Dictionary<int, UObject> objects = [];
    readonly Dictionary<int, Pose> poses = [];
    sealed record Pose(double[] Position, double[] Rotation, double[] Scale);

    public UObject Read(int index)
    {
        if (objects.TryGetValue(index, out var value)) return value;
        var entry = package.ExportMap[index];
        var offset = checked(package.Summary.TotalHeaderSize + (long)entry.CookedSerialOffset);
        using var archive = new FAssetArchive(new FByteArchive(path, bytes, provider.Versions), package);
        archive.Position = offset;
        var obj = new UObject { Name = package.ResolveObjectIndex(new CUE4Parse.UE4.IO.Objects.FPackageObjectIndex((ulong)index))!.Name.Text,
            Class = package.ResolveObjectIndex(entry.ClassIndex) };
        obj.Deserialize(archive, offset + (long)entry.CookedSerialSize);
        objects[index] = obj;
        return obj;
    }

    public double[] Position(int index) => Transform(index, []).Position;
    public double[] TransformPoint(int index, double[] point)
    {
        var pose = Transform(index, []);
        var shifted = Rotate(pose.Rotation, point.Zip(pose.Scale, (a, b) => a * b).ToArray());
        return shifted.Zip(pose.Position, (a, b) => a + b).ToArray();
    }
    public double[] Position(FPackageIndex reference)
    {
        if (reference.Index <= 0) throw new InvalidDataException("Scene transform is outside its streamed package");
        return Position(reference.Index - 1);
    }
    public double[] ActorPosition(int index)
    {
        var root = Read(index).GetOrDefault<FPackageIndex>("RootComponent");
        if (root is null || root.IsNull) throw new InvalidDataException("Actor root is not serialized");
        return Position(root);
    }

    Pose Transform(int index, HashSet<int> ancestry)
    {
        if (poses.TryGetValue(index, out var cached)) return cached;
        if (!ancestry.Add(index) || ancestry.Count > 64) throw new InvalidDataException("Cyclic scene attachment");
        var obj = Read(index);
        var p = obj.GetOrDefault("RelativeLocation", new FVector());
        var r = obj.GetOrDefault("RelativeRotation", new FRotator());
        var s = obj.GetOrDefault("RelativeScale3D", new FVector(1, 1, 1));
        var local = new Pose([p.X, p.Y, p.Z], Quaternion(r), [s.X, s.Y, s.Z]);
        var parent = obj.GetOrDefault<FPackageIndex>("AttachParent");
        if (parent is not null && !parent.IsNull)
        {
            if (parent.Index <= 0) throw new InvalidDataException("Attachment transform is outside its streamed package");
            var pose = Transform(parent.Index - 1, ancestry);
            var shifted = Rotate(pose.Rotation, local.Position.Zip(pose.Scale, (a, b) => a * b).ToArray());
            local = new Pose(obj.GetOrDefault("bAbsoluteLocation", false) ? local.Position : shifted.Zip(pose.Position, (a, b) => a + b).ToArray(),
                obj.GetOrDefault("bAbsoluteRotation", false) ? local.Rotation : Multiply(pose.Rotation, local.Rotation),
                obj.GetOrDefault("bAbsoluteScale", false) ? local.Scale : local.Scale.Zip(pose.Scale, (a, b) => a * b).ToArray());
        }
        ancestry.Remove(index);
        poses[index] = local;
        return local;
    }

    static double[] Quaternion(FRotator r)
    {
        var p = r.Pitch * Math.PI / 360; var y = r.Yaw * Math.PI / 360; var z = r.Roll * Math.PI / 360;
        var sp = Math.Sin(p); var cp = Math.Cos(p); var sy = Math.Sin(y); var cy = Math.Cos(y); var sr = Math.Sin(z); var cr = Math.Cos(z);
        return [cr*sp*sy-sr*cp*cy, -cr*sp*cy-sr*cp*sy, cr*cp*sy-sr*sp*cy, cr*cp*cy+sr*sp*sy];
    }
    static double[] Multiply(double[] a, double[] b) =>
        [a[3]*b[0]+a[0]*b[3]+a[1]*b[2]-a[2]*b[1], a[3]*b[1]-a[0]*b[2]+a[1]*b[3]+a[2]*b[0],
         a[3]*b[2]+a[0]*b[1]-a[1]*b[0]+a[2]*b[3], a[3]*b[3]-a[0]*b[0]-a[1]*b[1]-a[2]*b[2]];
    static double[] Rotate(double[] q, double[] p)
    {
        var value = Multiply(Multiply(q, [p[0], p[1], p[2], 0]), [-q[0], -q[1], -q[2], q[3]]);
        return [value[0], value[1], value[2]];
    }
}
