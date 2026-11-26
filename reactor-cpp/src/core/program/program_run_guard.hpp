// 程序下发守卫（C3，纯逻辑）：「下发」打开确认框时与「确认运行」时都按同一顺序重判，任一项不满足即拒绝。
// C#: Models/ProgramRunGuard.cs（ProgramRunInput / ProgramRunGuard）
// 顺序：未选通道 → 该通道已有程序在执行 → 并行程序已满 → 空程序 → 急停 → 报警 / 超温超压未解除 → 状态不是已启用 / 已结束 →
// 气路被占用（不论程序是否含气路步骤）→ 气路公共检查（仅含气路步骤时）→ 参数超限（报第一条）。
// 输入由调用方（命令层 / 控制线程）从设置与通道状态取一次实时值填好，这里不碰别的模块。
#pragma once
#include <optional>
#include <string>
#include <vector>

#include "core/program/program_types.hpp"

namespace reactor::core::program {

struct ProgramRunInput {
    bool has_channel = false;          // 已选中目标通道
    bool program_running = false;      // 该通道已有程序在执行
    int running_count = 0;             // 全机正在执行的程序数（≥ kMaxConcurrent 拒绝）
    int step_count = 0;
    ReactorState state = ReactorState::Idle;   // 目标通道状态
    bool over_temp_tripped = false;
    bool over_pressure_tripped = false;
    bool estop_locked = false;         // 急停锁定（C++ 版删掉了急停，保留字段以便原样判定；恒 false 即可）
    bool gas_busy = false;             // 气路被**非程序**的自动序列占用（泄漏测试）
    std::string gas_task;              // 占用者名称（空 = 「气路序列」）
    bool needs_gas = false;            // 程序含气路步骤
    std::optional<std::string> gas_block;        // 气路公共检查的拒绝原因（仅 needs_gas 时生效）
    std::optional<std::string> gas_block_code;   // gas_block 的错误码（空 = GAS_BUSY）
    std::vector<std::string> limit_errors;       // 参数超限（program_limits::validate）
};

namespace program_run_guard {
// 带 §11 错误码的判定（core::GuardReject）；nullopt = 可运行
std::optional<GuardReject> check(const ProgramRunInput& i);
// 只要文字（同 check）
std::optional<std::string> reason(const ProgramRunInput& i);
}  // namespace program_run_guard

}  // namespace reactor::core::program
