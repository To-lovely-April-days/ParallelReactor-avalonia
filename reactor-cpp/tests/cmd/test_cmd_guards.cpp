// 命令层的守卫顺序 / 错误码 / 中文文案（C# OperatorCommands.*）：在不起线程的控制核心上直接调（测试线程 = 控制线程）。
#include "cmd/cmd_commands.hpp"
#include "ctl/ctl_builtin_templates.hpp"
#include "ctl/ctl_phase2_util.hpp"
#include "testing.hpp"
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

using namespace reactor;
using namespace reactor::cmd;

namespace {

struct CRig {
    ctltest::PRig r;
    ControlArbiter arb;
    ControlLease lease{arb};
    store::SessionBook sessions;
    std::unique_ptr<Commands> cmd;
    int64_t mono = 0;

    explicit CRig(bool with_logger = false)
        : r(ctltest::fast_settings(), with_logger),
          sessions([this](const std::string& rel, const std::string& json) {
              if (r.logger) r.logger->write_file(rel, json);
          }) {
        CommandsDeps d;
        d.core = r.core.get();
        d.automation = r.au.get();
        d.logger = r.logger.get();
        d.sessions = &sessions;
        d.hw = r.hw.get();
        d.arbiter = &arb;
        d.lease = &lease;
        d.mono_ms = [this] { return mono; };
        cmd = std::make_unique<Commands>(d);
    }
    // 远程持有控制权（autoGrant）
    Source take_remote(const std::string& token = "tokA", const std::string& client = "PC-1") {
        lease.request(token, client, mono, true);
        return Source::remote(token, client);
    }
    OpResult wait(std::function<void(Done)> f) {
        OpResult out;
        bool got = false;
        f([&](const OpResult& x) { out = x; got = true; });
        for (int i = 0; i < 100 && !got; ++i) r.advance(100);
        CHECK(got);
        return out;
    }
};

nlohmann::json t1_steps() {
    auto all = ctl::builtin_templates::all("PR8");
    return ctl::builtin_templates::pc_steps_to_wire(all[0]["program"]["steps"]);
}

}  // namespace

TEST(cmd_guards, admit_mode_local_remote_holder_and_mode_remote) {
    CRig t;
    Source remote = Source::remote("tokA", "PC-1");
    auto r = t.cmd->start_heat(remote, 1, 60);
    CHECK(!r.ok);
    CHECK_EQ(r.code, std::string("MODE_LOCAL"));
    CHECK_EQ(r.message, std::string("本地控制中 · 远程只能执行停止类和记录类操作"));
    // 停止类远程照样能做
    auto s = t.wait([&](Done d) { t.cmd->stop_heat(remote, 1, d); });
    CHECK(s.ok);
    // 远程持有后：触屏控制类 MODE_REMOTE（warn），另一令牌 NOT_CONTROL_HOLDER
    Source holder = t.take_remote();
    auto l = t.cmd->set_setpoint(Source::local(), 1, 80);
    CHECK_EQ(l.code, std::string("MODE_REMOTE"));
    CHECK_EQ(l.level, std::string("warn"));
    CHECK_EQ(l.message, std::string("远程控制中（PC-1）"));
    CHECK_EQ(t.cmd->set_setpoint(Source::remote("tokB", "PC-2"), 1, 80).code, std::string("NOT_CONTROL_HOLDER"));
    auto ok = t.cmd->set_setpoint(holder, 1, 80);
    CHECK(ok.ok);
    CHECK_EQ(ok.message, std::string("1 号 目标温度已设为 80 ℃"));
    CHECK_EQ(ok.data["tempSpC"].get<int>(), 80);
    // 远程不能做本机专用的动作
    CHECK_EQ(t.cmd->set_band_bounds(holder, 90, 190, std::nullopt).code, std::string("NOT_EXPOSED"));
    CHECK_EQ(t.cmd->start_calib(holder, 1, false, 0, false).code, std::string("NOT_EXPOSED"));
}

