// 最简单的日志：写到 stderr，带时间和级别；systemd 会收进 journal。线程安全。
#pragma once
#include <cstdarg>

namespace reactor::log {

enum class Level { Debug, Info, Warn, Error };
void set_level(Level l);
void write(Level l, const char* module, const char* fmt, ...)
#if defined(__GNUC__)
    __attribute__((format(printf, 3, 4)))
#endif
    ;

}  // namespace reactor::log

#define LOGD(mod, ...) ::reactor::log::write(::reactor::log::Level::Debug, mod, __VA_ARGS__)
#define LOGI(mod, ...) ::reactor::log::write(::reactor::log::Level::Info, mod, __VA_ARGS__)
#define LOGW(mod, ...) ::reactor::log::write(::reactor::log::Level::Warn, mod, __VA_ARGS__)
#define LOGE(mod, ...) ::reactor::log::write(::reactor::log::Level::Error, mod, __VA_ARGS__)
