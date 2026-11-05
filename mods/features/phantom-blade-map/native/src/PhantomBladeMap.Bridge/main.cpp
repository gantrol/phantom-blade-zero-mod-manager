#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <Xinput.h>
#include <cmath>
#include <algorithm>
#include <array>
#include <cstring>
#include <filesystem>
#include <future>
#include <string>
#include <vector>
#include <Mod/CppUserModBase.hpp>
#include <LuaMadeSimple/LuaMadeSimple.hpp>
#include "core_abi.hpp"

// Only the pinned public SDK crosses the UE4SS boundary. Unreal reflection and
// UMG calls live in native_runtime.lua on the game's EngineTick thread.
using Lua = RC::LuaMadeSimple::Lua;

#pragma pack(push, 4)
struct FrameInput {
    uint8_t HasPlayer, IsGamePaused, IsMouseCursorVisible, IsNativeMapOpen;
    uint32_t Keys;
    float X, Y, Z, Yaw, Width, Height;
    double Time;
    uint32_t Generation;
};
struct FrameOutput {
    uint8_t Mode, Version;
    uint16_t Reserved;
    float X, Y, Size, Range;
    int32_t Floor;
    float CenterX, CenterY, CenterZ;
};
#pragma pack(pop)
static_assert(sizeof(FrameInput) == 44 && sizeof(FrameOutput) == 36);
static_assert(sizeof(LivePoiSnapshot) == 16 && sizeof(MapLine) == 28 && sizeof(MapFill) == 20);
static_assert(sizeof(PbmBridgeContext) == 208);

class PhantomBladeMapMod final : public RC::CppUserModBase {
    inline static PhantomBladeMapMod* instance = nullptr;
    HMODULE coreModule = nullptr;
    PbmInitializeFn initializeCore = nullptr;
    PbmTickFn tickCore = nullptr;
    PbmShutdownFn shutdownCore = nullptr;
    PbmExchangePoiCatalogFn exchangePoiCatalog = nullptr;
    PbmExchangeSurveyFn exchangeSurvey = nullptr;
    PbmSetRegionFn setRegion = nullptr;
    PbmControlFn control = nullptr;
    uint8_t lastMode = 0;
    std::future<int> initialization;
    bool started = false;
    bool ready = false;

    // System XInput only supplies values. It never injects inputs or calls the
    // game; B/A and all gameplay input remain untouched. Poll only in its map.
    HMODULE xinputModule = nullptr;
    using GetStateFn = DWORD(WINAPI*)(DWORD, XINPUT_STATE*);
    GetStateFn getState = nullptr;
    WORD previousButtons = 0;
    bool controllerActive = false;
    double nextZoom = 0;

