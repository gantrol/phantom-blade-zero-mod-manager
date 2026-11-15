#pragma once
#include <filesystem>
#include <string>
#include <string_view>

// Data-only bridge used by puerts.loadModule("pbz-map-surface-v1/...").
// poll never reads files or decompresses. process runs on the loader worker.
namespace MapSurfaceData {
void configure(const std::filesystem::path& directory);
bool poll(std::wstring_view request, std::string& response);
void process();
}
