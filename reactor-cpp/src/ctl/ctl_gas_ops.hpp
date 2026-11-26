// 第二阶段的气路序列公共底层（C#: ViewModels/MainViewModel.Gas.cs 的 GasBlock / ThrowIfGasUnsafe / ApplyValvesAsync /
// HoldAsync / ReadPressureSampleAsync / RunPurgeAsync，以及 MainViewModel.HeatBlock）。
//
//   gas_block / heat_block：开始自动气路序列 / 启动加热前的公共检查（输入从控制核心的实时状态取一次，判定在 core 里）。
//   gas_unsafe：序列运行中的安全检查（C# ThrowIfGasUnsafe，文案逐字）。
//   GasOps：把「整帧写阀 → 保持 n 秒（每 ≤1 s 查一次 gas_unsafe，期间阀门服务每 1 s 重写当前帧作 IO16R 心跳）→ 即时读压」
//           这些 C# 里的 await 写成续体（回调）。每个续体带代号：cancel() 之后挂起的续体一律丢弃，所以中止 = cancel() + 收尾。
//           出错（写阀失败、读压失败、gas_unsafe）走 fault 回调（C# GasFaultException 的消息）。
//           when_idle(fn)：等在途的写阀都回来再执行（收尾全关之前调，保证最后落到 IO16R 的是全关帧）。
//
// 线程：全部只在控制线程用；回调也在控制线程上。
#pragma once
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "core/op_codes.hpp"
#include "core/valve_frame.hpp"
#include "ctl/ctl_core.hpp"
#include <memory>

namespace reactor::ctl {

namespace gas_ops {
constexpr int64_t kProgramDeadMs = 1000;  // 决定记录：程序步骤的全闭死区 1.0 s
constexpr int64_t kPurgeDeadMs = 500;     // 吹扫 / 泄漏测试 / 标定 0.5 s（core::purge_plan::kDeadSec）

// C# MainViewModel.GasBlock(needPressure, selfId, programWait)。program_owned / program_gas_pending 由程序引擎给
std::optional<core::GuardReject> gas_block(const ControlCore& core, bool need_pressure, std::optional<int> self_id, bool program_wait,
                                           const std::vector<int>& program_owned, const std::string& program_gas_pending);
// C# MainViewModel.HeatBlock(id)
std::optional<core::GuardReject> heat_block(const ControlCore& core, std::optional<int> id, core::PressureUnit unit);
// C# ThrowIfGasUnsafe：nullopt = 安全，否则原因（「RV3 报警」「RV2 超压（3.52 MPa）」/ 租约失效原因）
std::optional<std::string> gas_unsafe(const ControlCore& core, core::PressureUnit unit);
// 某路当前压力 psi：最近一次读数（即时读之后就是即时读的值）；压力离线 nullopt
std::optional<double> pressure_of(const ControlCore& core, int id);
// 釜温 ℃（压力温度补偿用）；温控离线或读数 ≤ 1 返回 NaN（C# LeakViewModel.TempOf）
double temp_or_nan(const ControlCore& core, int id);
}  // namespace gas_ops

class GasOps {
public:
    using Then = std::function<void()>;
    using Fault = std::function<void(const std::string& why)>;

    GasOps(ControlCore& core, core::PressureUnit unit) : core_(core), unit_(unit) {}
    ~GasOps() { cancel(); }
    GasOps(const GasOps&) = delete;
    GasOps& operator=(const GasOps&) = delete;

    // 这套序列以谁的身份占用气路：cancel() 只在气路空闲或仍归自己时才停阀门心跳（别的占用者的心跳不能被它停掉）
    void set_owner(core::GasOwner owner, std::optional<int> rv = std::nullopt) { owner_ = owner; owner_rv_ = rv; }
    // 出错时调用（每次开始一段序列前设置）
    void set_fault(Fault f) { fault_ = std::move(f); }
    // 额外的安全检查（在 gas_unsafe 之前；程序用来查流程令牌）。返回原因 = 按 fault 处理
    void set_extra_check(std::function<std::optional<std::string>()> f) { extra_ = std::move(f); }

    // 整帧写阀（核心唯一写阀入口：不安全帧 / 越权 SV 拒绝、切换插全关死区、失败重试 2 次）。成功后作为心跳帧保持
    void apply(const core::ValveFrame& f, Then then, int64_t dead_ms = gas_ops::kPurgeDeadMs);
    // 保持 sec 秒（C# HoldAsync）：每 ≤1 s 查一次安全；sec ≤ 0 时立即继续
    void hold(const core::ValveFrame& f, double sec, Then then);
    // 不带安全检查的等待（只是定时）
    void delay(double sec, Then then);
    // 即时读一次 8 路压力（失败再读一次，仍失败 → fault「压力读取失败（8AI 通讯）」）
    void read(std::function<void(const PressureSample&)> then);
    // 一次安全检查：不安全 → fault 并返回 false
    bool check();
    // 管路吹扫（C# RunPurgeAsync）：按 core::purge_plan 逐帧下发并保持；progress(循环, 总循环, 阶段)
    void purge(int gas, int cycles, double fill_sec, double vent_sec, std::function<void(int, int, const std::string&)> progress,
               Then then);

    // 丢弃全部挂起的续体、停定时器、停心跳（在途的写阀照常回来，只是不再继续）
    void cancel();
    // 等在途写阀全部回来（没有在途时立即执行）。不受 cancel 影响
    void when_idle(Then then);
    int in_flight() const { return inflight_; }
    uint64_t epoch() const { return epoch_; }

private:
    void fail(const std::string& why);
    void hold_slice(core::ValveFrame f, int64_t end_ms, uint64_t ep, Then then);
    void purge_next(std::shared_ptr<std::vector<core::PurgeAction>> plan, size_t i, int cycles,
                    std::function<void(int, int, const std::string&)> progress, Then then);

    ControlCore& core_;
    core::PressureUnit unit_;
    Fault fault_;
    std::function<std::optional<std::string>()> extra_;
    core::GasOwner owner_ = core::GasOwner::None;
    std::optional<int> owner_rv_;
    uint64_t epoch_ = 1;
    EventLoop::TimerId timer_ = 0;
    int inflight_ = 0;
    std::vector<Then> idle_;
};

}  // namespace reactor::ctl
