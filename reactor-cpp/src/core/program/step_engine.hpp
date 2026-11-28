// v0.8 步骤的执行语义（STEPS-SPEC §1 / §2）——纯逻辑的显式状态机版本。
// C#: Models/StepEngine.cs（async/await 版：RunAsync / LeakAsync / ExchangeAsync / Stir / HeatAsync / PressAsync /
//     HoldAsync / ReactAsync / VentCoreAsync / EndAsync / FillToAsync / ExchangeCycleAsync / VentToAsync / LeakDropRate）
//
// 设计（设计文档 §4「每个通道一个显式状态机」）：
//   一个 StepRunner 执行一个冻结步骤。控制线程反复调用 tick(输入) → 输出：
//     - actions：按顺序执行的动作（写状态文字、记事件、开关阀、取 / 还气路、启停加热 / 稳压 / 计量、搅拌 …）；
//     - status：Running / Done / Fault；
//     - Running 时：wake_at_ms（到这个单调时刻再 tick）或 waiting（等某个外部事件完成后带着事件再 tick）。
//   tick 不读时钟、不做 I/O：「现在」、读数、外部事件都在 StepInput 里。提前 tick 是安全的（没到点 / 事件没到就原样返回）。
//   C# 里每一次 await 都对应这里的一个「等待点」，所以时序（0.2 s 读压、1 s / 2 s 判断、充气脉冲长度、静置 1 s）与 C# 一致。
//
// 跨步规则：进入 leak / exchange / press / vent / end 之前停止稳压（记「RVn 稳压结束（进入 x）」）并停止吸气计量。
// 「停止这一路」= Fault（程序失败，不自动放空；唯一例外：气密不通过且 onFail = stop 先放空再 Fault）。
// 气路租约：RequestGas 之后必须等 GasGranted；步骤用完或出错时输出 ReleaseGas（ctl 收到后全关阀并释放租约，同 C# DisposeAsync）。
#pragma once
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/program/params.hpp"
#include "core/program/program_limits.hpp"
#include "core/program/program_types.hpp"
#include "core/program/step_snapshot.hpp"
#include "core/program/text_format.hpp"
#include "core/stir.hpp"

