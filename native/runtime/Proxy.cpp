#include <windows.h>
#include <mutex>
#include <string>

extern "C" FARPROC ResolveVersionExport(unsigned index) {
    static std::once_flag once;
    static FARPROC exports[17]{};
    std::call_once(once, [] {
        wchar_t directory[MAX_PATH];
        const UINT length = GetSystemDirectoryW(directory, MAX_PATH);
        if (!length || length >= MAX_PATH) RaiseFailFastException(nullptr, nullptr, 0);
        const auto path = std::wstring(directory) + L"\\version.dll";
        const HMODULE module = LoadLibraryExW(path.c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!module) RaiseFailFastException(nullptr, nullptr, 0);
        const char* names[] = {
            "GetFileVersionInfoA",
            "GetFileVersionInfoByHandle",
            "GetFileVersionInfoExA",
            "GetFileVersionInfoExW",
            "GetFileVersionInfoSizeA",
            "GetFileVersionInfoSizeExA",
            "GetFileVersionInfoSizeExW",
            "GetFileVersionInfoSizeW",
            "GetFileVersionInfoW",
            "VerFindFileA",
            "VerFindFileW",
            "VerInstallFileA",
            "VerInstallFileW",
            "VerLanguageNameA",
            "VerLanguageNameW",
            "VerQueryValueA",
            "VerQueryValueW",
        };
        for (unsigned i = 0; i < 17; ++i) exports[i] = GetProcAddress(module, names[i]);
    });
    if (index >= 17 || !exports[index]) RaiseFailFastException(nullptr, nullptr, 0);
    return exports[index];
}
