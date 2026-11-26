// 每个控制周期末生成的不可变快照（设计文档 §4「共享方式」）。C#: Contracts/.../Api/TelemetrySnapshot.cs（SnapshotBundle）+ Api/Hmi/SnapshotBuilder.cs
//
// Snapshot = 对外遥测帧（WS telemetry / GET /snapshot）+ 接口层其他只读端点要用的附加数据（全量通道含曲线、设置副本、
// 门闸统计、4 模块通讯统计、报警表副本、PID 档位状态）。接口线程只读它，不碰控制线程的任何可变状态。
// to_json() 输出的字段名、嵌套、顺序与 C# TelemetrySnapshot 逐字一致（camelCase，null 照写，整数值的 double 写成整数）；
// 急停字段按设计文档 §5.3 恒为 active=false / 各部分 "pending"。
//
// 线程：Snapshot 构造后不可变，任何线程可读。SnapshotStore::publish 只由控制线程调；latest / subscribe 任何线程可调。
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "ctl/ctl_alarm_table.hpp"
#include "ctl/ctl_hooks.hpp"
#include "store/settings.hpp"

namespace reactor::ctl {

struct ApproachSnap {
    std::string phase;  // off / settle / creep / hold
    std::optional<double> target_c, current_sp_c;
    std::string status_text;
};

struct ProfileSegmentSnap {
    int index = 0;  // 1..16
    double temp_c = 0, minutes = 0;
    bool enabled = false, active = false;
};

struct ProfileSnap {
    bool running = false;
    int seg_count = 0;
    std::vector<ProfileSegmentSnap> segments;
    double elapsed_min = 0, total_minutes = 0;
    bool complete = false;
    std::string status_text;
};

struct ChannelSnap {
    int id = 0;
    bool installed = true;
    std::string state;
    std::optional<std::string> alarm_cause;  // overTemp / overPressure（仅 state=alarm）
    bool over_temp_tripped = false, over_pressure_tripped = false;
    std::optional<std::string> started_by;
    std::optional<double> temp_c;
    double temp_sp_c = 0;
    std::optional<double> temp_sp_device_c, heat_output_pct;
    bool temp_online = false, autotuning = false;
    std::optional<int> at_mode;
    std::string sp_mode = "fixed";
    ApproachSnap approach;
    ProfileSnap profile;
    std::optional<double> pressure_psi;
    std::optional<int> pressure_raw;
    double pressure_sp_psi = 0;
    std::optional<double> gas_consumed;
    bool valve_open = false, program_owned = false;
    ojson program;  // ChannelProgramSnap 或 null（第二阶段提供）
    std::optional<double> gas_rate, solution_volume;
};

struct StirSnap {
    bool on = false;
    int rpm_sp = 0;
    bool online = false;
    std::optional<double> motor_current_a, bus_voltage_v;
    std::optional<std::string> fault;
    std::optional<int> alarm_code;
    int ramp_ms_per_1000rpm = 1000;
};

struct GasSnap {
    std::array<bool, 3> inlets{};  // inert / A / B
    bool vent_open = false, manual_valve = false;
    std::optional<std::string> task, owner;  // owner：program / leak / calib
};

struct SessionSnap {
    bool active = false;
    std::optional<std::string> id, test_id, file;
};

struct CommSnap {
    bool temp = false, pressure = false, stir = false, io = false;
};

struct TickStatsSnap {
    double interval_ms = 1200;
    int64_t entered = 0, skipped = 0, overruns = 0;
    double last_took_ms = 0, max_took_ms = 0, last_overrun_ms = 0;
};

struct ModuleCommSnap {
    bool configured = false, online = false;
    int64_t polls = 0, fails = 0;
    std::optional<int64_t> last_ok_at_ms, values_at_ms;
};

struct Snapshot {
    // ---- 对外帧 ----
    int64_t seq = 0;
    int64_t ts_ms = 0;
    bool simulated = false;
    int64_t event_seq_applied = 0;
    ojson mode;
    CommSnap comm;
    std::array<ChannelSnap, 8> channels{};  // 全量（含曲线）；帧里非 curve 通道的 profile 写 null
    StirSnap stir;
    GasSnap gas;
    ojson program, programs, leak_test, calib;
    SessionSnap session;
    ojson autotune;  // 新字段（第二阶段）：帧末尾的 "autotune"，C# 没有（PC 端忽略未知字段）

    // ---- 附加（接口层其他端点） ----
    bool sim_refused = false;
    std::vector<int> installed;
    store::SettingsState settings;
    TickStatsSnap tick;
    ModuleCommSnap temp_comm, press_comm, stir_comm, io_comm;
    int64_t logger_dropped_lines = 0;
    std::vector<AlarmItem> alarms;          // 报警表副本（新的在前）
    std::array<int, 8> live_band{};         // 各通道仪表里当前生效的 PID 档（-1 未知）
    std::array<ojson, 8> program_runs{};    // GET /channels/{id}/program/run（C# ProgramRunSnap；null = 本次开机没执行过）
};

// 遥测帧 JSON（C# TelemetrySnapshot）
ojson to_json(const Snapshot& s);
// 单通道 JSON（C# ChannelSnap）；with_profile=false 时 profile 写 null
ojson channel_to_json(const ChannelSnap& c, bool with_profile);
ojson tick_stats_to_json(const TickStatsSnap& t);
ojson module_comm_to_json(const ModuleCommSnap& m);

class SnapshotStore {
public:
    using Ptr = std::shared_ptr<const Snapshot>;
    void publish(Ptr s);
    // 只换掉「最新」那份、不通知订阅者（设置改了以后马上让 GET /settings 读到新版本，不等下一个周期）
    void replace_latest(Ptr s);
    Ptr latest() const;  // 首个 Tick 之前为空（接口层回 503 STARTING）
    int subscribe(std::function<void(const Ptr&)> fn);  // 回调在控制线程上，只能非阻塞入队
    void unsubscribe(int id);

private:
    Ptr cur_;  // 用 std::atomic_load / atomic_store 读写
    mutable std::mutex sub_mu_;
    std::vector<std::pair<int, std::function<void(const Ptr&)>>> subs_;
    int next_id_ = 1;
};

}  // namespace reactor::ctl
