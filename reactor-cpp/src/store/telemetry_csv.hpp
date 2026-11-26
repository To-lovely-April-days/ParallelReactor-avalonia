// 运行记录 telemetry-YYYYMMDD.csv 的表头与行格式（C#: Service/TelemetryCsv.cs）。
// 列顺序固定：只能在末尾追加新列，不得改名或调整顺序（DataLogger 发现同名文件表头不同会另起 -1 / -2 文件）。
// 纯函数，任意线程可调。
#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "store/store_util.hpp"

namespace reactor::store {

// 一个通道（RV1..8）一行的数据。拿不到的值保持空 → 空单元格（不写 0）。
struct TelemetryChannel {
    std::optional<double> pv;            // AI-8 PV ℃（温控离线时为空）
    std::optional<int64_t> pv_raw;       // PV 寄存器原码
    std::optional<double> sp_written;    // 最近一次成功写入仪表的 SP ℃
    std::optional<double> target_sp;     // 目标温度 ℃
    std::optional<double> output_pct;    // 输出 %
    std::optional<int64_t> at;           // 工作模式 At 原值（0 APID / 1 自整定 / 2 ON/OFF / 3 手动 / 4 停止）
    std::string approach;                // 防超调逼近阶段（Off/Settle/Creep/Hold），空 = 没有
    std::optional<double> approach_sp;   // 逼近当前给定值 ℃
    std::optional<double> curve_sp;      // 曲线 SP(t) ℃
    std::optional<int64_t> press_raw;    // 8AI 原码
    std::optional<double> press_psi;     // 压力 psi（表压）；MPa 列由它换算
    std::string state;                   // 通道状态名
    bool over_temp = false;              // 超温切断已触发
    std::optional<bool> valve;           // SV 进气阀（软件已确认下发的状态）
    std::string program_step;            // 程序步「序号/阶段」（3/running）；没有程序时为空（末尾追加列）
    std::optional<double> gas_mmol;      // 吸气量 mmol（末尾追加列）
};

// 一个通讯模块的统计
struct TelemetryModule {
    bool configured = false;
    bool online = false;
    int64_t polls = 0;
    int64_t fails = 0;
    std::optional<WallTime> last_ok_at;
};

// 一行运行记录（每个控制周期一行）
struct TelemetryRow {
    static constexpr int kChannelCount = 8;

    WallTime time;
    std::string marker;                  // 标记（多个用 " | " 连接）
    std::optional<std::string> session;  // 会话 testId（空 = 由 DataLogger 填当前会话）
    std::array<TelemetryChannel, kChannelCount> ch{};

    // 全局
    std::optional<int64_t> srun;          // AI-8 全局运行状态
    std::optional<int64_t> ctn;           // AI-8 启用回路数
    bool stir_on = false;
    std::optional<int64_t> stir_rpm_set;  // 釜里的设定转速
    std::optional<int64_t> stir_motor_rpm;   // 下发电机的转速（末尾追加列）
    std::optional<int64_t> stir_ramp_ms;
    std::optional<int64_t> stir_alarm;
    std::optional<double> bus_voltage;
    std::array<bool, 3> gas{};            // 惰性 / A / B 气源阀
    bool vent = false;
    bool estopped = false;                // 「急停」列保留（新系统没有急停，恒为 0）

    TelemetryModule temp, press, io, stir;
    bool sim = false;                     // 模拟模式
};

namespace telemetry_csv {

constexpr double kMpaPerPsi = 1 / 145.037738;   // C#: TelemetryCsv.MpaPerPsi

// 表头的列名（顺序即列序）
const std::vector<std::string>& header_fields();
// 表头行（不含换行；缓存）
const std::string& header();
// 一行的字段（未转义）
std::vector<std::string> row_fields(const TelemetryRow& r);
// 格式化一行（不含换行）
std::string format(const TelemetryRow& r);

// 事件日志 events-YYYYMMDD.csv
const std::string& event_header();   // 时间,级别,来源,标题,详情
std::string format_event(const WallTime& t, const std::string& level, const std::string& source, const std::string& title,
                         const std::string& detail);

}  // namespace telemetry_csv
}  // namespace reactor::store