TEST(cmd_guards, strict_guards_only_for_remote) {
    CRig t;
    Source holder = t.take_remote();
    t.r.core->channel_mut(2).set_state(core::ReactorState::Idle);
    // 严格守卫：停用通道不能直接启动加热（远程）；本地照旧允许（C# CommandPolicy.Strict）
    auto r = t.cmd->start_heat(holder, 2, 60);
    CHECK_EQ(r.code, std::string("CHANNEL_STATE_INVALID"));
    CHECK_EQ(r.message, std::string("2 号 当前状态「停用」不可启动加热 · 请先启用通道"));
    // 超温报警值 250：远程 SP 须低于它
    t.r.core->channel_mut(2).set_state(core::ReactorState::Ready);
    auto hot = t.cmd->set_setpoint(holder, 2, 199);
    CHECK(hot.ok);
    // 范围：10..TMaxSp(200)
    auto bad = t.cmd->set_setpoint(holder, 2, 205);
    CHECK_EQ(bad.code, std::string("PARAM_OUT_OF_RANGE"));
    CHECK_EQ(bad.data["max"].get<int>(), 200);
    CHECK_EQ(bad.data["value"].get<int>(), 205);
    // 目标温度 < 35 拒绝启动
    t.r.core->channel_mut(3).tsp = 20;
    CHECK_EQ(t.cmd->start_heat(holder, 3, std::nullopt).message, std::string("请先设置目标温度（≥35℃）再启动加热"));
    auto ok = t.cmd->start_heat(holder, 3, 60);
    CHECK(ok.ok && ok.accepted);
    CHECK_EQ(ok.message, std::string("3 号 加热已启动 · 目标 60℃ · 防超调逼近"));
    CHECK(t.r.core->channel(3).started_by && *t.r.core->channel(3).started_by == holder.label());
    CHECK_EQ(holder.label(), std::string("远程（PC-1 · 令牌 tokA）"));
    // 通道号越界
    CHECK_EQ(t.cmd->start_heat(holder, 9, 60).code, std::string("CHANNEL_NOT_FOUND"));
    // sp 模式切换：远程加热中拒绝
    CHECK_EQ(t.cmd->set_sp_mode(holder, 3, "curve").code, std::string("CHANNEL_BUSY"));
    CHECK_EQ(t.cmd->set_sp_mode(holder, 3, "x").code, std::string("PARAM_OUT_OF_RANGE"));
}

TEST(cmd_guards, program_owned_and_stop_heat_aborts_program) {
    CRig t;
    auto put = t.cmd->replace_program(Source::local(), 1, t1_steps(), std::string("加氢筛选"));
    CHECK(put.ok);
    auto run = t.cmd->run_program(Source::local(), 1);
    CHECK(run.ok && run.accepted);
    t.r.advance(500);
    CHECK(t.r.pe().is_owned(1));
    auto owned = t.cmd->start_heat(Source::local(), 1, 60);
    CHECK_EQ(owned.code, std::string("CHANNEL_PROGRAM_OWNED"));
    CHECK_EQ(owned.message, std::string("1 号 正在执行程序 · 请先中止程序"));
    CHECK_EQ(owned.level, std::string("warn"));
    CHECK_EQ(t.cmd->reset_gas_counter(Source::local(), 1).code, std::string("CHANNEL_PROGRAM_OWNED"));
    // 停加热 = 中止程序（停止类永不拒绝）
    auto st = t.wait([&](Done d) { t.cmd->stop_heat(Source::local(), 1, d); });
    CHECK(st.ok);
    CHECK_EQ(st.message, std::string("1 号 停止加热 · 正在执行的程序已中止"));
    CHECK(st.data["programAborted"].get<bool>());
}

TEST(cmd_guards, stop_all_and_stop_channel_results) {
    CRig t;
    auto r = t.wait([&](Done d) { t.cmd->stop_all(Source::local(), false, d); });
    CHECK(r.ok);
    CHECK_EQ(r.message, std::string("已停止全部通道（含加热输出）· 阀门全关"));
    CHECK(r.data.contains("channels") && r.data["channels"].size() == 8);
    CHECK(!r.data["stirStopped"].get<bool>());
    auto c = t.wait([&](Done d) { t.cmd->stop_channel(Source::remote("tok", "tok"), 4, true, d); });
    CHECK(c.ok);
    CHECK_EQ(c.message, std::string("4 号 已停用，不参与运行"));
    CHECK(t.r.core->channel(4).state == core::ReactorState::Idle);
    // /estop 兼容：= 全部停止含搅拌，立即 accepted
    auto e = t.cmd->estop(Source::remote("tok", "tok"), "测试");
    CHECK(e.ok && e.accepted);
}

