// 吸气量用的容积标定（气体膨胀法，只在本机触屏上做）。C#: ViewModels/CalibViewModel.cs（RunAsync / Finish / SyncActivity）+
// Models/DeviceActivity.cs 的 CalibActivity（快照 calib）+ Commands/OperatorCommands.Calib.cs。计算在 core::volume_calib。
//
// 一遍 = 关阀读初始压力 → 最多 6 次「只开惰性气源给总管充气 2 s → 关 → 只开该路 SV 放进釜里、等 20 s 读压」（压力接近减压阀压力
// 且有效组数够了提前结束）→ 拟合比值。第一次（或要求重新标定总管体积）做两遍：空釜一遍 → 等操作员加水（waitWater 阶段，等触屏点
// 「继续第二遍」= start(ch, with_water=true, water_ml)）→ 加水一遍，得出总管体积与该路有效容积；之后其他通道空釜一遍即可。
// 结果写设置（EffectiveVolume / VolumeCalibUtc / ManifoldVolume，经 ControlCore::update_settings，吸气量计算立即改用）。
//
// 线程：只在控制线程用。命令层只在本机入口开放（设计文档 §5.2）。
#pragma once
#include <memory>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "ctl/ctl_core.hpp"
#include "ctl/ctl_gas_ops.hpp"
#include "ctl/ctl_op_result.hpp"
#include "store/data_logger.hpp"
#include <cstdint>

namespace reactor::ctl {

class ProgramEngine;
class LeakTest;

namespace calib_stage {  // C# CalibStageCodes
inline constexpr const char* kFirst = "first";
inline constexpr const char* kWaitWater = "waitWater";
inline constexpr const char* kSecond = "second";
inline constexpr const char* kDone = "done";
inline constexpr const char* kFailed = "failed";
}  // namespace calib_stage

class VolumeCalib {
public:
    static constexpr double kChargeSec = 2;       // C# CalibViewModel.ChargeSec
    static constexpr double kEqualizeSec = 20;    // C# EqualizeSec
    static constexpr double kMaxStartRatio = 0.5; // C# MaxStartRatio
    static constexpr double kMaxTempDrift = 1.5;  // C# MaxTempDrift

    VolumeCalib(ControlCore& core, const ProgramEngine* programs, const LeakTest* leak, store::DataLogger* logger,
                core::PressureUnit unit = core::PressureUnit::MPa);
    ~VolumeCalib();

    // POST /calib（本机；C# TryStartVolumeCalib）。with_water = false：空釜一遍；true：加水后的第二遍（须先做完同一通道的空釜一遍）。
    // redo_manifold：总管体积已知时也重新做两遍（面板「重新标定总管体积」）
    OpResult start(int ch, bool with_water, double water_ml, bool redo_manifold = false, const std::string& started_by = "触屏");
    // DELETE /calib/current（C# TryAbortVolumeCalib）
    OpResult abort();
    // 放弃第一遍的结果（面板「放弃」；快照 calib 变为 null）
    OpResult discard_first();
    // 面板上的加水体积（等加水时快照 waterMl 报它）
    void set_water_ml(double ml) { water_ml_ = ml; }

    bool running() const { return running_; }
    bool awaiting_water() const { return await_water_; }
    std::optional<int> first_pass_channel() const { return first_ ? std::optional<int>(first_->first) : std::nullopt; }
    bool two_runs() const;
    // 快照 calib（C# CalibSnap，字段与顺序不改；没有标定为 null）
    ojson snapshot() const;

private:
    void run(int ch, bool with_water, double water_ml, const std::string& by);
    void step(int k);
    void fault(const std::string& why);
    void cancelled();
    void cleanup();
    void finish();
    void set_fail(const std::string& title, const std::string& text);
    void sync_activity();
    void on_gas_abort(core::GasAbortCause cause, std::optional<int> channel);
    std::string P(double psi) const { return core::format_p(psi, unit_); }

    ControlCore& core_;
    const ProgramEngine* programs_;
    const LeakTest* leak_;
    store::DataLogger* logger_;
    core::PressureUnit unit_;
    std::unique_ptr<GasOps> ops_;
    int listener_ = 0;

    // 面板状态
    bool running_ = false, await_water_ = false, redo_manifold_ = false, has_result_ = false, failed_ = false, ending_ = false;
    int channel_ = 0;
    double water_ml_ = 5;  // 10 mL 釜：加 5 mL 水
    double progress_ = 0;
    std::string phase_, result_title_, result_text_;
    std::optional<std::pair<int, double>> first_;  // 第一遍（空釜）的通道与比值
    // 本遍
    bool with_water_ = false;
    double pass_water_ = 0;
    std::vector<std::pair<double, double>> steps_;
    double p_ = 0, t0_ = NAN, cap_ = 0;
    std::optional<std::string> fail_;
    bool aborted_ = false;

    // CalibActivity（快照）
    std::optional<std::string> stage_;
    std::optional<int> act_channel_;
    std::optional<int64_t> started_at_, waiting_since_;
    std::string started_by_;
    bool act_two_runs_ = false;
    std::optional<double> used_water_, first_pass_ml_, result_ml_;
    std::optional<std::string> act_text_;
    double final_progress_ = 0;
    std::optional<std::string> final_phase_;
};

}  // namespace reactor::ctl
