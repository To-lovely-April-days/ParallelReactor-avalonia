// 时间：纯逻辑里一律把「现在」当参数传进来（便于测试）；只有控制线程 / 接口层调用这里取真实时间。
#pragma once
#include <cstdint>
#include <string>

namespace reactor {

// 单调时钟毫秒（开机以来，不受改系统时间影响）。用于超时、节拍、速率计算。
int64_t mono_ms();
// 墙上时间：UNIX 毫秒（UTC）。用于记录、事件时间戳。
int64_t wall_ms();
// UTC ISO-8601，带毫秒和 Z：2026-11-26T08:02:15.123Z（接口 JSON 用）
std::string iso_utc(int64_t wall_ms);
// 本地时间格式化，如 "%Y%m%d"（日文件名）、"%Y-%m-%d %H:%M:%S"（CSV 时间列）
std::string local_fmt(int64_t wall_ms, const char* fmt);
// 解析 ISO-8601（必须带时区：Z 或 ±hh:mm）；失败返回 false
bool parse_iso(const std::string& s, int64_t& out_wall_ms);

}  // namespace reactor
