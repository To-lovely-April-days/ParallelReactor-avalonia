// 零依赖的小测试框架：TEST(组, 名) { CHECK(...); CHECK_NEAR(a, b, eps); CHECK_EQ(a, b); }
// 所有 tests/**/*.cpp 链到一个 run_tests 里，`run_tests [过滤子串]` 运行。
#pragma once
#include <cmath>
#include <cstdio>
#include <functional>
#include <sstream>
#include <string>
#include <vector>

namespace testing {

struct Case { const char* group; const char* name; std::function<void()> fn; };
std::vector<Case>& registry();
extern int g_fail;
extern const char* g_current;

struct Reg { Reg(const char* g, const char* n, std::function<void()> f) { registry().push_back({g, n, std::move(f)}); } };

template <class A, class B>
inline std::string show(const A& a, const B& b) {
    std::ostringstream o; o << a << " vs " << b; return o.str();
}

}  // namespace testing

#define TEST_CAT2(a, b) a##b
#define TEST_CAT(a, b) TEST_CAT2(a, b)
#define TEST(G, N)                                                                     \
    static void TEST_CAT(test_fn_, __LINE__)();                                        \
    static ::testing::Reg TEST_CAT(test_reg_, __LINE__)(#G, #N, TEST_CAT(test_fn_, __LINE__)); \
    static void TEST_CAT(test_fn_, __LINE__)()

#define CHECK(c)                                                                                   \
    do { if (!(c)) { ++::testing::g_fail;                                                          \
        std::printf("  FAIL [%s] %s:%d  %s\n", ::testing::g_current, __FILE__, __LINE__, #c); } } while (0)
#define CHECK_EQ(a, b)                                                                             \
    do { auto _a = (a); auto _b = (b); if (!(_a == _b)) { ++::testing::g_fail;                    \
        std::printf("  FAIL [%s] %s:%d  %s == %s  (%s)\n", ::testing::g_current, __FILE__, __LINE__, #a, #b, \
                    ::testing::show(_a, _b).c_str()); } } while (0)
#define CHECK_NEAR(a, b, eps)                                                                      \
    do { double _a = (a), _b = (b); if (!(std::fabs(_a - _b) <= (eps))) { ++::testing::g_fail;    \
        std::printf("  FAIL [%s] %s:%d  %s ≈ %s  (%g vs %g)\n", ::testing::g_current, __FILE__, __LINE__, #a, #b, _a, _b); } } while (0)