    void ReadMapController(PbmBridgeContext& ctx) {
        if (!ctx.HasPlayer || !ctx.IsGamePaused || !ctx.IsNativeMapOpen) {
            controllerActive = false; previousButtons = 0; nextZoom = 0; return;
        }
        XINPUT_STATE state{};
        bool connected = false;
        if (getState) for (DWORD i = 0; i < XUSER_MAX_COUNT; i++)
            if (getState(i,&state) == ERROR_SUCCESS) { connected = true; break; }
        if (!connected) { controllerActive = false; return; }
        const auto& pad = state.Gamepad;
        WORD pressed = controllerActive ? pad.wButtons & ~previousButtons : 0;
        const auto oldButtons = previousButtons;
        previousButtons = pad.wButtons;
        if (!controllerActive) { controllerActive = true; nextZoom = ctx.RealTimeSeconds+.25; return; }
        const WORD sticks = XINPUT_GAMEPAD_LEFT_THUMB | XINPUT_GAMEPAD_RIGHT_THUMB;
        const WORD shoulders = XINPUT_GAMEPAD_LEFT_SHOULDER | XINPUT_GAMEPAD_RIGHT_SHOULDER;
        auto chord = [&](WORD mask, unsigned bit) {
            if ((pad.wButtons & mask) != mask) return false;
            if ((oldButtons & mask) != mask) ctx.KeyJustPressedMask |= 1u << bit;
            return true;
        };
        if (chord(sticks,4) || chord(shoulders,20)) return;
        if (lastMode != 2) {
            if ((pad.wButtons & XINPUT_GAMEPAD_LEFT_THUMB) && (pressed & XINPUT_GAMEPAD_RIGHT_SHOULDER)) { ctx.KeyJustPressedMask |= 1u << 5; return; }
            if (pressed & XINPUT_GAMEPAD_RIGHT_THUMB) ctx.KeyJustPressedMask |= 1u << 21;
            if (pressed & XINPUT_GAMEPAD_RIGHT_SHOULDER) ctx.KeyJustPressedMask |= 1u << 18;
            return;
        }
        auto axis = [](SHORT value) {
            float amount = std::abs(static_cast<float>(value));
            return amount <= XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE ? 0.f :
                std::copysign(std::min(1.f,(amount-XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE)/(32767.f-XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE)),static_cast<float>(value));
        };
        ctx.PanX = axis(pad.sThumbRX); ctx.PanY = axis(pad.sThumbRY);
        auto bind = [&](WORD button, unsigned bit) { if (pressed & button) ctx.KeyJustPressedMask |= 1u << bit; };
        bind(XINPUT_GAMEPAD_DPAD_RIGHT,0); bind(XINPUT_GAMEPAD_RIGHT_THUMB,1);
        bind(XINPUT_GAMEPAD_DPAD_UP,7); bind(XINPUT_GAMEPAD_DPAD_DOWN,8);
        bind(XINPUT_GAMEPAD_LEFT_THUMB,9); bind(XINPUT_GAMEPAD_LEFT_SHOULDER,12);
        bind(XINPUT_GAMEPAD_DPAD_LEFT,17);
        bind(XINPUT_GAMEPAD_RIGHT_SHOULDER,18);
        if (pad.bLeftTrigger <= XINPUT_GAMEPAD_TRIGGER_THRESHOLD && pad.bRightTrigger <= XINPUT_GAMEPAD_TRIGGER_THRESHOLD) nextZoom = 0;
        if (ctx.RealTimeSeconds >= nextZoom) {
            if (pad.bRightTrigger > XINPUT_GAMEPAD_TRIGGER_THRESHOLD) ctx.KeyJustPressedMask |= 1u << 10;
            else if (pad.bLeftTrigger > XINPUT_GAMEPAD_TRIGGER_THRESHOLD) ctx.KeyJustPressedMask |= 1u << 11;
            nextZoom = ctx.RealTimeSeconds+.2;
        }
    }

