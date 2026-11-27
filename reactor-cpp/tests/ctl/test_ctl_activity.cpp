// 泄漏测试 / 容积标定 / 自整定 / 内置模板（第二阶段）。
#include <sys/stat.h>

#include "core/program/program_json.hpp"
#include "ctl_phase2_util.hpp"
#include "store/program_library.hpp"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <optional>
#include <string>
#include <thread>

using namespace ctltest;
using prog::ProgramRunPhase;
using prog::StepType;

namespace {
bool file_exists(const std::string& p) {
    struct stat st;
    return ::stat(p.c_str(), &st) == 0;
}
void done_channels(PRig& r, std::initializer_list<int> ids) {
    for (int id : ids) r.c().channel_mut(id).set_state(core::ReactorState::Done);
}
}  // namespace

// ============================ 泄漏测试 ============================

TEST(ctl_leak, pass_and_fail_with_result_files) {
    PRig r(fast_settings(), /*with_logger=*/true);
    r.phy.supply[0] = 55;          // 惰性气体减压阀略高于测试压力
    r.phy.leak_psi_h[1] = 20;      // RV2 漏
    done_channels(r, {1, 2, 3});
    auto res = r.au->leak().start({1, 2, 3}, std::nullopt, "触屏");
    if (!res.ok) std::printf("    拒绝：%s\n", res.message.c_str());
    CHECK(res.accepted);
    std::string run_id = res.data["runId"].get<std::string>();
    CHECK(run_id.rfind("leak-", 0) == 0);
    CHECK(r.c().gas_lease().owner() == core::GasOwner::Leak);
    // 泄漏测试进行中：程序含气路步骤 → 下发被拒（GAS_BUSY）
    r.pe().set_draft(4, steps({{StepType::Press, {{"gas", std::string("A")}, {"pres", 60.0}}}, {StepType::End, {}}}), "x");
    auto pr = r.pe().run(4);
    CHECK(!pr.ok);
    CHECK_EQ(pr.code, std::string("GAS_BUSY"));
    // 重复开始：LEAK_TEST_RUNNING
    CHECK_EQ(r.au->leak().start({1}, std::nullopt).code, std::string("LEAK_TEST_RUNNING"));
    r.advance(30000);
    auto snap = r.au->leak_test();
    CHECK(snap["running"].get<bool>());
    CHECK(snap["stage"].is_string());
    CHECK(snap["totalLeftSec"].is_number());
    CHECK(r.until([&] { return !r.au->leak().running(); }, 15 * 60 * 1000, 1000));
    snap = r.au->leak_test();
    CHECK(snap["done"].get<bool>());
    CHECK(snap["failed"].get<bool>());
    CHECK_EQ(snap["stage"].get<std::string>(), std::string("done"));
    CHECK_EQ(snap["channels"][0]["status"].get<std::string>(), std::string("pass"));
    CHECK_EQ(snap["channels"][1]["status"].get<std::string>(), std::string("fail"));
    CHECK_EQ(snap["channels"][2]["status"].get<std::string>(), std::string("pass"));
    CHECK_EQ(snap["channels"][3]["status"].get<std::string>(), std::string("untested"));
    CHECK(snap["channels"][1]["ratePsiH"].get<double>() > 10);
    CHECK(snap["channels"][0]["ratePsiH"].get<double>() < 1);
    CHECK(snap["vented"].get<bool>());
    CHECK(r.has_title("泄漏测试不通过"));
    CHECK(r.c().settings().settings.last_leak_test_utc_ms.has_value());
    CHECK(!r.c().gas_lease().busy());
    auto v = r.valves();
    CHECK(std::none_of(v.begin(), v.end(), [](bool b) { return b; }));
    r.logger->sync();
    CHECK(file_exists(r.dir + "/leak/" + run_id + ".csv"));
    CHECK(file_exists(r.dir + "/leak/" + run_id + "-summary.csv"));
    CHECK(file_exists(r.dir + "/leak/" + run_id + "-summary.json"));
}

