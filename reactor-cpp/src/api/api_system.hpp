// 接口层用到的系统操作：起子进程（不经 shell，argv 直接 execvp，带超时）、NTP 同步探测、NetworkManager（nmcli）、U 盘。
// 任意线程可调（会阻塞调用线程，最多 timeout_ms），只在 net 的工作线程上用。
#pragma once
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace reactor::api {

struct ProcResult {
    bool started = false;     // execvp 成功（程序存在）
    bool timed_out = false;
    int exit_code = -1;
    std::string out;          // stdout（最多 1 MB）
    std::string err;          // stderr（最多 64 KB）
};
// argv[0] 按 PATH 查找；不经 shell，参数原样传（不会被解释成命令）。超时 SIGKILL。
ProcResult run_process(const std::vector<std::string>& argv, int64_t timeout_ms);

// Linux：timedatectl show -p NTPSynchronized --value（2 s）；其他平台 / 失败为空
std::optional<bool> probe_clock_synced();

// —— NetworkManager（nmcli -t 输出解析）——
// nmcli 的 -t 输出按 ':' 分列，字段里的 ':' 和 '\\' 用反斜杠转义
std::vector<std::string> nmcli_split(const std::string& line);
struct WifiNet {
    std::string ssid, security;
    int signal = 0;
    bool in_use = false;
};
std::vector<WifiNet> parse_wifi_list(const std::string& out);

// —— U 盘 ——
struct UsbVolume {
    std::string path, label;
    int64_t free_bytes = 0, total_bytes = 0;
};
// root 下的挂载点（/media/<用户>/<卷> 或 /media/<卷>）里可写的目录
std::vector<UsbVolume> list_usb(const std::string& root);

}  // namespace reactor::api
