// 程序引擎内部：一次运行（C#: ViewModels/ProgramRun.cs）。只给 ctl_program_engine*.cpp 用。
#pragma once
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/program/text_format.hpp"
#include "core/temp_profile.hpp"
#include "ctl/ctl_program_engine.hpp"

namespace reactor::ctl {

// 排队取气路（C# AcquireGasAsync 的状态）
struct ProgramEngine::Acquire {
    bool active = false;
    uint64_t gen = 0;  // 发起时的 Run::gen（中止后作废）
    std::string task;
    bool vessel = true;
    bool quiet = false;
    bool queued = false;
    int64_t ticket = 0;
    int64_t since = 0;
    core::ReactorState state_at = core::ReactorState::Idle;
    std::string holder, base_status;
    bool announced = false;
    std::optional<std::string> blocked_why;
    EventLoop::TimerId timer = 0;
    std::function<void(int64_t)> granted;
    std::function<void(const std::string&)> failed;
    std::function<void()> aborted;
};

// 一个步骤在运行记录里的状态（C# ProgramRunStep：pending / running / completed / failed / aborted）
struct StepRec {
    std::string state = "pending";
    std::string message;
};

// 后台稳压（决定记录：每通道一个 2 s 状态机，补压时取短租约）
struct RegState {
    bool active = false;
    int gas = 0;
    double target = 0, band = 1;
    std::optional<double> gain;
    std::optional<int64_t> blocked_since;
    std::optional<std::string> blocked_why;
    uint64_t gen = 0;
    EventLoop::TimerId timer = 0;
    bool pulsing = false;
    std::unique_ptr<GasOps> ops;
};

struct ProgramEngine::Run {
    Run(int rv_, Steps s, std::string by, int64_t wall)
        : rv(rv_), steps(std::move(s)), info(rv_, static_cast<int>(steps.size()), std::move(by), wall), recs(steps.size()) {}

    int rv;
    Steps steps;
    std::string name;
    core::program::ProgramRunInfo info;
    std::vector<StepRec> recs;
    ojson steps_json;             // 冻结步骤的 §6.9 JSON（每次运行只转换一次）
    std::string progress_text;    // 「3 / 9」

    std::unique_ptr<core::program::StepRunner> runner;
    int current_index = -1;
    int64_t started_mono = 0;
    int64_t step_started_mono = 0;
    uint64_t gen = 1;             // 每次中止 / 收尾 +1：之前排的定时器与回调作废
    EventLoop::TimerId timer = 0;
    bool want_fresh = false;      // 下一次 tick 前即时读压（StepOutput::fresh_pressure）
    core::program::StepType last_wait_type = core::program::StepType::End;

    Acquire acq;                  // 步骤取气路
    Acquire reg_acq;              // 稳压脉冲取气路
    std::unique_ptr<GasOps> ops;  // 步骤的写阀 / 吹总管
    int64_t gas_gen = 0;          // 最近一次取到的租约代号（收尾只释放自己这段，C8）

    // 升温曲线（引擎私有曲线，不动通道的抽屉曲线配置）
    std::optional<core::TempProfile> curve;
    std::string curve_head;
    bool curve_inflight = false;
    EventLoop::TimerId curve_timer = 0;

    RegState reg;

    bool cancelled = false;       // 已发出中止（AbortProgram）
    bool finishing = false;       // 进入收尾（finally）
    bool aborted = false;
    std::optional<std::string> fail;
    std::optional<std::string> pending_fault;  // 稳压 / 超压判定的故障：按失败而非手动中止收尾
    bool skip_outputs = false;    // 全部停止 / 退出：输出已由快速路径停掉，收尾不再重复停加热、关 SV
    bool abort_stop_sent = false, abort_stop_done = false;
    std::vector<std::function<void()>> abort_stop_waiters;
    bool heat_touched_after_abort = false;
};

}  // namespace reactor::ctl
