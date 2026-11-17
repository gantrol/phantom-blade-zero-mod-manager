#pragma once
#include <string>
#include <string_view>

namespace ArenaIsolation {
// Bounded command bridge. UObject access happens only inside the game's
// GetManagerState call; disk copies and verification run on a worker.
bool poll(std::wstring_view request, std::string& response);
}
