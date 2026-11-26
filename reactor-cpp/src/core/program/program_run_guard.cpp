#include "core/program/program_run_guard.hpp"

#include "core/program/program_run_info.hpp"
#include <optional>
#include <string>

namespace reactor::core::program {

namespace program_run_guard {

std::optional<GuardReject> check(const ProgramRunInput& i) {
    using namespace op_codes;
    if (!i.has_channel) return GuardReject{kChannelNotFound, "当前未选中通道（先在上方通道条里选一路）"};
    if (i.program_running) return GuardReject{kProgramRunning, "该通道已有程序在执行 · 请等待完成或先中止"};
    if (i.running_count >= program_run_set::kMaxConcurrent)
        return GuardReject{kProgramRunning,
                              "并行程序已达 " + std::to_string(program_run_set::kMaxConcurrent) + " 个 · 请等待完成或先中止"};
    if (i.step_count <= 0) return GuardReject{kProgramInvalid, "空程序无法下发 · 请先从左侧步骤库添加步骤"};
    if (i.estop_locked) return GuardReject{kEstopActive, "急停状态下不可下发程序 · 请先复位"};
    if (i.state == ReactorState::Alarm || i.over_temp_tripped || i.over_pressure_tripped)
        return GuardReject{kChannelAlarm, "该通道存在报警或超温 / 超压未解除 · 请先处理并复位"};
    if (i.state != ReactorState::Ready && !state_is_ended(i.state))
        return i.state == ReactorState::Idle ? GuardReject{kChannelStateInvalid, "该通道已停用 · 请先启用"}
                                             : GuardReject{kChannelBusy, "该通道正在运行，不能下发程序"};
    if (i.gas_busy)
        return GuardReject{kGasBusy, (i.gas_task.empty() ? std::string("气路序列") : i.gas_task) + "进行中，请等待完成或先停止"};
    if (i.needs_gas && i.gas_block) return GuardReject{i.gas_block_code.value_or(kGasBusy), *i.gas_block};
    if (!i.limit_errors.empty()) {
        const auto& e = i.limit_errors;
        return GuardReject{kParamOutOfRange,
                              e.size() == 1 ? e[0] : e[0] + "（另有 " + std::to_string(e.size() - 1) + " 项超限）"};
    }
    return std::nullopt;
}

std::optional<std::string> reason(const ProgramRunInput& i) {
    auto r = check(i);
    if (!r) return std::nullopt;
    return r->message;
}

}  // namespace program_run_guard
}  // namespace reactor::core::program
