// 阀门业务服务（IO16R，12 路：SV1–8、惰性 / A / B 气源、排空）。C#: Service/ValveService.cs + MainViewModel.Gas.cs 的写阀部分
//
// 唯一的写阀入口（决定记录 2026-11-26「全闭阀死区由谁插」）：
//   write_frame(frame, opts)：
//     1. 阀门模块未配置 → 失败；core::valve_frame::check_apply（不安全帧 / 越权 SV）→ 失败（文案照 C#）；
//     2. 若 core::valve_frame::needs_dead_time(当前已确认帧, frame) → 先写全关帧，等 opts.dead_ms（吹扫 500、程序步骤 1000），再写目标帧；
//     3. 目标帧写失败重试 2 次（间隔 120 ms，C# WriteValvesAsync）；每次提交前核对停止代号（全部停止之后不再把开阀帧写下去）；
//     4. 写成功才更新「已确认阀态」并回调 on_shown_changed（核心据此记「阀门整帧下发」事件）。
//   只关不开的写：close_range / close_gas_sources / close_sv / close_all —— 联锁和收尾用，不按界面阀态重写整帧（C# CloseRangeAsync）。
//   保持心跳：set_hold(frame) 后每 1 s 重写一次该帧（只写硬件、不刷已确认阀态、不记事件；C# HoldAsync），clear_hold() 停。
//
// SV 打开计数（吸气量断基准用，C# GetSvOpenGen）：任何会打开 SVn 的写入在提交总线**之前**加一，写失败也加。
// 通讯统计按 C# 口径逐笔计（IO16R 不轮询）。
//
// 线程：只在控制线程调用；回调在控制线程上执行。
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

#include "core/valve_frame.hpp"
#include "ctl/ctl_io.hpp"
#include "hw/comm_stats.hpp"

namespace reactor::ctl {

struct ValveWriteOpts {
    int64_t dead_ms = 500;                  // 死区（core::purge_plan::kDeadSec = 0.5 s；程序步骤用 1000）
    bool lease_busy = false;                // 气路有占用时只能开参与通道的 SV（core::valve_frame::check_apply）
    std::vector<int> participants;
    std::string lease_task;
    int retries = 2;
};

class ValveService {
public:
    using DoneFn = std::function<void(bool ok, const std::string& err)>;
    static constexpr int64_t kHoldHeartbeatMs = 1000;  // C# HoldAsync：每 1 s 重写当前帧
    static constexpr int64_t kRetryDelayMs = 120;      // C# WriteValvesAsync 重试间隔
    static constexpr int64_t kCloseRetryDelayMs = 200; // C# CloseAllValvesAsync 重试间隔

    explicit ValveService(const CoreIo& io) : io_(io) {}

    bool configured() const { return io_.hw->io_configured(); }
    bool online() const { return stats_.online(); }
    const hw::CommStats& stats() const { return stats_; }
    const core::ValveFrame& shown() const { return shown_; }
    bool sv_open(int id) const { return shown_[static_cast<size_t>(id - 1)]; }
    int64_t sv_open_gen(int id) const { return id >= 1 && id <= 8 ? sv_gen_[static_cast<size_t>(id - 1)] : 0; }
    bool holding() const { return hold_.has_value(); }

    // 开机读回 12 路阀态 → 已确认阀态（读不到保持全关）。cb(ok)
    void read_back(std::function<void(bool ok, const core::ValveFrame& f)> cb);
    void write_frame(const core::ValveFrame& frame, const ValveWriteOpts& opts, DoneFn cb);
    void close_range(int start, int count, DoneFn cb, int retries = 0);
    void close_gas_sources(DoneFn cb) { close_range(core::valve_frame::kGasBase, 3, std::move(cb)); }
    void close_sv(int id, DoneFn cb);
    // 收尾全关（C# CloseAllValvesAsync）：keep_vent 时只关线圈 0–10，排空不写；否则 12 路整帧全关。失败重试 3 次（共 4 次）
    void close_all(bool keep_vent, DoneFn cb);
    void set_hold(const core::ValveFrame& frame);
    void clear_hold();
    // 全部停止的快速路径已把全关帧写下去（普通补发那一遍确认成功）：已确认阀态 = 全关
    void mark_all_closed();

    // 已确认阀态变化（prev → next），核心记事件 / 刷新显示
    std::function<void(const core::ValveFrame& prev, const core::ValveFrame& next)> on_shown_changed;

private:
    void attempt(const core::ValveFrame& frame, uint64_t epoch, int left, DoneFn cb);
    void show(const core::ValveFrame& f);
    void track(bool ok);
    void bump_opens(const core::ValveFrame& f);

    CoreIo io_;
    hw::CommStats stats_;
    core::ValveFrame shown_{};
    std::array<int64_t, 8> sv_gen_{};
    std::optional<core::ValveFrame> hold_;
    uint64_t hold_epoch_ = 0;
    EventLoop::TimerId hold_timer_ = 0;
};

}  // namespace reactor::ctl
