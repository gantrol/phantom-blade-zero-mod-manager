using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace PhantomBladeMap.Core.Runtime;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public unsafe struct PbmBridgeContext
{
    public byte HasPlayer, IsGamePaused, IsMouseCursorVisible, IsNativeMapOpen;
    public uint KeyJustPressedMask;
    public float PlayerX, PlayerY, PlayerZ, PlayerYawDeg, ViewportWidth, ViewportHeight;
    public double RealTimeSeconds;
    public byte* WorldNameUtf8;
    public int WorldNameLen;
    public uint Generation;
    public LivePoiSnapshot* LivePoisPtr;
    public int LivePoiCount, ReservedInput;
    public float PanX, PanY;
    public byte HudMode, ReservedMode1, ReservedMode2, ReservedMode3;
    public float CanvasPosX, CanvasPosY, CanvasSizePx, RangeCm;
    public int ActiveFloor;
    public float CenterX, CenterY, CenterZ;
    public uint FrameFlags;
    public LivePoiSnapshot* PointsPtr;
    public int PointCount, ReservedPoints;
    public MapLine* LinesPtr;
    public int LineCount, ReservedLines;
    public MapFill* FillsPtr;
    public int FillCount, ReservedFills;
    public byte* TitleTextUtf8;
    public byte* SubtitleTextUtf8;
    public byte* FilterTextUtf8;
    public byte* FooterTextUtf8;
    public byte* FontAssetPathUtf8;
}

public static unsafe class NativeExports
{
    private static MapRuntimeController? controller;
    private static LivePoiSnapshot* points;
    private static MapLine* lines;
    private static MapFill* fills;
    private static byte* texts;
    private static Task retirement = Task.CompletedTask;
    private const int TextCapacity = 512;

