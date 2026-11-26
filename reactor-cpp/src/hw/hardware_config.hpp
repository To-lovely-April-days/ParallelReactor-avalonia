// 硬件配置 hardware.json：4 条 RS485 总线的端口 / 波特率 / 站号 / 超时，加上模拟开关和几个设备参数。
// 代替 C# 的环境变量（Service/HardwareOptions.cs + run.sh）。默认值照 run.sh（端口、站号）+ 代码默认（波特率），见设计文档 §7。
//
// 文件格式（字段缺省 = 默认值；未知字段忽略）：
// {
//   "version": 1,
//   "sim": false,
//   "temp":  {"port": "/dev/ttyS5", "baud": 19200, "slave": 1, "parity": "N", "dataBits": 8, "stopBits": 1,
//             "timeoutMs": 300, "trace": "", "dpt": 1},
//   "stir":  {"port": "/dev/ttyS4", "baud": 38400, ...},
//   "press": {"port": "/dev/ttyS6", "baud": 9600, ..., "signal": "4-20", "fullScalePsi": 600},
//   "io":    {"port": "/dev/ttyS8", "baud": 9600, ..., "energize": "open"}
// }
// port 为空串 = 该模块未配置 → 离线主站（每笔事务失败）。trace 非空 = 把该总线每笔事务追加到这个文件。
//
// 模拟：sim=true 且**四个端口全为空**才真正模拟；sim=true 但配了任一端口 → 拒绝模拟、按真机跑（C# SimRefused，
// 防止现场配置残留 sim=true 时停止帧只写进内存）。开发机用 HardwareConfig::for_sim()。
//
// 线程：纯数据 + 纯函数。
#pragma once
#include <string>

#include "nlohmann/json.hpp"

namespace reactor::hw {

struct BusConfig {
    std::string port;
    int baud = 9600;
    int slave = 1;
    char parity = 'N';    // 'N' / 'E' / 'O'
    int data_bits = 8;    // 7 / 8
    int stop_bits = 1;    // 1 / 2
    int timeout_ms = 300; // C#: ModbusRtuMaster timeoutMs = 300
    std::string trace;    // 追踪文件路径，空 = 不追踪

    static BusConfig make(std::string port, int baud) {
        BusConfig b;
        b.port = std::move(port);
        b.baud = baud;
        return b;
    }
};

struct HardwareConfig {
    bool sim = false;
    BusConfig temp = BusConfig::make("/dev/ttyS5", 19200);
    BusConfig stir = BusConfig::make("/dev/ttyS4", 38400);
    BusConfig press = BusConfig::make("/dev/ttyS6", 9600);
    BusConfig io = BusConfig::make("/dev/ttyS8", 9600);
    int temp_dpt = 1;                    // C#: PR_TEMP_DPT
    std::string press_signal = "4-20";   // "4-20"（默认）或 "0-10"。C#: PR_PRESS_SIGNAL
    double press_fullscale_psi = 600;    // C#: PR_PRESS_FULLSCALE
    std::string io_energize = "open";    // "open"（通电 = 开，默认）或 "close"（常开阀）。C#: PR_IO_ENERGIZE

    bool any_port_configured() const {
        return !temp.port.empty() || !stir.port.empty() || !press.port.empty() || !io.port.empty();
    }
    bool sim_refused() const { return sim && any_port_configured(); }
    bool simulated() const { return sim && !any_port_configured(); }
    bool press_4to20() const { return press_signal != "0-10"; }
    bool io_energize_opens() const { return io_energize != "close"; }

    // 开发机模拟用：sim=true，四个端口清空，其余默认。
    static HardwareConfig for_sim();
};

void to_json(nlohmann::json& j, const HardwareConfig& c);
// 字段缺省用默认值。类型不对 / 取值越界返回 false，err 写明哪个字段（中文）；out 只在成功时改。
bool hardware_config_from_json(const nlohmann::json& j, HardwareConfig& out, std::string* err);
// 检查取值范围（波特率在支持表内、站号 1..247、超时 50..5000、dpt 0..3、满量程 > 0、signal / energize 合法）。
bool validate_hardware_config(const HardwareConfig& c, std::string* err);

// 读文件。文件不存在 / 读不出 / 解析失败 / 取值非法都返回 false（err 写原因），out 不变。调用方决定是否用默认值。
bool load_hardware_config(const std::string& path, HardwareConfig& out, std::string* err);
// 写文件（先写 path.tmp 再 rename，原子替换），带缩进。
bool save_hardware_config(const std::string& path, const HardwareConfig& c, std::string* err);

}  // namespace reactor::hw