TEST(cmd_guards, run_and_preflight) {
    CRig t;
    for (int i = 1; i <= 8; ++i) t.r.core->channel_mut(i).set_state(core::ReactorState::Idle);
    auto pre = t.cmd->compute_pre_run();
    CHECK(!pre.can_run);
    bool has_err = false;
    for (const auto& c : pre.checks) has_err = has_err || (c.title == "运行通道" && c.kind == "err");
    CHECK(has_err);
    auto r = t.cmd->run(Source::local(), false);
    CHECK_EQ(r.code, std::string("PREFLIGHT_FAILED"));
    t.r.core->channel_mut(2).set_state(core::ReactorState::Ready);
    t.r.core->channel_mut(2).tsp = 60;
    auto ok = t.cmd->run(Source::local(), false);
    CHECK(ok.ok && ok.accepted);
    CHECK_EQ(ok.data["requested"].size(), static_cast<size_t>(1));
    CHECK(t.r.core->channel(2).state == core::ReactorState::Heating);
    auto pf = t.cmd->preflight(Source::remote("v", "v"));
    CHECK(pf.contains("canRun") && pf.contains("checks") && pf.contains("channels") && pf.contains("summary"));
}

TEST(cmd_guards, copy_config_strict_skips_running) {
    CRig t;
    Source holder = t.take_remote();
    t.r.core->channel_mut(1).tsp = 77;
    // 严格守卫先逐项校验源通道（体积 0 不在 1..VolMax → PARAM_OUT_OF_RANGE）
    CHECK_EQ(t.cmd->copy_channel_config(holder, 1, std::vector<int>{2}).code, std::string("PARAM_OUT_OF_RANGE"));
    t.r.core->channel_mut(1).vol = 3;
    t.r.core->channel_mut(3).set_state(core::ReactorState::Heating);
    auto r = t.cmd->copy_channel_config(holder, 1, std::vector<int>{2, 3, 2, 1});
    CHECK(r.ok);
    CHECK_EQ(r.data["channels"].size(), static_cast<size_t>(1));
    CHECK_EQ(r.warnings.size(), static_cast<size_t>(1));
    if (!r.warnings.empty()) CHECK_EQ(r.warnings[0], std::string("3 号 运行中 · 已跳过"));
    CHECK_EQ(t.r.core->channel(2).tsp, 77.0);
    CHECK_EQ(t.cmd->copy_channel_config(holder, 1, std::vector<int>{12}).code, std::string("CHANNEL_NOT_FOUND"));
}

TEST(cmd_guards, sessions_owner_and_markers) {
    CRig t(true);
    Source a = Source::remote("tokA", "PC-1"), b = Source::remote("tokB", "PC-2");
    auto s = t.cmd->begin_session(a, "T-001", std::string("备注"));
    CHECK(s.ok);
    std::string id = s.data["id"].get<std::string>();
    CHECK(id.rfind("T-001-", 0) == 0);
    // 远程严格：已有会话 → SESSION_ACTIVE；别人不能结束 → SESSION_NOT_OWNER
    CHECK_EQ(t.cmd->begin_session(b, "T-002", std::nullopt).code, std::string("SESSION_ACTIVE"));
    CHECK_EQ(t.cmd->end_session(b).code, std::string("SESSION_NOT_OWNER"));
    auto m = t.cmd->add_marker(b, "加料");
    CHECK(m.ok);
    CHECK_EQ(m.data["id"].get<int>(), 1);
    CHECK_EQ(t.cmd->add_marker(b, "   ").code, std::string("BAD_REQUEST"));
    auto e = t.cmd->end_session(a);
    CHECK(e.ok);
    // 没有会话：结束 = silent，远程打标记 = NOT_FOUND
    CHECK(t.cmd->end_session(a).silent);
    CHECK_EQ(t.cmd->add_marker(a, "x").code, std::string("NOT_FOUND"));
    CHECK_EQ(t.cmd->begin_session(Source::local(), "  ", std::nullopt).message, std::string("测试编号不能为空"));
}

