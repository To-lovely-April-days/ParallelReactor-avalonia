// reactor-hmi：LVGL 触屏界面入口。
//
// 显示后端在编译时选：
//   REACTOR_USE_SDL=1   SDL 窗口（开发机；板子上 X11 下也能用，REACTOR_FULLSCREEN=1 全屏）
//   REACTOR_USE_X11=1   LVGL 自带的 X11 驱动
//   REACTOR_HEADLESS=1  不开窗口，渲染一帧写成 BMP（--shot 路径），用来在没有屏幕的机器上看效果
//
// 参数：
//   --width 1280 --height 800
//   --fonts DIR          字体目录（Inter-*.ttf、NotoSansSC-*.otf）；不给就按 程序目录/fonts → /opt/reactor/fonts → 工程 assets/fonts 找
//   --lang zh|en         界面语言（默认中文）
//   --page NAME          打开哪一页（见 page_names()）；无屏预览时就是截哪一页
//   --shot out.bmp       （无屏）写出的图片
//   --shots DIR          （无屏）把所有页面各截一张，写到 DIR/<页面名>.bmp
//   --server ADDR        后台地址：unix:/run/reactord/hmi.sock（默认，也可用环境变量 REACTOR_HMI_SERVER）
//                        或 tcp://127.0.0.1:8080（开发机连 PC 入口，要配 --token）
//   --token T            TCP 入口的 Bearer 令牌（也可用环境变量 REACTOR_TOKEN）；Unix 入口不用
//   --sim                不连后台，用模拟数据源（预览截图用这个：make -C tools/preview shots 会带上）
//   --wait-ms N          （无屏 + 连后台）截图前最多等多久连上、取完数据，默认 3000
//   --fake-serve PATH [秒] 开发用假后台（罐头数据，Unix socket），另开一个 --server unix:PATH 的界面连它
//   --selftest-net       网络层自检（内置假服务器：HTTP / chunked / WS 分片 / ping / 断线重连 / 重订阅 / 动作 → 端点），然后退出
//
// 数据：默认由 net/ 连 reactord（网络线程收到推送 / REST 结果后拼成 Model，经 ModelMailbox 交给这里的
// LVGL 主循环；命令结果经 UiQueue 回到这里弹 toast）。--sim 时用模拟数据源（model/sim_source）。
#include "lvgl.h"
#include "model/actions.hpp"
#include "model/model.hpp"
#include "model/sim_source.hpp"
#include "net/backend.hpp"
#include "net/selftest.hpp"
#include "ui/app.hpp"
#include "ui/pages/common2.hpp"
#include "ui/kit/i18n.hpp"
#include "ui/kit/theme.hpp"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <cstring>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <vector>

using namespace reactor::hmi;

