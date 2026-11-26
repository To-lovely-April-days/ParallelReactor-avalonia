// 程序 · 泄漏测试：开始前（Leak-Setup）、进行中（Leak-Run）、结果不通过（Leak-Result）、通过（Leak-Pass）。
// 状态来自遥测 leakTest（C# LeakSnap / LeakChannelSnap），逐点压降来自 Model::x.leak.samples，参数来自设置 leakTest。
#include "common2.hpp"

#include "../kit/draw.hpp"

#include <algorithm>
#include <cmath>
#include <ctime>
#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace reactor::hmi {

// 泄漏测试能不能选这一路（和后台 ctl LeakTest::start 同一条规则：停用 / 已结束 / 手动停止、没被程序占用）
static bool leak_selectable(const model::Model& m, int c) {
    const auto& ch = m.ch(c);
    return (ch.state == "idle" || ch.state == "done" || ch.state == "stopped") && !ch.programOwned && !(ch.program && ch.program->running);
}
// 不能选时格子里的说明
static std::string leak_block_reason(const model::Model& m, int c) {
    const auto& ch = m.ch(c);
    if (ch.programOwned || (ch.program && ch.program->running)) return tr("程序在跑 · 不能测");
    if (ch.state == "alarm") return tr("报警中 · 不能测");
    if (ch.state == "ready") return tr("已启用 · 需先停用");
    return tr("运行中 · 不能测");
}

using namespace kit;
using model::Model;