TEST(ctl_leak, abort_and_guards) {
    PRig r(fast_settings());
    r.phy.supply[0] = 55;
    CHECK_EQ(r.au->leak().start({}, std::nullopt).code, std::string("BAD_REQUEST"));
    CHECK_EQ(r.au->leak().start({1}, std::nullopt).code, std::string("CHANNEL_BUSY"));  // 已启用 = 运行中（C# IsRunning）
    done_channels(r, {1});
    r.c().update_settings([](store::SettingsState& s) { s.settings.over_pressure = 100; });
    CHECK_EQ(r.au->leak().start({1}, 95.0).code, std::string("PARAM_CROSS_CHECK"));    // ≥ 超压报警值 × 90%
    r.c().update_settings([](store::SettingsState& s) { s.settings.over_pressure = 510; });
    CHECK(r.au->leak().abort().silent);
    CHECK(r.au->leak().start({1}, std::nullopt).accepted);
    r.advance(8000);
    auto ab = r.au->leak().abort();
    CHECK(ab.ok && ab.quiet);
    r.advance(3000);
    CHECK(!r.au->leak().running());
    auto snap = r.au->leak_test();
    CHECK(snap["aborted"].get<bool>());
    CHECK(r.has_title("泄漏测试中止"));
    CHECK(!r.c().gas_lease().busy());
    // 全部停止也中止泄漏测试
    CHECK(r.au->leak().start({1}, std::nullopt).accepted);
    r.advance(5000);
    r.c().request_stop_all({});
    r.advance(3000);
    CHECK(!r.au->leak().running());
    CHECK(r.au->leak_test()["aborted"].get<bool>());
}

// ============================ 容积标定 ============================

TEST(ctl_calib, one_run_with_known_manifold) {
    auto st = fast_settings();
    st.settings.manifold_volume = 2.0;
    PRig r(st);
    r.phy.supply[0] = 80;
    r.phy.vm = 2.0;
    r.phy.vg[0] = 8.0;
    done_channels(r, {1});
    CHECK_EQ(r.au->volume_calib().start(1, true, 5).code, std::string("BAD_REQUEST"));  // 还没做空釜一遍
    auto res = r.au->volume_calib().start(1, false, 5);
    CHECK(res.accepted);
    CHECK(r.c().gas_lease().owner() == core::GasOwner::Calib);
    CHECK(r.until([&] { return !r.au->volume_calib().running(); }, 10 * 60 * 1000, 1000));
    auto snap = r.au->calib();
    CHECK(snap.is_object());
    if (!snap.is_object()) return;
    CHECK_EQ(snap["stage"].get<std::string>(), std::string("done"));
    CHECK(snap["done"].get<bool>());
    auto v = r.c().settings().settings.effective_volume[0];
    CHECK(v.has_value());
    if (v) CHECK_NEAR(*v, 8.0, 0.3);
    CHECK(r.c().settings().settings.volume_calib_utc_ms[0].has_value());
    CHECK(r.has_title("1 号 容积标定完成"));
    CHECK(!r.c().gas_lease().busy());
}

TEST(ctl_calib, two_runs_wait_for_water_then_fit) {
    PRig r(fast_settings());
    r.phy.supply[0] = 80;
    r.phy.vm = 2.0;
    r.phy.vg[0] = 8.0;
    done_channels(r, {1});
    CHECK(!r.c().settings().settings.manifold_volume.has_value());
    CHECK(r.au->volume_calib().start(1, false, 5).accepted);
    CHECK(r.until([&] { return !r.au->volume_calib().running(); }, 10 * 60 * 1000, 1000));
    CHECK(r.au->volume_calib().awaiting_water());
    auto snap = r.au->calib();
    CHECK_EQ(snap["stage"].get<std::string>(), std::string("waitWater"));
    CHECK(snap["waitingOperator"].get<bool>());
    CHECK_NEAR(snap["waterMl"].get<double>(), 5.0, 1e-9);
    // 操作员泄压、加入 5 mL 水：气相体积 8 → 3
    r.phy.p[0] = 0;
    r.phy.pm = 0;
    r.phy.vg[0] = 3.0;
    CHECK(r.au->volume_calib().start(1, true, 5).accepted);
    CHECK(r.until([&] { return !r.au->volume_calib().running(); }, 10 * 60 * 1000, 1000));
    snap = r.au->calib();
    CHECK_EQ(snap["stage"].get<std::string>(), std::string("done"));
    auto m = r.c().settings().settings.manifold_volume;
    auto v = r.c().settings().settings.effective_volume[0];
    CHECK(m.has_value() && v.has_value());
    if (m) CHECK_NEAR(*m, 2.0, 0.15);
    if (v) CHECK_NEAR(*v, 8.0, 0.4);
    CHECK(!r.au->volume_calib().awaiting_water());
}

// ============================ 自整定 ============================