namespace {

uint32_t tick_ms() {
    using namespace std::chrono;
    static const auto t0 = steady_clock::now();
    return static_cast<uint32_t>(duration_cast<milliseconds>(steady_clock::now() - t0).count());
}

#if defined(REACTOR_HEADLESS) && REACTOR_HEADLESS
std::vector<uint32_t> g_fb;
int g_w = 0, g_h = 0;

void flush_cb(lv_display_t* disp, const lv_area_t* a, uint8_t* px) {
    const uint32_t* src = reinterpret_cast<const uint32_t*>(px);
    int w = lv_area_get_width(a);
    for (int y = a->y1; y <= a->y2; ++y)
        std::memcpy(&g_fb[static_cast<size_t>(y) * g_w + a->x1], src + static_cast<size_t>(y - a->y1) * w, w * 4);
    lv_display_flush_ready(disp);
}

bool write_bmp(const std::string& path) {
    FILE* fp = std::fopen(path.c_str(), "wb");
    if (!fp) return false;
    uint32_t img = static_cast<uint32_t>(g_w * g_h * 4);
    uint8_t h[54] = {'B', 'M'};
    auto put32 = [&](int off, uint32_t v) { for (int i = 0; i < 4; ++i) h[off + i] = (v >> (8 * i)) & 0xFF; };
    put32(2, 54 + img); put32(10, 54); put32(14, 40); put32(18, g_w); put32(22, static_cast<uint32_t>(-g_h));
    h[26] = 1; h[28] = 32; put32(34, img);
    std::fwrite(h, 1, 54, fp);
    std::fwrite(g_fb.data(), 1, img, fp);  // LVGL 32 位色在内存里就是 BGRA，和 BMP 一致
    std::fclose(fp);
    return true;
}
#endif

const std::vector<std::string>& page_names() {
    static const std::vector<std::string> v = {
        "main-f1", "main-f2", "plan-all", "plan-one", "chan-quick", "chan-quick-run", "chan-quick-kbd", "chan-full",
        "chan-full-curve", "chan-full-run", "alarm-page", "alarm-l1", "alarm-l2", "alarm-l3", "lang-pop", "kbd-num",
        "kbd-text", "stop-all", "abort-ask",
        // 第二批
        "prog-1", "prog-addstep", "prog-curve8", "prog-curve16", "prog-param-leak", "prog-param-exchange", "prog-param-stir",
        "prog-param-heat", "prog-param-press", "prog-param-hold", "prog-param-react", "prog-param-vent", "prog-param-end",
        "lib-mine", "lib-tpl", "prog-run", "prog-abort-ask", "prog-done", "prog-aborted",
        "leak-setup", "leak-run", "leak-result", "leak-pass",
        "curve-gas", "curve-temp", "curve-press", "curve-three",
        "rec-list", "rec-abort", "rec-export", "rec-files", "rec-range-export",
        "set-general", "set-params", "set-pid", "set-calib", "set-channels", "set-net", "set-net-pw", "set-comm", "set-data",
        "set-user", "set-about", "set-quit",
        // 联调补的
        "ctl-request"};
    return v;
}

struct Options {
    bool sim = false;
    std::string server, token;
    int wait_ms = 3000;
};

// 一套界面：数据源（模拟 / 后台）+ 动作 + 屏幕。成员顺序有讲究：信箱和回调队列要比 net 活得久。
struct Session {
    model::ModelMailbox mailbox;
    net::UiQueue ui;
    std::unique_ptr<model::SimSource> sim;
    std::unique_ptr<model::LoggingActions> log;
    std::unique_ptr<net::NetBackend> net;
    model::Actions* act = nullptr;
    std::unique_ptr<App> app;
    uint32_t t0 = 0;
    ~Session() {
        app.reset();
        if (net) net->stop();
    }
};

void pump(Session& s) {
    if (s.sim) {
        s.sim->step(static_cast<int64_t>(tick_ms() - s.t0));
        s.mailbox.post(s.sim->snapshot());
    }
    s.ui.drain();  // 网络线程投递回来的命令结果（toast）
    if (auto m = s.mailbox.take()) s.app->apply(std::move(m));
}

// 连后台时：模拟源的「注入某种状态」用不了，按后台当前的状态挑一个合适的通道 / 运行打开对应的页面。
// 只改界面自己的状态（选中的通道、编辑副本里选中的步骤、弹层），不向后台发任何命令。
// 后台没有对应状态（比如没有跑完的程序）时退回最接近的页面，并在 stderr 说明。
void setup_page_net(Session& s, const std::string& name) {
    App& app = *s.app;
    const model::Model& m = app.m();
    auto note = [&](const char* why) { std::fprintf(stderr, "页面 %s：%s\n", name.c_str(), why); };
    auto installed = [&](int ch) { return m.ch(ch).installed; };
    auto running = [&](int ch) { return m.ch(ch).program && m.ch(ch).program->running; };
    auto heating = [&](int ch) {
        const std::string& st = m.ch(ch).state;
        return st == "heating" || st == "wait" || st == "react" || st == "pressing" || st == "stir";
    };
    // 第一路满足条件的通道；没有返回 0
    auto first = [&](const std::function<bool(int)>& f) {
        for (int ch = 1; ch <= 8; ++ch)
            if (f(ch)) return ch;
        return 0;
    };
    auto or_ = [](int a, int b) { return a ? a : b; };
    int run_ch = first(running);
    int idle_ch = or_(first([&](int ch) { return installed(ch) && !running(ch) && !heating(ch) && !last_result(m, ch); }),
                      or_(first(installed), 1));
    int heat_ch = first([&](int ch) { return installed(ch) && !running(ch) && heating(ch); });
    auto with_result = [&](const char* state) {
        return first([&](int ch) { return m.ch(ch).program && m.ch(ch).program->state == state && last_result(m, ch); });
    };
    auto prog = [&](int sub, int ch) { app.go(PageId::Program, sub * 100 + ch); };
    auto show_tier = [&](int tier) {
        app.go(PageId::Home, 0);
        for (auto& a : m.alarms)
            if (a.dto.state == "active" && a.ext.tier == tier) { app.show_alarm(a); return; }
        for (auto& a : m.alarms)
            if (a.ext.tier == tier) { app.show_alarm(a); return; }
        note("后台没有这一级的报警，只显示主页");
    };
    static const std::vector<std::pair<std::string, std::pair<PageId, int>>> T = {
        {"main-f1", {PageId::Home, 0}}, {"main-f2", {PageId::Home, 1}}, {"plan-all", {PageId::Home, 2}},
        {"alarm-page", {PageId::Alarm, 0}}, {"curve-gas", {PageId::Curve, 0}}, {"curve-temp", {PageId::Curve, 1}},
        {"curve-press", {PageId::Curve, 2}}, {"curve-three", {PageId::Curve, 3}}, {"rec-files", {PageId::Records, 1}},
        {"set-general", {PageId::Settings, 0}}, {"set-params", {PageId::Settings, 1}}, {"set-pid", {PageId::Settings, 2}},
        {"set-calib", {PageId::Settings, 3}}, {"set-channels", {PageId::Settings, 4}}, {"set-net", {PageId::Settings, 5}},
        {"set-comm", {PageId::Settings, 6}}, {"set-data", {PageId::Settings, 7}}, {"set-user", {PageId::Settings, 8}},
        {"set-about", {PageId::Settings, 9}}};
    for (auto& e : T)
        if (e.first == name) { app.go(e.second.first, e.second.second); return; }

    if (name == "plan-one") app.go(PageId::Home, 10 + or_(run_ch, idle_ch));
    else if (name == "chan-quick") { app.go(PageId::Home, 0); app.open_quick(idle_ch); }
    else if (name == "chan-quick-run") {
        if (!heat_ch && !run_ch) note("后台没有在加热的通道");
        app.go(PageId::Home, 0);
        app.open_quick(or_(heat_ch, or_(run_ch, idle_ch)));
    } else if (name == "chan-quick-kbd") {
        app.go(PageId::Home, 0);
        app.open_quick(idle_ch);
        const auto& c = m.ch(idle_ch);
        kit::NumSpec sp;
        sp.title = trf("%d 号 · 目标温度", idle_ch);
        sp.unit = "°C"; sp.value = model::temp_sp(c); sp.min = 35; sp.max = m.settings.limits.tempSpMaxC;
        if (!std::isnan(sp.value)) sp.last = c.tempSpC;
        sp.current = model::fmt_temp(c) + " °C";
        int ch = idle_ch;
        kit::NumPad::open(sp, [&s, ch](double v) { s.act->set_temp_sp(ch, v); });
    } else if (name == "chan-full") app.go(PageId::ChanFull, or_(first([&](int ch) { return heat_ch == ch && m.ch(ch).spMode != "curve"; }), idle_ch));
    else if (name == "chan-full-curve") {
        int ch = first([&](int k) { return m.ch(k).spMode == "curve" || (m.ch(k).profile && !running(k)); });
        if (!ch) note("后台没有曲线升温的通道");
        app.go(PageId::ChanFull, or_(ch, idle_ch));
    } else if (name == "chan-full-run") {
        if (!run_ch) note("后台没有在跑程序的通道");
        app.go(PageId::ChanFull, or_(run_ch, idle_ch));
    } else if (name == "alarm-l1") show_tier(1);
    else if (name == "alarm-l2") show_tier(2);
    else if (name == "alarm-l3") show_tier(3);
    else if (name == "lang-pop") { app.go(PageId::Home, 0); app.open_lang(); }
    else if (name == "kbd-num") {
        app.go(PageId::Home, 0);
        const auto& c = m.ch(idle_ch);
        kit::NumSpec sp;
        sp.title = trf("%d 号 · 目标温度", idle_ch);
        sp.unit = "°C"; sp.value = model::temp_sp(c); sp.min = 20; sp.max = 200; sp.decimals = 1;
        if (!std::isnan(sp.value)) sp.last = c.tempSpC;
        sp.current = model::fmt_temp(c) + " °C";
        kit::NumPad::open(sp, nullptr);
    } else if (name == "kbd-text") { app.go(PageId::Home, 0); kit::TextKbd::open(tr("给这个程序起个名字"), "", 30, nullptr); }
    else if (name == "stop-all") { app.go(PageId::Home, 0); app.stop_all_confirm(); }
    else if (name == "abort-ask" || name == "prog-abort-ask") {
        if (!run_ch) { note("后台没有在跑程序的通道"); app.go(PageId::Home, 0); return; }
        if (name == "abort-ask") app.go(PageId::Home, 0);
        else prog(0, run_ch);
        app.abort_confirm(run_ch);
    }
    // ---- 程序页 ----
    else if (name == "prog-1") prog(0, idle_ch);
    else if (name == "prog-addstep") { prog(0, idle_ch); prog_open_add_step(app, idle_ch); }
    else if (name == "prog-curve8" || name == "prog-curve16") {
        // 编辑副本里找一路有曲线升温步骤的（8 段以内 / 超过 8 段）
        prog_sync(m);
        bool want16 = name == "prog-curve16";
        int pick = 0, step = 0;
        for (int ch = 1; ch <= 8 && !pick; ++ch) {
            if (running(ch) || !installed(ch)) continue;  // 停用的路程序页不给编辑
            const auto& d = prog_edit().doc[(size_t)ch - 1];
            for (size_t k = 0; k < d.steps.size(); ++k)
                if (d.steps[k].type == "heat" && d.steps[k].code("mode") == "curve" &&
                    (want16 ? d.steps[k].segments.size() > 8 : d.steps[k].segments.size() <= 8)) {
                    pick = ch;
                    step = (int)k;
                    break;
                }
        }
        if (!pick) { note("后台没有这样的曲线升温程序，显示编辑页"); prog(0, idle_ch); return; }
        prog_edit().sel[(size_t)pick - 1] = step;
        prog_edit().seg[(size_t)pick - 1] = 0;
        prog(0, pick);
    } else if (name.rfind("prog-param-", 0) == 0) {
        // 只动界面的编辑副本：这一路没有这种步骤就在副本里插一步（不下发）
        std::string type = name.substr(11);
        prog_sync(m);
        auto& d = prog_edit().doc[(size_t)idle_ch - 1];
        int idx = -1;
        for (size_t k = 0; k < d.steps.size(); ++k)
            if (d.steps[k].type == type && idx < 0) idx = (int)k;
        if (idx < 0) {
            d.steps.insert(d.steps.empty() ? d.steps.end() : d.steps.end() - 1, model::new_step(m.x.schema, type));
            idx = d.steps.size() > 1 ? (int)d.steps.size() - 2 : 0;
        }
        prog_edit().sel[(size_t)idle_ch - 1] = idx;
        prog(0, idle_ch);
    } else if (name == "lib-mine") prog(1, idle_ch);
    else if (name == "lib-tpl") { prog_edit().lib_tpl = true; prog(1, idle_ch); }
    else if (name == "prog-run") {
        if (!run_ch) note("后台没有在跑程序的通道");
        prog(0, or_(run_ch, idle_ch));
    } else if (name == "prog-done" || name == "prog-aborted") {
        int ch = with_result(name == "prog-done" ? "completed" : "aborted");
        if (!ch && name == "prog-aborted") ch = with_result("failed");
        if (!ch) note("后台没有这样结束的程序");
        prog(0, or_(ch, idle_ch));
    } else if (name.rfind("leak-", 0) == 0) prog(2, 0);  // 泄漏测试页按后台的测试状态自己切（选择 / 运行中 / 结果）
    // ---- 记录 ----
    else if (name == "rec-list" || name == "rec-abort" || name == "rec-export") {
        std::string id;
        for (auto& r : m.x.runs)
            if (name == "rec-abort" ? r.result == "abort" : (r.result == "done" || r.result == "warn")) { id = r.id; break; }
        if (id.empty() && !m.x.runs.empty()) id = m.x.runs.front().id;
        if (id.empty()) note("后台还没有运行记录");
        records_select() = id;
        app.go(PageId::Records, 0);
        if (name == "rec-export") records_open_export(app);
    } else if (name == "rec-range-export") { app.go(PageId::Records, 1); records_open_range_export(app); }
    else if (name == "set-net-pw") { app.go(PageId::Settings, 5); settings_open_wifi_password(app, "Lab-2.4G"); }
    else if (name == "set-quit") { app.go(PageId::Settings, 0); settings_confirm_quit(app); }
    else if (name == "ctl-request") {
        app.go(PageId::Home, 0);  // 有待处理的申请时弹窗自己出来（App::update_control_request）
        if (!m.controlPending) note("后台没有待处理的控制申请");
    } else {
        app.go(PageId::Home, 0);
        if (!name.empty() && name != "main-f1") std::fprintf(stderr, "不认识的页面 %s（可用：见 --help）\n", name.c_str());
    }
}

void setup_page(Session& s, const std::string& name) {
    if (!s.sim) { setup_page_net(s, name); return; }
    App& app = *s.app;
    auto refresh = [&] { pump(s); };
    if (name == "main-f1" || name.empty()) app.go(PageId::Home, 0);
    else if (name == "main-f2") app.go(PageId::Home, 1);
    else if (name == "plan-all") app.go(PageId::Home, 2);
    else if (name == "plan-one") app.go(PageId::Home, 13);
    else if (name == "chan-quick") { app.go(PageId::Home, 0); app.open_quick(5); }
    else if (name == "chan-quick-run") { app.go(PageId::Home, 0); app.open_quick(3); }
    else if (name == "chan-quick-kbd") {
        app.go(PageId::Home, 0);
        app.open_quick(5);
        kit::NumSpec sp;
        sp.title = trf("%d 号 · 目标温度", 5);
        sp.unit = "°C"; sp.value = 80; sp.min = 35; sp.max = 200; sp.last = 80; sp.current = "25.0 °C";
        kit::NumPad::open(sp, [&s](double v) { s.act->set_temp_sp(5, v); });
    } else if (name == "chan-full") app.go(PageId::ChanFull, 5);
    else if (name == "chan-full-curve") { s.act->set_heat_mode(5, "curve"); refresh(); app.go(PageId::ChanFull, 5); }
    else if (name == "chan-full-run") app.go(PageId::ChanFull, 3);
    else if (name == "alarm-page") app.go(PageId::Alarm);
    else if (name == "alarm-l1") {
        app.go(PageId::Home, 0);
        app.show_alarm(s.sim->model().alarms.front());
    } else if (name == "alarm-l2") {
        app.go(PageId::Home, 0);
        s.sim->inject_l2();
        refresh();
        app.show_alarm(s.sim->model().alarms.front());
    } else if (name == "alarm-l3") {
        app.go(PageId::Home, 0);
        s.sim->inject_l3();
        refresh();
        app.show_alarm(s.sim->model().alarms.front());
    } else if (name == "lang-pop") { app.go(PageId::Home, 0); app.open_lang(); }
    else if (name == "kbd-num") {
        app.go(PageId::Home, 0);
        kit::NumSpec sp;
        sp.title = tr("5 号 · 第 5 步 升温 · 目标温度");
        sp.unit = "°C"; sp.value = 80; sp.min = 20; sp.max = 200; sp.decimals = 1; sp.last = 80; sp.current = "25.0 °C";
        kit::NumPad::open(sp, nullptr);
    } else if (name == "kbd-text") {
        app.go(PageId::Home, 0);
        kit::TextKbd::open(tr("给这个程序起个名字"), "加氢筛选-", 30, nullptr);
    } else if (name == "stop-all") { app.go(PageId::Home, 0); app.stop_all_confirm(); }
    else if (name == "abort-ask") { app.go(PageId::Home, 0); app.abort_confirm(3); }
    // ---- 第二批 ----
    else if (name == "prog-1") app.go(PageId::Program, 5);
    else if (name == "prog-addstep") { app.go(PageId::Program, 5); prog_open_add_step(app, 5); }
    else if (name == "prog-curve8" || name == "prog-curve16") {
        s.sim->inject_heat_curve(5, name == "prog-curve8" ? 8 : 16);
        refresh();
        prog_edit().seg[4] = name == "prog-curve8" ? 4 : 9;
        app.go(PageId::Program, 5);
    } else if (name.rfind("prog-param-", 0) == 0) {
        std::string type = name.substr(11);
        prog_sync(s.sim->model());
        auto& d = prog_edit().doc[4];
        int idx = -1;
        for (size_t k = 0; k < d.steps.size(); ++k)
            if (d.steps[k].type == type && idx < 0) idx = (int)k;
        if (idx < 0) {
            d.steps.insert(d.steps.end() - 1, model::new_step(s.sim->model().x.schema, type));
            idx = (int)d.steps.size() - 2;
        }
        prog_edit().sel[4] = idx;
        app.go(PageId::Program, 5);
    } else if (name == "lib-mine") app.go(PageId::Program, 105);
    else if (name == "lib-tpl") { prog_edit().lib_tpl = true; app.go(PageId::Program, 105); }
    else if (name == "prog-run") app.go(PageId::Program, 3);
    else if (name == "prog-abort-ask") { app.go(PageId::Program, 3); app.abort_confirm(3); }
    else if (name == "prog-done") { s.sim->inject_prog_done(3); refresh(); app.go(PageId::Program, 3); }
    else if (name == "prog-aborted") { s.sim->inject_prog_aborted(3); refresh(); app.go(PageId::Program, 3); }
    else if (name == "leak-setup") app.go(PageId::Program, 200);
    else if (name == "leak-run") { s.sim->inject_leak("run"); refresh(); app.go(PageId::Program, 200); }
    else if (name == "leak-result") { s.sim->inject_leak("fail"); refresh(); app.go(PageId::Program, 200); }
    else if (name == "leak-pass") { s.sim->inject_leak("pass"); refresh(); app.go(PageId::Program, 200); }
    else if (name == "curve-gas") app.go(PageId::Curve, 0);
    else if (name == "curve-temp") app.go(PageId::Curve, 1);
    else if (name == "curve-press") app.go(PageId::Curve, 2);
    else if (name == "curve-three") app.go(PageId::Curve, 3);
    else if (name == "rec-list") { records_select() = "run-1125-1305"; app.go(PageId::Records, 0); }
    else if (name == "rec-abort") { records_select() = "run-1125-0910"; app.go(PageId::Records, 0); }
    else if (name == "rec-export") { records_select() = "run-1125-1305"; app.go(PageId::Records, 0); records_open_export(app); }
    else if (name == "rec-files") app.go(PageId::Records, 1);
    else if (name == "rec-range-export") { app.go(PageId::Records, 1); records_open_range_export(app); }
    else if (name == "set-general") app.go(PageId::Settings, 0);
    else if (name == "set-params") app.go(PageId::Settings, 1);
    else if (name == "set-pid") app.go(PageId::Settings, 2);
    else if (name == "set-calib") app.go(PageId::Settings, 3);
    else if (name == "set-channels") app.go(PageId::Settings, 4);
    else if (name == "set-net") app.go(PageId::Settings, 5);
    else if (name == "set-net-pw") { app.go(PageId::Settings, 5); settings_open_wifi_password(app, "Lab-2.4G", "lab12345"); }  // 预览：框里已经打了 8 位
    else if (name == "set-comm") { s.sim->inject_stir_offline(); refresh(); app.go(PageId::Settings, 6); }
    else if (name == "set-data") app.go(PageId::Settings, 7);
    else if (name == "set-user") app.go(PageId::Settings, 8);
    else if (name == "set-about") app.go(PageId::Settings, 9);
    else if (name == "set-quit") { app.go(PageId::Settings, 0); settings_confirm_quit(app); }
    else if (name == "ctl-request") { app.go(PageId::Home, 0); s.sim->inject_control_request(); refresh(); }
    else std::fprintf(stderr, "不认识的页面 %s（可用：见 --help）\n", name.c_str());
}

std::unique_ptr<Session> new_session(int W, int H, const Options& o) {
    reset_page_state();
    auto s = std::make_unique<Session>();
    s->t0 = tick_ms();
    if (o.sim) {
        s->sim = std::make_unique<model::SimSource>();
        s->log = std::make_unique<model::LoggingActions>(s->sim.get());
        s->sim->set_language(lang_code(lang()));  // 模拟后台记住的界面语言（真机由 PUT /system/language 保存）
        s->act = s->log.get();
    } else {
        net::Endpoint ep;
        std::string err;
        if (!net::parse_endpoint(o.server, ep, &err)) {
            std::fprintf(stderr, "%s\n", err.c_str());
            std::exit(2);
        }
        s->net = std::make_unique<net::NetBackend>(ep, o.token, s->mailbox, s->ui);
        s->act = &s->net->actions();
        s->net->start();
    }
    // 命令结果：失败照抄后台的中文 message，成功时只有后台给了提示（warnings）才弹
    s->act->on_result = [](bool ok, const std::string& msg) {
        if (!msg.empty()) kit::toast(msg, ok);
    };
    s->app = std::make_unique<App>(*s->act, W, H);
    s->app->build(lv_screen_active());
    pump(*s);
    return s;
}

#if defined(REACTOR_HEADLESS) && REACTOR_HEADLESS
void teardown(std::unique_ptr<Session>& s) {
    s->app->close_overlays();
    kit::Bubble::hide();
    kit::AlarmFull::hide();
    lv_obj_clean(lv_layer_top());
    lv_obj_clean(lv_layer_sys());
    lv_obj_clean(lv_screen_active());
    s.reset();
}
#endif

}  // namespace