namespace {

constexpr double kPsi = 0.00689475729;

int stage_index(const model::LeakSnap& L) {
    std::string s = L.stage.value_or("");
    if (s == "fill" || s == "fillWait") return 0;
    if (s == "settle" || s == "balance") return 1;
    if (s == "measure") return 2;
    return 3;
}

// 箭头流程条（Leak-Run 顶上）
struct Chevrons { int cur = 0; double w = 0; };
void chevron_draw(lv_obj_t* o, lv_layer_t* L, const lv_area_t& a, void* user) {
    (void)o;
    auto* c = static_cast<Chevrons*>(user);
    int W = lv_area_get_width(&a), H = lv_area_get_height(&a);
    int n = 4, tip = 16;
    double seg = (W + tip * 0.0) / n;
    for (int i = 0; i < n; ++i) {
        uint32_t fill = i < c->cur ? 0x16a05a : i == c->cur ? col::BLUE : 0xe6ebf2;
        float x0 = (float)(i * seg), x1 = (float)((i + 1) * seg - 6);
        lv_draw_triangle_dsc_t d;
        lv_draw_triangle_dsc_init(&d);
        d.bg_color = lv_color_hex(fill);
        d.bg_opa = LV_OPA_COVER;
        // 矩形部分（两个三角形）+ 右边尖角 + 左边缺口（第一个没有）
        float bx = x1 - tip;
        auto tri = [&](float ax, float ay, float bx2, float by, float cx, float cy) {
            d.p[0] = pt(a.x1 + ax, a.y1 + ay); d.p[1] = pt(a.x1 + bx2, a.y1 + by); d.p[2] = pt(a.x1 + cx, a.y1 + cy);
            lv_draw_triangle(L, &d);
        };
        draw_rect(L, a, x0, 0, bx - x0 + 1, (float)H, fill, LV_OPA_COVER);
        tri(bx, 0, x1, H / 2.0f, bx, (float)H);
        if (i > 0) {
            // 左边挖一个凹口：用卡片底色盖住
            d.bg_color = lv_color_hex(0xffffff);
            tri(x0, 0, x0 + tip, H / 2.0f, x0, (float)H);
        }
    }
}

// 每路结果 / 进度的一行
// compact：8 路一起测时一路只有 ~60 px 高，压力 / 压降和进度条放一行
void chan_result(lv_obj_t* p, int x, int y, int w, const model::LeakChannelSnap& lc, double thr_mpa_h, bool running, bool compact = false) {
    mini_vessel(p, x, y, 34, liquid_color(25), false);
    lv_obj_t* n = label_at(p, x + 34, y + 2, trf("%d 号", lc.id).c_str(), 19, 700, col::DEEP);
    lv_obj_update_layout(n);
    box(p, x + 34 + lv_obj_get_width(n) + 8, y + 15, 14, 3, chan_color(lc.id));
    double rate = lc.ratePsiH ? *lc.ratePsiH * kPsi : 0;
    bool fail = lc.status == "fail" || lc.status == "fillFail" || lc.status == "tooHigh";
    bool high = running && rate > thr_mpa_h;
    if (running) {
        lv_obj_t* s = label(p, tr(lc.detail.empty() ? "测量中" : lc.detail.c_str()), 13.5, high ? 700 : 400, high ? col::AMBER : col::SEC);
        lv_obj_update_layout(s);
        lv_obj_set_pos(s, x + w - lv_obj_get_width(s), y + 6);
    } else {
        int sx = x + w;
        lv_obj_t* s = label(p, tr(fail ? "不通过" : "通过"), 15, 600, fail ? col::RED : 0x14995a);
        lv_obj_update_layout(s);
        sx -= lv_obj_get_width(s);
        lv_obj_set_pos(s, sx, y + 6);
        uint32_t cc = fail ? col::RED : 0x14995a;
        lv_obj_t* cir = box(p, sx - 32, y + 3, 24, 24, 0xffffff, 12);
        lv_obj_set_style_border_width(cir, 2, 0);
        lv_obj_set_style_border_color(cir, lv_color_hex(cc), 0);
        lv_obj_center(icon(cir, fail ? &ic::close20 : &ic::check16, cc));
    }
    std::vector<SpanDef> sp = {{tr("压力 "), 13.5, 400, col::SEC},
                               {model::fmt_mpa(lc.pressurePsi), 17, 700, col::DEEP},
                               {" MPa     ", 12.5, 400, col::MUT},
                               {tr("压降 "), 13.5, 400, col::SEC},
                               {lc.ratePsiH ? model::fmt_num(rate, 3) : std::string("—"), 17, 700, fail ? col::RED : col::DEEP},  // 测量前 3 min 还没有速率
                               {" MPa/h", 12.5, 400, col::MUT}};
    rich(p, x, y + (compact ? 30 : 42), w, sp);
    double frac = rate / (thr_mpa_h * 2.2);
    uint32_t bc = running ? (high ? 0xc27c1f : col::BLUE) : fail ? col::RED : 0x16a05a;
    if (!compact) bar(p, x, y + 72, w, 6, std::max(0.04, frac), bc, 1 / 2.2);
}

void leak_chart(TimeChart& tc, const Model& m, const std::vector<int>& chs, double upto, double total, bool running) {
    const auto& S = m.x.leak.samples;
    double thr = m.x.leakSettings.rateLimitPsiPerHr * kPsi;  // MPa/h
    ChartConfig cfg;
    cfg.x0 = 0; cfg.x1 = total;
    cfg.xticks = nice_ticks(0, total, 5, nullptr);  // 整分钟刻度（原来 total/3 四舍五入成 0 2 3 5 不等距）
    cfg.x_label = [](double x) { return std::to_string((int)std::lround(x)) + " min"; };
    cfg.left = 52; cfg.right = 70; cfg.bottom = 26; cfg.top = 22; cfg.frame = false; cfg.xgrid = false;
    if (running) {
        cfg.now = upto; cfg.shade_future = true; cfg.now_width = 0;
        cfg.marker = upto; cfg.marker_text = tr("现在");
    }
    ChartPanel p;
    double lo = -thr * 1000 * total / 60 * 1.15;
    for (auto& s : S)
        for (int c : chs) lo = std::min(lo, (double)s.dpKPa[(size_t)c - 1] * 1.08);
    p.ymin = lo; p.ymax = 0.8;
    double step = 0;
    p.yticks = nice_ticks(lo, 0, 5, &step);
    p.ydec = step < 0.1 ? 2 : step < 1 ? 1 : 0;  // 压降很小时刻度是 0.5 / 0.2：整数显示会出现两个「0」
    p.unit_top = "kPa";
    std::string ok_label;
    double ok_top = -1e9;
    int ok_last = 0;
    for (int c : chs) {
        ChartSeries se;
        se.color = chan_color(c);
        se.width = 2;
        for (auto& s : S) { se.xs.push_back(s.min); se.y.push_back(s.dpKPa[(size_t)c - 1]); }
        se.end_dot = true;
        double rate = 0;
        for (auto& lc : m.snap.leakTest.channels) if (lc.id == c && lc.ratePsiH) rate = *lc.ratePsiH * kPsi;
        if (rate > thr) {
            se.end_label = trf("%d 号", c);
        } else if (!se.y.empty()) {
            ok_label += std::to_string(c) + " ";
            if (se.y.back() > ok_top) { ok_top = se.y.back(); ok_last = (int)p.series.size(); }
        }
        p.series.push_back(se);
    }
    if (!ok_label.empty() && ok_last < (int)p.series.size()) {
        ok_label.pop_back();  // 末尾的空格
        p.series[(size_t)ok_last].end_label = trf("%s 号", ok_label.c_str());
        p.series[(size_t)ok_last].end_label_color = col::SEC;
    }
    ChartSeries th;
    th.xs = {0, total};
    th.y = {0, (float)(-thr * 1000 * total / 60)};
    th.color = col::RED; th.width = 1; th.dash = 6; th.gap = 5;
    th.end_label = tr("阈值");
    p.series.push_back(th);
    tc.set(cfg, {p});
}

std::vector<int> leak_channels(const Model& m) {
    std::vector<int> v;
    for (auto& lc : m.snap.leakTest.channels) v.push_back(lc.id);
    return v;
}

}  // namespace

