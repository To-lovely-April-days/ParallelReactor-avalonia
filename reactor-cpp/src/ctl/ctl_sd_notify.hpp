// systemd 通知（不依赖 libsystemd）：往环境变量 NOTIFY_SOCKET 指的 Unix 数据报套接字发一行文本。
// 用法：启动完成 sd_notify("READY=1")；控制线程每个周期 sd_notify("WATCHDOG=1")（配合 WatchdogSec）；退出前 "STOPPING=1"。
// NOTIFY_SOCKET 以 '@' 开头表示 Linux 抽象命名空间。没有设置 NOTIFY_SOCKET（开发机 / 测试）时什么也不做，返回 false。
// 线程：任何线程可调（每次新开一个套接字，不保存状态）。
#pragma once
#include <string>

namespace reactor::ctl {

bool sd_notify(const std::string& state);
// systemd 是否要求看门狗（WATCHDOG_USEC 已设置且 > 0）
bool sd_watchdog_enabled();

}  // namespace reactor::ctl