int main(int argc, char** argv) {
    int W = 1280, H = 800;
    std::string fonts, shot = "reactor-hmi.bmp", page = "main-f1", shots_dir, lang_arg = "zh";
    Options opt;
    const char* env_srv = std::getenv("REACTOR_HMI_SERVER");
    const char* env_tok = std::getenv("REACTOR_TOKEN");
    opt.server = env_srv && *env_srv ? env_srv : "unix:/run/reactord/hmi.sock";
    opt.token = env_tok ? env_tok : "";
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        auto next = [&]() -> std::string { return i + 1 < argc ? argv[++i] : std::string(); };
        if (a == "--width") W = std::atoi(next().c_str());
        else if (a == "--height") H = std::atoi(next().c_str());
        else if (a == "--fonts") fonts = next();
        else if (a == "--font") next();  // 旧参数，忽略
        else if (a == "--shot") shot = next();
        else if (a == "--shots") shots_dir = next();
        else if (a == "--page") page = next();
        else if (a == "--lang") lang_arg = next();
        else if (a == "--sim") opt.sim = true;
        else if (a == "--server") opt.server = next();
        else if (a == "--token") opt.token = next();
        else if (a == "--wait-ms") opt.wait_ms = std::atoi(next().c_str());
        else if (a == "--selftest-net") return net::selftest_main();
        else if (a == "--fake-serve") { std::string pth = next(); return net::fake_serve_main(pth, i + 1 < argc ? std::atoi(argv[i + 1]) : 0); }
        else if (a == "--help" || a == "-h") {
            std::printf("reactor-hmi [--sim | --server unix:/run/reactord/hmi.sock | --server tcp://H:P --token T] [--selftest-net]\n"
                        "            [--width W] [--height H] [--fonts DIR] [--lang zh|en] [--page NAME] [--shot F.bmp] [--shots DIR] [--wait-ms N]\n页面：");
            for (auto& n : page_names()) std::printf(" %s", n.c_str());
            std::printf("\n");
            return 0;
        }
    }
    if (lang_arg == "en") set_lang(Lang::En);

    lv_init();
    lv_tick_set_cb(tick_ms);

