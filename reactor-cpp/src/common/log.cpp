#include "common/log.hpp"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <ctime>
#include <mutex>
#include <cstdarg>

namespace reactor::log {

namespace {
std::atomic<int> g_level{static_cast<int>(Level::Info)};
std::mutex g_mu;
const char* name(Level l) {
    switch (l) { case Level::Debug: return "D"; case Level::Info: return "I"; case Level::Warn: return "W"; default: return "E"; }
}
}  // namespace

void set_level(Level l) { g_level = static_cast<int>(l); }

void write(Level l, const char* module, const char* fmt, ...) {
    if (static_cast<int>(l) < g_level.load()) return;
    using namespace std::chrono;
    auto now = system_clock::now();
    std::time_t t = system_clock::to_time_t(now);
    int ms = static_cast<int>(duration_cast<milliseconds>(now.time_since_epoch()).count() % 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char ts[32];
    std::strftime(ts, sizeof ts, "%H:%M:%S", &tm);
    char msg[2048];
    va_list ap;
    va_start(ap, fmt);
    std::vsnprintf(msg, sizeof msg, fmt, ap);
    va_end(ap);
    std::lock_guard<std::mutex> g(g_mu);
    std::fprintf(stderr, "%s.%03d %s [%s] %s\n", ts, ms, name(l), module, msg);
}

}  // namespace reactor::log
