// 16 段曲线升温程序（每个反应釜一份）。C#: Models/TempProfile.cs
// AI-8 无原生多段程序：上位机按已运行时间分段线性插值算当前 SP，周期写 SP 寄存器。
// 段 1 从启动时的 PV 开始，段 N 从段 N−1 的目标开始；走完保持最后一段温度。
// 与 C# 的差别：C# 直接读 DateTime.UtcNow；这里「现在」由调用方传入（单调毫秒）。界面属性（Enabled/Active）
// 换成 enabled(i) / active_segment() 查询。
#pragma once
#include <array>
#include <cstdint>
#include <string>

namespace reactor::core {

struct ProfileSegment {
    double temp = 50;     // 段目标温度 ℃（C# 默认 50）
    double minutes = 10;  // 段时长 分钟（C# 默认 10）
};

namespace temp_profile {
constexpr int kMaxSegments = 16;
constexpr int64_t kForceRewriteMs = 30000;  // C#: ForceRewriteEvery = 30 s
}  // namespace temp_profile

class TempProfile {
public:
    std::array<ProfileSegment, temp_profile::kMaxSegments> segments{};
    int seg_count = 4;  // 启用段数 1..16（使用时夹到范围内）

    bool running() const { return running_; }
    const std::string& status_text() const { return status_; }
    // 第 i 段（1 起）是否在启用段数范围内
    bool enabled(int index1) const;
    // 当前正在执行的段（1 起），0 = 无（未开始 / 已走完 / 已停止）；由 current_target 更新
    int active_segment() const { return active_; }

    // 启动：记录起点 PV 与时刻
    void start(double current_pv, int64_t now_ms);
    // 停止（不再下发，仪表保持最后写入的 SP）
    void stop();
    // 当前应写入的给定值（分段线性插值）；同时更新状态文字与当前段
    double current_target(int64_t now_ms);
    // 去抖：与上次成功下发相差 ≥0.1 ℃，或距上次成功下发 ≥30 s
    bool should_write(double target, int64_t now_ms) const;
    // 记录一次成功下发（失败不调用）
    void mark_written(double target, int64_t now_ms);
    // 已运行分钟（未运行为 0）
    double elapsed_min(int64_t now_ms) const;
    // 前 seg_count 段时长合计（每段下限 0.01 分钟）
    double total_minutes() const;
    // 曲线是否已走完（程序引擎据此判定升温·曲线步骤完成）
    bool is_complete(int64_t now_ms) const;

private:
    int n() const;
    bool running_ = false;
    std::string status_ = "未启动";
    int active_ = 0;
    double start_temp_ = 0;
    int64_t start_ms_ = 0;
    double last_written_ = 0;
    bool has_written_ = false;
    int64_t last_write_ms_ = 0;
};

}  // namespace reactor::core
