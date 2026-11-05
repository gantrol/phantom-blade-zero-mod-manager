using PhantomBladeMap.Core.Protocol;

namespace PhantomBladeMap.Core.Runtime;

// One in-flight calculation, no work queue. The next request always uses the
// latest view. A completed old-world result never becomes visible after travel.
internal sealed class NavigationView
{
    private readonly record struct Request(PbmMap? Map, string World, long Revision, AtlasRegion? Area, AtlasLayer? Layer, float X, float Y, float Z, float Range, bool Overview, uint Generation, long DiscoveryRevision, bool Browsing)
    {
        public bool Covers(Request next, float margin = .125f) => Area == next.Area && Layer == next.Layer && World == next.World && Map == next.Map && Generation == next.Generation && Range == next.Range &&
            Overview == next.Overview && Browsing == next.Browsing && MathF.Abs(X-next.X) < Range*margin && MathF.Abs(Y-next.Y) < Range*margin;
    }
    private NavigationGeometry published = new();
    private NavigationGeometry? spare;
    private Task<NavigationGeometry>? pending;
    private CancellationTokenSource? cancellation;
    private Request publishedRequest, pendingRequest;
    private ulong epoch, pendingEpoch;
    private bool visible;
    public bool Loading => pending is not null;
    public bool Limited => visible && published.Limited;
    public bool HasTerrain => visible && published.HasTerrain;
    public NavigationLayers? Layers => visible ? published.Layers : null;
    public ReadOnlySpan<MapFill> Fills => visible ? published.Fills : ReadOnlySpan<MapFill>.Empty;
    public ReadOnlySpan<MapLine> Lines => visible ? published.Lines : ReadOnlySpan<MapLine>.Empty;

    public void Reset()
    {
        visible = false;
        epoch++;
        publishedRequest = default;
        cancellation?.Cancel();
        CollectCompleted();
    }

    private void CollectCompleted()
    {
        if (pending is not { IsCompleted: true }) return;
        var finished = pending;
        pending = null;
        try
        {
            var result = finished.GetAwaiter().GetResult();
            if (pendingEpoch == epoch && !cancellation!.IsCancellationRequested)
            {
                spare = published; published = result; publishedRequest = pendingRequest;
            }
            else spare = result;
        }
        finally
        {
            cancellation!.Dispose(); cancellation = null;
            pendingRequest = default;
        }
    }

    public async Task StopAsync()
    {
        visible = false;
        cancellation?.Cancel();
        try { if (pending is not null) await pending.ConfigureAwait(false); }
        finally
        {
            cancellation?.Dispose(); cancellation = null; pending = null;
            pendingRequest = publishedRequest = default;
            published.Reset(); spare = null;
        }
    }

    public void Update(PbmMap? map, ExplorationStore exploration, string world, AtlasRegion? area, AtlasLayer? layer, float x, float y, float z, float range, bool overview, uint generation, DiscoveryMask? discovery, long discoveryRevision, bool browsing = false)
    {
        visible = false;
        var requested = new Request(map,world,exploration.Revision(world),area,layer,x,y,z,range,overview,generation,discoveryRevision,browsing);
        CollectCompleted();
        // Keep the previous, overscanned frame while its replacement is built.
        // Prefetch before its display boundary; never reuse a different session
        // or a different selected floor, even when the view coordinates match.
        visible = publishedRequest.Covers(requested) && (overview || browsing || published.Layers.Contains(z));
        var fresh = visible && publishedRequest.Covers(requested,.0625f) &&
            (overview || layer is not null || MathF.Abs(publishedRequest.Z-z)<50) &&
            publishedRequest.Revision == requested.Revision && publishedRequest.DiscoveryRevision == requested.DiscoveryRevision;
        if (fresh || pending is not null) return;
        var samples = exploration.Snapshot(world,x,y,range*1.125f);
        var worker = spare ?? new NavigationGeometry();
        spare = null; pendingRequest = requested; pendingEpoch = epoch;
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        pending = Task.Run(() =>
        {
            try { worker.Build(map,samples,area,layer,x,y,z,range,overview,token,discovery,browsing); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { worker.Reset(); }
            return worker;
        });
    }
}
