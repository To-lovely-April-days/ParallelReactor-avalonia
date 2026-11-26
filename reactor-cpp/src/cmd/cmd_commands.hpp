// 命令层：触屏与远程共用的唯一写入口。C#: Commands/OperatorCommands.*.cs（每个命令的仲裁、守卫顺序、错误码、中文文案照抄）
//
// 与 C# 的区别：C# 跑在 UI 线程上直接改 ViewModel；这里每个命令都在**控制线程**上执行（接口层经 ControlCore::post 投递），
// 读写的是 ctl::ControlCore / ctl::Automation 的状态，「同步头」= 本次调用里完成的守卫 + 第一次状态修改，
// 「异步尾」= 等硬件结果的回调（done 在控制线程上调用，恰好一次）。不需要等硬件的命令直接返回 OpResult。
//
// 每个命令的第一步是 admit（仲裁）：远程控制类要本令牌持有控制权；远程不能做 LocalOnly 的动作；远程控制中触屏的控制类 → MODE_REMOTE。
// 严格守卫（C# CommandPolicy.Strict）只对远程生效。
//
// 线程：除构造外全部方法只在控制线程上调用。
#pragma once
#include <array>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include "cmd/cmd_arbiter.hpp"
#include "cmd/cmd_lease.hpp"
#include "cmd/cmd_source.hpp"
#include "core/units.hpp"
#include "ctl/ctl_automation.hpp"
#include "ctl/ctl_core.hpp"
#include "ctl/ctl_op_result.hpp"
#include "hw/hardware.hpp"
#include "store/data_logger.hpp"
#include "store/session.hpp"
#include "store/settings.hpp"
#include <cstdint>

namespace reactor::cmd {

using ctl::OpResult;
using ctl::ojson;
using Done = std::function<void(const OpResult&)>;

struct CommandsDeps {
    ctl::ControlCore* core = nullptr;          // 必填
    ctl::Automation* automation = nullptr;     // 必填
    store::DataLogger* logger = nullptr;       // 会话 / 标记（可空：会话命令回 INTERNAL）
    store::SessionBook* sessions = nullptr;    // 可空
    hw::Hardware* hw = nullptr;                // 搅拌驱动器参数下发（可空）
    ControlArbiter* arbiter = nullptr;         // 必填
    ControlLease* lease = nullptr;             // 必填（允许 / 拒绝 / 切回本地）
    std::function<int64_t()> mono_ms;          // 租约时钟（缺省 reactor::mono_ms）
    core::PressureUnit unit = core::PressureUnit::MPa;
    bool simulated = false;
    std::string prefs_path;                    // 每路手动设定（到位范围 / 防超调）的存档；空 = 不存
};

// 每一路的手动加热设定（接口契约 §3.3，C# 没有）：到位范围 ±℃（同程序 heat.band）、防超调逼近开关
struct ChannelPrefs {
    double band_c = 2.0;
    bool anti_overshoot = true;
};

struct PreRunCheck {
    std::string kind, title, detail;   // ok / warn / err
};
struct PreRunResult {
    std::vector<PreRunCheck> checks;
    bool can_run = false;
    std::string summary;
    std::vector<int> channels;   // 将参与本次运行的通道
};

// 存储里的语言 → 线上短码（zh-CN → zh、en-US → en）
std::string wire_language(const std::string& stored);

class Commands {
public:
    explicit Commands(CommandsDeps d);

    // 仲裁（每个命令第一步）：nullopt = 放行；MODE_REMOTE 按 warn，其余 err
    std::optional<OpResult> admit(const Source& src, ActionClass cls, Exposure exp = Exposure::Remote) const;

    // ---- 系统 ----
    void stop_all(const Source& src, bool stop_stir, Done done);
    OpResult estop(const Source& src, const std::string& note);   // 兼容 /estop：= 全部停止（含搅拌），立即 accepted
    OpResult run(const Source& src, bool dry_run);
    PreRunResult compute_pre_run() const;
    ojson preflight(const Source& src);   // GET /preflight 的正文（C# PreflightDto）

    // ---- 通道 ----
    OpResult set_band(const Source& src, int id, double band_c);
    OpResult set_anti_overshoot(const Source& src, int id, bool on);
    ChannelPrefs prefs(int id) const;   // 任意线程（内部有锁）
    OpResult enable_channel(const Source& src, int id, bool default_volume);
    OpResult start_channel(const Source& src, int id, std::optional<double> temp_c);
    void stop_channel(const Source& src, int id, bool disable, Done done);
    OpResult copy_channel_config(const Source& src, int from, const std::optional<std::vector<int>>& to);
    OpResult set_volume(const Source& src, int id, double ml);
    OpResult reset_gas_counter(const Source& src, int id);

