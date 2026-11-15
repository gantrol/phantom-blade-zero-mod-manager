#include <windows.h>
#include <bcrypt.h>
#include "MapSurfaceData.hpp"
#include "vendor/miniz/miniz.h"
#include <algorithm>
#include <array>
#include <fstream>
#include <map>
#include <memory>
#include <mutex>
#include <vector>

namespace MapSurfaceData {
namespace {
constexpr size_t MaxChunk = 1024 * 1024;
constexpr size_t MaxRequests = 4;
constexpr ULONGLONG ExpiryMs = 30000;
struct Job {
    std::filesystem::path file;
    ULONGLONG touched = GetTickCount64();
    bool running = false, done = false;
    std::string result;
};
std::filesystem::path root;
std::mutex mutex;
std::map<std::wstring, std::shared_ptr<Job>, std::less<>> jobs;

bool digits(std::wstring_view text) {
    return !text.empty() && text.size() <= 16 && std::all_of(text.begin(), text.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; });
}
bool coordinate(std::wstring_view text) {
    if (text.starts_with(L'-')) text.remove_prefix(1);
    return text.size() <= 7 && digits(text);
}
uint32_t u32(const uint8_t* p) {
    return uint32_t(p[0]) | uint32_t(p[1]) << 8 | uint32_t(p[2]) << 16 | uint32_t(p[3]) << 24;
}
std::string base64(const std::vector<uint8_t>& bytes) {
    constexpr char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string result; result.reserve((bytes.size() + 2) / 3 * 4);
    for (size_t i = 0; i < bytes.size(); i += 3) {
        const uint32_t value = uint32_t(bytes[i]) << 16 | (i + 1 < bytes.size() ? uint32_t(bytes[i + 1]) << 8 : 0)
            | (i + 2 < bytes.size() ? bytes[i + 2] : 0);
        result += alphabet[value >> 18]; result += alphabet[value >> 12 & 63];
        result += i + 1 < bytes.size() ? alphabet[value >> 6 & 63] : '=';
        result += i + 2 < bytes.size() ? alphabet[value & 63] : '=';
    }
    return result;
}
std::string read(const std::filesystem::path& file) {
    // Reject directory links as well as linked files; only this mod's data root is readable.
    for (auto path = file; !path.empty(); path = path.parent_path()) {
        const auto attr = GetFileAttributesW(path.c_str());
        if (attr == INVALID_FILE_ATTRIBUTES || (attr & FILE_ATTRIBUTE_REPARSE_POINT)) return "!SURFACE_PATH";
        if (path == path.parent_path()) break;
    }
    std::ifstream input(file, std::ios::binary | std::ios::ate);
    const auto length = input.tellg();
    if (length < 48 || length > static_cast<std::streamoff>(MaxChunk + 48)) return "!SURFACE_SIZE";
    std::vector<uint8_t> packed(static_cast<size_t>(length));
    input.seekg(0);
    if (!input.read(reinterpret_cast<char*>(packed.data()), length)) return "!SURFACE_READ";
    const auto rawSize = u32(packed.data() + 4), compressedSize = u32(packed.data() + 8);
    if (u32(packed.data()) != 0x31534250 || u32(packed.data() + 12) != 2 || rawSize < 12
        || rawSize > MaxChunk || compressedSize != packed.size() - 48) return "!SURFACE_HEADER";
    std::vector<uint8_t> raw(rawSize);
    tinfl_decompressor decoder; tinfl_init(&decoder);
    size_t consumed = compressedSize, produced = rawSize;
    const auto status = tinfl_decompress(&decoder, packed.data() + 48, &consumed, raw.data(), raw.data(), &produced,
        TINFL_FLAG_PARSE_ZLIB_HEADER | TINFL_FLAG_USING_NON_WRAPPING_OUTPUT_BUF);
    if (status != TINFL_STATUS_DONE || consumed != compressedSize || produced != rawSize) return "!SURFACE_DEFLATE";
    std::array<uint8_t, 32> digest{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, raw.data(), rawSize, digest.data(), 32) < 0
        || !std::equal(digest.begin(), digest.end(), packed.begin() + 16)) return "!SURFACE_CHECKSUM";
    // Transfer ASCII through the game's existing string bridge. Binary data is never evaluated as JS.
    return base64(raw);
}
}

void configure(const std::filesystem::path& directory) { root = directory; }

bool poll(std::wstring_view request, std::string& response) {
    constexpr std::wstring_view prefix = L"pbz-map-surface-v1/";
    if (!request.starts_with(prefix)) return false;
    response = "!SURFACE_REQUEST";
    request.remove_prefix(prefix.size());
    const auto slash = request.find(L'/');
    if (slash == request.npos || !digits(request.substr(0, slash))) return true;
    const auto owner = request.substr(0, slash + 1), relative = request.substr(slash + 1);
    if (relative == L"cancel") {
        // Worker locks protect only bounded queue bookkeeping, never file I/O.
        std::lock_guard lock(mutex);
        std::erase_if(jobs, [&](const auto& item) { return item.first.starts_with(owner); });
        response = "~"; return true;
    }
    std::unique_lock lock(mutex, std::try_to_lock);
    if (!lock.owns_lock()) { response = "~"; return true; }
    const auto separator = relative.find(L'/');
    if (separator == relative.npos) return true;
    const auto world = relative.substr(0, separator), key = relative.substr(separator + 1);
    if (world.empty() || world.size() > 64 || !std::all_of(world.begin(), world.end(), [](wchar_t c) {
        return (c >= L'A' && c <= L'Z') || (c >= L'a' && c <= L'z') || (c >= L'0' && c <= L'9') || c == L'_';
    })) return true;
    const auto underscore = key.find(L'_');
    if (underscore == key.npos || !coordinate(key.substr(0, underscore)) || !coordinate(key.substr(underscore + 1))) return true;
    const auto now = GetTickCount64();
    std::erase_if(jobs, [&](const auto& item) { return now - item.second->touched > ExpiryMs; });
    const auto found = jobs.find(request);
    if (found != jobs.end()) {
        found->second->touched = now;
        if (found->second->done) { response = std::move(found->second->result); jobs.erase(found); }
        else response = "~";
    } else {
        if (jobs.size() < MaxRequests) {
            auto job = std::make_shared<Job>();
            job->file = root / world / (std::wstring(key) + L".pbs");
            jobs.emplace(request, std::move(job));
        }
        response = "~";
    }
    return true;
}

void process() {
    for (;;) {
        std::shared_ptr<Job> job;
        {
            std::lock_guard lock(mutex);
            const auto found = std::find_if(jobs.begin(), jobs.end(), [](const auto& item) { return !item.second->running; });
            if (found == jobs.end()) return;
            job = found->second; job->running = true;
        }
        std::string result;
        try { result = read(job->file); }
        catch (...) { result = "!SURFACE_READ"; }
        {
            std::lock_guard lock(mutex);
            job->result = std::move(result); job->done = true;
        }
    }
}
}
