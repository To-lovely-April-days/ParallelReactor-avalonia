// 12 路阀门整帧 + 吹扫序列 + 泄漏率计算。C#: Models/GasSequence.cs（ValveFrame、PurgeAction、PurgePlan、LeakMath）
// 以及 ViewModels/MainViewModel.Gas.cs ApplyValvesAsync / CloseAllValvesAsync、MainViewModel.Logging.cs ValveName / LogValveFrame
// 里的纯判定部分（下发前检查、超压保持排空的关阀帧、整帧变化的事件文字）。
// 下标与 IO16R 线圈一致：0-7 = RV1-8 进气阀 SV，8 = 惰性气体，9 = 气体 A，10 = 气体 B，11 = 排空 Vent。
// 规则：气源最多开一路；气源与排空不同时开；气源 / 排空之间切换要先插一拍全关（死区 0.5 s）。
#pragma once
#include <array>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::core {

namespace valve_frame {
constexpr int kCount = 12;
constexpr int kGasBase = 8;  // 惰性=8, A=9, B=10
constexpr int kVent = 11;
}  // namespace valve_frame

using ValveFrame = std::array<bool, valve_frame::kCount>;

namespace valve_frame {
// 全关
ValveFrame all_closed();
// 只开一路气源（0=惰性 1=A 2=B，越界夹到 0..2），可同时开若干 RV 的 SV（1..8，越界忽略）
ValveFrame gas(int gas, const std::vector<int>& rv_ids = {});
// 只开排空，可同时开若干 RV 的 SV
ValveFrame vent_only(const std::vector<int>& rv_ids = {});
// 安全约束：气源最多开一路，且气源与排空不能同时开
bool is_safe(const ValveFrame& f);

// 下发前检查（C#: MainViewModel.ApplyValvesAsync）：不安全帧拒绝；气路有占用时只能开参与通道的 SV。
// 返回 nullopt = 可以下发，否则为与 C# 逐字相同的拒绝原因（C# 里是 GasFaultException 的消息）。
std::optional<std::string> check_apply(const ValveFrame& f, bool lease_busy, const std::vector<int>& participants,
                                       const std::string& lease_task);
// 从 prev 切换到 next 之前是否必须先插一拍全关：prev 开着某个气源 / 排空，而 next 要打开一个 prev 没开的气源 / 排空。
// （C# 由各序列在切换处手工插入 AllClosed + DeadSec；这里把规则写成判定，供 ctl 的阀门下发统一执行。）
bool needs_dead_time(const ValveFrame& prev, const ValveFrame& next);
// 序列结束 / 中止时的全关帧（C#: CloseAllValvesAsync）：keep_vent（超压中止期间、非急停）时排空保持 vent_on，
// 硬件上只写线圈 0-10；否则 12 路全关。
ValveFrame close_all(bool keep_vent, bool vent_on);
// 阀名（事件文字）："SV1".."SV8" / "惰性气体" / "气体A" / "气体B" / "排空"
std::string valve_name(int i);
// 整帧变化的事件说明（C#: LogValveFrame）：无变化返回 nullopt；
// 否则 "{变化 空格分隔} · 当前开启：{阀名、分隔 或 无}"，gas_task 非空时再加 " · {gas_task}"
std::optional<std::string> change_text(const ValveFrame& prev, const ValveFrame& next, const std::string& gas_task);
}  // namespace valve_frame

// 吹扫序列的一个动作：整帧写入后保持 seconds 秒
struct PurgeAction {
    int cycle = 0;
    std::string phase;
    ValveFrame valves{};
    double seconds = 0;
};

// 管路吹扫（只吹扫总管，8 路 SV 全程关）：准备（全关）→ 每循环 充气 → 切换（全关）→ 排气 → 切换（全关）。
namespace purge_plan {
constexpr double kDeadSec = 0.5;  // C#: PurgePlan.DeadSec 阀态切换死区
// 步骤里的气体名 → 气源下标："气体 A"=1，"气体 B"=2，其他=0（惰性）
int gas_index(const std::string& label);
std::vector<PurgeAction> build(int gas, int cycles, double fill_sec, double vent_sec);
}  // namespace purge_plan

// 泄漏率计算（温度补偿 + 最小二乘）
namespace leak_math {
constexpr double kAtmPsi = 14.696;  // C#: LeakMath.AtmPsi
// 把温度 t_c 下的表压折算到参考温度 ref_c
double compensate(double psig, double t_c, double ref_c);
// 压降速率 psi/h（正 = 在降）；样本 (分钟, psi)，少于 2 点或 x 无变化返回 0
double drop_rate_per_hour(const std::vector<std::pair<double, double>>& samples);
}  // namespace leak_math

}  // namespace reactor::core