#if defined(REACTOR_HEADLESS) && REACTOR_HEADLESS
    g_w = W; g_h = H;
    g_fb.assign(static_cast<size_t>(W) * H, 0);
    lv_display_t* disp = lv_display_create(W, H);
    static std::vector<uint8_t> buf;
    buf.resize(static_cast<size_t>(W) * H * 4);
    lv_display_set_buffers(disp, buf.data(), nullptr, static_cast<uint32_t>(buf.size()), LV_DISPLAY_RENDER_MODE_FULL);
    lv_display_set_flush_cb(disp, flush_cb);
#elif LV_USE_SDL
    lv_display_t* disp = lv_sdl_window_create(W, H);
    lv_sdl_mouse_create();
#elif LV_USE_X11
    lv_display_t* disp = lv_x11_window_create("reactor-hmi", W, H);
    lv_x11_inputs_create(disp, nullptr);
#else
#error "没有选显示后端：REACTOR_USE_SDL / REACTOR_USE_X11 / REACTOR_HEADLESS 选一个"
#endif
    (void)disp;

    FontSet fs = fonts_init(fonts, argv[0]);
    std::printf("reactor-hmi %dx%d，字体：%s\n", W, H, fs.summary.c_str());

    auto session = new_session(W, H, opt);

#if defined(REACTOR_HEADLESS) && REACTOR_HEADLESS
    auto render = [&](const std::string& name, const std::string& path) {
        if (session->net) {
            // 连后台：先等连上、REST 数据取回来（最多 --wait-ms），连不上就截「正在连接后台服务…」
            int64_t end = tick_ms() + opt.wait_ms;
            while ((int64_t)tick_ms() < end && !session->net->connected()) {
                pump(*session);
                lv_timer_handler();
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
            for (int i = 0; i < 50 && session->net->connected(); ++i) {
                pump(*session);
                lv_timer_handler();
                std::this_thread::sleep_for(std::chrono::milliseconds(20));
            }
        }
        setup_page(*session, name);
        for (int i = 0; i < 6; ++i) {
            pump(*session);
            lv_timer_handler();
            std::this_thread::sleep_for(std::chrono::milliseconds(15));
        }
        lv_refr_now(nullptr);
        if (!write_bmp(path)) { std::fprintf(stderr, "写不了 %s\n", path.c_str()); return false; }
        std::printf("已写出 %s\n", path.c_str());
        return true;
    };
    if (!shots_dir.empty()) {
        for (auto& n : page_names()) {
            if (!render(n, shots_dir + "/" + n + ".bmp")) return 1;
            teardown(session);
            session = new_session(W, H, opt);
        }
        return 0;
    }
    return render(page, shot) ? 0 : 1;
#else
    setup_page(*session, page);
    uint32_t last_pump = 0;
    for (;;) {
        uint32_t now = tick_ms();
        if (now - last_pump >= (session->net ? 30u : 200u)) { pump(*session); last_pump = now; }
        if (session->net && session->net->quit_requested()) break;  // 设置 · 退出程序：只退界面
        uint32_t wait = lv_timer_handler();
        std::this_thread::sleep_for(std::chrono::milliseconds(wait < 5 ? 5 : (wait > 30 ? 30 : wait)));
    }
    session.reset();
    return 0;
#endif
}
