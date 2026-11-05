namespace PhantomBladeMap.Extractor;

// A real shared boundary, including the height on each side. Preserve its
// endpoints for corridor straightening; a midpoint alone loses the opening.
readonly record struct SharedPortal(float X0, float Y0, float X1, float Y1,
    float FromZ0, float FromZ1, float ToZ0, float ToZ1)
{
    public int EdgeFrom { get; init; }
    public int EdgeTo { get; init; }
    public float Width => MathF.Sqrt((X1-X0)*(X1-X0)+(Y1-Y0)*(Y1-Y0));
    public float HeightDelta => Math.Max(Math.Abs(FromZ0-ToZ0), Math.Abs(FromZ1-ToZ1));
    public (float X, float Y, float Z) Center => ((X0+X1)/2, (Y0+Y1)/2, (FromZ0+FromZ1+ToZ0+ToZ1)/4);
    // Keep the existing step allowance, but no extra character-width erosion:
    // cooked walkable polygons already account for the navigation agent.
    public bool Routable => Width >= 1f && HeightDelta <= 45f;
    public float[] Endpoints => [X0,Y0,FromZ0,ToZ0,X1,Y1,FromZ1,ToZ1];
}

static class PortalGeometry
{
    public static SharedPortal? Find(NavMesh mesh, int p, int q)
    {
        SharedPortal? best = null;
        for (var e=0; e<mesh.Count[p]; e++)
        {
            var (a,b)=mesh.Edge(p,e);
            var dx=mesh.Vx[b]-mesh.Vx[a]; var dy=mesh.Vy[b]-mesh.Vy[a]; var length=dx*dx+dy*dy;
            if (length<.0001f) continue;
            for (var f=0; f<mesh.Count[q]; f++)
            {
                var (c,d)=mesh.Edge(q,f);
                var ux=mesh.Vx[d]-mesh.Vx[c]; var uy=mesh.Vy[d]-mesh.Vy[c];
                if (dx*ux+dy*uy>=0) continue;
                var tc=((mesh.Vx[c]-mesh.Vx[a])*dx+(mesh.Vy[c]-mesh.Vy[a])*dy)/length;
                var td=((mesh.Vx[d]-mesh.Vx[a])*dx+(mesh.Vy[d]-mesh.Vy[a])*dy)/length;
                if (MathF.Abs((mesh.Vx[c]-mesh.Vx[a])*dy-(mesh.Vy[c]-mesh.Vy[a])*dx)>MathF.Sqrt(length)*2 ||
                    MathF.Abs((mesh.Vx[d]-mesh.Vx[a])*dy-(mesh.Vy[d]-mesh.Vy[a])*dx)>MathF.Sqrt(length)*2) continue;
                var lo=Math.Max(0,Math.Min(tc,td)); var hi=Math.Min(1,Math.Max(tc,td));
                if ((hi-lo)*MathF.Sqrt(length)<.01f) continue;
                float From(float t)=>mesh.Vz[a]+t*(mesh.Vz[b]-mesh.Vz[a]);
                float To(float t)=>mesh.Vz[c]+(t-tc)/(td-tc)*(mesh.Vz[d]-mesh.Vz[c]);
                var portal=new SharedPortal(mesh.Vx[a]+lo*dx,mesh.Vy[a]+lo*dy,mesh.Vx[a]+hi*dx,mesh.Vy[a]+hi*dy,
                    From(lo),From(hi),To(lo),To(hi)) { EdgeFrom=e, EdgeTo=f };
                if (best is null || (portal.Routable && !best.Value.Routable) ||
                    portal.Routable==best.Value.Routable && portal.Width>best.Value.Width) best=portal;
            }
        }
        return best;
    }
}
