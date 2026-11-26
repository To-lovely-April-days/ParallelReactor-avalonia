// 温控业务服务（AI-8 一台 1 拖 8）。C#: Service/TempService.cs
//   - 每个控制周期轮询一轮（PV / OP / At 各块读 8 路 + Srun + Ctn，5 笔），上一轮没读完就跳过（C# _polling）；
//     一轮 5 笔全成功算 1 次成功（C# 口径的 CommStats），连续 3 轮失败 = 离线，离线时各通道读数无效；
//   - 开机：读 Ctn，<8 写 8 再回读（自愈）；8 路逐路 At=4（遇通讯失败即停）；无存档 PID 时读仪表 PID 作低温档种子；
//   - 启动加热全链：Srun=0 → At=0 → 按档 PID → SP（stop_on_fail，带停止代号，见 ctl_io.hpp）；
//   - 写 SP 前按温度选档写 PID（同档不重写，C# _lastBand + PidGen）；
//   - 停止 / 超温切断：At=4。
// 自整定（At=1、读 SP ≥35 ℃ 预检、完成回读存档）属于第二阶段，这里只留 hooks：autotuning 状态随轮询更新，
// 「整定刚结束」通过 on_autotune_finished 回调交出去；read_setpoint() 给自整定的 SP 预检用。
//
// 线程：全部方法只在控制线程调用；回调也在控制线程上执行。
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <optional>

#include "ctl/ctl_io.hpp"
#include "hw/comm_stats.hpp"
#include "store/settings.hpp"

namespace reactor::ctl {

struct TempChannel {
    double pv = 0;               // 最近一轮的 PV ℃（离线时为旧值，用 online 判断）
    std::optional<int16_t> pv_raw;
    double output_pct = 0;
    int mode = -1;               // At 原值，-1 = 尚未读到
    bool online = false;         // 读数有效
    bool autotuning = false;     // 回读 At == 1
    double sp = 0;               // 软件最近一次成功写入的 SP
    bool sp_known = false;       // 开机后是否写成功过 SP（仪表 SV 不回读）
};

class TempService {
public:
    static constexpr int kChannels = 8;
    using DoneFn = std::function<void(bool ok)>;

    TempService(const CoreIo& io, const store::SettingsState* settings);

    bool configured() const { return io_.hw->temp_configured(); }
    bool online() const { return stats_.online(); }
    const hw::CommStats& stats() const { return stats_; }
    std::optional<int64_t> values_at() const { return values_at_; }
    int srun() const { return srun_; }
    int ctn() const { return ctn_; }
    const TempChannel& ch(int id) const { return ch_[static_cast<size_t>(id - 1)]; }
    TempChannel& ch_mut(int id) { return ch_[static_cast<size_t>(id - 1)]; }
    int live_band(int id) const { return last_band_[static_cast<size_t>(id - 1)]; }

    // 档位：给定温度落在哪一档（超出上限归最高档）。C#: BandIndexForTemp
    int band_index_for(double t) const;
    // 档位 PID 被外部改过（手改 / 整定完成）：该通道的修改计数 +1，写到一半的套档不再记为已生效
    void bump_pid_gen(int id) { ++pid_gen_[static_cast<size_t>(id - 1)]; }
    void forget_live_band(int id) { last_band_[static_cast<size_t>(id - 1)] = -1; }

    // 开机初始化（Ctn 自愈、8 路 At=4、可选读 PID 种子）。seed 回调在控制线程上收到每路读到的 PID。
    void init(bool seed_from_instrument, std::function<void(int id, const hw::PidParams&)> seed);
    // 每个控制周期调用一次
    void poll();

    // 启动加热全链（Srun=0、At=0、档 PID、SP）。guard_epoch 见 ctl_io.hpp；中途发生全部停止 → ok=false
    void start_channel(int id, double band_temp, double initial_sp, uint64_t guard_epoch, DoneFn cb);
    // 写 SP（先按 band_temp 选档写 PID，同档不重写；PID 写失败也照写 SP，同 C# WriteTempSpSafeAsync）
    void write_sp_safe(int id, double sp, double band_temp, DoneFn cb);
    // 只写 SP
    void set_setpoint(int id, double sp, DoneFn cb);
    // At=4（停止 / 停用 / 停加热）
    void stop_channel(int id, DoneFn cb);
    // 超温 / 超压切断：At=4；成功后清「整定中」显示（整定打断标记由 on_autotune_aborted 交给第二阶段）
    void over_temp_stop(int id, DoneFn cb);
    // 读 SP（自整定预检用，第二阶段）
    void read_setpoint(int id, std::function<void(std::optional<double>)> cb);
    // 自整定启动（第二阶段新增，C# TempService.AutotuneAsync 的写入部分）：Srun=0 → At=1（stop_on_fail，带停止代号）。
    // 成功后该通道显示「整定中」、仪表 PID 档未知（整定会改写仪表 PID）
    void start_autotune(int id, uint64_t guard_epoch, DoneFn cb);
    // 读该通道仪表里的 P / I / D（自整定完成回读，第二阶段新增）
    void read_pid(int id, std::function<void(std::optional<hw::PidParams>)> cb);

    // ---- 第二阶段扩展点 ----
    std::function<void(int id)> on_autotune_finished;  // 轮询看到 At 从 1 变成非 1
    std::function<void(int id)> on_autotune_aborted;   // 切断 / 停止打断了正在进行的整定

private:
    std::vector<hw::ModbusRequest> pid_reqs(int id, int band) const;

    CoreIo io_;
    const store::SettingsState* settings_;
    std::array<TempChannel, kChannels> ch_{};
    std::array<int, kChannels> last_band_{{-1, -1, -1, -1, -1, -1, -1, -1}};
    std::array<int, kChannels> pid_gen_{};
    hw::CommStats stats_;  // C# 口径：一轮 5 笔算 1 次
    bool polling_ = false;
    std::optional<int64_t> values_at_;
    int srun_ = -1, ctn_ = -1;
};

}  // namespace reactor::ctl
