using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;

namespace PhantomBladeMap.Extractor;

sealed record ActorRecord(string Class, string Name, string Package, double X, double Y, double Z, double Yaw, Dictionary<string, string> Save);

// Placed actors that matter for collection and traversal. Transforms need a
// game mapping file; without one only the class census is produced.
static partial class ActorExtractor
{
    [GeneratedRegex("Chest|SpecialMove_|Door|Bell|Pickup|PickUp|Collect|Reward|Treasure|MemoryFlower|RongShu|HuPo|Hupo|HeiShi|LongZhu|DaoCaoRen|Teleport|Item", RegexOptions.IgnoreCase)]
    private static partial Regex Interesting();

    public static (List<ActorRecord> Actors, SortedDictionary<string, int> Census, int Failures) Read(DefaultFileProvider provider, IEnumerable<string> packages, bool withTransforms)
    {
        var actors = new ConcurrentBag<ActorRecord>();
        var census = new ConcurrentDictionary<string, int>();
        var failures = 0;
        Parallel.ForEach(packages, new ParallelOptions { MaxDegreeOfParallelism = 6 }, path =>
        {
            if (provider.LoadPackage(path) is not IoPackage package) return;
            for (var i = 0; i < package.ExportMap.Length; i++)
            {
                var cls = package.ResolveObjectIndex(package.ExportMap[i].ClassIndex)?.Name.Text ?? "";
                if (!Interesting().IsMatch(cls) || cls.EndsWith("Component", StringComparison.Ordinal)) continue;
                census.AddOrUpdate(cls, 1, (_, n) => n + 1);
                if (!withTransforms) continue;
                try
                {
                    var actor = package.ExportsLazy[i].Value;
                    var root = actor.GetOrDefault<FPackageIndex>("RootComponent");
                    if (root is null || root.IsNull || root.Load() is not UObject component) continue;
                    var location = component.GetOrDefault("RelativeLocation", new FVector());
                    var rotation = component.GetOrDefault("RelativeRotation", new FRotator());
                    actors.Add(new ActorRecord(cls, actor.Name, path, location.X, location.Y, location.Z, rotation.Yaw, SaveFields(package, actor)));
                }
                catch { Interlocked.Increment(ref failures); }
            }
        });
        return (actors.OrderBy(a => a.Class).ThenBy(a => a.X).ThenBy(a => a.Y).ToList(), new SortedDictionary<string, int>(census), failures);
    }

    // Persistent identity lives on the actor's save component.
    static Dictionary<string, string> SaveFields(IoPackage package, UObject actor)
    {
        var fields = new Dictionary<string, string>();
        foreach (var lazy in package.ExportsLazy)
        {
            var export = lazy.Value;
            if (export.Outer?.Name.Text != actor.Name || !export.ExportType.Contains("Save", StringComparison.Ordinal)) continue;
            foreach (var property in export.Properties)
            {
                var value = property.Tag?.GenericValue?.ToString();
                if (!string.IsNullOrEmpty(value) && value.Length < 200) fields[property.Name.Text] = value;
            }
        }
        return fields;
    }
}
