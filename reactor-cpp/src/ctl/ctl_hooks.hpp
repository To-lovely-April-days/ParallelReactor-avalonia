// 第二 / 第三阶段的扩展点。控制核心只认这些接口，不认具体实现；全部在控制线程上调用。
//
// 第二阶段（程序引擎 / 气路序列 / 泄漏测试 / 容积标定 / 自整定）实现 ProgramHooks、ActivityHooks，
// 并通过 ControlCore 的公开方法驱动设备（write_valves / set_valve_hold / start_fixed_heat / start_curve_with /
// begin_pressure_watch / start_uptake …），通过 add_gas_abort_listener 收到「无条件中止气路」。
// 第三阶段（命令层 / 控制租约 / 接口）实现 ModeHooks，并经 ControlCore::post / call 把命令投递到控制线程执行。
#pragma once
#include <cstdint>
#include <string>
#include <vector>

#include "nlohmann/json.hpp"

namespace reactor::ctl {

using ojson = nlohmann::ordered_json;

// 中止程序的来由（程序引擎据此写结束原因 / 选择收尾方式）
enum class AbortReason {
    StopAll,       // 全部停止
    ChannelStop,   // 该通道被停止 / 停用
    OverPressure,  // 超压联锁（持有气路的程序按失败「气路被超压联锁中止」收尾）
    Shutdown,      // 退出服务
};

// 程序引擎（第二阶段）。默认实现 = 没有程序在跑。
class ProgramHooks {
public:
    virtual ~ProgramHooks() = default;
    // 通道正被程序执行（命令层的 CHANNEL_PROGRAM_OWNED；快照 programOwned）
    virtual bool is_owned(int ch) const { (void)ch; return false; }
    // 中止全部程序，返回中止的个数（全部停止 / 退出）。实现方负责各自的收尾（不放空）
    virtual int abort_all(AbortReason why) { (void)why; return 0; }
    // 中止该通道的程序，返回是否中止了（单路停止 / 停用）。中止时由程序收尾停该路加热，核心据此跳过重复的 At=4
    virtual bool abort_channel(int ch, AbortReason why) { (void)ch; (void)why; return false; }
    // 全机搅拌的程序引用计数清空（core::StirShare::clear，全部停止时）
    virtual void clear_stir_share() {}
    // 快照 channels[i].program（C# ChannelProgramSnap），没有为 null
    virtual ojson channel_program(int ch) const { (void)ch; return nullptr; }
    // 快照 program（已废弃字段，C# ProgramSnap）
    virtual ojson legacy_program() const {
        return ojson{{"running", false}, {"channel", nullptr}, {"status", ""}, {"stepIndex", -1}, {"stepCount", 0},
                     {"phase", "准备运行"}, {"detail", "等待开始"}};
    }
    // 快照 programs（C# ProgramsSnap）
    virtual ojson programs() const {
        return ojson{{"running", 0}, {"channels", ojson::array()}, {"gasHolder", nullptr}, {"waitingGas", ojson::array()},
                     {"summary", ""}};
    }
    // GET /channels/{id}/program/run 的记录（C# ProgramRunSnap；本次开机没执行过为 null）。第二阶段新增
    virtual ojson program_run(int ch) const { (void)ch; return nullptr; }
    // 遥测 CSV「程序步」列（"3/running"），没有程序为空
    virtual std::string telemetry_step(int ch) const { (void)ch; return ""; }
    // 每个控制周期末（安全判定之后）调用一次
    virtual void on_tick(int64_t now_mono_ms) { (void)now_mono_ms; }
};

// 泄漏测试 / 容积标定的进行状态（第二阶段）。默认 = 本次开机没做过。
class ActivityHooks {
public:
    virtual ~ActivityHooks() = default;
    // 快照 leakTest（C# LeakSnap；没做过时的样子见默认实现）
    virtual ojson leak_test() const {
        return ojson{{"running", false},   {"progress", 0},      {"phase", nullptr},        {"runId", nullptr},
                     {"done", false},      {"failed", false},    {"stage", nullptr},        {"stageLeftSec", nullptr},
                     {"totalLeftSec", nullptr}, {"startedAt", nullptr}, {"startedBy", nullptr}, {"targetPsi", nullptr},
                     {"thresholdPsiH", nullptr}, {"channels", nullptr}, {"vented", nullptr},  {"aborted", false},
                     {"failReason", nullptr}, {"finishedAt", nullptr}};
    }
    // 快照 calib（C# CalibSnap；没有标定为 null）
    virtual ojson calib() const { return nullptr; }
    // 快照 autotune（新字段，C# 没有：各通道自整定的进度；本次开机没整定过为 null）。第二阶段新增
    virtual ojson autotune() const { return nullptr; }
};

// 控制权（第三阶段的租约）。默认 = 本地控制。
class ModeHooks {
public:
    virtual ~ModeHooks() = default;
    virtual ojson mode() const { return ojson{{"control", "local"}, {"holder", nullptr}}; }
};

}  // namespace reactor::ctl