TEST(ctl_autotune, completion_saves_pid_into_starting_band) {
    PRig r(fast_settings());
    int64_t rev0 = r.c().settings().revision;
    std::optional<ctl::OpResult> got;
    // 显式档位与目标温度不符：PARAM_OUT_OF_RANGE
    r.au->tuner().start(1, 60, 1, false, [&](const ctl::OpResult& x) { got = x; });
    r.c().settle();
    CHECK(got && !got->ok && got->code == "PARAM_OUT_OF_RANGE");
    got.reset();
    r.au->tuner().start(1, 60, std::nullopt, false, [&](const ctl::OpResult& x) { got = x; });
    r.c().settle();
    CHECK(got.has_value());
    if (!got) return;
    if (!got->ok) std::printf("    %s\n", got->message.c_str());
    CHECK(got->ok);
    CHECK_EQ(got->message, std::string("1 号 已设目标 60℃ 并启动自整定"));
    CHECK_EQ(r.au->tuner().tune_band(1), 0);
    CHECK(r.c().temp().ch(1).autotuning);
    // 模拟仪表：At=1 持续若干周期后回 0 并写一组 PID → 轮询看到整定结束 → 回读存档
    CHECK(r.until([&] { return r.c().settings().band_tune[0][0].has_value(); }, 60000, 1200));
    const auto& meta = r.c().settings().band_tune[0][0];
    CHECK(meta && meta->tuned_sp_c && std::fabs(*meta->tuned_sp_c - 60) < 0.2);
    CHECK(r.c().settings().revision > rev0);
    auto snap = r.au->autotune();
    CHECK(snap.is_object());
    if (snap.is_object()) {
        CHECK_EQ(snap["channels"][0]["state"].get<std::string>(), std::string("done"));
        CHECK(snap["channels"][0]["result"].is_object());
    }
    CHECK(r.has_title("自整定完成"));
}

TEST(ctl_autotune, cancel_does_not_store) {
    PRig r(fast_settings());
    std::optional<ctl::OpResult> got;
    r.au->tuner().start(2, 70, std::nullopt, false, [&](const ctl::OpResult& x) { got = x; });
    r.c().settle();
    CHECK(got && got->ok);
    got.reset();
    r.au->tuner().cancel(2, [&](const ctl::OpResult& x) { got = x; });
    r.c().settle();
    CHECK(got && got->ok);
    CHECK_EQ(got->message, std::string("2 号 已取消自整定并停止输出"));
    r.advance(1200 * 12);
    CHECK(!r.c().settings().band_tune[1][0].has_value());
    CHECK(!r.c().temp().ch(2).autotuning);
    auto snap = r.au->autotune();
    CHECK(snap.is_object() && snap["channels"][0]["state"].get<std::string>() == "aborted");
}

// ============================ 内置模板 ============================

TEST(ctl_templates, builtin_seeded_read_only_and_runnable) {
    char tmpl[] = "/tmp/reactor-ctl2-lib-XXXXXX";
    std::string dir = mkdtemp(tmpl);
    {
        store::ProgramLibrary lib(dir);
        auto s1 = ctl::builtin_templates::seed(lib);
        CHECK_EQ(s1.added, 4);
        auto list = lib.list(store::LibKind::Template);
        CHECK_EQ(list.size(), 4u);
        auto t1 = lib.get(store::LibKind::Template, "T1");
        CHECK(t1.has_value());
        if (!t1) return;
        CHECK(t1->body["builtIn"].get<bool>());
        CHECK_EQ(t1->body["program"]["name"].get<std::string>(), std::string("加氢筛选"));
        CHECK_EQ(t1->body["program"]["steps"].size(), 9u);
        // 参数顺序 = C# PcProgram.WriteSteps（先选项、再数值）+ StepDefaults.Fill 补缺（MPa 2 位、压降 4 位）
        CHECK_EQ(t1->body["program"]["steps"][0]["params"].dump(),
                 std::string(R"({"after":"vent","onFail":"stop","pres":3,"hold":30,"settle":5,"maxDrop":0.0138})"));
        // 只读：API 改 / 删都拒
        CHECK(!lib.update(store::LibKind::Template, "T1", t1->body, t1->revision).ok);
        CHECK(!lib.remove(store::LibKind::Template, "T1", t1->revision).ok);
        // 再种一次：不变
        auto s2 = ctl::builtin_templates::seed(lib);
        CHECK_EQ(s2.added, 0);
        CHECK_EQ(s2.unchanged, 4);
        // 每个模板换成 §6.9（psi）都能通过 PUT 解析与全局限值
        for (const auto& e : list) {
            auto wire = ctl::builtin_templates::pc_steps_to_wire(e.body["program"]["steps"]);
            auto parsed = core::program::program_json::parse(wire);
            if (parsed.error) std::printf("    %s：%s\n", e.id.c_str(), parsed.error->message.c_str());
            CHECK(!parsed.error.has_value());
        }
        // 重启后再读：仍是 4 个内置模板
    }
    {
        store::ProgramLibrary lib2(dir);
        CHECK_EQ(lib2.list(store::LibKind::Template).size(), 4u);
        CHECK_EQ(ctl::builtin_templates::seed(lib2).unchanged, 4);
    }
    // 模板载入草稿并下发
    {
        PRig r(fast_settings());
        store::ProgramLibrary lib3(dir);
        auto t2 = lib3.get(store::LibKind::Template, "T2");
        CHECK(t2.has_value());
        if (t2) {
            auto res = r.pe().replace_program(1, ctl::builtin_templates::pc_steps_to_wire(t2->body["program"]["steps"]), std::string("只做气密"));
            CHECK(res.ok);
            CHECK_EQ(r.pe().draft(1).size(), 2u);
            CHECK(r.pe().check_run(1).silent);
        }
    }
    if (std::system(("rm -rf '" + dir + "'").c_str()) != 0) {}
}

