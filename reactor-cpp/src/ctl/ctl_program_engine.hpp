// 程序引擎（第二阶段）。C#: ViewModels/ProgramViewModel.Run.cs（ExecuteAsync / StepIo / AcquireGasAsync / RegulateAsync /
// AbortProgram / ReleaseStir / finally 收尾）+ ViewModels/ProgramRun.cs + Commands/OperatorCommands.Program.cs（替换 / 复制 / 下发 / 中止的守卫）。
//
// 结构（设计文档 §4「每个通道一个显式状态机」）：
//   - 每通道一份草稿（PUT 时冻结成 StepSnapshot 列表；开机预置「加氢筛选」，同 C#）；
//   - 下发时复制草稿建一个 Run（最多 8 个并行）：阶段 preparing → running ⇄ waitingGas → finishing → completed | failed | aborted；
//     每步一个 core::program::StepRunner，由控制线程的单次定时器在 wake_at / 外部事件到来时 tick，动作在这里落地（见 .cpp）；
//   - 共用气路：气路步骤经 GasQueue 先到先得排队（单调时钟，每通道一张票，超过 ProgramGasWaitMin → 步骤故障）；
//     稳压 = 每通道一个 2 s 状态机，补压时排队取短租约（开阀 → 保持 → 关阀静置 1 s → 读压 → 释放），其他程序可在两次补压之间取用气路；
//   - 共用搅拌：core::StirShare 引用计数（最后一个释放者才停；释放后按剩余最近登记者恢复转速并记事件）；
//   - 中止：中止按钮（abort）/ 单路停止（abort_channel）/ 全部停止（abort_all）/ 超压联锁（气路中止监听，持有气路的程序失败）。
//     收尾顺序照 C# ExecuteAsync 的 finally：停稳压 → 只释放自己的气路占用并全关阀 → 停曲线 / 逼近 → 停本路加热 → 关 SVn →
//     释放搅拌 → 停计量 → 通道转已结束 → 记事件。不自动放空。
//
// 线程：全部方法只在控制线程调用（命令层经 ControlCore::call 投递；见 ctl_automation.hpp 的任意线程包装）。
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/gas_lease.hpp"
#include "core/program/program_run_info.hpp"
#include "core/program/step_engine.hpp"
#include "core/program/step_snapshot.hpp"
#include "core/stir.hpp"
#include "ctl/ctl_core.hpp"
#include "ctl/ctl_gas_ops.hpp"
#include "ctl/ctl_op_result.hpp"
#include "nlohmann/json.hpp"
#include "store/session.hpp"

namespace reactor::ctl {

class ProgramEngine : public ProgramHooks {
public:
    using Snap = core::program::StepSnapshot;
    using Steps = std::vector<Snap>;

    // sessions 可空（没有会话簿时不写 manifest 的 programRuns）
    ProgramEngine(ControlCore& core, store::SessionBook* sessions = nullptr, core::PressureUnit unit = core::PressureUnit::MPa);
    ~ProgramEngine() override;
    ProgramEngine(const ProgramEngine&) = delete;
    ProgramEngine& operator=(const ProgramEngine&) = delete;

    // ================= 草稿 =================
    // PUT /channels/{id}/program（C# TryReplaceProgram）：可编辑检查 → 解析（新旧格式）→ 全局限值 → 冻结为 StepSnapshot
    OpResult replace_program(int ch, const nlohmann::json& steps, std::optional<std::string> name = std::nullopt);
    // POST /channels/{id}/program/copy（C# TryCopyProgram）。strict = 远程（严格守卫：目标已停用 / 正在运行时跳过并写进 warnings）
    OpResult copy_program(int from, const std::vector<int>& to, bool strict);
    // 草稿（§6.9 格式的 steps 数组）+ 名字 + migrated
    ojson draft_json(int ch) const;
    const Steps& draft(int ch) const { return drafts_[idx(ch)].steps; }
    // 直接放一份草稿（测试 / 程序库载入；不做检查）
    void set_draft(int ch, Steps steps, std::string name = "");

    // ================= 下发 / 中止 =================
    // 下发守卫（C# ProgramViewModel.RunGuard）：nullopt = 可运行
    std::optional<core::GuardReject> run_guard(int ch, const Steps& steps) const;
    // 打开确认框前的预检（C# CheckProgramRun）：被拒「下发被拒绝 · …」并记「程序下发被拒绝」事件；通过 = silent
    OpResult check_run(int ch);
    // 确认运行（C# TryRunProgram）：按此刻状态再判一次守卫，通过即冻结当前草稿开始执行；返回 accepted + 提示项
    OpResult run(int ch, const std::string& started_by = "local");
    // 中止（C# TryAbortProgram，幂等：没有程序 = silent）
    OpResult abort(int ch);

    // ================= 查询（控制线程） =================
    bool is_owned(int ch) const override;
    int running_count() const;
    std::vector<int> running_ids() const;
    // C# ProgramGasPending：有程序在稳压 / 排队等气路时的说明（泄漏测试 / 手动阀控据此 GAS_BUSY），没有为空串
    std::string gas_pending() const;
    // 正在执行、且持有共用搅拌的程序通道（C# StirStepRv，命令层 TrySetStir 守卫用）
    std::optional<int> stir_step_rv() const;
    const core::GasQueue& gas_queue() const { return queue_; }
    const core::StirShare& stir_share() const { return stir_; }
    // 正在执行的，否则最近一次已结束的运行信息；都没有为 nullptr
    const core::program::ProgramRunInfo* run_info(int ch) const;
    // 当前全局限值（设置里取）
    core::program::ProgramLimitSettings limits() const;
    // 程序方面的气路公共检查（programWait = true，GasBlockReason(needPressure, selfId: rv, programWait: true)）
    std::optional<core::GuardReject> gas_block_for(int rv, bool need_pressure) const;