    [UnmanagedCallersOnly(EntryPoint = "PbmExchangePoiCatalog", CallConvs = [typeof(CallConvCdecl)])]
    public static int ExchangePoiCatalog(byte* world, int worldLength, int pageOffset,
        byte* observations, int observationLength, byte* output, int capacity)
    {
        if (controller is null || world == null || output == null || worldLength is < 1 or > 1024 ||
            observationLength is < 0 or > 32768 || capacity != 32768 ||
            (observationLength > 0 && observations == null)) return -1;
        try
        {
            var result = controller.Catalog.Exchange(Encoding.UTF8.GetString(world, worldLength), pageOffset,
                observationLength == 0 ? "" : Encoding.UTF8.GetString(observations, observationLength));
            return Encoding.UTF8.GetBytes(result, new Span<byte>(output, capacity));
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmExchangeSurvey", CallConvs = [typeof(CallConvCdecl)])]
    public static int ExchangeSurvey(byte* world, int worldLength, uint generation, int page,
        byte* observations, int observationLength, byte* output, int capacity)
    {
        if (controller is null || world == null || output == null || worldLength is < 1 or > 1024 ||
            observationLength is < 0 or > 2048 || capacity != 8192 ||
            (observationLength > 0 && observations == null)) return -1;
        try
        {
            var result = controller.ExchangeSurvey(Encoding.UTF8.GetString(world, worldLength), generation, page,
                observationLength == 0 ? "" : Encoding.UTF8.GetString(observations, observationLength));
            return Encoding.UTF8.GetBytes(result, new Span<byte>(output, capacity));
        }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmControl", CallConvs = [typeof(CallConvCdecl)])]
    public static int Control(byte* operation, int operationLength, byte* payload, int payloadLength, byte* output, int capacity)
    {
        if (controller is null || operation == null || payload == null || output == null || operationLength is < 1 or > 16 || payloadLength is < 0 or > 8192 || capacity != 8192) return -1;
        try { return Encoding.UTF8.GetBytes(controller.Control(Encoding.UTF8.GetString(operation,operationLength),Encoding.UTF8.GetString(payload,payloadLength)),new Span<byte>(output,capacity)); }
        catch { return -1; }
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmSetRegion", CallConvs = [typeof(CallConvCdecl)])]
    public static int SetRegion(uint generation, byte* region, int length, int layer, float foot, int grounded)
    {
        if (controller is null || region == null || length is < 0 or > 64 || grounded is < 0 or > 1) return 0;
        try { controller.SetRegion(generation,Encoding.UTF8.GetString(region,length),layer,foot,grounded != 0); return 1; }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmInitialize", CallConvs = [typeof(CallConvCdecl)])]
    public static int Initialize(byte* root, byte* locale, int initialVisible)
    {
        try
        {
            if (controller is not null || !retirement.IsCompleted) return 0;
            if (sizeof(PbmBridgeContext) != 208 || sizeof(LivePoiSnapshot) != 16 || sizeof(MapLine) != 28 || sizeof(MapFill) != 20) return 0;
            controller = new MapRuntimeController(Marshal.PtrToStringUTF8((nint)root)!, Marshal.PtrToStringUTF8((nint)locale), initialVisible != 0);
            points = (LivePoiSnapshot*)NativeMemory.AllocZeroed((nuint)(sizeof(LivePoiSnapshot) * MapRuntimeController.MaxPoints));
            lines = (MapLine*)NativeMemory.AllocZeroed((nuint)(sizeof(MapLine) * MapRuntimeController.MaxLines));
            fills = (MapFill*)NativeMemory.AllocZeroed((nuint)(sizeof(MapFill) * NavigationGeometry.MaxFills));
            texts = (byte*)NativeMemory.AllocZeroed(5 * TextCapacity);
            return 1;
        }
        catch { Free(); return 0; }
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmTick", CallConvs = [typeof(CallConvCdecl)])]
    public static int Tick(PbmBridgeContext* c)
    {
        if (c == null || controller is null || c->WorldNameLen < 0 || c->WorldNameLen > 2048 || c->LivePoiCount < 0 || c->LivePoiCount > MapRuntimeController.MaxPoints) return 0;
        try
        {
            var world = c->WorldNameLen > 0 ? Encoding.UTF8.GetString(c->WorldNameUtf8, c->WorldNameLen) : "";
            controller.Tick(c->HasPlayer != 0, c->IsGamePaused != 0, c->IsMouseCursorVisible != 0, c->IsNativeMapOpen != 0,
                c->PlayerX, c->PlayerY, c->PlayerZ, c->PlayerYawDeg, c->ViewportWidth, c->ViewportHeight,
                c->RealTimeSeconds, (PbmHotkeyMask)c->KeyJustPressedMask, world, c->Generation,
                new ReadOnlySpan<LivePoiSnapshot>(c->LivePoisPtr, c->LivePoiCount), c->PanX, c->PanY);
            c->HudMode = (byte)controller.CurrentHudMode;
            c->CanvasPosX = controller.CanvasPosX; c->CanvasPosY = controller.CanvasPosY;
            c->CanvasSizePx = controller.CanvasSizePx; c->RangeCm = controller.RangeCm; c->ActiveFloor = controller.ActiveFloor;
            c->CenterX = controller.CenterX; c->CenterY = controller.CenterY; c->CenterZ = controller.CenterZ;
            c->FrameFlags = controller.FrameFlags;
            controller.Fills.CopyTo(new Span<MapFill>(fills, NavigationGeometry.MaxFills));
            c->FillsPtr = fills; c->FillCount = controller.Fills.Length;
            controller.Points.CopyTo(new Span<LivePoiSnapshot>(points, MapRuntimeController.MaxPoints));
            controller.Lines.CopyTo(new Span<MapLine>(lines, MapRuntimeController.MaxLines));
            c->PointsPtr = points; c->PointCount = controller.Points.Length;
            c->LinesPtr = lines; c->LineCount = controller.Lines.Length;
            c->TitleTextUtf8 = WriteText(controller.TitleText, 0);
            c->SubtitleTextUtf8 = WriteText(controller.SubtitleText, 1);
            c->FilterTextUtf8 = WriteText(controller.FilterText, 2);
            c->FooterTextUtf8 = WriteText(controller.FooterText, 3);
            c->FontAssetPathUtf8 = WriteText(controller.FontAssetPath, 4);
            return 1;
        }
        catch { return 0; }
    }

    private static byte* WriteText(string text, int slot)
    {
        var target = texts + slot * TextCapacity;
        Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), new Span<byte>(target, TextCapacity - 1), true,
            out _, out var written, out _);
        target[written] = 0;
        return target;
    }

    [UnmanagedCallersOnly(EntryPoint = "PbmShutdown", CallConvs = [typeof(CallConvCdecl)])]
    public static void Shutdown() => Free();

    private static void Free()
    {
        var previous = controller;
        controller = null;
        if (previous is not null)
        {
            // Workers own managed value snapshots only, never these ABI buffers.
            // Observe completion/failure even if there is no subsequent frame.
            retirement = previous.RetireAsync().ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        NativeMemory.Free(points); points = null;
        NativeMemory.Free(lines); lines = null;
        NativeMemory.Free(fills); fills = null;
        NativeMemory.Free(texts); texts = null;
    }
}
