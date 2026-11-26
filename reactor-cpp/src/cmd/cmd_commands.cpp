// 命令层：仲裁、系统（全部停止 / 开始运行 / 预运行检查）、记录（报警 / 会话 / 标记）、控制权、设置。
// C#: Commands/OperatorCommands.cs、.System.cs、.Records.cs、.Control.cs、.Settings.cs、Api/Hmi/ControlBridge.cs、
//     ViewModels/MainViewModel.cs ComputePreRun、Models/RunSelection.cs、ViewModels/MainViewModel.Logging.cs（会话）
#include <algorithm>
#include "core/chan_name.hpp"
#include <cmath>

#include "cmd/cmd_commands.hpp"
#include "common/clock.hpp"
#include "core/num_format.hpp"
#include "core/op_codes.hpp"
#include "core/program/program_step_format.hpp"
#include "core/units.hpp"
#include "store/store_util.hpp"

namespace reactor::cmd {

namespace oc = core::op_codes;
using core::ReactorState;
using core::fmt_dec;

namespace {
std::string join_rv(const std::vector<int>& ids) { return core::chan_names(ids); }
std::string trim(const std::string& s) {
    size_t a = 0, b = s.size();
    while (a < b && (s[a] == ' ' || s[a] == '\t' || s[a] == '\r' || s[a] == '\n')) ++a;
    while (b > a && (s[b - 1] == ' ' || s[b - 1] == '\t' || s[b - 1] == '\r' || s[b - 1] == '\n')) --b;
    return s.substr(a, b - a);
}
// UTF-16 码元数（C# string.Length）
size_t u16len(const std::string& s) {
    size_t n = 0;
    for (size_t i = 0; i < s.size(); ++i) {
        unsigned char c = static_cast<unsigned char>(s[i]);
        if ((c & 0xC0) == 0x80) continue;
        n += (c >= 0xF0) ? 2 : 1;
    }
    return n;
}
}  // namespace

Commands::Commands(CommandsDeps d) : d_(std::move(d)) {
    if (!d_.mono_ms) d_.mono_ms = [] { return reactor::mono_ms(); };
    std::string text;
    if (!d_.prefs_path.empty() && store::read_file(d_.prefs_path, text)) {
        auto j = nlohmann::json::parse(text, nullptr, false);
        if (j.is_object() && j.contains("channels") && j["channels"].is_array())
            for (const auto& c : j["channels"]) {
                int id = c.value("id", 0);
                if (id < 1 || id > 8) continue;
                auto& p = prefs_[static_cast<size_t>(id - 1)];
                if (c.contains("bandC") && c["bandC"].is_number()) p.band_c = std::clamp(c["bandC"].get<double>(), 0.5, 10.0);
                if (c.contains("antiOvershoot") && c["antiOvershoot"].is_boolean()) p.anti_overshoot = c["antiOvershoot"].get<bool>();
            }
    }
}

int64_t Commands::now_mono() const { return d_.mono_ms(); }

std::optional<OpResult> Commands::admit(const Source& src, ActionClass cls, Exposure exp) const {
    auto dec = d_.arbiter->check(cls, src, exp);
    if (dec.allowed) return std::nullopt;
    return OpResult::fail(dec.code, dec.message, dec.code == oc::kModeRemote ? "warn" : "err");
}

// ==================== 全部停止 / 兼容急停 ====================

void Commands::stop_all(const Source& src, bool stop_stir, Done done) {
    if (auto d = admit(src, ActionClass::Stop)) return done(*d);
    bool gas = core().gas_lease().busy();
    auto programs_before = programs().running_ids();
    ctl::StopAllOptions o;
    o.stop_stir = stop_stir;
    o.source = src.label();
    core().request_stop_all(o, [gas, programs_before, done](const ctl::StopAllResult& r) {
        ojson progs = ojson::array();
        for (int id : programs_before) progs.push_back(id);
        ojson chans = ojson::array();
        for (int id : r.channels) chans.push_back(id);
        ojson data{{"gasAborted", gas || r.gas_aborted},
                   {"programAborted", r.programs_aborted > 0 || !programs_before.empty()},
                   {"programsAborted", progs},
                   {"channels", chans},
                   {"stirStopped", r.stir_stopped}};
        const std::string valve_msg = "阀门全关写入失败（IO16R 通讯）· 请立即手动关闭气源";
        // 失败报警（「阀门全关写入失败」「全部停止 · 停止加热未确认」）由控制核心记，这里只给结果
        if (!r.heat_ok)
            return done(OpResult::fail(oc::kHardwareWriteFailed,
                                       "停止加热输出未确认（AI-8 通讯失败）· 请到设备现场确认" + (r.valves_ok ? std::string() : " · " + valve_msg),
                                       "err", data));
        if (!r.valves_ok) return done(OpResult::fail(oc::kHardwareWriteFailed, valve_msg, "err", data));
        done(OpResult::done((gas || r.gas_aborted) ? "已停止全部通道并中止气路序列 · 阀门全关" : "已停止全部通道（含加热输出）· 阀门全关",
                            "warn", data));
    });
}

OpResult Commands::estop(const Source& src, const std::string& note) {
    // 设计文档 §5.3：/estop 保留给旧 PC 端，= 全部停止（含搅拌），不锁存、不收回控制权；立即 accepted（急停面板本身就是反馈）
    if (auto d = admit(src, ActionClass::Stop)) return *d;
    ctl::StopAllOptions o;
    o.stop_stir = true;
    o.source = note.empty() ? src.label() : src.label() + " · " + note;
    core().request_stop_all(o, {});
    return OpResult::accept("", "ok", ojson{{"source", src.label()}});
}

// ==================== 开始运行 / 预运行检查 ====================

namespace {
bool can_start_heat(ReactorState s, bool ot, bool op, bool owned) {
    return !owned && (s == ReactorState::Ready || s == ReactorState::Done) && !ot && !op;
}
}  // namespace

PreRunResult Commands::compute_pre_run() const {
    PreRunResult r;
    auto add = [&](const char* kind, const std::string& title, const std::string& detail) {
        r.checks.push_back(PreRunCheck{kind, title, detail});
    };
    const auto& st = core().settings().settings;
    // 没有急停（设计文档 §6）：C# 的「急停」检查项去掉

    // 温控：开始运行要下发加热，温控必须在线
    if (!core().temp().configured()) add("err", "温控", "温控未配置串口（hardware.json temp.port），无法加热");
    else if (!core().temp().online()) add("err", "温控", "温控离线（AI-8 通讯失败），请检查接线与串口");
    else add("ok", "温控", "AI-8 在线");

    // 压力
    std::vector<int> hot;
    for (int i = 1; i <= 8; ++i) {
        const auto& c = core().channel(i);
        if (c.state != ReactorState::Idle && c.p >= st.over_pressure) hot.push_back(i);
    }
    if (!core().pressure().configured()) add("err", "压力 / 超压监视", "压力采集未配置串口（hardware.json press.port），无法监视超压");
    else if (!core().pressure().online()) add("err", "压力 / 超压监视", "压力采集离线（8AI 通讯失败），无法监视超压");
    else if (!hot.empty()) add("err", "压力 / 超压监视", join_rv(hot) + " 压力不低于超压报警值，需先泄压处理");
    else add("ok", "压力 / 超压监视", "8 路在线 · 均低于超压报警值 " + core::format_p(st.over_pressure, d_.unit));

    // 阀门
    bool vent_on = core().valves().shown()[static_cast<size_t>(core::valve_frame::kVent)];
    if (!core().valves().configured()) add("warn", "阀门", "阀门未配置串口（hardware.json io.port），无法确认阀门状态");
    else if (!core().valves().online()) add("warn", "阀门", "阀门模块离线，无法确认阀门状态");
    else if (vent_on) add("err", "阀门", "排空阀处于开启状态，运行前必须关闭");
    else add("ok", "阀门", "IO16R 在线 · 排空阀已关闭");

    // 气路
    const auto& l = core().gas_lease();
    if (l.busy() && l.owner() != core::GasOwner::Program) add("err", "气路", l.task() + "进行中，请等待完成或先停止");
    else if (l.busy()) add("warn", "气路", l.task() + "进行中（程序通道由程序控制，本次运行不使用气路）");
    else add("ok", "气路", "气路空闲");

    // 控制模式
    if (core().manual_valve()) add("warn", "控制模式", "手动阀控仍开启，自动运行时建议关闭");
    else add("ok", "控制模式", "自动模式，手动阀控已关闭");

    // 搅拌器
    int rpm = core().stir().rpm();
    if (!core().stir().configured()) add("warn", "搅拌器", "搅拌未配置串口（hardware.json stir.port），本次运行不搅拌");
    else if (!core().stir().online()) add("warn", "搅拌器", "搅拌驱动器离线，开始运行后可能无法搅拌");
    else add("ok", "搅拌器", "在线 · 开始后以 " + std::to_string(rpm) + " rpm 运转（全部反应釜共用）");

    // 报警 / 超温 / 超压未解除的通道：阻塞运行
    std::vector<int> blocking;
    for (int i = 1; i <= 8; ++i)
        if (core().channel(i).state == ReactorState::Alarm || core().is_over_temp_tripped(i) || core().is_over_pressure_tripped(i))
            blocking.push_back(i);
    if (!blocking.empty()) add("err", "报警通道", join_rv(blocking) + " 存在报警或超温/超压未解除的通道，请先处理并停止 / 复位");
    else add("ok", "报警通道", "无报警或超温/超压未解除的通道");

    auto prvs = programs().running_ids();
    if (!prvs.empty()) {
        std::string names = join_rv(prvs);
        add("warn", "程序通道",
            names + " 正在执行程序，不参与本次运行 · 注意搅拌器全机共用：开始运行会以 " + std::to_string(rpm) + " rpm 开启搅拌（" + names +
                " 也会被搅拌），程序的搅拌步骤结束 / 中止时也会停掉搅拌");
    }

    for (int i = 1; i <= 8; ++i)
        if (can_start_heat(core().channel(i).state, core().is_over_temp_tripped(i), core().is_over_pressure_tripped(i),
                           programs().is_owned(i)))
            r.channels.push_back(i);
    if (!r.channels.empty()) add("ok", "运行通道", std::to_string(r.channels.size()) + " / 8 个通道将参与本次运行");
    else add("err", "运行通道", "没有可开始的通道（已启用 / 已结束），请先启用至少一个反应釜");

    int ok = 0, warn = 0, err = 0;
    for (const auto& c : r.checks) {
        if (c.kind == "ok") ++ok;
        else if (c.kind == "warn") ++warn;
        else ++err;
    }
    r.can_run = err == 0;
    r.summary = err == 0 ? "检查通过 · " + std::to_string(ok) + " 项正常" + (warn > 0 ? " · " + std::to_string(warn) + " 项提示" : "")
                         : std::to_string(err) + " 项异常需处理 · " + std::to_string(warn) + " 项提示 · " + std::to_string(ok) + " 项正常";
    return r;
}

namespace {
ojson checks_json(const std::vector<PreRunCheck>& cs) {
    ojson a = ojson::array();
    for (const auto& c : cs) a.push_back(ojson{{"kind", c.kind}, {"title", c.title}, {"detail", c.detail}});
    return a;
}
ojson ids_json(const std::vector<int>& ids) {
    ojson a = ojson::array();
    for (int i : ids) a.push_back(i);
    return a;
}
}  // namespace

OpResult Commands::run(const Source& src, bool dry_run) {
    if (auto d = admit(src, dry_run ? ActionClass::Record : ActionClass::Control)) return *d;
    if (!dry_run && gas_busy_not_program())
        return OpResult::fail(oc::kGasBusy, gas_task() + "进行中 · 无法开始运行");
    auto pre = compute_pre_run();
    if (dry_run)
        return pre.can_run ? OpResult::done(pre.summary, "ok",
                                            ojson{{"canRun", true}, {"checks", checks_json(pre.checks)}, {"channels", ids_json(pre.channels)}})
                           : OpResult::fail(oc::kPreflightFailed, pre.summary, "err",
                                            ojson{{"canRun", false}, {"checks", checks_json(pre.checks)}});
    if (!pre.can_run)
        return OpResult::fail(oc::kPreflightFailed, "存在未处理的异常项，无法开始运行", "err", ojson{{"checks", checks_json(pre.checks)}});
    std::vector<int> skipped;
    for (int i = 1; i <= 8; ++i)
        if (core().channel(i).state != ReactorState::Idle && std::find(pre.channels.begin(), pre.channels.end(), i) == pre.channels.end())
            skipped.push_back(i);
    for (int id : pre.channels) {
        auto& c = core().channel_mut(id);
        c.set_state(ReactorState::Heating);
        c.started_by = started_by_of(src);
    }
    if (!core().stir().on() && core().stir().configured()) core().stir_start(core().stir().rpm());
    for (int id : pre.channels) {
        const auto& c = core().channel(id);
        if (c.sp_mode == "fixed" && c.tsp >= 35) heat_channel(id);
        else core().start_band_heat(id, c.tsp);
    }
    return OpResult::accept("预运行检查通过 · 已开始运行（" + std::to_string(pre.channels.size()) + " 个通道）· PID 已按温区自动套档", "ok",
                            ojson{{"requested", ids_json(pre.channels)}, {"skipped", ids_json(skipped)}, {"stirOn", core().stir().on()}});
}

ojson Commands::preflight(const Source& src) {
    auto r = run(src, true);
    auto pre = compute_pre_run();
    return ojson{{"canRun", r.ok && pre.can_run},
                 {"summary", r.ok || r.code == oc::kPreflightFailed ? pre.summary : r.message},
                 {"checks", checks_json(pre.checks)},
                 {"channels", ids_json(pre.channels)}};
}

// ==================== 报警 / 会话 / 标记 ====================

OpResult Commands::ack_alarm(const Source& src, const std::string& alarm_id) {
    if (auto d = admit(src, ActionClass::Record)) return *d;
    const auto* a = core().alarms().find(alarm_id);
    if (!a) return OpResult::fail(oc::kNotFound, "报警 " + alarm_id + " 不存在");
    core().ack_alarm(alarm_id);
    a = core().alarms().find(alarm_id);
    auto r = OpResult::done("已确认", "ok", ojson{{"id", alarm_id}, {"stat", a ? a->state : std::string()}});
    r.set_quiet();
    return r;
}

OpResult Commands::ack_all_alarms(const Source& src) {
    if (auto d = admit(src, ActionClass::Record)) return *d;
    int n = core().alarms().ack_all();   // 每条变化经 on_change 推 WS alarm
    auto r = OpResult::done(n > 0 ? "已确认 " + std::to_string(n) + " 条报警" : "没有需要确认的报警", "ok", ojson{{"acked", n}});
    r.set_quiet();
    return r;
}

OpResult Commands::begin_session(const Source& src, const std::string& test_id_in, std::optional<std::string> note) {
    if (auto d = admit(src, ActionClass::Record)) return *d;
    std::string test_id = trim(test_id_in);
    if (test_id.empty()) return OpResult::fail(oc::kBadRequest, "测试编号不能为空", "warn");
    if (u16len(test_id) > 64) return OpResult::fail(oc::kBadRequest, "测试编号过长（≤ 64 字符）", "warn");
    if (!d_.logger || !d_.sessions) return OpResult::fail(oc::kInternal, "没有配置运行记录");
    if (CommandPolicy::strict(src))
        if (auto cur = d_.logger->session_key())
            return OpResult::fail(oc::kSessionActive, "已有测试会话进行中（" + *cur + "）· 请先结束", "warn", ojson{{"sessionId", *cur}});
    // 本地：已有会话时先结束它（C# BeginTestSession 里的 EndTestSession）
    if (d_.logger->session_key()) end_session_on_exit();
    int64_t now = core().clock().wall_ms();
    std::string path = d_.logger->begin_session(test_id, store::WallTime::local(now));
    std::string key = d_.logger->session_key().value_or("");
    d_.logger->add_marker("会话开始 " + key);
    std::string n = note ? trim(*note) : std::string();
    core().alarms().log("info", "系统", "测试会话开始",
                        key + " · " + store::base_name(path) + " · " + src.label() + (n.empty() ? "" : " · " + n), "session.started");
    int64_t head = d_.logger->events() ? d_.logger->events()->head() : 0;
    auto m = d_.sessions->begin(key, d_.logger->session_id().value_or(test_id), n, now, src.is_remote() ? "remote" : "local",
                                src.is_remote() ? std::optional<std::string>(src.client_name) : std::nullopt, d_.simulated, head,
                                store::base_name(path));
    session_owner_ = std::make_pair(key, src);
    return OpResult::done("测试会话已开始 · " + key, "ok",
                          ojson{{"id", key}, {"testId", d_.logger->session_id().value_or(test_id)}, {"startedAt", store::fmt_utc_json(m.started_at_ms)},
                                {"startEventSeq", m.start_event_seq}, {"file", store::base_name(path)}});
}

void Commands::end_session_on_exit() {
    if (!d_.logger) return;
    auto key = d_.logger->session_key();
    if (!key) return;
    std::string end = "会话结束 " + *key;
    d_.logger->add_marker(end);
    d_.logger->end_session(core().build_telemetry_row(), end);
    core().alarms().log("info", "系统", "测试会话结束", *key + " · 触屏", "session.ended");
    if (d_.sessions) d_.sessions->end(core().clock().wall_ms(), d_.logger->events() ? d_.logger->events()->head() : 0);
    session_owner_.reset();
}

OpResult Commands::end_session(const Source& src) {
    if (auto d = admit(src, ActionClass::Record)) return *d;
    if (!d_.logger) return OpResult::none();
    auto id = d_.logger->session_key();
    if (!id) return OpResult::none();
    if (src.is_remote()) {
        bool owner = session_owner_ && session_owner_->first == *id && session_owner_->second.is_remote() &&
                     !session_owner_->second.client_id.empty() && session_owner_->second.client_id == src.client_id;
        if (!owner)
            return OpResult::fail(oc::kSessionNotOwner, "只有会话创建者或设备触屏可以结束该会话", "warn", ojson{{"sessionId", *id}});
    }
    std::string end = "会话结束 " + *id;
    d_.logger->add_marker(end);
    d_.logger->end_session(core().build_telemetry_row(), end);
    core().alarms().log("info", "系统", "测试会话结束", *id + " · " + src.label(), "session.ended");
    int64_t now = core().clock().wall_ms();
    if (d_.sessions) d_.sessions->end(now, d_.logger->events() ? d_.logger->events()->head() : 0);
    session_owner_.reset();
    return OpResult::done("测试会话已结束 · " + *id, "ok", ojson{{"id", *id}, {"endedAt", store::fmt_utc_json(now)}});
}

OpResult Commands::add_marker(const Source& src, const std::string& text_in) {
    if (auto d = admit(src, ActionClass::Record)) return *d;
    std::string text = trim(text_in);
    if (text.empty()) return OpResult::fail(oc::kBadRequest, "标记内容不能为空", "warn");
    if (u16len(text) > 200) return OpResult::fail(oc::kBadRequest, "标记过长（≤ 200 字符）", "warn");
    if (!d_.logger) return OpResult::fail(oc::kInternal, "没有配置运行记录");
    auto key = d_.logger->session_key();
    if (CommandPolicy::strict(src) && !key) return OpResult::fail(oc::kNotFound, "当前没有活动会话", "warn");
    d_.logger->add_marker(text);
    core().alarms().log("info", "系统", "标记", text + (src.is_remote() ? " · " + src.label() : "") + (key ? " · 会话 " + *key : ""),
                        "session.marker");
    int64_t now = core().clock().wall_ms();
    std::optional<store::SessionMarker> mk;
    if (d_.sessions)
        mk = d_.sessions->add_marker(text, now, d_.logger->events() ? d_.logger->events()->head() : 0,
                                     src.is_remote() ? "remote" : "local",
                                     src.is_remote() ? std::optional<std::string>(src.client_name) : std::nullopt);
    return OpResult::done("已添加标记", "ok",
                          ojson{{"id", mk ? ojson(mk->id) : ojson(nullptr)},
                                {"text", text},
                                {"ts", mk ? ojson(store::fmt_utc_json(mk->ts_ms)) : ojson(nullptr)},
                                {"seq", mk ? ojson(mk->seq) : ojson(nullptr)},
                                {"sessionId", key ? ojson(*key) : ojson(nullptr)}});
}

// ==================== 控制权（本地） ====================

OpResult Commands::grant_control(const Source& src, const std::string& request_id) {
    if (auto d = admit(src, ActionClass::Record, Exposure::LocalOnly)) return *d;
    auto lease = d_.lease->grant(request_id, now_mono());
    if (!lease) return OpResult::fail(oc::kNotFound, "该申请已过期或不存在", "warn");
    return OpResult::done("已允许 " + lease->client + " 远程控制", "ok", ojson{{"requestId", request_id}, {"client", lease->client}});
}

OpResult Commands::deny_control(const Source& src, const std::string& request_id) {
    if (auto d = admit(src, ActionClass::Record, Exposure::LocalOnly)) return *d;
    bool ok = d_.lease->deny(request_id, now_mono());
    return ok ? OpResult::done("已拒绝远程控制申请", "warn", ojson{{"requestId", request_id}}) : OpResult::none();
}

OpResult Commands::take_back_control(const Source& src) {
    if (auto d = admit(src, ActionClass::Record, Exposure::LocalOnly)) return *d;
    bool was = d_.lease->take_back(now_mono());
    return was ? OpResult::done("已切回本地控制 · 远程租约作废", "ok") : OpResult::none();
}

void Commands::on_remote_lost(const std::string& policy, const std::string& token, const std::string& client) {
    Source src = Source::remote(token, client);
    if (policy == "estop") {
        // 没有急停：按「全部停止（含搅拌）」处理（设计文档 §5.3 的 /estop 兼容语义）
        ctl::StopAllOptions o;
        o.stop_stir = true;
        o.source = src.label() + " · 心跳丢失（onRemoteLost = estop）";
        core().request_stop_all(o, {});
        core().alarms().log("warn", "系统", "远程心跳丢失 · 已全部停止", client + " · onRemoteLost = estop（新后端按全部停止含搅拌处理）",
                            "control.remoteLostStop");
        core().emit_notice("err", "远程 " + client + " 心跳丢失 · 已按设置全部停止");
        return;
    }
    if (policy == "stopHeat") {
        std::string label = started_by_of(src);
        std::vector<int> progs, chans;
        for (int rvid : programs().running_ids()) {
            const auto* info = programs().run_info(rvid);
            if (info && info->is_active() && info->started_by == label) progs.push_back(rvid);
        }
        for (int p : progs) programs().abort(p);
        for (int i = 1; i <= 8; ++i) {
            const auto& c = core().channel(i);
            if (c.started_by && *c.started_by == label && c.state != ReactorState::Idle) chans.push_back(i);
        }
        for (int id : chans) core().stop_channel(id, false, {});
        core().alarms().log("warn", "系统", "远程心跳丢失 · 已停止该客户端启动的过程",
                            client + " · onRemoteLost = stopHeat · 中止程序 " + (progs.empty() ? std::string("无") : join_rv(progs)) +
                                " · 停止通道 " + (chans.empty() ? std::string("无") : join_rv(chans)),
                            "control.remoteLostStop");
        core().emit_notice("warn", "远程 " + client + " 心跳丢失 · 已停止其启动的 " + std::to_string(chans.size()) + " 个通道 / " +
                                       std::to_string(progs.size()) + " 个程序");
        return;
    }
    core().emit_notice("warn", "远程 " + client + " 宽限期满 · 已回到本地控制 · 设备继续运行（onRemoteLost = keep）");
}

// ==================== 设置（本地） ====================

void Commands::stir_device_tail(store::SettingKey key, double value, Done done) {
    hw::Hardware* hw = d_.hw;
    if (!hw || !hw->stir_configured() || !hw->stir_bus) {
        auto r = OpResult::fail(oc::kHwNotConfigured, "搅拌未配置串口（hardware.json stir.port），驱动器参数未下发");
        r.set_quiet();
        return done(r);
    }
    std::vector<hw::ModbusRequest> reqs;
    hw::ModbusRequest read;
    std::string what, unit_fmt;
    if (key == store::SettingKey::StirCurrent) {
        reqs = hw->stir.req_set_peak_current(value);
        read = hw->stir.req_read_peak_current();
        what = "峰值电流";
    } else if (key == store::SettingKey::StirStandbyPct) {
        reqs = hw->stir.req_set_standby_pct(static_cast<int>(value));
        read = hw->stir.req_read_standby_pct();
        what = "待机电流";
    } else {
        reqs = hw->stir.req_set_microstep(static_cast<int>(value));
        read = hw->stir.req_read_microstep();
        what = "细分";
    }
    reqs.push_back(read);
    size_t n = reqs.size();
    ctl::ControlCore* core = d_.core;
    hw->stir_bus->submit_seq(std::move(reqs), hw::Priority::Normal, true,
                             [core, key, what, n, done](const std::vector<hw::ModbusResult>& rs) {
                                 bool ok = rs.size() == n;
                                 for (const auto& r : rs) ok = ok && r.ok;
                                 std::optional<double> v;
                                 if (ok && !rs.empty()) {
                                     if (key == store::SettingKey::StirCurrent) v = hw::StepperDriver::decode_tenths(rs.back());
                                     else if (auto u = hw::StepperDriver::decode_u16(rs.back())) v = *u;
                                 }
                                 core->post([key, what, v, done] {
                                     const char* name = key == store::SettingKey::StirCurrent     ? "StirCurrent"
                                                        : key == store::SettingKey::StirStandbyPct ? "StirStandbyPct"
                                                                                                   : "StirMicrostep";
                                     if (!v) {
                                         auto r = OpResult::fail(oc::kHardwareWriteFailed, "搅拌驱动器" + what + "写入未确认（DM2C 无回读）");
                                         r.set_quiet();
                                         return done(r);
                                     }
                                     std::string msg = key == store::SettingKey::StirCurrent
                                                           ? "搅拌电机峰值电流已设为 " + fmt_dec(*v, 1, 1) + " A 并保存"
                                                       : key == store::SettingKey::StirStandbyPct
                                                           ? "搅拌待机电流已设为 " + fmt_dec(*v, 0) + "% 并保存"
                                                           : "细分已设为 " + fmt_dec(*v, 0) + " 脉冲/转 并保存";
                                     done(OpResult::done(msg, "ok", ojson{{"key", name}, {"value", core::program::json_number(*v)}}));
                                 });
                             });
}

void Commands::patch_settings(const Source& src, const std::vector<store::SettingChange>& changes, std::optional<bool> stop_heat_on_op,
                              SettingsExtra extra, std::optional<int64_t> expected_revision, Done done) {
    if (auto d = admit(src, ActionClass::Record, Exposure::LocalOnly)) return done(*d);
    bool extra_changed = false;
    if (extra) {
        auto probe = core().settings().settings;
        extra_changed = extra(probe);
    }
    if (changes.empty() && !stop_heat_on_op && !extra_changed) {
        // 什么都没变（值相同）：不加 revision
        if (expected_revision && *expected_revision != core().settings().revision)
            return done(OpResult::fail(oc::kRevisionConflict, "设置已在别处修改过（当前版本 " + std::to_string(core().settings().revision) +
                                                                  "），请刷新后再改",
                                       "err", ojson{{"currentRevision", core().settings().revision}}));
        return done(OpResult::done("设置没有变化", "ok",
                                   ojson{{"revision", core().settings().revision}, {"changed", ojson::array()}, {"stopHeatChanged", false}}));
    }
    auto before = core().settings();
    // 先在副本上试（全有或全无），再真正改
    auto trial = before;
    auto pr = store::patch_settings(trial, changes, expected_revision, true);
    if (!pr.ok) {
        ojson detail = nullptr;
        if (pr.violation)
            detail = ojson{{"min", core::program::json_number(pr.violation->min)}, {"max", core::program::json_number(pr.violation->max)},
                           {"value", core::program::json_number(pr.violation->value)}};
        if (pr.code == oc::kRevisionConflict) detail = ojson{{"currentRevision", pr.revision}};
        if (pr.bad_key && detail.is_object()) detail["key"] = store::setting_specs::of(*pr.bad_key).name;
        std::string msg = pr.code == oc::kRevisionConflict
                              ? "设置已在别处修改过（当前版本 " + std::to_string(pr.revision) + "），请刷新后再改"
                              : pr.message;
        return done(OpResult::fail(pr.code, msg, "err", detail));
    }
    bool stop_changed = stop_heat_on_op && *stop_heat_on_op != before.settings.stop_heat_on_over_pressure;
    core().update_settings(
        [&](store::SettingsState& s) {
            pr = store::patch_settings(s, changes, expected_revision, true);
            if (stop_changed) s.settings.stop_heat_on_over_pressure = *stop_heat_on_op;
            if (extra) extra(s.settings);
            if (pr.changed.empty() && (stop_changed || extra_changed)) ++s.revision;
        },
        /*bump_revision=*/false);
    ojson changed = ojson::array();
    std::vector<store::SettingKey> device;
    const auto& after = core().settings().settings;
    for (auto k : pr.changed) {
        const auto& spec = store::setting_specs::of(k);
        double o = store::setting_specs::get(before.settings, k), n = store::setting_specs::get(after, k);
        std::string unit = spec.unit && *spec.unit ? std::string(" ") + spec.unit : "";
        log_setting(spec.label, fmt_dec(o, 3) + unit, fmt_dec(n, 3) + unit);
        changed.push_back(spec.name);
        if (k == store::SettingKey::StirCurrent || k == store::SettingKey::StirStandbyPct || k == store::SettingKey::StirMicrostep)
            device.push_back(k);
        if ((k == store::SettingKey::StirGearRatio || k == store::SettingKey::StirRampMs) && core().stir().on())
            core().stir_set_rpm(core().stir().rpm());   // 新传动比 / 加减速立即生效（釜转速不变）
    }
    if (stop_changed) log_setting("超压时停止加热", before.settings.stop_heat_on_over_pressure ? "开" : "关", *stop_heat_on_op ? "开" : "关");
    auto result = OpResult::done("设置已保存", "ok",
                                 ojson{{"revision", core().settings().revision}, {"changed", changed}, {"stopHeatChanged", stop_changed}});
    result.set_quiet();
    if (device.empty()) return done(result);
    // 写驱动器的项逐个下发（DM2C 存 EEPROM），回读不到的放进 warnings（设置已保存）
    auto shared = std::make_shared<OpResult>(result);
    auto remaining = std::make_shared<size_t>(device.size());
    for (auto k : device) {
        stir_device_tail(k, store::setting_specs::get(after, k), [shared, remaining, done](const OpResult& r) {
            if (!r.ok) shared->warnings.push_back(r.message);
            if (--*remaining == 0) done(*shared);
        });
    }
}

OpResult Commands::set_band_bounds(const Source& src, double low_mid, double mid_high, std::optional<int64_t> expected_revision) {
    if (auto d = admit(src, ActionClass::Control, Exposure::LocalOnly)) return *d;
    for (int i = 1; i <= 8; ++i)
        if (core().temp().ch(i).autotuning) return OpResult::fail(oc::kAutotuneActive, core::chan_name(i) + " 正在自整定 · 整定结束后再改档位分界");
    auto old = core().settings().band_bounds;
    store::PatchResult pr;
    core().update_settings([&](store::SettingsState& s) { pr = store::set_band_bounds(s, low_mid, mid_high, expected_revision); },
                           /*bump_revision=*/false);
    if (!pr.ok) {
        ojson detail = nullptr;
        if (pr.violation) detail = ojson{{"min", core::program::json_number(pr.violation->min)},
                                         {"max", core::program::json_number(pr.violation->max)},
                                         {"value", core::program::json_number(pr.violation->value)}};
        if (pr.code == oc::kRevisionConflict) detail = ojson{{"currentRevision", pr.revision}};
        return OpResult::fail(pr.code, pr.message, "err", detail);
    }
    if (old[0] != low_mid) log_setting("低温 / 中温 分界", fmt_dec(old[0], 3) + " ℃", fmt_dec(low_mid, 3) + " ℃");
    if (old[1] != mid_high) log_setting("中温 / 高温 分界", fmt_dec(old[1], 3) + " ℃", fmt_dec(mid_high, 3) + " ℃");
    for (int i = 1; i <= 8; ++i) core().temp_mut().forget_live_band(i);   // 档位变了：下一次写 SP 时按新档重写 PID
    static const char* kNames[] = {"低温", "中温", "高温"};
    ojson bands = ojson::array();
    auto br = store::band_ranges(core().settings().band_bounds);
    for (size_t i = 0; i < br.size(); ++i)
        bands.push_back(ojson{{"index", i}, {"name", kNames[i]}, {"loC", core::program::json_number(br[i].lo)},
                              {"hiC", core::program::json_number(br[i].hi)}});
    auto r = OpResult::done("温度档位分界已保存", "ok", ojson{{"bands", bands}, {"revision", core().settings().revision}});
    r.set_quiet();
    return r;
}

std::string wire_language(const std::string& stored) {
    if (stored == "zh-CN" || stored == "zh" || stored.empty()) return "zh";
    if (stored == "en-US" || stored == "en") return "en";
    auto dash = stored.find('-');
    return dash == std::string::npos ? stored : stored.substr(0, dash);
}

OpResult Commands::set_language(const Source& src, const std::string& lang) {
    if (auto d = admit(src, ActionClass::Record, Exposure::LocalOnly)) return *d;
    static const char* kKnown[] = {"zh", "en", "ja", "ko", "de", "fr", "es", "ru"};   // 接口契约 §3.6：存短码
    bool known = false;
    for (const char* k : kKnown) known = known || lang == k;
    if (!known)
        return OpResult::fail(oc::kParamOutOfRange, "不支持的语言「" + lang + "」（zh / en / ja / ko / de / fr / es / ru）", "err",
                              ojson{{"value", lang}});
    // 存储里沿用原来的写法（zh-CN），线上用短码：边界上换算（决定：zh-CN ↔ zh、en-US / en ↔ en）
    std::string old = wire_language(core().settings().settings.language);
    if (old == lang) {
        auto r = OpResult::none();
        r.data = ojson{{"code", lang}};
        return r;
    }
    core().update_settings([&](store::SettingsState& s) { s.settings.language = lang == "zh" ? "zh-CN" : lang; });
    log_setting("界面语言", old, lang);
    auto r = OpResult::done("界面语言已设为 " + lang, "ok", ojson{{"code", lang}});
    r.set_quiet();
    return r;
}

}  // namespace reactor::cmd