void build_leak(lv_obj_t* body, App& app, const Model& m, std::function<void()> refresh) {
    lv_obj_update_layout(body);
    ProgEdit& e = prog_edit();
    const model::LeakSnap& L = m.snap.leakTest;
    int W = app.w(), BH = lv_obj_get_height(body);
    double thr = m.x.leakSettings.rateLimitPsiPerHr * kPsi;
    const auto& ls = m.x.leakSettings;
    if (L.running) e.leak_result_seen = false;
    // ================= 进行中 =================
    if (L.running) {
        lv_obj_t* top = card(body, 12, 4, W - 24, 116);
        auto* cv = new Chevrons{stage_index(L), 0};
        lv_obj_t* ch = box(top, 18, 26, 1016, 64, 0, 0, true);
        on_draw(ch, chevron_draw, cv);
        lv_obj_add_event_cb(ch, [](lv_event_t* ev) { delete static_cast<Chevrons*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, cv);
        const char* names[4] = {"充压", "平衡", "测量", "判读 · 泄压"};
        double seg = 1016 / 4.0;
        int si = cv->cur;
        for (int i = 0; i < 4; ++i) {
            int x = 18 + (int)(i * seg) + (i ? 22 : 16);
            uint32_t tc = i <= si ? 0xffffff : col::MUT;
            lv_obj_t* t = label_at(top, x + (i < si ? 22 : 0), 38, tr(names[i]), 17, 700, tc);
            (void)t;
            if (i < si) icon(top, &ic::check18, 0xffffff, x, 40);
            std::string sub = i < si ? tr("已完成")
                              : i == si ? (i == 2 ? trf("%d / %d min", (int)std::lround(ls.measureMin - L.stageLeftSec.value_or(0) / 60),
                                                        (int)ls.measureMin)
                                                  : std::string(tr("进行中")))
                              : i == 1 ? trf("%d min", (int)ls.settleMin) : i == 2 ? trf("%d min", (int)ls.measureMin) : std::string(tr("约 1 min"));
            label_at(top, x, 62, sub.c_str(), 13, 400, i <= si ? 0xffffff : col::MUT);
        }
        box(top, W - 24 - 210, 14, 1, 88, col::DIV);
        lv_obj_t* l1 = label(top, tr("还要"), 13.5, 400, col::SEC);
        lv_obj_align(l1, LV_ALIGN_TOP_RIGHT, -150, 30);
        lv_obj_t* l2 = label(top, trf("%d min", (int)std::ceil(L.totalLeftSec.value_or(0) / 60)).c_str(), 24, 700, col::DEEP);
        lv_obj_align(l2, LV_ALIGN_TOP_RIGHT, -126, 48);
        // 中止：进度环 + 红方块
        lv_obj_t* sb = box(top, W - 24 - 102, 14, 76, 92, 0, 0, true);
        lv_obj_t* arc = lv_arc_create(sb);
        lv_obj_remove_style_all(arc);
        lv_obj_set_size(arc, 66, 66);
        lv_obj_set_pos(arc, 5, 0);
        lv_arc_set_bg_angles(arc, 0, 360);
        lv_arc_set_rotation(arc, 270);
        lv_arc_set_angles(arc, 0, (int)(360 * L.progress));
        lv_obj_set_style_arc_width(arc, 4, LV_PART_MAIN);
        lv_obj_set_style_arc_color(arc, lv_color_hex(0xdbe5f0), LV_PART_MAIN);
        lv_obj_set_style_arc_width(arc, 4, LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(arc, lv_color_hex(col::BLUE), LV_PART_INDICATOR);
        lv_obj_remove_flag(arc, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_t* sq = box(sb, 5 + 33 - 10, 33 - 10, 20, 20, col::RED, 4);
        (void)sq;
        lv_obj_t* sl = label(sb, tr("中止"), 13, 700, col::RED);
        lv_obj_align(sl, LV_ALIGN_TOP_MID, 0, 72);
        on_tap(sb, [&app] {
            confirm_stop(tr("中止泄漏测试？"), "", "", tr("中止后关闭进气阀并排空泄压，这次测试不出结果。"), tr("继续测"), tr("中止"),
                         [&app] { app.act().leak_abort(); });
        });
        // 左：压降曲线
        int cy = 132, chh = BH - cy - 12;
        lv_obj_t* lc = card(body, 12, cy, 822, chh);
        card_head(lc, 16, 16, tr("测量阶段的压降"), tr("相对测量开始时的压力 · 越往下漏得越快 · 红色虚线 = 阈值"), 17);
        TimeChart* tc = new TimeChart;
        lv_obj_add_event_cb(lc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
        tc->create(lc, 8, 54, 822 - 16, chh - 62);
        double upto = m.x.leak.samples.empty() ? 0 : m.x.leak.samples.back().min;
        leak_chart(*tc, m, leak_channels(m), upto, ls.measureMin, true);
        // 右：每一路
        lv_obj_t* rc = card(body, 846, cy, W - 846 - 12, chh);
        card_head(rc, 16, 16, trf("%d 路", (int)L.channels.size()).c_str(), tr("测完才判定"), 17);
        int y = 50, rw = W - 846 - 12 - 32;
        // 行高按路数排：最多 5 路用宽松的 92 px，再多就紧凑（8 路也放得下，底下那行提示不被压住）
        bool compact = (int)L.channels.size() * 92 > chh - 50 - 40;
        int rh = compact ? std::max(56, (chh - 50 - 40) / std::max(1, (int)L.channels.size())) : 92;
        for (auto& c : L.channels) {
            chan_result(rc, 16, y, rw, c, thr, true, compact);
            y += rh;
            box(rc, 16, y - (compact ? 6 : 10), rw, 1, col::DIV);
        }
        label_at(rc, 16, chh - 30, tr("可以切到别的页，测试在后台继续"), 13, 400, col::SEC);
        return;
    }
    // ================= 结果 =================
    if (L.done && !L.aborted && !e.leak_result_seen) {
        int npass = 0, nall = (int)L.channels.size();
        std::vector<int> fails;
        for (auto& c : L.channels) {
            if (c.status == "pass") ++npass;
            else fails.push_back(c.id);
        }
        bool ok = fails.empty();
        lv_obj_t* top = card(body, 12, 4, W - 24, 92);
        lv_obj_t* ring_ = box(top, 22, 18, 56, 56, 0xffffff, 28);
        lv_obj_set_style_border_width(ring_, 4, 0);
        lv_obj_set_style_border_color(ring_, lv_color_hex(ok ? 0x16a05a : col::RED), 0);
        if (ok) {
            lv_obj_t* a = label(ring_, trf("%d/%d", npass, nall).c_str(), 15, 700, 0x16a05a);
            lv_obj_align(a, LV_ALIGN_CENTER, 0, -5);
            lv_obj_t* b = label(ring_, tr("通过"), 9.5, 600, 0x16a05a);
            lv_obj_align(b, LV_ALIGN_CENTER, 0, 11);
        } else {
            lv_obj_t* xi = icon(ring_, &ic::close20, col::RED);
            lv_image_set_scale(xi, 400);
            lv_obj_center(xi);
        }
        std::string title;
        if (ok) title = trf("测试通过 · %d 路密封良好", nall);
        else {
            std::string f;
            for (size_t k = 0; k < fails.size(); ++k) f += (k ? tr("、") : "") + std::to_string(fails[k]);
            title =trf("测试不通过 · %s 号压降超标", f.c_str());
        }
        label_at(top, 94, 20, title.c_str(), 22, 700, ok ? 0x14995a : col::RED);
        std::string end = L.finishedAt ? md(*L.finishedAt) + " " + hm(*L.finishedAt) : "";
        std::string sub = ok ? trf("压降都 < %s MPa/h · %s 结束 · 已经排空阀泄压 · 记录已保存", model::fmt_num(thr, 3).c_str(), end.c_str())
                             : trf("%d / %d 路通过 · %s 结束 · 已经排空阀泄压 · 记录已保存", npass, nall, end.c_str());
        label_at(top, 94, 54, sub.c_str(), 14, 400, col::SEC);
        std::vector<int> all = leak_channels(m);
        lv_obj_t* b2 = nullptr;
        lv_obj_t* b1 = nullptr;
        if (ok) {
            b2 = box(top, 0, 0, 102, 48, col::BLUE, 9);
            lv_obj_center(label(b2, tr("去编辑程序"), 15.5, 700, 0xffffff));
            on_tap(b2, [&app] { app.go(PageId::Program, 0); });
            b1 = tool_btn(top, 0, 0, tr("再测一次"), nullptr, col::INK, 48, 15.5);
            on_tap(b1, [&app, all] { app.act().leak_start(all); });
        } else {
            b2 = tool_btn(top, 0, 0, tr("重新选择通道"), nullptr, col::INK, 48, 15.5);
            on_tap(b2, [refresh] { prog_edit().leak_result_seen = true; refresh(); });
            std::string f;
            for (size_t k = 0; k < fails.size(); ++k) f += (k ? tr("、") : "") + std::to_string(fails[k]);
            b1 =tool_btn(top, 0, 0, trf("只重测 %s 号", f.c_str()).c_str(), &ic::retest18, col::INK, 48, 15.5);
            on_tap(b1, [&app, fails] { app.act().leak_start(fails); });
        }
        lv_obj_update_layout(b2);
        lv_obj_update_layout(b1);
        int bx = W - 24 - 18 - lv_obj_get_width(b2);
        lv_obj_set_pos(b2, bx, 22);
        lv_obj_set_pos(b1, bx - 10 - lv_obj_get_width(b1), 22);
        // 左：各路结果 + 曲线
        int cy = 108, chh = BH - cy - 12;
        lv_obj_t* lc = card(body, 12, cy, 822, chh);
        card_head(lc, 16, 16, tr("各路结果"), trf("通过条件：压降 < %s MPa/h · 竖红线 = 阈值", model::fmt_num(thr, 3).c_str()).c_str(), 17);
        int colw = 380;
        for (size_t k = 0; k < L.channels.size() && k < 4; ++k)
            chan_result(lc, 16 + (int)(k % 2) * (colw + 30), 50 + (int)(k / 2) * 92, colw, L.channels[k], thr, false);
        int gy = 50 + ((int)std::min<size_t>(L.channels.size(), 4) + 1) / 2 * 92;
        TimeChart* tc = new TimeChart;
        lv_obj_add_event_cb(lc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
        tc->create(lc, 8, gy, 822 - 16, chh - gy - 6);
        leak_chart(*tc, m, all, ls.measureMin, ls.measureMin, false);
        // 右
        int rw = W - 846 - 12;
        lv_obj_t* rc = card(body, 846, cy, rw, chh);
        if (!ok) {
            card_head(rc, 16, 18, trf("%d 号按这个顺序查", fails.front()).c_str(), tr("从最常见的开始"), 17);
            const char* items[5][2] = {{"面密封 O 圈", "最常见。拆下搅拌组件，看 O 圈是否压扁、有切口或残留物，必要时更换"},
                                       {"注射口密封", "在注射口涂皂水，冒泡就清洗或更换注射口 O 圈"},
                                       {"沟槽密封 O 圈", "面密封换过仍不通过时再查"},
                                       {"夹具螺栓", "是否按对角顺序均匀拧紧；过松会漏，过紧会伤螺纹"},
                                       {"进气接头", "看接头是否松动，可涂皂水检漏"}};
            int y = 56;
            for (int i = 0; i < 5; ++i) {
                lv_obj_t* nb = box(rc, 16, y, 24, 24, i == 0 ? 0xfdecec : 0xeef1f5, 12);
                lv_obj_center(label(nb, std::to_string(i + 1).c_str(), 13, 700, i == 0 ? col::RED : col::SEC));
                label_at(rc, 52, y, tr(items[i][0]), 16, 700, col::INK);
                lv_obj_t* d = label_at(rc, 52, y + 24, tr(items[i][1]), 13, 400, col::SEC);
                lv_obj_set_width(d, rw - 70);
                lv_label_set_long_mode(d, LV_LABEL_LONG_WRAP);
                lv_obj_update_layout(d);
                y += 24 + lv_obj_get_height(d) + 16;
                box(rc, 16, y - 8, rw - 32, 1, col::DIV);
            }
            label_at(rc, 16, chh - 30, trf("查完点「只重测 %d 号」", fails.front()).c_str(), 13, 400, col::SEC);
        } else {
            label_at(rc, 16, 18, tr("下一次"), 18, 700, col::INK);
            lv_obj_t* nb = box(rc, 16, 52, rw - 32, 80, 0xedf6f0, 10);
            int64_t next = (L.finishedAt ? *L.finishedAt : m.snap.ts) + (int64_t)(ls.reminderDays * 86400000.0);
            std::time_t tt = (std::time_t)(next / 1000);
            std::tm tm{};
            localtime_r(&tt, &tm);
            cal_badge(nb, 16, 14, tm.tm_mon + 1, tm.tm_mday, 0x16a05a, 50, 52);
            label_at(nb, 80, 18, trf("%s 再测", md(next).c_str()).c_str(), 16, 700, col::INK);
            label_at(nb, 80, 44, trf("每 %d 天一次，在设置里改", (int)ls.reminderDays).c_str(), 13, 400, col::SEC);
            label_at(rc, 16, 150, tr("最近几次"), 18, 700, col::INK);
            int y = 186;
            for (size_t k = 0; k < m.x.leak.history.size() && k < 5; ++k) {
                const auto& h = m.x.leak.history[k];
                label_at(rc, 16, y, md(h.at).c_str(), 15, 700, col::INK);
                label_at(rc, 80, y + 1, h.channels.c_str(), 14, 400, col::SEC);
                lv_obj_t* r = label(rc, tr(h.pass ? "通过" : "不通过"), 14, 600, h.pass ? 0x14995a : col::RED);
                lv_obj_align(r, LV_ALIGN_TOP_RIGHT, -16, y + 1);
                box(rc, 16, y + 30, rw - 32, 1, col::DIV);
                y += 44;
            }
            label_at(rc, 16, chh - 30, tr("全部记录在「记录」页"), 13, 400, col::SEC);
        }
        return;
    }
    // ================= 开始前 =================
    int cy = 4, chh = BH - 16;
    int lw = 820;
    lv_obj_t* lc = card(body, 12, cy, lw, chh);
    card_head(lc, 20, 26, tr("选择要测的通道"), tr("点格子选上或取消；只有停用或已结束的通道能测"), 20);
    // 后台规则（ctl LeakTest::start）：通道状态是 停用（idle）或 已结束（done），且没被程序占用
    auto selectable = [&m](int c) { return leak_selectable(m, c); };
    if (!e.leak_init) {
        for (int c = 1; c <= 8; ++c) e.leak_pick[(size_t)c - 1] = selectable(c);
        e.leak_init = true;
    }
    lv_obj_t* clr = tool_btn(lc, 0, 20, tr("清空"), nullptr, col::INK, 40, 15);
    lv_obj_update_layout(clr);
    lv_obj_set_x(clr, lw - 20 - lv_obj_get_width(clr));
    lv_obj_t* all = tool_btn(lc, 0, 20, tr("选全部可测"), nullptr, col::INK, 40, 15);
    lv_obj_update_layout(all);
    lv_obj_set_x(all, lv_obj_get_x(clr) - 8 - lv_obj_get_width(all));
    on_tap(clr, [refresh] { prog_edit().leak_pick = {}; refresh(); });
    on_tap(all, [refresh, selectable] {
        for (int c = 1; c <= 8; ++c) prog_edit().leak_pick[(size_t)c - 1] = selectable(c);
        refresh();
    });
    std::vector<int> picked;
    int tw = (lw - 40 - 3 * 12) / 4, th = 111;
    for (int c = 1; c <= 8; ++c) {
        bool can = selectable(c), on = can && e.leak_pick[(size_t)c - 1];
        if (on) picked.push_back(c);
        int x = 20 + ((c - 1) % 4) * (tw + 12), y = 72 + ((c - 1) / 4) * (th + 12);
        lv_obj_t* t = box(lc, x, y, tw, th, can ? 0xffffff : 0xf5f7fa, 12);
        ring(t, on ? col::BLUE : can ? col::RING : 0xe9edf2, on ? 3 : 2);
        const auto& chs = m.ch(c);
        mini_vessel(t, 16, 28, 34, can ? liquid_color(25) : liquid_color(chs.tempC.value_or(25)), !can);
        label_at(t, 68, 24, trf("%d 号", c).c_str(), 26, 700, on ? col::BLUE : can ? col::DEEP : col::DIS);
        std::string st = !can ? leak_block_reason(m, c) : on ? tr("测这一路") : tr("不测");
        lv_obj_t* stl = label_at(t, 68, 64, st.c_str(), 14, 400, !can ? col::MUT2 : col::SEC);
        lv_obj_set_width(stl, tw - 76);
        lv_label_set_long_mode(stl, LV_LABEL_LONG_WRAP);
        if (on) {
            lv_obj_t* d = box(t, tw - 32, th / 2 - 6, 13, 13, 0x16a05a, 7);
            lv_obj_set_style_outline_width(d, 3, 0);
            lv_obj_set_style_outline_color(d, lv_color_hex(0x16a05a), 0);
            lv_obj_set_style_outline_opa(d, 50, 0);
        }
        if (can) on_tap(t, [c, refresh] { prog_edit().leak_pick[(size_t)c - 1] = !prog_edit().leak_pick[(size_t)c - 1]; refresh(); });
    }
    // 上次泄漏测试
    lv_obj_t* lb = box(lc, 20, chh - 104, lw - 40, 82, 0xfdf8f0, 10);
    int64_t last = ls.lastTestUtc.value_or(0);
    if (last) {
        std::time_t tt = (std::time_t)(last / 1000);
        std::tm tm{};
        localtime_r(&tt, &tm);
        cal_badge(lb, 16, 14, tm.tm_mon + 1, tm.tm_mday, 0xb7791f, 50, 54);
        int days = days_between(last, m.snap.ts);
        bool due = days > ls.reminderDays;
        label_at(lb, 80, 16, trf("上次泄漏测试 · %d 天前", days).c_str(), 16.5, 700, due ? col::AMBER : col::INK);
        bool pass = m.x.leak.history.empty() || m.x.leak.history.front().pass;
        label_at(lb, 80, 46, trf("%s · %s · 设置里定的是每 %d 天测一次", md(last).c_str(), tr(pass ? "全部通过" : "有不通过"), (int)ls.reminderDays).c_str(),
                 14, 400, col::SEC);
    } else {
        // 从没测过（新装 / 数据清过）：别留一块空框
        label_at(lb, 20, 16, tr("还没有做过泄漏测试"), 16.5, 700, col::AMBER);
        label_at(lb, 20, 46, trf("建议装好釜后先测一次 · 设置里定的是每 %d 天测一次", (int)ls.reminderDays).c_str(), 14, 400, col::SEC);
    }
    // 右：怎么测
    int rx = 12 + lw + 12, rw = W - rx - 12;
    lv_obj_t* rc = card(body, rx, cy, rw, chh);
    label_at(rc, 16, 26, tr("怎么测"), 20, 700, col::INK);
    struct Row { const char* k; std::string v; const char* u; };
    const Row rows[] = {{"测试气体", tr("惰性"), ""},
                        {"测试压力", model::fmt_mpa(ls.testPsi), "MPa"},
                        {"平衡时间", model::fmt_num(ls.settleMin, 0), "min"},
                        {"测量时间", model::fmt_num(ls.measureMin, 0), "min"},
                        {"通过条件 · 压降 <", model::fmt_num(thr, 3), "MPa/h"}};
    int y = 66;
    for (auto& r : rows) {
        label_at(rc, 16, y + 4, tr(r.k), 14.5, 400, col::SEC);
        lv_obj_t* u = label(rc, r.u, 12.5, 400, col::MUT);
        lv_obj_set_pos(u, rw - 16 - 50, y + 8);
        lv_obj_t* v = label(rc, r.v.c_str(), 18, 700, col::DEEP);
        lv_obj_align(v, LV_ALIGN_TOP_RIGHT, -16 - 50 - 5, y);
        box(rc, 16, y + 32, rw - 32, 1, col::DIV);
        y += 34;
    }
    label_at(rc, 16, y + 10, tr("这几项在「设置 · 泄漏测试」里改"), 12.5, 400, col::SEC);
    y += 44;
    const char* steps[4] = {"充压", "平衡", "测量", "判读 · 泄压"};
    std::string durs[4] = {"≤ 3 min", trf("%d min", (int)ls.settleMin), trf("%d min", (int)ls.measureMin), tr("约 1 min")};
    for (int i = 0; i < 4; ++i) {
        lv_obj_t* nb = box(rc, 16, y, 24, 24, 0xeef1f5, 12);
        lv_obj_center(label(nb, std::to_string(i + 1).c_str(), 12.5, 700, col::SEC));
        label_at(rc, 50, y + 1, tr(steps[i]), 15, 700, col::INK);
        lv_obj_t* d = label(rc, durs[i].c_str(), 13.5, 400, col::SEC);
        lv_obj_align(d, LV_ALIGN_TOP_RIGHT, -16, y + 3);
        y += 30;
    }
    bool can_go = !picked.empty() && !(m.snap.gas.owner && *m.snap.gas.owner == "leak");
    lv_obj_t* pb = box(rc, (rw - 92) / 2, chh - 168, 92, 92, can_go ? col::BLUE : 0xb3c2d4, 46);
    lv_obj_set_style_outline_width(pb, 6, 0);
    lv_obj_set_style_outline_color(pb, lv_color_hex(col::BLUE), 0);
    lv_obj_set_style_outline_opa(pb, can_go ? 36 : 0, 0);
    lv_obj_t* pi = icon(pb, &ic::play_fill36, 0xffffff);
    lv_obj_align(pi, LV_ALIGN_CENTER, 3, 0);
    lv_obj_t* t1 = label(rc, trf("开始测试 · %d 路", (int)picked.size()).c_str(), 17, 700, can_go ? col::DEEP : col::DIS);
    lv_obj_align(t1, LV_ALIGN_TOP_MID, 0, chh - 66);
    std::string chs;
    for (int c : picked) chs += std::to_string(c) + " ";
    double est = 3 + ls.settleMin + ls.measureMin + 1;
    lv_obj_t* t2 = label(rc, picked.empty() ? tr("先在左边选通道") : trf("%s号 · 预计约 %d min", chs.c_str(), (int)est).c_str(), 13.5, 400, col::SEC);
    lv_obj_align(t2, LV_ALIGN_TOP_MID, 0, chh - 38);
    if (can_go) on_tap(pb, [&app, picked] { app.act().leak_start(picked); });
}

}  // namespace reactor::hmi
