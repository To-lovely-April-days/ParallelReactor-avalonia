// 控制核心（第一阶段）。设计文档 §2–§7；C#: ViewModels/MainViewModel*.cs 的业务部分 + Service/ControlLoop.cs
//
// 线程模型（设计文档 §4）：
//   - 一个控制线程（EventLoop）独占本类全部可变状态：8 个通道、全局（搅拌 / 阀门 / 手动阀控 / 模块在线）、报警表、
//     设置运行副本、气路租约。除了标注「任何线程」的方法，其余方法都只能在控制线程上调用（接口层经 post / call 投递）。
//   - 每条 RS485 一个总线 I/O 线程（hw::Bus）：所有设备读写都是异步提交，结果投递回控制线程处理（ctl_io.hpp）。
//   - 每个控制周期（1.2 s）末生成不可变快照，原子替换后通知订阅者（WS 层）。
//   - 「全部停止」快速路径（request_stop_all）任何线程可调：先往紧急队列塞停止帧，再按普通优先级补一遍，然后投递控制线程做收尾。
//
// 生命周期：构造 → start() →（运行）→ shutdown()。shutdown 之后 Hardware 的总线已停，不能再用。
// 测试：CoreOptions::threaded = false 时不起线程，调用方线程就是控制线程，用 tick() / settle() / EventLoop 手动推进（见 tests/ctl）。
#pragma once
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "core/gas_lease.hpp"
#include "core/over_pressure_guard.hpp"
#include "core/over_temp_guard.hpp"
#include "core/units.hpp"
#include "ctl/ctl_alarm_table.hpp"
#include "ctl/ctl_channel.hpp"
#include "ctl/ctl_clock.hpp"
#include "ctl/ctl_event_loop.hpp"
#include "ctl/ctl_hooks.hpp"
#include "ctl/ctl_io.hpp"
#include "ctl/ctl_pressure_service.hpp"
#include "ctl/ctl_snapshot.hpp"
#include "ctl/ctl_stir_service.hpp"
#include "ctl/ctl_temp_service.hpp"
#include "ctl/ctl_tick_gate.hpp"
#include "ctl/ctl_valve_service.hpp"
#include "hw/hardware.hpp"
#include "store/data_logger.hpp"
#include "store/settings.hpp"
#include "store/telemetry_csv.hpp"
#include <utility>

namespace reactor::ctl {

struct CoreOptions {
    int64_t tick_ms = 1200;               // C#: ControlLoop.TickInterval
    int64_t settings_poll_ms = 200;       // 设置防抖写盘的检查间隔（SettingsStore 800 ms 防抖）
    bool threaded = true;                 // false = 不起控制线程（测试手动驱动）
    bool watchdog = true;                 // 每个周期 sd_notify("WATCHDOG=1")
    int comm_grace_ticks = 5;             // C# TrackModules：开机前 5 个 Tick 不判模块掉线
    std::vector<int> installed{1, 2, 3, 4, 5, 6, 7, 8};  // C# PR_CHANNELS（只影响快照 installed）
    core::PressureUnit pressure_unit = core::PressureUnit::MPa;  // 事件文案里的压力单位（C# Units 默认 MPa）
    double heat_band_c = 2.0;             // 升温超时的「到位范围」±℃（= 程序 heat 步骤默认 band）
    int64_t press_deviation_hold_ms = 30000;  // 压力偏离持续多久才警告（设计文档 §6）
};

struct CoreDeps {
    hw::Hardware* hw = nullptr;                    // 必填；shutdown 时由核心停掉总线
    Clock* clock = nullptr;                        // 必填
    store::DataLogger* logger = nullptr;           // 可空（测试）：事件 / 遥测行写到这里
    store::SettingsStore* settings_store = nullptr;// 可空：防抖写盘
    store::SettingsState settings;                 // 初始设置（settings.json 读出的，或默认）
};

struct StopAllOptions {
    bool stop_stir = false;        // C# TryStopAll(stopStir)；/estop 兼容端点传 true
    std::string source = "触屏";   // 事件文案里的来源
};

struct StopAllResult {
    bool heat_ok = true, valves_ok = true, stir_ok = true;  // 普通优先级补发那一遍的结果（未配置的模块按成功）
    bool stir_requested = false;
    bool urgent_heat_ok = false, urgent_valves_ok = false, urgent_stir_ok = false;  // 紧急那一遍
    bool gas_aborted = false;
    std::string gas_task;
    int programs_aborted = 0;
    std::vector<int> channels;     // 被停的非停用通道
    bool stir_stopped = false;
};

struct ChannelStopResult {
    bool heat_ok = true, valve_ok = true;
    bool program_aborted = false;
    std::optional<std::string> gas_aborted;  // 被中止的气路序列名
    std::string heat_error, valve_error;
};

class ControlCore {
public:
    ControlCore(CoreDeps deps, CoreOptions opt = {});
    ~ControlCore();
    ControlCore(const ControlCore&) = delete;
    ControlCore& operator=(const ControlCore&) = delete;

