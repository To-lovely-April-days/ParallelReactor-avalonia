// 程序 · 运行中（Prog-Run）、跑完（Prog-Done）、已中止（Prog-Aborted）
#include "common2.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;

std::string& records_select();  // records_page.cpp：记录页要选中的运行 id

namespace {

constexpr float kNaN = std::numeric_limits<float>::quiet_NaN();

double trend_at(const model::Trend* t, const std::vector<float>& v, int64_t at) {
    if (!t || v.empty()) return NAN;
    long i = std::lround(double(at - t->t0) / (double)t->stepMs);
    if (i < 0) return NAN;
    if (i >= (long)v.size()) i = (long)v.size() - 1;
    for (long k = i; k >= 0 && k > i - 30; --k)
        if (!std::isnan(v[(size_t)k])) return v[(size_t)k];
    return NAN;
}

// 节点（圆 + 两行字），kind：done / cur / todo / abort / skip
void node(lv_obj_t* p, int cx, int cy, int idx, const std::string& type, const std::string& kind, const std::string& value,
          const std::string& sub, uint32_t sub_color, double cur_frac) {
    int d = 40;
    lv_obj_t* c = nullptr;
    if (kind == "done") {
        c = box(p, cx - d / 2, cy - d / 2, d, d, 0x14995a, d / 2);
        lv_obj_center(icon(c, &ic::check20b, 0xffffff));
    } else if (kind == "abort") {
        c = box(p, cx - d / 2, cy - d / 2, d, d, col::RED, d / 2);
        lv_obj_center(icon(c, &ic::close20, 0xffffff));
    } else if (kind == "cur") {
        lv_obj_t* arc = lv_arc_create(p);
        lv_obj_remove_style_all(arc);
        lv_obj_set_pos(arc, cx - 28, cy - 28);
        lv_obj_set_size(arc, 56, 56);
        lv_arc_set_bg_angles(arc, 0, 360);
        lv_arc_set_rotation(arc, 270);
        lv_arc_set_angles(arc, 0, (int)(360 * std::max(0.04, std::min(1.0, cur_frac))));
        lv_obj_set_style_arc_width(arc, 4, LV_PART_MAIN);
        lv_obj_set_style_arc_color(arc, lv_color_hex(0xdbe5f0), LV_PART_MAIN);
        lv_obj_set_style_arc_width(arc, 4, LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(arc, lv_color_hex(col::BLUE), LV_PART_INDICATOR);
        lv_obj_set_style_arc_rounded(arc, true, LV_PART_INDICATOR);
        lv_obj_remove_flag(arc, LV_OBJ_FLAG_CLICKABLE);
        c = box(p, cx - d / 2, cy - d / 2, d, d, col::BLUE, d / 2);
        lv_obj_center(icon(c, step_icon(type, 20), 0xffffff));
    } else {
        c = box(p, cx - d / 2, cy - d / 2, d, d, 0xffffff, d / 2);
        ring(c, kind == "skip" ? 0xdde3ea : 0xc9d3de, 2);
        lv_obj_center(icon(c, step_icon(type, 20), kind == "skip" ? 0xc3cad3 : col::SEC));
    }
    char b[64];
    std::snprintf(b, sizeof b, "%02d %s", idx + 1, tr(model::zh_step_name(type).c_str()));
    uint32_t nc = kind == "cur" ? col::BLUE : kind == "abort" ? col::RED : kind == "skip" ? col::DIS : col::SEC;
    lv_obj_t* l1 = label(p, b, 15.5, 700, nc);
    lv_obj_update_layout(l1);
    lv_obj_set_pos(l1, cx - lv_obj_get_width(l1) / 2, cy + 30);
    lv_obj_t* l2 = label(p, value.c_str(), 14, 600, kind == "skip" ? col::DIS : kind == "cur" ? col::BLUE : col::SEC);
    lv_obj_update_layout(l2);
    lv_obj_set_pos(l2, cx - lv_obj_get_width(l2) / 2, cy + 52);
    lv_obj_t* l3 = label(p, sub.c_str(), 12.5, kind == "cur" ? 600 : 400, sub_color);
    lv_obj_update_layout(l3);
    lv_obj_set_pos(l3, cx - lv_obj_get_width(l3) / 2, cy + 73);
}

// 连接线（做完的段绿色实线，没执行的段虚线）
void link(lv_obj_t* p, int x1, int x2, int cy, uint32_t c, bool dashed) {
    if (!dashed) {
        box(p, x1, cy - 1, x2 - x1, 3, c);
        return;
    }
    for (int x = x1; x < x2; x += 8) box(p, x, cy - 1, std::min(4, x2 - x), 2, c);
}

lv_obj_t* head_card(lv_obj_t* body, int W, int ch, const std::string& name, lv_obj_t** right_out) {
    lv_obj_t* c = card(body, 12, 6, W - 24, 204);
    label_at(c, 16, 16, trf("%d 号 · %s", ch, name.c_str()).c_str(), 20, 700, col::INK);
    *right_out = c;
    return c;
}

int place_after(lv_obj_t* c, lv_obj_t* prev, lv_obj_t* o, int gap, int y) {
    lv_obj_update_layout(prev);
    lv_obj_update_layout(o);
    int x = lv_obj_get_x(prev) + lv_obj_get_width(prev) + gap;
    lv_obj_set_pos(o, x, y);
    (void)c;
    return x + lv_obj_get_width(o);
}

// 右上的按钮（从右往左）
void right_buttons(lv_obj_t* c, int cw, std::vector<lv_obj_t*> btns) {
    int x = cw - 16;
    for (auto* b : btns) {
        lv_obj_update_layout(b);
        x -= lv_obj_get_width(b);
        lv_obj_set_pos(b, x, 14);
        x -= 10;
    }
}

lv_obj_t* filled_btn(lv_obj_t* p, const char* text, const lv_image_dsc_t* im, uint32_t color = col::BLUE) {
    const lv_font_t* f = font(15.5, 700);
    int w = text_width(text, f) + (im ? (int)im->header.w + 8 : 0) + 34;
    lv_obj_t* b = box(p, 0, 0, w, 42, color, 9);
    int x = 17;
    if (im) {
        icon(b, im, 0xffffff, x, (42 - (int)im->header.h) / 2);
        x += (int)im->header.w + 8;
    }
    lv_obj_t* l = label(b, text, 15.5, 700, 0xffffff);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, x, 0);
    lv_obj_add_flag(b, LV_OBJ_FLAG_CLICKABLE);
    return b;
}

void big_value(lv_obj_t* p, int x, int y, const char* cap, const std::string& v, const char* unit, uint32_t color, double px = 34) {
    label_at(p, x, y, cap, 13.5, 400, col::SEC);
    lv_obj_t* l = label_at(p, x, y + 18, v.c_str(), px, 700, color);
    lv_obj_update_layout(l);
    label_at(p, x + lv_obj_get_width(l) + 4, y + 18 + (int)(px * 0.62), unit, 13, 400, col::MUT);
}

}  // namespace

