#pragma once
#include <cstdint>
#pragma pack(push, 4)
struct LivePoiSnapshot { uint8_t Kind, State; uint16_t Reserved; float X, Y, Z; };
struct MapLine { float X1, Y1, Z1, X2, Y2, Z2; uint32_t Kind; };
struct MapFill { float X, Y, Width, Height; uint32_t Kind; };
#pragma pack(pop)
#pragma pack(push, 8)
struct PbmBridgeContext {
    uint8_t HasPlayer, IsGamePaused, IsMouseCursorVisible, IsNativeMapOpen;
    uint32_t KeyJustPressedMask;
    float PlayerX, PlayerY, PlayerZ, PlayerYawDeg, ViewportWidth, ViewportHeight;
    double RealTimeSeconds;
    const uint8_t* WorldNameUtf8;
    int32_t WorldNameLen;
    uint32_t Generation;
    const LivePoiSnapshot* LivePoisPtr;
    int32_t LivePoiCount, ReservedInput;
    float PanX, PanY;
    uint8_t HudMode, ReservedMode1, ReservedMode2, ReservedMode3;
    float CanvasPosX, CanvasPosY, CanvasSizePx, RangeCm;
    int32_t ActiveFloor;
    float CenterX, CenterY, CenterZ;
    uint32_t FrameFlags;
    const LivePoiSnapshot* PointsPtr;
    int32_t PointCount, ReservedPoints;
    const MapLine* LinesPtr;
    int32_t LineCount, ReservedLines;
    const MapFill* FillsPtr;
    int32_t FillCount, ReservedFills;
    const uint8_t* TitleTextUtf8;
    const uint8_t* SubtitleTextUtf8;
    const uint8_t* FilterTextUtf8;
    const uint8_t* FooterTextUtf8;
    const uint8_t* FontAssetPathUtf8;
};
#pragma pack(pop)
using PbmInitializeFn = int32_t(__cdecl*)(const uint8_t* modRootUtf8, const uint8_t* localeUtf8, int32_t initialVisible);
using PbmTickFn = int32_t(__cdecl*)(PbmBridgeContext* ctx);
using PbmShutdownFn = void(__cdecl*)();
using PbmSetRegionFn = int32_t(__cdecl*)(uint32_t, const uint8_t*, int32_t, int32_t, float, int32_t);
using PbmExchangeSurveyFn = int32_t(__cdecl*)(const uint8_t*, int32_t, uint32_t, int32_t,
    const uint8_t*, int32_t, uint8_t*, int32_t);
using PbmExchangePoiCatalogFn = int32_t(__cdecl*)(const uint8_t*, int32_t, int32_t,
    const uint8_t*, int32_t, uint8_t*, int32_t);

using PbmControlFn = int32_t(__cdecl*)(const uint8_t*, int32_t, const uint8_t*, int32_t, uint8_t*, int32_t);