// ============================ 任意线程包装（起控制线程） ============================

TEST(ctl_automation, threaded_async_wrappers) {
    char tmpl[] = "/tmp/reactor-ctl2-thr-XXXXXX";
    std::string dir = mkdtemp(tmpl);
    ctl::SystemClock clock;
    auto hw = hw::make_hardware(hw::HardwareConfig::for_sim());
    store::ProgramLibrary lib(dir);
    ctl::CoreDeps d;
    d.hw = hw.get();
    d.clock = &clock;
    ctl::CoreOptions opt;
    opt.watchdog = false;
    opt.tick_ms = 200;
    ctl::ControlCore core(std::move(d), opt);
    ctl::AutomationDeps ad;
    ad.core = &core;
    ad.library = &lib;
    auto au = std::make_unique<ctl::Automation>(ad);
    CHECK_EQ(au->seed_result().added, 4);
    core.start();
    core.call([&] { core.channel_mut(1).set_state(core::ReactorState::Ready); }).get();
    nlohmann::json js = nlohmann::json::parse(R"([{"type":"stir","params":{"rpm":300}},{"type":"stir","params":{"rpm":0}},{"type":"end","params":{}}])");
    auto put = au->replace_program_async(1, js, std::string("搅拌一下")).get();
    CHECK(put.ok);
    auto draft = au->draft_json_async(1).get();
    CHECK_EQ(draft["steps"].size(), 3u);
    CHECK(au->check_run_async(1).get().silent);
    auto run = au->run_program_async(1, "PC-2").get();
    CHECK(run.accepted);
    bool done = false;
    for (int i = 0; i < 100 && !done; ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        auto s = core.snapshot();
        done = s && s->channels[0].program.is_object() && s->channels[0].program["state"] == "completed";
    }
    CHECK(done);
    auto s = core.snapshot();
    CHECK(s && s->program_runs[0].is_object() && s->program_runs[0]["startedBy"] == "PC-2");
    CHECK(au->abort_program_async(1).get().silent);
    // 命令后会立刻补发快照，程序可能在第一次读到压力之前就显示完成：泄漏测试要压力在线，先等一下
    for (int i = 0; i < 40; ++i) {
        auto cs = core.snapshot();
        if (cs && cs->comm.pressure) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    }
    auto lt = au->start_leak_test_async({1}, std::nullopt, "PC-2").get();
    CHECK(lt.accepted);  // 程序跑完 RV1 已结束，可以测
    auto ab = au->abort_leak_test_async(false).get();
    CHECK(ab.ok && !ab.silent);
    for (int i = 0; i < 40 && core.call([&] { return au->leak().running(); }).get(); ++i)
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
    CHECK(!core.call([&] { return au->leak().running(); }).get());
    auto at = au->start_autotune_async(9, 60, std::nullopt, false).get();
    CHECK_EQ(at.code, std::string("CHANNEL_NOT_FOUND"));
    core.shutdown(false);
    au.reset();
    if (std::system(("rm -rf '" + dir + "'").c_str()) != 0) {}
}
