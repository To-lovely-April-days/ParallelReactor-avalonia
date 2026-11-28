// 一次程序执行的纯数据与并行程序集合的小件。
// C#: Models/ProgramRunInfo.cs（ProgramRunPhase / ProgramRunInfo / ProgramRunSet）
// 阶段：preparing → running ⇄ waitingGas → finishing → completed | failed | aborted（API 小写首字母）。
#pragma once
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

namespace reactor::core::program {

enum class ProgramRunPhase { Preparing, Running, WaitingGas, Finishing, Completed, Failed, Aborted };

// 每通道一份（控制线程读写；快照构建只读它）
struct ProgramRunInfo {
    ProgramRunInfo(int rv_, int step_count_, std::string started_by_, int64_t started_at_wall_ms_)
        : rv(rv_), step_count(step_count_), started_by(std::move(started_by_)), started_at_wall_ms(started_at_wall_ms_) {}

    int rv;
    int step_count;
    int index = -1;                                  // 当前步骤下标（0 起；尚未开始 −1）
    ProgramRunPhase phase = ProgramRunPhase::Preparing;
    std::string status;                              // 运行状态一行文字
    std::string message;                             // 监视详情
    std::string phase_text = "准备执行";              // 监视阶段文字（API program.phase）
    std::string started_by;                          // local / 远程来源标签
    int64_t started_at_wall_ms;
    std::optional<int> waiting_on;                   // 等待气路时当前占用者的通道（泄漏测试占用时 nullopt）
    std::optional<std::string> end_reason;           // 反应步骤写入的结束原因；没有反应步骤为 nullopt
    // 暂停（2026-11-29 新增）：暂停期间阶段仍是 running（is_active 不变，算「有动作」），快照 state 报 "paused"
    bool paused = false;
    std::optional<int64_t> paused_at_wall_ms;        // 最近一次暂停的时刻（继续后清空）
    std::string paused_by;                           // 暂停来源（触屏 / 远程（…））

    bool waiting_gas() const { return phase == ProgramRunPhase::WaitingGas; }
    bool is_active() const;
};

namespace program_run_set {
constexpr int kMaxConcurrent = 8;   // C#: ProgramRunSet.MaxConcurrent（= 通道数）

bool is_active(ProgramRunPhase p);
using Runs = std::map<int, ProgramRunInfo>;
// 该通道是否被正在执行的程序占用
bool is_owned(const Runs& runs, int rv);
// 正在执行的通道号（升序）
std::vector<int> running_ids(const Runs& runs);
// 首个（通道号最小）正在执行的程序；没有返回 nullptr
const ProgramRunInfo* first_running(const Runs& runs);
// 「正在运行：RV1 RV3」；没有程序执行时为空串
std::string summary(const Runs& runs);
// API 用的小写阶段名（preparing / waitingGas …）
const char* phase_name(ProgramRunPhase p);
}  // namespace program_run_set

}  // namespace reactor::core::program
