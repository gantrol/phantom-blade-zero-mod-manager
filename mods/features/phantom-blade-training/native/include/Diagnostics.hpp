#pragma once
#include <functional>
#include <string>
namespace arena::diagnostics {
#ifdef ARENA_DIAGNOSTICS
void start();
void tick();
void set_handler(std::function<std::string(const std::string&)>);
#else
inline void start() {}
inline void tick() {}
inline void set_handler(std::function<std::string(const std::string&)>) {}
#endif
}