    // ================= 生命周期（启动线程调） =================
    // 开机流程（温控 Ctn 自愈 / 8 路 At=4 / PID 种子、阀态读回、搅拌参数读回、启动事件）+ 定时器；threaded 时起控制线程
    void start();
    // 退出：中止程序与气路；stop_outputs 时停搅拌、8 路 At=4、阀门全关并等写完；设置落盘；停循环；停总线。可重复调用
    void shutdown(bool stop_outputs = true);

    // ================= 任何线程 =================
    bool post(std::function<void()> fn) { return loop_.post(std::move(fn)); }
    template <class F>
    auto call(F f) -> std::future<decltype(f())> { return loop_.call(std::move(f)); }
    SnapshotStore& snapshots() { return snaps_; }
    std::shared_ptr<const Snapshot> snapshot() const { return snaps_.latest(); }
    // 全部停止（设计文档 §4）。done 在控制线程上、普通补发那一遍全部回来之后调用
    void request_stop_all(StopAllOptions opts, std::function<void(const StopAllResult&)> done = {});
    EventLoop& loop() { return loop_; }
    Clock& clock() { return *clock_; }

    // ================= 手动驱动（测试 / 工具；调用线程即控制线程） =================
    // 等所有总线空闲并执行完控制线程队列，直到两边都没有新工作（timeout_ms 内）
    void settle(int timeout_ms = 3000);
    // 立即执行一个控制周期
    void tick();

    // ================= 以下只在控制线程 =================
    // ---- 状态读取 ----
    const Channel& channel(int id) const { return ch_[idx(id)]; }
    Channel& channel_mut(int id) { return ch_[idx(id)]; }  // 第二 / 三阶段改给定值、体积、状态用
    const TempService& temp() const { return temp_; }
    const PressureService& pressure() const { return press_; }
    const ValveService& valves() const { return valves_; }
    const StirService& stir() const { return stir_; }
    const store::SettingsState& settings() const { return settings_; }
    AlarmTable& alarms() { return alarms_; }
    bool is_over_temp_tripped(int id) const { return id >= 1 && id <= 8 && ot_guard_.is_tripped(id - 1); }
    bool is_over_pressure_tripped(int id) const { return id >= 1 && id <= 8 && op_guard_.is_tripped(id - 1); }
    bool any_over_pressure_tripped() const;
    // 停用 / 已结束通道的超温报警（不阻塞其他通道的气路，C# IsIdleOverTempAlarm）
    bool is_idle_over_temp_alarm(int id) const;
    // 这一路现在有没有动作（程序 / 加热温控 / 气路 / 报警态）：停止与全部停止据此决定要不要转「手动停止」
    bool channel_has_activity(int id) const;
    bool manual_valve() const { return manual_valve_; }
    int64_t snapshot_seq() const { return snap_seq_; }
    const TickGate& tick_gate() const { return gate_; }

    // ---- 设置（设置副本归控制线程；改完 revision+1、防抖写盘、运行参数立即生效） ----
    // fn 里自己调 store::patch_settings / set_band_bounds（它们成功时已经 revision+1）时传 bump_revision=false
    void update_settings(const std::function<void(store::SettingsState&)>& fn, bool bump_revision = true);

    // ---- 事件 / 报警 ----
    // 只写事件文件（不进报警表），显式 kind
    void file_event(const std::string& level, const std::string& origin, const std::string& title, const std::string& detail,
                    const std::string& kind);
    bool ack_alarm(const std::string& id) { return alarms_.ack(id); }
    int subscribe_alarms(std::function<void(const AlarmItem&)> fn);   // 在控制线程上回调（WS alarm 消息）
    // 提示（C# Toast 的等价物，不落盘；WS / 触屏自己决定怎么显示）。kind：ok / warn / err
    int subscribe_notices(std::function<void(const std::string& kind, const std::string& msg)> fn);
    // 发一条提示（第二阶段的程序 / 泄漏测试 / 标定用，C# Toast）
    void emit_notice(const std::string& kind, const std::string& msg) { notice(kind, msg); }