// ================================================================ 整次运行三块图
void run_chart(TimeChart& tc, const Model& m, int ch, const model::Trend* trend, int64_t from, int64_t to, int64_t now,
               bool aborted, bool show_plan) {
    const model::PlanExt& pe = m.plans[(size_t)ch - 1];
    ChartConfig cfg;
    cfg.x0 = (double)from; cfg.x1 = (double)to;
    double span_h = (to - from) / 3600000.0;
    cfg.xticks = clock_ticks(from, to, span_h > 6 ? 60 : span_h > 2 ? 30 : 15);
    cfg.x_label = [](double x) { return hm((int64_t)x); };
    cfg.left = 40; cfg.gap = 6; cfg.bottom = 22;
    cfg.ytick_px = 12;
    if (show_plan) {
        cfg.now = (double)now; cfg.now_color = col::ORANGE; cfg.now_width = 2;
        for (auto& sg : pe.segs) {
            cfg.vlines.push_back((double)sg.start);
            if (now >= sg.start && now < sg.end) cfg.highlight = std::make_pair((double)sg.start, (double)sg.end);
        }
    }
    if (aborted) { cfg.now = (double)now; cfg.grey_future = true; cfg.stop_at = (double)now; cfg.now_width = 0; cfg.now_color = col::RED; }
    std::vector<ChartPanel> ps(3);
    const char* T[3] = {"温度", "压力", "吸气量"};
    const char* U[3] = {"°C", "MPa", "mmol"};
    for (int i = 0; i < 3; ++i) {
        ChartPanel& p = ps[(size_t)i];
        p.title = tr(T[i]); p.unit = U[i]; p.title_box = false;
        ChartSeries a;
        a.color = col::BLUE; a.width = 2;
        if (trend) {
            const auto& src = i == 0 ? trend->temp[(size_t)ch - 1] : i == 1 ? trend->press[(size_t)ch - 1] : trend->gas[(size_t)ch - 1];
            a.x0 = (double)trend->t0; a.dx = (double)trend->stepMs;
            a.y = src;
            // 只画到「现在」
            for (size_t k = 0; k < a.y.size(); ++k)
                if (trend->t_at((int)k) > now || trend->t_at((int)k) < from) a.y[k] = kNaN;
        }
        double lo = 1e9, hi = -1e9;
        for (float v : a.y) if (!std::isnan(v)) { lo = std::min(lo, (double)v); hi = std::max(hi, (double)v); }
        if (show_plan) {
            const auto& plan = i == 0 ? pe.tempPlan : pe.pressPlan;
            ChartSeries pl;
            pl.color = 0x9fb0c4; pl.width = 2; pl.dash = 6; pl.gap = 5;
            for (auto& pt : plan) {
                if (pt.t < now) continue;
                if (pl.xs.empty()) {
                    // 从「现在」的计划值接上
                    double v0 = pt.v;
                    for (size_t k = 0; k + 1 < plan.size(); ++k)
                        if (now >= plan[k].t && now <= plan[k + 1].t) v0 = plan[k].v;
                    pl.xs.push_back((double)now); pl.y.push_back((float)v0);
                }
                pl.xs.push_back((double)pt.t); pl.y.push_back((float)pt.v);
                lo = std::min(lo, pt.v); hi = std::max(hi, pt.v);
            }
            if (i < 2 && !pl.xs.empty()) p.series.push_back(pl);
        }
        p.series.insert(p.series.begin(), a);
        if (i == 0) { p.ymin = 20; p.ymax = std::max(90.0, hi + 8); p.yticks = {20, std::round((20 + p.ymax - 8) / 2 / 10) * 10, std::round((p.ymax - 8) / 10) * 10}; }
        if (i == 1) { p.ymin = 0; p.ymax = std::max(3.0, std::ceil(hi + 0.2)); p.yticks = nice_ticks(0, p.ymax, 3); }
        if (i == 2) { p.ymin = 0; p.ymax = std::max(5.0, std::ceil(hi * 1.15)); p.yticks = {0, p.ymax}; }
        if (i == 2 && show_plan)
            for (auto& sg : pe.segs)
                if (sg.kind == "react" && now < sg.start) {
                    p.hatch = std::make_pair((double)sg.start, (double)sg.end);
                    p.hatch_text = tr("反应开始后计量 · 吸气停止就结束");
                }
    }
    tc.set(cfg, ps);
}

