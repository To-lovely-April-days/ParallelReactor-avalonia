#include "testing.hpp"

#include <cstring>

namespace testing {
std::vector<Case>& registry() { static std::vector<Case> r; return r; }
int g_fail = 0;
const char* g_current = "";
}  // namespace testing

int main(int argc, char** argv) {
    const char* filter = argc > 1 ? argv[1] : nullptr;
    int run = 0, failed_cases = 0;
    for (auto& c : testing::registry()) {
        std::string full = std::string(c.group) + "." + c.name;
        if (filter && full.find(filter) == std::string::npos) continue;
        testing::g_current = c.name;
        int before = testing::g_fail;
        c.fn();
        ++run;
        if (testing::g_fail != before) { ++failed_cases; std::printf("✗ %s\n", full.c_str()); }
    }
    std::printf("%d 个用例，%d 个失败，%d 处断言失败\n", run, failed_cases, testing::g_fail);
    return testing::g_fail ? 1 : 0;
}