    // ---- 加热（C# MainViewModel：StartFixedHeat / SetFixedTarget / WriteTempSpSafeAsync / 曲线引擎） ----
    // 定值加热：SP 走防超调逼近（先写 目标−Δ，之后每周期推进）。target 有值时先改目标温度。
    // started_by 有值时同 C# TryStartHeat：Idle/Done/Ready → Heating，并记 startedBy。同时打开升温超时监视。
    void start_fixed_heat(int id, std::optional<double> target = std::nullopt, std::optional<std::string> started_by = std::nullopt);
    // 加热中改目标（逼近重规划）；未加热时直接写 SP（C# SetFixedTarget）
    void retarget(int id, double target);
    // 全链启动 + SP（C# StartBandHeatAsync：开始运行里 TSp<35 或曲线模式的通道）
    void start_band_heat(int id, double sp);
    void set_sp_mode(int id, const std::string& mode);  // fixed / curve
    // 曲线：写入段（≤16）；启动（以当前 PV 为起点，按最高段温度选 PID 档，全链启动失败则停曲线并报警）；停止（保持最后 SP）
    void set_profile(int id, const std::vector<core::ProfileSegment>& segs);
    void start_curve(int id, std::optional<std::string> started_by = std::nullopt);
    void stop_curve(int id);
    // 停加热：停曲线 / 逼近 / 升温超时监视，At=4；失败记报警（C# StopHeatCoreAsync）。cb(ok) 可空
    void stop_heat(int id, std::function<void(bool ok)> cb = {});
    // 给第二阶段：全链启动 / 写 SP（按 band_temp 选档）
    void start_heat_chain(int id, double band_temp, double initial_sp, std::function<void(bool ok)> cb);
    void write_temp_sp(int id, double sp, double band_temp, std::function<void(bool ok)> cb);
    // 自整定启动写入（Srun=0 → At=1，带停止代号：全部停止之后不再写下去）。第二阶段新增
    void start_autotune_chain(int id, std::function<void(bool ok)> cb) { temp_.start_autotune(id, stop_.current(), std::move(cb)); }

    // ---- 通道 ----
    // 停止（→ Done）/ 停用（→ Idle）：停曲线 / 逼近；该通道被程序执行 → 中止程序（程序收尾停加热）；
    // 参与气路序列 → 标失效并通知中止；否则 At=4；SV 开着则关。C# TryStopChannel + StopChannelTailAsync
    void stop_channel(int id, bool disable, std::function<void(const ChannelStopResult&)> cb = {});

    // ---- 阀门（唯一写入口，见 ctl_valve_service.hpp） ----
    // 租约检查参数由核心按当前气路租约自动填入（opts 里只看 dead_ms / retries）
    void write_valves(const core::ValveFrame& f, ValveWriteOpts opts, ValveService::DoneFn cb);
    // 收尾全关（超压中止期间保持排空，C# CloseAllValvesAsync）。cb 可空；失败记报警
    void close_all_valves(ValveService::DoneFn cb = {});
    // 只关一路 SV（程序收尾 C# CloseReactorValveAsync）：失败记报警「SVn 关闭失败」。第二阶段新增
    void close_sv(int id, ValveService::DoneFn cb = {});
    void set_valve_hold(const core::ValveFrame& f) { valves_.set_hold(f); }
    void clear_valve_hold() { valves_.clear_hold(); }
    int64_t sv_open_gen(int id) const { return valves_.sv_open_gen(id); }
    void set_manual_valve(bool on) { manual_valve_ = on; }

    // ---- 搅拌 ----
    void stir_start(int rpm, std::function<void(bool)> cb = {});
    void stir_stop(std::function<void(bool)> cb = {});
    void stir_set_rpm(int rpm, std::function<void(bool)> cb = {});
    void stir_clear_fault(std::function<void(bool)> cb = {});

    // ---- 吸气量（C# MainViewModel.Uptake.cs） ----
    bool start_uptake(int id, const std::string& why);
    void stop_uptake(int id, const std::string& why);
    void reset_uptake(int id);
    double gas_volume_ml(int id) const;

    // ---- 气路租约（第二阶段的序列在这里登记；超压联锁 / 全部停止从这里无条件中止） ----
    const core::GasLease& gas_lease() const { return lease_; }
    int64_t begin_gas(core::GasOwner owner, const std::string& task, const std::vector<int>& participants,
                      const std::vector<int>& watch, std::optional<int> owner_rv = std::nullopt);
    bool end_gas(core::GasOwner owner, int64_t gen = 0);
    // 无条件中止任何占用者（超压联锁 / 全部停止 / 退出）：记来由并通知监听者；监听者应同步收尾并 end_gas
    void abort_all_gas(core::GasAbortCause cause);
    core::GasAbortCause last_gas_abort_cause() const { return last_abort_cause_; }
    bool abort_keep_vent() const { return abort_keep_vent_; }
    // 监听「中止气路」：cause 为来由；channel 有值 = 只因该通道被停止 / 停用（C# AbortGas(owner)）
    int add_gas_abort_listener(std::function<void(core::GasAbortCause cause, std::optional<int> channel)> fn);
    void remove_gas_abort_listener(int id);  // 第二阶段新增（监听者先于核心析构时注销）