    // ================= ProgramHooks =================
    int abort_all(AbortReason why) override;
    bool abort_channel(int ch, AbortReason why) override;
    void clear_stir_share() override;
    ojson channel_program(int ch) const override;
    ojson legacy_program() const override;
    ojson programs() const override;
    ojson program_run(int ch) const override;
    std::string telemetry_step(int ch) const override;
    void on_tick(int64_t now_mono_ms) override;

    // 观察（WS progress 推送 / 测试）：运行结束时回调（rv, 终态阶段）
    std::function<void(int rv, core::program::ProgramRunPhase phase)> on_finished;
    // 每个步骤状态变化（rv, 步骤下标 0 起, running / completed / failed / aborted, 文字）：步骤开始 / 完成 / 失败 / 中止 / 等待气路
    std::function<void(int rv, int index, const std::string& state, const std::string& message)> on_step;

    struct Run;  // 内部（.cpp）

private:
    struct Draft {
        Steps steps;
        std::string name;
        bool migrated = false;
        std::vector<std::string> notes;
    };
    struct Acquire;
    static size_t idx(int ch) { return static_cast<size_t>(ch - 1); }

    std::shared_ptr<Run> active(int ch) const;
    std::optional<OpResult> edit_locked(int ch) const;
    OpResult run_blocked(const core::GuardReject& why);
    void start(int ch, Steps steps, const std::string& started_by, const std::string& name);

    // 执行（ctl_program_engine.cpp）
    void next_step(const std::shared_ptr<Run>& r);
    void schedule(const std::shared_ptr<Run>& r, int64_t at_ms);
    void wake(const std::shared_ptr<Run>& r, core::program::StepEvent ev);
    void tick(const std::shared_ptr<Run>& r, core::program::StepEvent ev, std::optional<double> fresh_p, bool fresh_read);
    core::program::StepInput input(const std::shared_ptr<Run>& r, core::program::StepEvent ev, std::optional<double> p,
                                   bool fresh) const;
    void act(const std::shared_ptr<Run>& r, const core::program::Action& a);
    void step_fault(const std::shared_ptr<Run>& r, const std::string& why);
    void cancel_flow(const std::shared_ptr<Run>& r, bool aborted);
    void request_abort(const std::shared_ptr<Run>& r, AbortReason why);
    void finish(const std::shared_ptr<Run>& r);
    void finish_outputs(const std::shared_ptr<Run>& r);
    void finish_tail(const std::shared_ptr<Run>& r);
    void release_gas(const std::shared_ptr<Run>& r, std::function<void()> then = {});
    void stop_heat_for(const std::shared_ptr<Run>& r, const std::string& ok_msg, std::function<void(bool)> then);
    void release_stir(int rv);
    void stir_request(int rv, int rpm);
    void mark_waiting(const std::shared_ptr<Run>& r, const std::optional<std::string>& reason);
    void set_status(const std::shared_ptr<Run>& r, const std::string& text);
    void set_rec(const std::shared_ptr<Run>& r, int i, const std::string& state, const std::string& message);
    void log(const std::string& level, const std::string& title, const std::string& detail, const std::string& kind);

    // 气路（ctl_program_engine_gas.cpp）
    void acquire(const std::shared_ptr<Run>& r, Acquire& a, const std::string& task, bool vessel, bool quiet,
                 std::function<void(int64_t gen)> granted, std::function<void(const std::string& fault)> failed,
                 std::function<void()> aborted);
    void acquire_poll(const std::shared_ptr<Run>& r, Acquire& a);
    void acquire_end(const std::shared_ptr<Run>& r, Acquire& a);
    std::string holder_text() const;
    void line_flush(const std::shared_ptr<Run>& r, const core::program::Action& a);
    void run_curve(const std::shared_ptr<Run>& r, const core::program::Action& a);
    void curve_slice(const std::shared_ptr<Run>& r, uint64_t gen);
    void start_regulation(const std::shared_ptr<Run>& r, int gas, double target, double refill_pct);
    void stop_regulation(const std::shared_ptr<Run>& r);
    void reg_schedule(const std::shared_ptr<Run>& r);
    void reg_tick(const std::shared_ptr<Run>& r, uint64_t gen);
    void reg_pulse(const std::shared_ptr<Run>& r, uint64_t gen, int64_t gas_gen);
    void reg_fault(const std::shared_ptr<Run>& r, const std::string& why, bool log_err);
    void on_gas_abort(core::GasAbortCause cause, std::optional<int> channel);

    // 快照（ctl_program_engine_snap.cpp）
    ojson channel_program_json(const Run& r) const;
    ojson remaining_json(const Run& r) const;

    ControlCore& core_;
    store::SessionBook* sessions_;
    core::PressureUnit unit_;
    std::array<Draft, 8> drafts_;
    std::map<int, std::shared_ptr<Run>> runs_;   // 正在执行（含收尾中）
    std::map<int, std::shared_ptr<Run>> last_;   // 每通道最近一次已结束的
    std::shared_ptr<Run> last_ended_;            // 全机最近结束的一次
    core::GasQueue queue_;
    core::StirShare stir_;
    std::map<int, int> stir_rpm_;                // rv → 请求转速：StepInput::stir_holders 要 (rv, rpm)，StirShare::holders() 只给通道号
    std::vector<core::StirHolder> stir_holders() const;
    int gas_listener_ = 0;
};

}  // namespace reactor::ctl