    static int Initialize(const Lua& lua) {
        auto& self = *instance;
        auto locale = std::string(lua.get_string());
        auto initialVisible = static_cast<int32_t>(lua.get_integer());
        if (!self.started) {
            self.started = true;
            HMODULE bridge = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                reinterpret_cast<LPCWSTR>(&Initialize), &bridge);
            std::wstring modulePath(32768, L'\0');
            auto length = GetModuleFileNameW(bridge, modulePath.data(), static_cast<DWORD>(modulePath.size()));
            if (length == 0 || length >= modulePath.size()) { lua.set_integer(-1); return 1; }
            modulePath.resize(length);
            auto dlls = std::filesystem::path(modulePath).parent_path();
            auto corePath = dlls / L"PhantomBladeMap.Core.dll";
            self.coreModule = LoadLibraryExW(corePath.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (!self.coreModule) { lua.set_integer(-1); return 1; }
            self.initializeCore = reinterpret_cast<PbmInitializeFn>(GetProcAddress(self.coreModule, "PbmInitialize"));
            self.tickCore = reinterpret_cast<PbmTickFn>(GetProcAddress(self.coreModule, "PbmTick"));
            self.shutdownCore = reinterpret_cast<PbmShutdownFn>(GetProcAddress(self.coreModule, "PbmShutdown"));
            self.exchangePoiCatalog = reinterpret_cast<PbmExchangePoiCatalogFn>(GetProcAddress(self.coreModule, "PbmExchangePoiCatalog"));
            self.exchangeSurvey = reinterpret_cast<PbmExchangeSurveyFn>(GetProcAddress(self.coreModule, "PbmExchangeSurvey"));
            self.control = reinterpret_cast<PbmControlFn>(GetProcAddress(self.coreModule,"PbmControl"));
            self.setRegion = reinterpret_cast<PbmSetRegionFn>(GetProcAddress(self.coreModule, "PbmSetRegion"));
            if (!self.control || !self.setRegion || !self.initializeCore || !self.tickCore || !self.shutdownCore || !self.exchangePoiCatalog || !self.exchangeSurvey) { lua.set_integer(-1); return 1; }
            auto utf8 = dlls.parent_path().u8string();
            std::string root(reinterpret_cast<const char*>(utf8.data()), utf8.size());
            // Cache/marker reads finish on a worker before the core is published.
            self.initialization = std::async(std::launch::async, [&self, root, locale, initialVisible] {
                self.xinputModule = LoadLibraryExW(L"xinput1_4.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
                if (self.xinputModule) self.getState = reinterpret_cast<GetStateFn>(GetProcAddress(self.xinputModule, "XInputGetState"));
                return self.initializeCore(reinterpret_cast<const uint8_t*>(root.c_str()),
                    reinterpret_cast<const uint8_t*>(locale.c_str()), initialVisible);
            });
        }
        if (self.initialization.valid() &&
            self.initialization.wait_for(std::chrono::seconds(0)) == std::future_status::ready)
            self.ready = self.initialization.get() != 0;
        lua.set_integer(self.ready ? 1 : self.initialization.valid() ? 0 : -1);
        return 1;
    }

    static size_t DecodePacket(std::string_view hex, void* target, size_t capacity) {
        if (hex.size() % 2 != 0 || hex.size() / 2 > capacity)
            throw std::runtime_error("native_packet_size");
        auto digit = [](char value) -> uint8_t {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            throw std::runtime_error("native_packet_encoding");
        };
        auto* bytes = static_cast<uint8_t*>(target);
        for (size_t i = 0; i < hex.size(); i += 2) bytes[i / 2] = (digit(hex[i]) << 4) | digit(hex[i + 1]);
        return hex.size() / 2;
    }

    template<class T, size_t Limit>
    static size_t CopyRecords(std::string_view hex, std::array<T, Limit>& target) {
        auto size = DecodePacket(hex, target.data(), sizeof(target));
        if (size % sizeof(T) != 0) throw std::runtime_error("native_record_size");
        return size / sizeof(T);
    }

    static int Tick(const Lua& lua) {
        // The SDK's get_string uses NUL termination. Small input packets use hex;
        // set_string(data, length) preserves the larger binary output buffers.
        FrameInput input{};
        auto frame = lua.get_string();
        if (DecodePacket(frame, &input, sizeof(input)) != sizeof(input)) throw std::runtime_error("native_frame_size");
        auto world = std::string(lua.get_string());
        std::array<LivePoiSnapshot, 192> pois{};
        auto poiCount = CopyRecords(lua.get_string(), pois);

        if (!instance->ready) throw std::runtime_error("native_core_not_ready");

        PbmBridgeContext ctx{};
        ctx.HasPlayer = input.HasPlayer; ctx.IsGamePaused = input.IsGamePaused;
        ctx.IsMouseCursorVisible = input.IsMouseCursorVisible; ctx.IsNativeMapOpen = input.IsNativeMapOpen;
        ctx.KeyJustPressedMask = input.Keys;
        ctx.PlayerX = input.X; ctx.PlayerY = input.Y; ctx.PlayerZ = input.Z; ctx.PlayerYawDeg = input.Yaw;
        ctx.ViewportWidth = input.Width; ctx.ViewportHeight = input.Height; ctx.RealTimeSeconds = input.Time;
        ctx.WorldNameUtf8 = reinterpret_cast<const uint8_t*>(world.data()); ctx.WorldNameLen = static_cast<int32_t>(world.size());
        ctx.LivePoisPtr = pois.data(); ctx.LivePoiCount = static_cast<int32_t>(poiCount);
        ctx.Generation = input.Generation;
        instance->ReadMapController(ctx);
        if (!instance->tickCore(&ctx)) throw std::runtime_error("native_core_tick_failed");
        if (ctx.PointCount < 0 || ctx.PointCount > 192 || ctx.LineCount < 0 || ctx.LineCount > 192 || ctx.FillCount < 0 || ctx.FillCount > 320)
            throw std::runtime_error("native_output_size");
        instance->lastMode = ctx.HudMode;
        FrameOutput output{ctx.HudMode, 4, static_cast<uint16_t>(ctx.FrameFlags | (instance->controllerActive ? 128u : 0u) | (ctx.KeyJustPressedMask & (32u | 64u))), ctx.CanvasPosX, ctx.CanvasPosY, ctx.CanvasSizePx, ctx.RangeCm, ctx.ActiveFloor, ctx.CenterX, ctx.CenterY, ctx.CenterZ};
        lua.set_string(reinterpret_cast<const char*>(&output), sizeof(output));
        lua.set_string(ctx.PointsPtr ? reinterpret_cast<const char*>(ctx.PointsPtr) : "", static_cast<size_t>(ctx.PointCount) * sizeof(LivePoiSnapshot));
        lua.set_string(ctx.LinesPtr ? reinterpret_cast<const char*>(ctx.LinesPtr) : "", static_cast<size_t>(ctx.LineCount) * sizeof(MapLine));
        lua.set_string(ctx.FillsPtr ? reinterpret_cast<const char*>(ctx.FillsPtr) : "", static_cast<size_t>(ctx.FillCount) * sizeof(MapFill));
        for (auto text : {ctx.TitleTextUtf8, ctx.SubtitleTextUtf8, ctx.FilterTextUtf8, ctx.FooterTextUtf8, ctx.FontAssetPathUtf8}) {
            auto value = text ? reinterpret_cast<const char*>(text) : "";
            lua.set_string(value, strnlen_s(value, 512));
        }
        return 9;
    }

    static int ExchangePoiCatalog(const Lua& lua) {
        auto world = std::string(lua.get_string());
        auto offset = lua.get_integer();
        auto observations = std::string(lua.get_string());
        if (!instance->ready || world.size() > 1024 || observations.size() > 32768 || offset < -1 || offset > 2048)
            throw std::runtime_error("catalog_packet_size");
        std::array<uint8_t, 32768> output{};
        auto length = instance->exchangePoiCatalog(reinterpret_cast<const uint8_t*>(world.data()),
            static_cast<int32_t>(world.size()), static_cast<int32_t>(offset),
            reinterpret_cast<const uint8_t*>(observations.data()), static_cast<int32_t>(observations.size()),
            output.data(), static_cast<int32_t>(output.size()));
        if (length < 0 || length > static_cast<int32_t>(output.size())) throw std::runtime_error("catalog_exchange_failed");
        lua.set_string(reinterpret_cast<const char*>(output.data()), static_cast<size_t>(length));
        return 1;
    }

    static int ExchangeSurvey(const Lua& lua) {
        auto world = std::string(lua.get_string());
        auto generation = lua.get_integer();
        auto offset = lua.get_integer();
        auto observations = std::string(lua.get_string());
        if (!instance->ready || world.empty() || world.size() > 1024 || observations.size() > 2048 ||
            generation < 0 || generation > UINT32_MAX || offset < -1 || offset > 16384)
            throw std::runtime_error("survey_packet_size");
        std::array<uint8_t, 8192> output{};
        auto length = instance->exchangeSurvey(reinterpret_cast<const uint8_t*>(world.data()),
            static_cast<int32_t>(world.size()), static_cast<uint32_t>(generation), static_cast<int32_t>(offset),
            reinterpret_cast<const uint8_t*>(observations.data()), static_cast<int32_t>(observations.size()),
            output.data(), static_cast<int32_t>(output.size()));
        if (length < 0 || length > static_cast<int32_t>(output.size())) throw std::runtime_error("survey_exchange_failed");
        lua.set_string(reinterpret_cast<const char*>(output.data()), static_cast<size_t>(length));
        return 1;
    }

    static int SetRegion(const Lua& lua) {
        auto generation = lua.get_integer();
        auto region = std::string(lua.get_string());
        auto layer = lua.get_integer();
        auto foot = lua.get_number();
        auto grounded = lua.get_integer();
        if (!instance->ready || generation < 0 || generation > UINT32_MAX || region.size() > 64 ||
            layer < -1 || layer > 15 || !std::isfinite(foot) || grounded < 0 || grounded > 1)
            throw std::runtime_error("region_packet_size");
        if (!instance->setRegion(static_cast<uint32_t>(generation),reinterpret_cast<const uint8_t*>(region.data()),
            static_cast<int32_t>(region.size()),static_cast<int32_t>(layer),static_cast<float>(foot),static_cast<int32_t>(grounded)))
            { lua.set_bool(false); return 1; }
        lua.set_bool(true);
        return 1;
    }

    static int Control(const Lua& lua) {
        auto operation=std::string(lua.get_string()); auto payload=std::string(lua.get_string());
        if (!instance->ready || operation.empty() || operation.size()>16 || payload.size()>8192) throw std::runtime_error("control_packet");
        std::array<uint8_t,8192> output{};
        auto length=instance->control(reinterpret_cast<const uint8_t*>(operation.data()),static_cast<int32_t>(operation.size()),
            reinterpret_cast<const uint8_t*>(payload.data()),static_cast<int32_t>(payload.size()),output.data(),static_cast<int32_t>(output.size()));
        if (length<0 || length>8192) throw std::runtime_error("control_failed");
        lua.set_string(reinterpret_cast<const char*>(output.data()),static_cast<size_t>(length)); return 1;
    }

    static void OnLuaStart(RC::CppUserModBase*, Lua&, Lua& main, Lua&, Lua*) {
        main.register_function("PbmNativeControl", &Control);
        main.register_function("PbmNativeInitialize", &Initialize);
        main.register_function("PbmNativeTick", &Tick);
        main.register_function("PbmNativePoiCatalog", &ExchangePoiCatalog);
        main.register_function("PbmNativeSurvey", &ExchangeSurvey);
        main.register_function("PbmNativeRegion", &SetRegion);
    }

public:
    PhantomBladeMapMod() {
        instance = this;
        ModName = L"PhantomBladeMap";
        ModVersion = L"0.2.1";
        ModDescription = L"PhantomBladeMap native core with game-thread Lua reflection";
        ModAuthors = L"PhantomBladeMap";
        register_on_lua_start(static_cast<RC::ModEvent_OnLuaStartSelf>(&OnLuaStart));
    }
    ~PhantomBladeMapMod() override {
        ready = false;
        if (initialization.valid()) initialization.wait();
        if (shutdownCore) shutdownCore();
        if (xinputModule) FreeLibrary(xinputModule);
        // NativeAOT owns process-lifetime runtime state: do not FreeLibrary it.
        instance = nullptr;
    }
};

extern "C" __declspec(dllexport) RC::CppUserModBase* start_mod() { return new PhantomBladeMapMod(); }
extern "C" __declspec(dllexport) void uninstall_mod(RC::CppUserModBase* mod) { delete mod; }