    // ---- 新三级警告的监视（设计文档 §6；定值加热自动打开升温超时，压力偏离由第二阶段在充压完成后打开） ----
    void begin_heat_watch(int id, double target, double band_c);
    void end_heat_watch(int id);
    void begin_pressure_watch(int id, double target_psi);
    void end_pressure_watch(int id);

    // ---- 扩展点 ----
    void set_program_hooks(ProgramHooks* h) { programs_ = h ? h : &default_programs_; }
    void set_activity_hooks(ActivityHooks* h) { activity_ = h ? h : &default_activity_; }
    void set_mode_hooks(ModeHooks* h) { mode_ = h ? h : &default_mode_; }
    TempService& temp_mut() { return temp_; }          // 第二阶段自整定用（on_autotune_finished 等）
    PressureService& pressure_mut() { return press_; } // 第二阶段即时读压（read_now）

    // ---- 遥测行（测试 / 会话结束时补最后一行用） ----
    store::TelemetryRow build_telemetry_row() const;

private:
    static size_t idx(int id) { return static_cast<size_t>(id - 1); }
    void boot();
    void notice(const std::string& kind, const std::string& msg);
    void apply_runtime_settings();
    std::string fmt_p(double psi) const;
    std::string local_time(int64_t wall_ms) const;

    // safety（ctl_core_safety.cpp）
    void track_modules();
    void track_module(int k, const char* name, bool configured, bool online, const hw::CommStats& s);
    void check_over_temp(Channel& c, bool real_t);
    void set_over_temp_alarm(Channel& c);
    void send_over_temp_stop(int id, core::OverTempAction why);
    void check_over_pressure(Channel& c, bool real_p);
    void flush_over_pressure_valves();
    void send_over_pressure_heat(int id, const std::string& why, int seq);
    void check_heat_timeouts(bool real_t);
    void check_pressure_deviation(bool real_p);
    void on_stir_alarm_code(int code);

    // heating（ctl_core_heat.cpp）
    void step_curves();
    void step_approaches(bool real_t);
    void report_heat_start_failed(int id);
    void update_uptake(Channel& c, bool real_t, bool real_p);

    // stop-all（ctl_core.cpp）
    struct StopAllState;
    void stop_all_cleanup(const std::shared_ptr<StopAllState>& st);
    void stop_all_maybe_finish(const std::shared_ptr<StopAllState>& st);

    // publish（ctl_core_publish.cpp）
    void publish_snapshot();

    CoreOptions opt_;
    hw::Hardware* hw_;
    Clock* clock_;
    store::DataLogger* logger_;
    store::SettingsStore* settings_store_;
    store::SettingsState settings_;

    EventLoop loop_;
    StopEpoch stop_;
    CoreIo io_;
    TempService temp_;
    PressureService press_;
    ValveService valves_;
    StirService stir_;
    AlarmTable alarms_;
    SnapshotStore snaps_;
    TickGate gate_;

    std::array<Channel, 8> ch_;
    core::OverTempGuard ot_guard_{8};
    core::OverPressureGuard op_guard_{8};
    std::array<int64_t, 8> ot_fail_log_at_{}, op_valve_fail_log_at_{}, op_heat_fail_log_at_{};
    struct OpPending { int id; std::string why; int seq; };
    std::vector<OpPending> op_valve_pending_;
    int mod_ticks_ = 0;
    std::array<std::optional<bool>, 4> mod_prev_{};
    int stir_fault_code_ = 0;   // C# StirFaultWatch._code
    bool manual_valve_ = false;

    core::GasLease lease_;
    core::GasAbortCause last_abort_cause_ = core::GasAbortCause::None;
    bool abort_keep_vent_ = false;
    std::vector<std::pair<int, std::function<void(core::GasAbortCause, std::optional<int>)>>> gas_listeners_;
    std::vector<std::pair<int, std::function<void(const std::string&, const std::string&)>>> notice_subs_;
    std::vector<std::pair<int, std::function<void(const AlarmItem&)>>> alarm_subs_;
    int next_sub_ = 1;

    ProgramHooks default_programs_;
    ActivityHooks default_activity_;
    ModeHooks default_mode_;
    ProgramHooks* programs_ = &default_programs_;
    ActivityHooks* activity_ = &default_activity_;
    ModeHooks* mode_ = &default_mode_;

    int64_t snap_seq_ = 0;
    bool started_ = false, shut_ = false;
    EventLoop::TimerId tick_timer_ = 0, settings_timer_ = 0;
};

}  // namespace reactor::ctl