namespace reactor::core::program {

namespace step_engine {
constexpr double kPollSec = 0.2;          // C#: StepEngine.PollSec（置换 / 放空读压间隔 s）
constexpr double kOfflineSec = 60;        // C#: StepEngine.OfflineSec（传感器离线超过这个时间按超时 / 故障 s）
constexpr double kHotVentC = 60;          // C#: StepEngine.HotVentC（高于这个温度放空记 warn ℃）
constexpr double kLeakTolPct = 2;         // C#: StepEngine.LeakTolPct（气密检查充气偏差 %）
constexpr double kLeakFillMaxSec = 180;   // C#: StepEngine.LeakFillMaxSec
constexpr double kLeakVentTo = 2.9;       // C#: StepEngine.LeakVentTo（psi）
constexpr double kLeakVentMaxSec = 120;   // C#: StepEngine.LeakVentMaxSec
constexpr double kFlushFillSec = 3;       // C#: StepEngine.FlushFillSec（吹总管充 s，ctl 的 LineFlush 用）
constexpr double kFlushVentSec = 5;       // C#: StepEngine.FlushVentSec（吹总管排 s）

// 开始前要停止稳压与计量的步骤（STEPS-SPEC §2）
bool breaks_gas(StepType t);
// 压降速率 psi/h（正 = 在降）：(P0 − P1·T0/T1) / (hold/60)，绝压 + 绝对温度补偿（core::leak_math::compensate）；任一温度读不到就不补偿
double leak_drop_rate(double p0, std::optional<double> t0, double p1, std::optional<double> t1, double hold_min);
}  // namespace step_engine

// 外部事件（tick 时带上；没有就是 None）
enum class StepEvent {
    None,
    GasGranted,      // RequestGas 的气路租约已取得（排队结束）
    LineFlushDone,   // LineFlush 吹总管已做完
    CurveFinished,   // RunCurve 的曲线已跑完
    CurveRejected,   // RunCurve 没有有效段（C# RunCurveAsync 返回 false），什么也没做
};

// Running 时在等什么
enum class Await { None, Gas, LineFlush, Curve };

struct StepInput {
    int64_t now_ms = 0;                      // 单调时间
    ProgramLimitSettings limits;             // 当前全局限值（设置可能在下发后被改）
    std::optional<double> temp_c;            // 釜温 PV（离线 nullopt）
    // 釜压 psi（离线 nullopt）。气路阶段（输出 fresh_pressure = true 时）请在 tick 前读一次最新值（C# ReadPressureAsync）。
    std::optional<double> pressure_psi;
    bool regulation_active = false;          // 这一路的后台稳压回路在运行
    bool uptake_counting = false;
    bool uptake_valid = false;
    double uptake_mmol = 0;
    std::optional<double> gas_rate;          // 最近 1 min 平均吸气速率 mmol/min
    std::optional<std::string> gas_block_vessel;   // GasBlockReason(needPressure: true)：null = 可以
    std::optional<std::string> gas_block_line;     // GasBlockReason(needPressure: false)
    std::optional<std::string> heat_block;         // HeatBlockReason()
    std::string approach_text;               // 逼近器状态文字（没有为空）
    int stir_rpm = 0;                        // 全局转速（釜转速）
    bool stir_on = false;
    std::vector<StirHolder> stir_holders;    // core::StirShare::holders()（登记顺序，最后一个 = 当前生效者）
    std::optional<std::string> end_reason;   // 当前运行的结束原因（ProgramRunInfo.end_reason；end 步骤写进事件）
    StepEvent event = StepEvent::None;
};

enum class ActionKind {
    Status,          // text = 运行状态一行文字
    Log,             // level（info / warn / err）, text = 标题, detail
    SetState,        // state
    SetPressureSp,   // value = psi（界面上的压力目标）
    StopRegulation,  // 停这一路的后台稳压
    StartRegulation, // gas, value = 目标 psi, value2 = 补气阈值 %
    StartUptake,     // text = 原因
    StopUptake,      // text = 原因（没在计量时 ctl 什么也不做）
    RequestGas,      // text = 占用任务名, flag = vessel（开这一路 SV、监视、需要压力读数）；之后等 GasGranted
    ReleaseGas,      // 全关阀并释放气路（C# IStepGas.DisposeAsync）
    Valves,          // valve（+ gas）：阀态由 ctl 经 ValveFrame 拼整帧下发（只在持有气路时出现）
    LineFlush,       // gas, count = 次数, text = 状态文字前缀（ctl 追加「 c/n · 阶段」）；之后等 LineFlushDone
    StartFixedHeat,  // value = 目标 ℃（防过冲逼近）
    RunCurve,        // segments, text = head；之后等 CurveFinished / CurveRejected
    StopHeat,        // text = 原因（停曲线 / 逼近 / 本路加热）
    StirRequest,     // count = rpm：StirShare.Request(rv, rpm) 并写全局转速
    StirSetOn,       // flag = on
    StirRelease,     // 释放这一路的搅拌持有（最后一个释放者才停；仍有持有者时恢复其转速并记事件）
    CloseOwnValve,   // 关这一路的 SV（开着时）
    SetEndReason,    // text = 结束原因（写进 ProgramRunInfo.end_reason）
};

enum class ValveCmd {
    AllClosed,    // 全关
    GasToVessel,  // 开 SVrv + 气源 gas（0 惰性 / 1 A / 2 B）
    VentVessel,   // 开 SVrv + 排空
};

struct Action {
    ActionKind kind = ActionKind::Status;
    std::string text;
    std::string level;
    std::string detail;
    ReactorState state = ReactorState::Idle;
    double value = 0;
    double value2 = 0;
    int gas = 0;
    int count = 0;
    bool flag = false;
    ValveCmd valve = ValveCmd::AllClosed;
    std::vector<Segment> segments;
};

enum class StepStatus { Running, Done, Fault };
// Step = StepFaultException（停止这一路）；Gas = GasFaultException（充压充不上去 / 读压失败）
enum class FaultKind { Step, Gas };

struct StepOutput {
    std::vector<Action> actions;
    StepStatus status = StepStatus::Running;
    int64_t wake_at_ms = -1;        // Running 且 waiting == None：到这个时刻再 tick
    Await waiting = Await::None;    // Running：在等的外部事件
    bool fresh_pressure = false;    // 下一次 tick 会用压力读数做气路判断：请在 tick 前读一次最新压力
    FaultKind fault_kind = FaultKind::Step;
    std::string fault;              // Fault 时的中文原因
};

struct RunnerConfig {
    int rv = 1;                                 // 通道号 1..8
    PressureUnit unit = PressureUnit::MPa;      // 文案里的压力单位
};

class StepRunner {
public:
    // head = 状态文字前缀（如「03/09 升温」）
    StepRunner(StepSnapshot step, std::string head, RunnerConfig cfg);
    ~StepRunner();
    StepRunner(StepRunner&&) noexcept;
    StepRunner& operator=(StepRunner&&) noexcept;
    StepRunner(const StepRunner&) = delete;
    StepRunner& operator=(const StepRunner&) = delete;

    StepOutput tick(const StepInput& in);
    // 中止（程序被停 / 超压联锁 / 全部停止）：返回收尾动作（持有气路时 ReleaseGas），之后 tick 不再做事。
    // 停稳压、停加热、关 SV、释放搅拌、停计量这些「程序收尾」由 ctl 的程序引擎做（C# ExecuteAsync 的 finally）。
    std::vector<Action> abort();

    const StepSnapshot& step() const;
    bool finished() const;
    bool gas_held() const;
    // 正在等的外部事件（取气路 / 吹总管 / 曲线）；没有在等为 Await::None。程序暂停据此判断要不要从头重做本步
    Await waiting() const;

    struct Impl;

private:
    std::unique_ptr<Impl> impl_;
};

}  // namespace reactor::core::program