    // ---- 温度 ----
    OpResult start_heat(const Source& src, int id, std::optional<double> temp_c);
    void stop_heat(const Source& src, int id, Done done);
    OpResult set_setpoint(const Source& src, int id, double temp_c);
    OpResult set_sp_mode(const Source& src, int id, const std::string& mode);
    OpResult replace_profile(const Source& src, int id, const std::vector<std::pair<double, double>>& segs);
    OpResult start_profile(const Source& src, int id);
    OpResult stop_profile(const Source& src, int id);
    void set_pid(const Source& src, int id, int band, double p, double i, double d, Done done);
    void start_autotune(const Source& src, int id, double temp_c, std::optional<int> band, Done done);
    void cancel_autotune(const Source& src, int id, Done done);

    // ---- 搅拌 / 阀门 ----
    void set_stir(const Source& src, std::optional<bool> on, std::optional<int> rpm, Done done);
    void set_reactor_valve(const Source& src, int id, bool open, Done done);
    void set_gas_inlet(const Source& src, int index, bool open, Done done);
    void set_vent(const Source& src, bool open, Done done);
    OpResult set_manual_valve_mode(const Source& src, bool on);   // 本地

    // ---- 泄漏测试 / 程序 / 容积标定 ----
    OpResult start_leak_test(const Source& src, const std::vector<int>& channels, std::optional<double> test_psi);
    OpResult abort_leak_test(const Source& src);
    OpResult replace_program(const Source& src, int id, const nlohmann::json& steps, std::optional<std::string> name);
    OpResult run_program(const Source& src, int id);
    OpResult check_program_run(const Source& src, int id);
    OpResult abort_program(const Source& src, int id);
    OpResult copy_program(const Source& src, int from, const std::vector<int>& to);
    OpResult start_calib(const Source& src, int id, bool with_water, double water_ml, bool redo_manifold);
    OpResult abort_calib(const Source& src);
    OpResult discard_calib(const Source& src);

    // ---- 记录 ----
    OpResult ack_alarm(const Source& src, const std::string& alarm_id);
    OpResult ack_all_alarms(const Source& src);
    OpResult begin_session(const Source& src, const std::string& test_id, std::optional<std::string> note);
    OpResult end_session(const Source& src);
    OpResult add_marker(const Source& src, const std::string& text);
    void end_session_on_exit();   // 退出服务时（C# ExitAppTailAsync 的 EndTestSession）

    // ---- 控制权（本地） ----
    OpResult grant_control(const Source& src, const std::string& request_id);
    OpResult deny_control(const Source& src, const std::string& request_id);
    OpResult take_back_control(const Source& src);
    // 宽限期满（租约已作废）：policy = keep / stopHeat / estop（estop = 全部停止含搅拌，新后端没有急停）
    void on_remote_lost(const std::string& policy, const std::string& token, const std::string& client);

    // ---- 设置（本地） ----
    // changes：按 SettingSpec::name；expected_revision 给了且不等 → REVISION_CONFLICT。写设备的项（搅拌电流 / 待机 / 细分）另下发驱动器
    // stop_heat_on_op：「超压时停止加热」开关（bool 项，不在 SettingSpecs 里）；extra：其余非数值项（界面偏好 / 数据保留），
    // 在同一次修改里改设置副本并返回有没有变化（revision 只加 1）。成功 data = {revision, changed:[SettingSpec::name…], stopHeatChanged}
    using SettingsExtra = std::function<bool(store::AppSettings&)>;
    void patch_settings(const Source& src, const std::vector<store::SettingChange>& changes, std::optional<bool> stop_heat_on_op,
                        SettingsExtra extra, std::optional<int64_t> expected_revision, Done done);
    OpResult set_band_bounds(const Source& src, double low_mid, double mid_high, std::optional<int64_t> expected_revision);
    OpResult set_language(const Source& src, const std::string& lang);

private:
    ctl::ControlCore& core() const { return *d_.core; }
    ctl::ProgramEngine& programs() const { return d_.automation->programs(); }
    int64_t now_mono() const;
    std::optional<OpResult> channel_or(int id) const;
    std::optional<OpResult> program_owned(int id) const;
    std::optional<OpResult> heat_blocked(int id) const;
    std::optional<OpResult> profile_locked(const Source& src, int id) const;
    bool gas_busy_not_program() const;
    std::string gas_task() const;
    void log_setting(const std::string& key, const std::string& old_v, const std::string& new_v);
    void manual_valve_tail(const Source& src, const std::string& name, bool open, const core::ValveFrame& frame,
                           const std::string& open_kind, Done done);
    std::optional<OpResult> local_manual_gate(const Source& src, const std::string& need_mode) const;
    void stir_device_tail(store::SettingKey key, double value, Done done);
    void heat_channel(int id);           // 按这一路的手动设定启动加热（防超调开 → 逼近；关 → 直接写 SP）+ 升温超时监视
    void save_prefs() const;

    mutable std::mutex prefs_mu_;
    std::array<ChannelPrefs, 8> prefs_{};

    CommandsDeps d_;
    bool manual_valve_busy_ = false;
    std::optional<std::pair<std::string, Source>> session_owner_;
};

}  // namespace reactor::cmd