// ================================================================ 运行中
void build_prog_run(lv_obj_t* body, App& app, const Model& m, int ch) {
    lv_obj_update_layout(body);
    int W = app.w(), BH = lv_obj_get_height(body), cw = W - 24;
    const auto& c = m.ch(ch);
    const model::ProgressExt& pg = m.progress[(size_t)ch - 1];
    const model::PlanExt& pe = m.plans[(size_t)ch - 1];
    const model::ProgramDoc& doc = m.x.programs[(size_t)ch - 1];
    int si = c.program ? c.program->stepIndex : 0;
    int n = (int)doc.steps.size();
    int64_t now = m.snap.ts;
    int64_t start = c.program && c.program->startedAt ? *c.program->startedAt : now;
    int64_t latest = pe.segs.empty() ? now : pe.segs.back().end;
    lv_obj_t* hc = nullptr;
    head_card(body, W, ch, pg.programName.empty() ? doc.name : pg.programName, &hc);
    lv_obj_t* t = lv_obj_get_child(hc, 0);
    bool waiting = c.program && c.program->waitingGas;
    lv_obj_t* pl = pill(hc, (waiting ? trf("等气路 · 第 %d / %d 步", si + 1, n) : trf("运行中 · 第 %d / %d 步", si + 1, n)).c_str(),
                        waiting ? col::AMBER : 0x14995a, waiting ? 0xfdf3e1 : 0xe3f4ea, 13.5, nullptr, true);
    int x = place_after(hc, t, pl, 12, 17);
    std::string meta = trf("%s 开始 · 已 %s", hm(start).c_str(), model::fmt_hmin(pg.elapsedSec).c_str());
    bool has_react = false;
    for (auto& s : doc.steps) has_react = has_react || s.type == "react";
    if (has_react) meta += trf(" · 反应时长看吸气，最晚 %s 结束", hm(latest).c_str());
    label_at(hc, x + 18, 21, meta.c_str(), 14, 400, col::MUT);
    lv_obj_t* b1 = tool_btn(hc, 0, 0, tr("在曲线页看"), &ic::chart_line16);
    lv_obj_t* b2 = tool_btn(hc, 0, 0, tr("中止程序…"), &ic::stop_sq14, col::RED, 40, 15, 0xf0b4b4);
    right_buttons(hc, cw, {b2, b1});
    on_tap(b1, [&app] { app.go(PageId::Curve, 3); });
    on_tap(b2, [&app, ch] { app.abort_confirm(ch); });
    // 步骤节点
    int x0 = 76, x1 = cw - 76, cy = 100;
    double sp = n > 1 ? double(x1 - x0) / (n - 1) : 0;
    for (int k = 0; k + 1 < n; ++k)
        link(hc, x0 + (int)(k * sp) + 20, x0 + (int)((k + 1) * sp) - 20, cy, k < si ? 0x14995a : 0xdfe5ec, false);
    for (int k = 0; k < n; ++k) {
        const auto& st = doc.steps[(size_t)k];
        std::string kind = k < si ? "done" : k == si ? "cur" : "todo";
        std::string sub;
        uint32_t sc = col::MUT;
        const model::PlanSeg* sg = k < (int)pe.segs.size() ? &pe.segs[(size_t)k] : nullptr;
        if (kind == "done" && sg) { sub = trf("用了 %s", model::fmt_hmin((sg->end - sg->start) / 1000).c_str()); sc = 0x14995a; }
        else if (kind == "cur" && sg) {
            int64_t el = c.program && c.program->stepElapsedMin ? (int64_t)(*c.program->stepElapsedMin * 60)
                                                                 : std::max<int64_t>(0, (now - sg->start) / 1000);
            sub = trf("已 %s · 约 %s", model::fmt_hmin(el).c_str(), model::fmt_hmin((sg->end - sg->start) / 1000).c_str());
            if (waiting) sub = trf("排队第 %d 位", pg.gasQueueRank.value_or(1));
            sc = col::BLUE;
        } else if (sg) sub = trf("约 %s", hm(sg->start).c_str());
        node(hc, x0 + (int)std::lround(k * sp), cy, k, st.type, kind, tr(model::step_value_line(st).c_str()), sub, sc, pg.stepFraction);
    }
    // ---------- 下面：左边这一步的读数，右边整次运行 ----------
    int by = 222, bh = BH - by - 12;
    lv_obj_t* bc = card(body, 12, by, cw, bh);
    const model::StepDoc* st = si < n ? &doc.steps[(size_t)si] : nullptr;
    std::string ty = st ? st->type : "";
    lv_obj_t* bd = box(bc, 16, 16, 30, 30, col::BLUE, 15);
    lv_obj_center(icon(bd, step_icon(ty, 16), 0xffffff));
    std::string title = trf("第 %d 步 · %s", si + 1, tr(model::zh_step_name(ty).c_str()));
    if (ty == "heat" && st->code("mode", "fixed") == "fixed") title = trf("第 %d 步 · 升温到 %s °C", si + 1, model::fmt_num(st->num("temp", 80), 0).c_str());
    if (ty == "press") title = trf("第 %d 步 · 充压到 %s MPa", si + 1, model::fmt_press_psi(st->num("pres", 290.1)).c_str());
    label_at(bc, 56, 20, title.c_str(), 17, 700, col::INK);
    const model::Trend* tr_ = m.x.trend.get();
    if (waiting) {
        big_value(bc, 16, 64, tr("排队等气路"), trf("第 %d 位", pg.gasQueueRank.value_or(1)), "", col::AMBER, 30);
        std::string wf;
        for (int w : pg.waitingFor) wf += std::to_string(w) + " ";
        label_at(bc, 16, 134, trf("在等 %s号用完气路 · 已等 %s", wf.c_str(), model::fmt_hmin(pg.waitedSec).c_str()).c_str(), 14, 400, col::SEC);
    } else if (ty == "heat") {
        double sp_c = c.tempSpC, t0 = trend_at(tr_, tr_ ? tr_->temp[(size_t)ch - 1] : std::vector<float>{}, pe.segs.size() > (size_t)si ? pe.segs[(size_t)si].start : now);
        if (std::isnan(t0)) t0 = 25;
        double tn = c.tempC.value_or(t0);
        big_value(bc, 16, 64, tr("现在"), model::fmt_num(tn, 1), "°C", col::BLUE);
        big_value(bc, 132, 64, tr("目标"), model::fmt_num(sp_c, 0), "°C", col::DEEP);
        bar(bc, 16, 136, 322, 7, (tn - t0) / std::max(1.0, sp_c - t0), col::BLUE);
        double t5 = tr_ ? trend_at(tr_, tr_->temp[(size_t)ch - 1], now - 5 * 60000) : NAN;
        double rate = std::isnan(t5) ? 0 : (tn - t5) / 5.0;
        big_value(bc, 16, 156, tr("升温速率"), model::fmt_num(rate, 1), "°C/min", col::INK, 22);
        std::string eta = rate > 0.05 ? hm(now + (int64_t)((sp_c - tn) / rate * 60000)) : std::string("—");
        big_value(bc, 132, 156, tr("预计到温"), eta, "", col::INK, 22);
    } else if (ty == "react") {
        big_value(bc, 16, 64, tr("吸气量"), model::fmt_mmol(c.gasConsumed), "mmol", col::BLUE);
        big_value(bc, 180, 64, tr("吸气速率"), c.gasRate ? model::fmt_num(*c.gasRate, 3) : std::string("—"), "mmol/min", col::DEEP, 26);
        // 已反应：用后台给的这一步实际已进行时间（计划起点和实际常差十几分钟）；旧后台没有时退回计划
        int64_t el = c.program && c.program->stepElapsedMin ? (int64_t)(*c.program->stepElapsedMin * 60)
                     : si < (int)pe.segs.size() ? std::max<int64_t>(0, (now - pe.segs[(size_t)si].start) / 1000) : 0;
        double maxs = st->num("time", 480) * 60;
        double fr = c.program && c.program->stepElapsedMin ? std::min(1.0, el / std::max(1.0, maxs)) : pg.stepFraction;
        bar(bc, 16, 136, 322, 7, fr, col::BLUE);
        big_value(bc, 16, 156, tr("已反应"), model::fmt_hmin(el), "", col::INK, 22);
        big_value(bc, 180, 156, tr("最长"), model::fmt_dur_min(st->num("time", 480)), "", col::INK, 22);
    } else if (ty == "press" || ty == "leak" || ty == "exchange" || ty == "vent") {
        double tgt = ty == "press" ? st->num("pres", 290.1) : ty == "leak" ? st->num("pres", 435.1) : ty == "vent" ? st->num("to", 2.9) : st->num("fill", 72.5);
        big_value(bc, 16, 64, tr("现在"), model::fmt_mpa(c.pressurePsi), "MPa", col::BLUE);
        big_value(bc, 170, 64, tr("目标"), model::fmt_press_psi(tgt), "MPa", col::DEEP);
        double pn = c.pressurePsi.value_or(0);
        bar(bc, 16, 136, 322, 7, ty == "vent" ? 1 - pn / std::max(1.0, 290.0) : pn / std::max(1.0, tgt), col::BLUE);
        big_value(bc, 16, 156, tr("这一步已"), model::fmt_hmin(si < (int)pe.segs.size() ? (now - pe.segs[(size_t)si].start) / 1000 : 0), "", col::INK, 22);
    } else {
        big_value(bc, 16, 64, tr("设定"), st ? tr(model::step_value_line(*st).c_str()) : "", "", col::BLUE, 30);
        big_value(bc, 16, 136, tr("这一步已"), model::fmt_hmin(si < (int)pe.segs.size() ? (now - pe.segs[(size_t)si].start) / 1000 : 0), "", col::INK, 22);
    }
    if (si + 1 < n) {
        const auto& nx = doc.steps[(size_t)si + 1];
        lv_obj_t* nb = box(bc, 16, bh - 70, 322, 54, 0xf3f6f9, 8);
        char b[96];
        std::snprintf(b, sizeof b, "%02d %s %s", si + 2, tr(model::zh_step_name(nx.type).c_str()), tr(model::step_value_line(nx).c_str()));
        std::string s2 = model::step_sub_line(nx);
        rich(nb, 12, 8, 300, {{tr("下一步 "), 13.5, 400, col::SEC}, {b, 13.5, 700, col::INK}, {s2.empty() ? "" : " · " + s2, 13.5, 700, col::INK}});
        bool gas = nx.type == "press" || nx.type == "exchange" || nx.type == "leak";
        label_at(nb, 12, 29, tr(gas ? "气路被占用时会排队" : "上一步完成后自动开始"), 13, 400, col::MUT);
    }
    // 右：整次运行
    int rx = 356, rw = cw - rx - 12;
    lv_obj_t* hd = label_at(bc, rx, 14, tr("整次运行"), 15, 700, col::INK);
    lv_obj_update_layout(hd);
    int lx = rx + lv_obj_get_width(hd) + 14;
    box(bc, lx, 24, 16, 3, col::BLUE);
    label_at(bc, lx + 20, 15, tr("实际"), 13, 400, col::SEC);
    for (int i = 0; i < 3; ++i) box(bc, lx + 62 + i * 6, 24, 3, 2, 0x9fb0c4);
    label_at(bc, lx + 84, 15, tr("计划"), 13, 400, col::SEC);
    box(bc, lx + 126, 17, 3, 14, col::ORANGE);
    label_at(bc, lx + 134, 15, tr("现在"), 13, 400, col::SEC);
    lv_obj_t* rr = label(bc, trf("%s 开始 → 最晚 %s 结束", hm(start).c_str(), hm(latest).c_str()).c_str(), 13, 400, col::MUT);
    lv_obj_align(rr, LV_ALIGN_TOP_RIGHT, -12, 15);
    TimeChart* tc = new TimeChart;
    lv_obj_add_event_cb(bc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
    tc->create(bc, rx - 40, 58, rw + 40, bh - 62);
    run_chart(*tc, m, ch, tr_, start, std::max(latest, now + 3600000), now, false, true);
    // 步骤号（图上面一行）：挤在一起时只留当前步和反应步的名字，其余号码放不下就不写
    int lastR = -1000;
    for (size_t k = 0; k < pe.segs.size(); ++k) {
        int px = rx - 40 + tc->x_to_px((double)pe.segs[k].start) + 2;
        bool cur = (int)k == si, react = pe.segs[k].kind == "react";
        bool important = cur || react;
        if (!important && px < lastR + 6) continue;
        if (!important && k + 1 < pe.segs.size()) {
            int nx = rx - 40 + tc->x_to_px((double)pe.segs[k + 1].start);
            bool next_imp = (int)k + 1 == si || pe.segs[k + 1].kind == "react";
            if (next_imp && nx - px < 18) continue;
        }
        std::string s2 = important ? trf("%02d %s", (int)k + 1, tr(model::zh_step_name(pe.segs[k].kind).c_str())) : std::to_string(k + 1);
        lv_obj_t* l = label(bc, s2.c_str(), 12, cur ? 700 : 500, cur ? col::BLUE : (int)k < si ? 0x14995a : col::MUT);
        lv_obj_set_pos(l, std::max(px, lastR + 6), 38);
        lv_obj_update_layout(l);
        lastR = lv_obj_get_x(l) + lv_obj_get_width(l);
    }
}

// ================================================================ 跑完 / 已中止
void build_prog_result(lv_obj_t* body, App& app, const Model& m, int ch, const model::RunRecord& r) {
    lv_obj_update_layout(body);
    int W = app.w(), BH = lv_obj_get_height(body), cw = W - 24;
    bool ab = r.result == "abort";
    const auto& c = m.ch(ch);
    const model::ProgramDoc& doc = r.program;
    int n = (int)doc.steps.size();
    int ai = ab && c.program ? c.program->stepIndex : n;
    lv_obj_t* hc = nullptr;
    head_card(body, W, ch, r.programName, &hc);
    lv_obj_t* t = lv_obj_get_child(hc, 0);
    lv_obj_t* pl = ab ? pill(hc, tr("已中止"), col::RED, 0xfdecec, 13.5, &ic::close20) : pill(hc, tr("已完成"), 0x14995a, 0xe3f4ea, 13.5, &ic::check16);
    int x = place_after(hc, t, pl, 12, 17);
    std::string meta = hm(r.start) + " – " + hm(r.end) + " · ";
    meta += ab ? trf("运行了 %s · 操作员手动中止", model::fmt_hmin((r.end - r.start) / 1000).c_str())
               : trf("用时 %s", model::fmt_hmin((r.end - r.start) / 1000).c_str()) + (r.endReason.empty() ? "" : " · " + std::string(tr(r.endReason.c_str())));
    label_at(hc, x + 18, 21, meta.c_str(), 14, 400, col::MUT);
    lv_obj_t* b2 = filled_btn(hc, tr(ab ? "用同样设置再跑一次" : "再跑一次"), &ic::rerun18);
    lv_obj_t* b1 = tool_btn(hc, 0, 0, tr("看运行记录"), &ic::doc18, col::INK, 42);
    lv_obj_t* b0 = tool_btn(hc, 0, 0, tr("改程序"), &ic::pencil16, col::INK, 42);
    right_buttons(hc, cw, {b2, b1, b0});
    std::string rid = r.id;
    on_tap(b1, [&app, rid] { records_select() = rid; app.go(PageId::Records, 0); });
    model::ProgramDoc again = doc;
    on_tap(b2, [&app, ch, again, rid] {
        prog_edit().result_dismissed[ch - 1] = rid;
        app.act().put_program(ch, again);
        app.act().start_program(ch);
    });
    on_tap(b0, [&app, ch, rid] {
        prog_edit().result_dismissed[ch - 1] = rid;
        app.go(PageId::Program, ch);
    });
    int x0 = 76, x1 = cw - 76, cy = 100;
    double sp = n > 1 ? double(x1 - x0) / (n - 1) : 0;
    for (int k = 0; k + 1 < n; ++k)
        link(hc, x0 + (int)(k * sp) + 20, x0 + (int)((k + 1) * sp) - 20, cy, k < ai ? 0x14995a : 0xc9d3de, k >= ai);
    for (int k = 0; k < n; ++k) {
        const auto& st = doc.steps[(size_t)k];
        std::string kind = k < ai ? "done" : k == ai ? "abort" : "skip";
        std::string sub;
        uint32_t sc = 0x14995a;
        if (kind == "done") {
            int mins = k < (int)r.stepMinutes.size() ? r.stepMinutes[(size_t)k] : 0;
            sub = (k == n - 1 && !ab) ? hm(r.end) : trf("用了 %s", model::fmt_hmin(mins * 60LL).c_str());
        } else if (kind == "abort") { sub = hm(r.end) + tr(" 中止"); sc = col::RED; }
        else { sub = tr("没有执行"); sc = col::DIS; }
        node(hc, x0 + (int)std::lround(k * sp), cy, k, st.type, kind, tr(model::step_value_line(st).c_str()), sub, sc, 1);
    }
    int by = 222, bh = BH - by - 12;
    lv_obj_t* bc = card(body, 12, by, cw, bh);
    // 运行详情（GET /runs/{id}）的 trend 只带各路吸气量；温度 / 压力从最近 6 h 的曲线里取（运行在这个范围里时）
    const model::Trend* trd = r.trend.get();
    auto has_temp = [ch](const model::Trend* t) {
        if (!t) return false;
        for (double v : t->temp[(size_t)ch - 1]) if (!std::isnan(v)) return true;
        return false;
    };
    if (!has_temp(trd) && has_temp(m.x.trend.get())) trd = m.x.trend.get();
    if (!ab) {
        label_at(bc, 16, 16, tr("这一次的结果"), 17, 700, col::INK);
        const model::RunChannelStat* s = nullptr;
        for (auto& x2 : r.perCh) if (x2.ch == ch) s = &x2;
        big_value(bc, 16, 54, tr("吸气量"), model::fmt_num(s ? s->mmol : r.avgMmol, 2), "mmol", col::BLUE, 34);
        int react_min = -1;  // 程序里没有反应步：显示「—」
        for (size_t k = 0; k < doc.steps.size() && k < r.stepMinutes.size(); ++k)
            if (doc.steps[k].type == "react") react_min = r.stepMinutes[k];
        big_value(bc, 186, 54, tr("反应用时"), react_min < 0 ? std::string("—") : model::fmt_hmin(react_min * 60LL), "", col::INK, 22);
        big_value(bc, 16, 128, tr("最快吸气"), model::fmt_num(s ? s->maxRate : 0, 3), "mmol/min", col::INK, 22);
        big_value(bc, 186, 128, tr("温度最大偏差"), model::fmt_num(r.tempDevC, 1), "°C", col::INK, 22);
        lv_obj_t* nb = box(bc, 16, bh - 70, 322, 54, 0xf3f6f9, 8);
        label_at(nb, 12, 8, trf("加热已停，正在自然冷却（现在 %s °C）", model::fmt_temp(c).c_str()).c_str(), 13.5, 400, col::SEC);
        label_at(nb, 12, 29, trf("釜内还有 %s MPa", model::fmt_mpa(c.pressurePsi, 2).c_str()).c_str(), 13.5, 400, col::SEC);
    } else {
        std::string at = doc.steps.empty() || ai >= n ? "" : tr(model::zh_step_name(doc.steps[(size_t)ai].type).c_str());
        label_at(bc, 16, 16, trf("中止在第 %d 步 %s", ai + 1, at.c_str()).c_str(), 17, 700, col::RED);
        double tl = NAN, plast = NAN;
        if (trd) {
            for (auto it = trd->temp[(size_t)ch - 1].rbegin(); it != trd->temp[(size_t)ch - 1].rend(); ++it) if (!std::isnan(*it)) { tl = *it; break; }
            for (auto it = trd->press[(size_t)ch - 1].rbegin(); it != trd->press[(size_t)ch - 1].rend(); ++it) if (!std::isnan(*it)) { plast = *it; break; }
        }
        label_at(bc, 16, 42, trf("%s · 当时 %s °C · %s MPa", hm(r.end).c_str(), std::isnan(tl) ? "—" : model::fmt_num(tl, 1).c_str(),
                                 std::isnan(plast) ? "—" : model::fmt_num(plast, 2).c_str()).c_str(), 13.5, 400, col::SEC);
        label_at(bc, 16, 76, tr("系统已经做了"), 13.5, 600, col::SEC);
        const std::string did[3] = {tr("加热已停，自然冷却中"), trf("%d 号进气阀已关", ch), tr("吸气量停止计量，已记的保留")};
        for (int i = 0; i < 3; ++i) {
            icon(bc, &ic::check_sm16, 0x14995a, 16, 104 + i * 28);
            label_at(bc, 42, 102 + i * 28, did[i].c_str(), 14.5, 400, col::INK);
        }
        lv_obj_t* nb = box(bc, 16, bh - 50, 322, 36, 0xfdf6e9, 8);
        lv_obj_t* nl = label(nb, tr("釜里的气没有放掉；要放空请到主页手动开排空"), 13.5, 400, col::INK);
        lv_obj_align(nl, LV_ALIGN_LEFT_MID, 12, 0);
    }
    int rx = 356, rw = cw - rx - 12;
    label_at(bc, rx, 14, tr("整次运行"), 15, 700, col::INK);
    lv_obj_t* rr = label(bc, ab ? tr("后面灰色是没有执行的部分") : (hm(r.start) + " → " + hm(r.end)).c_str(), 13, 400, col::MUT);
    lv_obj_align(rr, LV_ALIGN_TOP_RIGHT, -12, 15);
    TimeChart* tc = new TimeChart;
    lv_obj_add_event_cb(bc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
    tc->create(bc, rx - 40, 44, rw + 40, bh - 48);
    int64_t to = ab ? r.start + std::max<int64_t>(r.plannedSec * 1000, r.end - r.start + 3600000) : r.end;
    run_chart(*tc, m, ch, trd, r.start, to, r.end, ab, false);
}

}  // namespace reactor::hmi