TEST(cmd_guards, settings_patch_band_bounds_language_and_ack_all) {
    CRig t;
    auto st0 = t.r.core->settings().revision;
    OpResult r = t.wait([&](Done d) {
        t.cmd->patch_settings(Source::local(), {{store::SettingKey::TMaxSp, 180}}, std::nullopt, nullptr, st0, d);
    });
    CHECK(r.ok);
    CHECK_EQ(r.data["revision"].get<int64_t>(), st0 + 1);
    CHECK_EQ(t.r.core->settings().settings.t_max_sp, 180.0);
    // 旧 revision → 冲突
    OpResult c = t.wait([&](Done d) { t.cmd->patch_settings(Source::local(), {{store::SettingKey::TMaxSp, 170}}, std::nullopt, nullptr, st0, d); });
    CHECK_EQ(c.code, std::string("REVISION_CONFLICT"));
    // 越界
    OpResult o = t.wait([&](Done d) { t.cmd->patch_settings(Source::local(), {{store::SettingKey::TMaxSp, 9999}}, std::nullopt, nullptr, std::nullopt, d); });
    CHECK_EQ(o.code, std::string("PARAM_OUT_OF_RANGE"));
    auto bb = t.cmd->set_band_bounds(Source::local(), 90, 190, std::nullopt);
    CHECK(bb.ok);
    CHECK_EQ(t.r.core->settings().band_bounds[0], 90.0);
    CHECK_EQ(t.cmd->set_band_bounds(Source::local(), 300, 190, std::nullopt).code, std::string("PARAM_OUT_OF_RANGE"));
    CHECK(t.cmd->set_language(Source::local(), "en").ok);
    CHECK_EQ(t.r.core->settings().settings.language, std::string("en"));
    CHECK(t.cmd->set_language(Source::local(), "zh").ok);
    CHECK_EQ(t.r.core->settings().settings.language, std::string("zh-CN"));
    CHECK_EQ(wire_language("zh-CN"), std::string("zh"));
    CHECK_EQ(t.cmd->set_language(Source::local(), "xx").code, std::string("PARAM_OUT_OF_RANGE"));
    t.r.core->alarms().log("warn", "系统", "测试报警", "x", "test");
    auto ack = t.cmd->ack_all_alarms(Source::remote("v", "v"));
    CHECK(ack.ok);
    CHECK(ack.data["acked"].get<int>() >= 1);
    CHECK_EQ(t.cmd->ack_alarm(Source::local(), "nope").code, std::string("NOT_FOUND"));
}

TEST(cmd_guards, manual_valves_local_gate_and_remote) {
    CRig t;
    Source holder = t.take_remote();
    // 远程直接开关（v0.7，不经手动阀控开关）
    auto r = t.wait([&](Done d) { t.cmd->set_reactor_valve(holder, 2, true, d); });
    CHECK(r.ok);
    CHECK_EQ(r.message, std::string("SV2 已开"));
    CHECK(t.r.core->valves().sv_open(2));
    auto c = t.wait([&](Done d) { t.cmd->set_reactor_valve(Source::local(), 2, false, d); });
    CHECK_EQ(c.code, std::string("CHANNEL_STATE_INVALID"));   // 触屏关阀同样要先开「手动阀控」（C# LocalManualGate 不分方向）
    auto c2 = t.wait([&](Done d) { t.cmd->set_reactor_valve(holder, 2, false, d); });
    CHECK(c2.ok);
    t.lease.take_back(t.mono);
    // 本地开阀：没开「手动阀控」→ CHANNEL_STATE_INVALID（warn）
    auto g = t.wait([&](Done d) { t.cmd->set_gas_inlet(Source::local(), 0, true, d); });
    CHECK(!g.ok);
    CHECK_EQ(g.code, std::string("CHANNEL_STATE_INVALID"));
    CHECK(t.cmd->set_manual_valve_mode(Source::local(), true).ok);
    auto g2 = t.wait([&](Done d) { t.cmd->set_gas_inlet(Source::local(), 0, true, d); });
    CHECK(g2.ok);
    CHECK_EQ(g2.message, std::string("惰性气体 进气阀 已开"));
    // 气源开着再开排空：不安全组合拒绝
    auto v = t.wait([&](Done d) { t.cmd->set_vent(Source::local(), true, d); });
    CHECK(!v.ok);
}
