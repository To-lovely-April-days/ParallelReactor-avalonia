// 记录页：运行记录（Rec-List / Rec-Abort，导出 Rec-Export）、每日数据文件（Rec-Files，按时间段导出 Rec-RangeExport）。
// 运行记录来自新端点 GET /runs（由会话 manifest、事件和遥测 CSV 汇总）；文件列表来自 GET /files（C# HistoryFileInfo）。
#include "common2.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <array>
#include <cstdint>
#include <cstdlib>
#include <memory>
#include <string>
#include <vector>

namespace reactor::hmi {

// 中止信息里有没有量值（超温 / 超压联锁才有）；手动中止、程序故障的 quantity 为空
static bool a_quantity_known(const model::RunAbortInfo& a) {
    return (a.quantity == "pressure" || a.quantity == "temp") && a.rangeHi > 0;
}

using namespace kit;
using model::Model;
using model::RunRecord;

std::string& records_select() {
    static std::string s;
    return s;
}

namespace {

struct RecState {
    int filter = 0;          // 0 全部 / 1 完成 / 2 有警告 / 3 中止
    std::string day;         // 每日数据文件：选中的日子 yyyyMMdd
    int month_ofs = 0;       // 日历：相对「这个月」
};
RecState& rs() {
    static RecState s;
    return s;
}

std::string ymd(int64_t ms) {
    std::time_t t = (std::time_t)(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char b[16];
    std::strftime(b, sizeof b, "%Y%m%d", &tm);
    return b;
}
int64_t day_start(const std::string& d) {
    std::tm tm{};
    tm.tm_year = std::atoi(d.substr(0, 4).c_str()) - 1900;
    tm.tm_mon = std::atoi(d.substr(4, 2).c_str()) - 1;
    tm.tm_mday = std::atoi(d.substr(6, 2).c_str());
    tm.tm_isdst = -1;
    return (int64_t)std::mktime(&tm) * 1000;
}

const RunRecord* find_run(const Model& m, const std::string& id) {
    for (auto& r : m.x.runs) if (r.id == id) return &r;
    return nullptr;
}

bool pass_filter(const RunRecord& r, int f) {
    if (f == 0) return true;
    if (f == 1) return r.result == "done" || r.result == "leak";
    if (f == 2) return r.result == "warn";
    return r.result == "abort";
}

uint32_t result_color(const RunRecord& r) {
    if (r.result == "running") return col::BLUE;
    if (r.result == "abort") return col::RED;
    if (r.result == "warn") return 0xc27c1f;
    return 0x16a05a;
}

lv_obj_t* blue_btn(lv_obj_t* p, int x, int y, const char* text, const lv_image_dsc_t* im, int h = 48) {
    const lv_font_t* f = font(16, 700);
    int w = text_width(text, f) + (im ? scale::U((int)im->header.w) + 10 : 0) + 40;
    lv_obj_t* b = box(p, x, y, w, h, col::BLUE, 9);
    int cx = 20;
    if (im) { icon(b, im, 0xffffff, cx, (h - scale::U((int)im->header.h)) / 2); cx += scale::U((int)im->header.w) + 10; }
    lv_obj_align(label(b, text, 16, 700, 0xffffff), LV_ALIGN_LEFT_MID, cx, 0);
    lv_obj_add_flag(b, LV_OBJ_FLAG_CLICKABLE);
    return b;
}

void kpi(lv_obj_t* p, int x, int y, int w, const lv_image_dsc_t* im, const char* cap, const std::string& v, const char* unit) {
    lv_obj_t* t = box(p, x, y, w, 68, 0xffffff, 10);
    ring(t, 0xe1e7ee, 1);
    lv_obj_t* c = box(t, 12, 12, 44, 44, 0xeef3f9, 22);
    lv_obj_center(icon(c, im, col::BLUE));
    label_at(t, 68, 10, cap, 13, 400, col::SEC);
    lv_obj_t* l = label_at(t, 68, 28, v.c_str(), 22, 700, col::DEEP);
    lv_obj_update_layout(l);
    label_at(t, 68 + lv_obj_get_width(l) + 4, 38, unit, 12.5, 400, col::MUT);
}

// 「各路吸气量」：横轴按小时，开始充反应气的虚线，中止的红线
void gas_chart(TimeChart& tc, const RunRecord& r, const model::Trend* t, const std::vector<int>& chs, int64_t now_for_running) {
    ChartConfig cfg;
    double end_min = r.end ? (r.end - r.start) / 60000.0 : (now_for_running - r.start) / 60000.0;
    double span = r.result == "abort" ? std::ceil((end_min + 10) / 30) * 30 : std::max(60.0, std::ceil(end_min / 60) * 60);
    if (r.result == "running") span = std::max(span, end_min + 30);
    cfg.x0 = 0; cfg.x1 = span;
    double step = span > 300 ? 60 : 30;
    for (double x = 0; x <= span + 0.1; x += step) cfg.xticks.push_back(x);
    cfg.x_label = [](double x) {
        int m = (int)std::lround(x);
        if (m % 60 == 0) return std::to_string(m / 60) + " h";
        return std::to_string(m / 60) + " h " + std::to_string(m % 60);
    };
    cfg.left = 40; cfg.right = 10; cfg.top = 22; cfg.bottom = 24; cfg.frame = true; cfg.ytick_px = 12;
    if (r.gasStart > r.start) { cfg.marker = (r.gasStart - r.start) / 60000.0; cfg.marker_text = tr("开始充反应气"); }
    if (r.result == "abort") {
        cfg.now = end_min; cfg.grey_future = true; cfg.now_width = 0;
        cfg.stop_at = end_min;
        cfg.stop_text = hm(r.end) + tr(" 中止");
    }
    ChartPanel p;
    p.unit_top = "mmol";
    double hi = 1;
    if (t) {
        for (int ch : chs) {
            ChartSeries se;
            se.x0 = (t->t0 - r.start) / 60000.0;
            se.dx = t->stepMs / 60000.0;
            se.y = t->gas[(size_t)ch - 1];
            se.color = chan_color(ch);
            se.width = 2;
            for (float v : se.y) if (!std::isnan(v)) hi = std::max(hi, (double)v);
            p.series.push_back(se);
        }
    }
    p.ymin = 0; p.ymax = std::ceil(hi + 0.4);
    p.yticks = nice_ticks(0, p.ymax, 5);
    tc.set(cfg, {p});
}

}  // namespace

void reset_records_state() { rs() = RecState{}; records_select().clear(); }

// ================================================================ 导出对话框
void records_open_export(App& app) {
    const Model& m = app.m();
    const RunRecord* r = find_run(m, records_select());
    if (!r) return;
    auto kinds = std::make_shared<std::array<bool, 4>>(std::array<bool, 4>{true, true, false, false});
    auto chs = std::make_shared<std::array<bool, 8>>();
    for (int c : r->channels) (*chs)[(size_t)c - 1] = true;
    DialogSpec s;
    s.w = 640; s.color = col::BLUE; s.icon = nullptr;
    s.title = tr("导出到 U 盘");
    s.subtitle = r->programName + " · " + md_hm(r->start);
    s.secondary = tr("取消"); s.primary = tr("导出");
    std::string folder = ymd(r->start).substr(0, 8) + "-" + hm(r->start).substr(0, 2) + hm(r->start).substr(3, 2) + " " + r->programName;
    bool usb = m.x.usb.present;
    std::vector<int> run_chs = r->channels;
    s.body = [kinds, chs, folder, usb, run_chs](lv_obj_t* b, int w) {
        label_at(b, 0, 0, tr("导出什么"), 13.5, 400, col::SEC);
        const char* ext[4] = {"CSV", "PDF", "JSONL", "JSON"};
        const char* tt[4] = {"读数表格", "运行报告", "事件记录", "程序设置"};
        const char* ss[4] = {"每 15 s 一行", "一页纸结果", "阀门、报警、操作", "能在另一台导入"};
        int tw = (w - 10) / 2;
        for (int i = 0; i < 4; ++i) {
            auto* lt = new LampTile;
            lt->create(b, (i % 2) * (tw + 10), 24 + (i / 2) * 64, tw, 56, ext[i], tr(tt[i]), tr(ss[i]), (*kinds)[(size_t)i]);
            lv_obj_add_event_cb(b, [](lv_event_t* ev) { delete static_cast<LampTile*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, lt);
            lt->on_change = [kinds, i, lt](bool on) { (*kinds)[(size_t)i] = on; lt->set(on); };
        }
        label_at(b, 0, 160, tr("哪几路"), 13.5, 400, col::SEC);
        for (int c = 1; c <= 8; ++c) {
            bool in = std::find(run_chs.begin(), run_chs.end(), c) != run_chs.end();
            lv_obj_t* chip = chan_chip(b, (c - 1) * 60, 186, c, (*chs)[(size_t)c - 1], !in, 54, 40);
            if (!in) continue;
            on_tap(chip, [chs, c, chip] {
                bool on = !(*chs)[(size_t)c - 1];
                (*chs)[(size_t)c - 1] = on;
                lv_obj_set_style_bg_color(chip, lv_color_hex(on ? 0xffffff : col::TILE), 0);
                ring(chip, on ? 0x8fa6bf : col::RING, 2);
            });
        }
        icon(b, &ic::usb16, usb ? 0x16a05a : col::RED, 0, 242);
        std::string path = usb ? std::string(tr("存到 U 盘 / 反应记录 / ")) + folder + tr(" · 约 2.1 MB") : std::string(tr("没有插 U 盘：插上以后再导出"));
        label_at(b, 22, 240, path.c_str(), 13.5, 400, usb ? col::SEC : col::RED);
        return 264;
    };
    std::string id = r->id;
    s.on_primary = [&app, kinds, chs, id] {
        model::ExportReq q;
        q.runId = id;
        const char* k[4] = {"csv", "pdf", "events", "program"};
        for (int i = 0; i < 4; ++i) if ((*kinds)[(size_t)i]) q.kinds.push_back(k[i]);
        for (int c = 1; c <= 8; ++c) if ((*chs)[(size_t)c - 1]) q.channels.push_back(c);
        q.stepSec = 15;
        app.act().export_usb(q);
        toast(tr("已开始导出到 U 盘"));
    };
    Dialog::open(s);
}

void records_open_range_export(App& app) {
    const Model& m = app.m();
    std::string day = rs().day.empty() && m.x.days.size() > 1 ? m.x.days[1].day : rs().day;
    if (day.empty() && !m.x.days.empty()) day = m.x.days.front().day;
    const model::DayInfo* di = nullptr;
    for (auto& d : m.x.days) if (d.day == day) di = &d;
    int64_t d0 = day_start(day);
    struct R { int64_t from, to; int step; std::array<bool, 2> kinds; int quick; };
    auto st = std::make_shared<R>(R{d0, d0 + 86400000LL, 15, {true, true}, -1});
    if (di && !di->runs.empty()) {
        const auto& rr = di->runs.front();
        st->from = rr.start / 3600000 * 3600000;
        st->to = std::min<int64_t>(d0 + 86400000LL, (rr.end + 3599999) / 3600000 * 3600000 - 4 * 3600000LL);
        if (st->to <= st->from) st->to = st->from + 5 * 3600000LL;
    }
    std::vector<model::DayRunRef> runs = di ? di->runs : std::vector<model::DayRunRef>{};
    DialogSpec s;
    s.w = 660; s.color = col::BLUE; s.icon = nullptr;
    s.title = tr("按时间段导出");
    s.subtitle = tr("原始数据 · 8 路全部");
    s.secondary = tr("取消"); s.primary = tr("导出");
    int64_t now = m.snap.ts;
    s.body = [st, runs, d0, now](lv_obj_t* b, int w) {
        int fw = (w - 36) / 2;
        auto field = [&](int x, const char* cap, int64_t t) {
            label_at(b, x, 0, tr(cap), 13.5, 400, col::SEC);
            lv_obj_t* f = box(b, x, 22, fw, 46, col::TILE, 10);
            ring(f, col::RING, 2);
            icon(f, &ic::cal_clock22, 0, 14, 12);
            label_at(f, 48, 10, md_hm(t).c_str(), 19, 700, col::DEEP);
            return f;
        };
        lv_obj_t* f1 = field(0, "从", st->from);
        lv_obj_t* f2 = field(fw + 36, "到", st->to);
        (void)f1; (void)f2;
        label_at(b, fw + 10, 34, "→", 17, 400, col::MUT);
        // 快捷
        LampGroup* q = new LampGroup;
        lv_obj_add_event_cb(b, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, q);
        q->create(b, 0, 80, {tr("最近 1 小时"), tr("这一天"), tr("按一次运行…")}, {100, 80, 120}, st->quick, 40, 14.5, 8);
        q->on_change = [st, runs, d0, now](int i) {
            st->quick = i;
            if (i == 0) { st->to = now; st->from = now - 3600000; }
            if (i == 1) { st->from = d0; st->to = d0 + 86400000LL; }
            if (i == 2 && !runs.empty()) { st->from = runs.front().start; st->to = runs.front().end ? runs.front().end : now; }
        };
        lv_obj_t* tot = label(b, trf("共 %d h", (int)std::lround((st->to - st->from) / 3600000.0)).c_str(), 14, 400, col::SEC);
        lv_obj_align(tot, LV_ALIGN_TOP_RIGHT, 0, 90);
        // 当天时间轴：跑过的程序 + 选中的时间段
        int ty = 146;
        lv_obj_t* tl = box(b, 0, ty, w, 14, 0xe9edf2, 7);
        (void)tl;
        for (auto& r : runs) {
            double a = double(r.start - d0) / 86400000.0, e = double((r.end ? r.end : now) - d0) / 86400000.0;
            int x1 = (int)(a * w), x2 = (int)(std::min(1.0, e) * w);
            box(b, x1, ty, std::max(6, x2 - x1), 14, r.result == "abort" ? 0xeaa3a3 : 0x9fd3b5, 7);
            lv_obj_t* l = label(b, r.name.c_str(), 12, 400, col::SEC);
            lv_obj_set_pos(l, x1, ty - 18);
        }
        double a = double(st->from - d0) / 86400000.0, e = double(st->to - d0) / 86400000.0;
        lv_obj_t* selb = box(b, (int)(a * w), ty - 3, std::max(8, (int)((e - a) * w)), 20, 0x7fbfa8, 6);
        lv_obj_set_style_bg_opa(selb, 120, 0);
        ring(selb, 0x1d3557, 2);
        const char* tk[5] = {"0:00", "6:00", "12:00", "18:00", "24:00"};
        for (int i = 0; i < 5; ++i) {
            lv_obj_t* l = label(b, tk[i], 12, 400, col::MUT);
            lv_obj_update_layout(l);
            int lx = i * w / 4 - lv_obj_get_width(l) / 2;
            lv_obj_set_pos(l, std::max(0, std::min(w - (int)lv_obj_get_width(l), lx)), ty + 20);
        }
        label_at(b, 0, 196, tr("导出什么"), 13.5, 400, col::SEC);
        const char* kn[2] = {"读数", "事件"};
        int kx = 0;
        for (int i = 0; i < 2; ++i) {
            LampGroup* k = new LampGroup;
            lv_obj_add_event_cb(b, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, k);
            k->allow_reselect = true;
            k->create(b, kx, 218, {tr(kn[i])}, {70}, st->kinds[(size_t)i] ? 0 : -1, 40, 15, 0);
            k->on_change = [st, i, k](int) { st->kinds[(size_t)i] = !st->kinds[(size_t)i]; k->set(st->kinds[(size_t)i] ? 0 : -1); };
            kx += k->width() + 8;
        }
        label_at(b, 0, 272, tr("读数间隔（越疏文件越小）"), 13.5, 400, col::SEC);
        LampGroup* iv = new LampGroup;
        lv_obj_add_event_cb(b, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, iv);
        iv->create(b, 0, 294, {tr("每 1.2 s · 原始"), tr("每 15 s"), tr("每 1 min")}, {120, 80, 80}, st->step == 0 ? 0 : st->step == 15 ? 1 : 2, 40, 15, 8);
        iv->on_change = [st, iv](int i) { st->step = i == 0 ? 0 : i == 1 ? 15 : 60; iv->set(i); };
        lv_obj_t* pb = box(b, 0, 350, w, 44, 0xf3f6f9, 8);
        file_icon(pb, 10, 6, "CSV");
        file_icon(pb, 44, 6, "JSONL");
        std::string nm = ymd(st->from) + "_" + hm(st->from).substr(0, 2) + hm(st->from).substr(3, 2) + "-" + hm(st->to).substr(0, 2) +
                         hm(st->to).substr(3, 2);
        lv_obj_t* l = label(pb, (std::string(tr("存到 U 盘 / 原始数据 / ")) + nm + tr(" / · 约 1.4 MB")).c_str(), 13.5, 400, col::SEC);
        lv_obj_align(l, LV_ALIGN_LEFT_MID, 84, 0);
        return 394;
    };
    s.on_primary = [&app, st] {
        model::ExportReq q;
        q.from = st->from; q.to = st->to; q.stepSec = st->step;
        if (st->kinds[0]) q.kinds.push_back("telemetry");
        if (st->kinds[1]) q.kinds.push_back("events");
        app.act().export_usb(q);
        toast(tr("已开始导出到 U 盘"));
    };
    Dialog::open(s);
}

// ================================================================ 页面
class RecordsPage : public Page {
public:
    explicit RecordsPage(int tab) : tab_(tab == 1 ? 1 : 0) {}
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        H_ = app.h() - kTopH;
        tabs_.create(root, 8, 4, W_ - 16, {{&ic::doc18, "运行记录", "", TabDef::None}, {&ic::table18, "每日数据文件", "", TabDef::None}}, tab_);
        tabs_.on_select = [this](int i) { tab_ = i; sig_.clear(); if (app_) update(app_->m()); };
        usb_ = box(tabs_.obj(), 0, 0, 10, 44, 0, 0, true);
        body_ = box(root, 0, 56, W_, H_ - 56, 0, 0, true);
    }
    void update(const Model& m) override {
        if (finger_down()) return;  // 拖列表时不重建
        char sig[200];
        std::snprintf(sig, sizeof sig, "%d/%d/%s/%s/%d/%zu/%d/%lld/%d", tab_, rs().filter, records_select().c_str(), rs().day.c_str(),
                      rs().month_ofs, m.x.runs.size(), (int)m.x.usb.present, (long long)(m.snap.ts / 10000), ver_);
        if (sig == sig_) return;
        sig_ = sig;
        lv_obj_clean(usb_);
        lv_obj_t* ul = label(usb_, m.x.usb.present ? trf("U 盘已插入 · 剩 %s", fmt_bytes(m.x.usb.freeBytes).c_str()).c_str() : tr("没有插 U 盘"),
                             13.5, 400, col::SEC);
        lv_obj_update_layout(ul);
        int uw = lv_obj_get_width(ul) + 24;
        lv_obj_set_size(usb_, uw, 44);
        lv_obj_set_pos(usb_, W_ - 16 - 24 - uw, 0);
        icon(usb_, &ic::usb16, m.x.usb.present ? 0x16a05a : col::MUT2, 0, 14);
        lv_obj_set_pos(ul, 24, 13);
        lv_obj_clean(body_);
        if (tab_ == 0) build_runs(m);
        else build_files(m);
    }

private:
    void refresh() { ++ver_; if (app_) update(app_->m()); }

    void build_runs(const Model& m) {
        int BH = H_ - 56;
        RecState& s = rs();
        if (!find_run(m, records_select())) {
            for (auto& r : m.x.runs)
                if (r.result == "done" && r.end) { records_select() = r.id; break; }
        }
        // ---- 左：列表 ----
        int lw = 440;
        lv_obj_t* lc = card(body_, 12, 0, lw, BH - 12);
        int nall = 0, ndone = 0, nwarn = 0, nab = 0;
        for (auto& r : m.x.runs) {
            ++nall;
            ndone += r.result == "done" || r.result == "leak";
            nwarn += r.result == "warn";
            nab += r.result == "abort";
        }
        LampGroup* lg = new LampGroup;
        lv_obj_add_event_cb(lc, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, lg);
        lg->create(lc, 14, 12, {trf("全部 %d", nall), trf("完成 %d", ndone), trf("有警告 %d", nwarn), trf("中止 %d", nab)}, {80, 80, 90, 70},
                   s.filter, 36, 14, 7);
        lg->on_change = [this](int i) { rs().filter = i; forget_scroll("records"); refresh(); };
        lv_obj_t* list = box(lc, 0, 58, lw, BH - 12 - 62, 0, 0, true);
        scroll_list(list, "records");
        int y = 0;
        std::string last_day;
        int64_t now = m.snap.ts;
        for (auto& r : m.x.runs) {
            if (!pass_filter(r, s.filter)) continue;
            std::string dw = day_word(r.start, now);
            if (dw != last_day) {
                last_day = dw;
                label_at(list, 86, y + 6, dw.c_str(), 13, 700, col::INK);
                y += 30;
            }
            bool on = r.id == records_select();
            box(list, 85, y, 2, 61, col::DIV);
            if (on) {
                lv_obj_t* sb = box(list, 102, y + 3, lw - 116, 54, 0xeef4fb, 9);
                ring(sb, col::BLUE, 2);
            }
            label_at(list, 26, y + 10, hm(r.start).c_str(), 15, 700, col::INK);
            lv_obj_t* dot = box(list, 80, y + 14, 12, 12, 0xffffff, 6);
            lv_obj_set_style_border_width(dot, 3, 0);
            lv_obj_set_style_border_color(dot, lv_color_hex(result_color(r)), 0);
            label_at(list, 112, y + 8, r.programName.c_str(), 16, 700, on ? col::BLUE : col::INK);
            std::string sub = r.result == "running" ? trf("已 %s · %d 路", model::fmt_hmin((now - r.start) / 1000).c_str(), (int)r.channels.size())
                                                    : trf("%s · %d 路", model::fmt_hmin((r.end - r.start) / 1000).c_str(), (int)r.channels.size());
            label_at(list, 112, y + 33, sub.c_str(), 13, 400, col::SEC);
            std::string rt = tr(r.result == "running" ? "在主页看" : r.resultText.c_str());
            uint32_t rc = r.result == "running" ? col::BLUE : r.result == "abort" ? col::RED : r.result == "warn" ? 0xc27c1f : col::SEC;
            lv_obj_t* rl = label(list, rt.c_str(), 13.5, r.result == "running" || r.result == "abort" || r.result == "warn" ? 700 : 400, rc);
            lv_obj_update_layout(rl);
            lv_obj_set_pos(rl, lw - 26 - lv_obj_get_width(rl), y + 10);
            lv_obj_t* hit = box(list, 0, y, lw, 61, 0, 0, true);
            std::string id = r.id;
            on_tap(hit, [this, id] { records_select() = id; refresh(); });
            y += 61;
        }
        restore_scroll(list, "records");
        // ---- 右：详情 ----
        const RunRecord* r = find_run(m, records_select());
        int rx = 12 + lw + 12, rw = W_ - rx - 12;
        lv_obj_t* rc = card(body_, rx, 0, rw, BH - 12);
        if (!r) {
            lv_obj_center(label(rc, tr("没有运行记录"), 16, 400, col::MUT));
            return;
        }
        build_detail(rc, rw, BH - 12, m, *r);
    }

    void build_detail(lv_obj_t* rc, int rw, int rh, const Model& m, const RunRecord& r) {
        int64_t now = m.snap.ts;
        lv_obj_t* nm = label_at(rc, 20, 18, r.programName.c_str(), 22, 700, col::INK);
        lv_obj_update_layout(nm);
        lv_obj_t* pl = nullptr;
        if (r.result == "abort") pl = pill(rc, trc("中止 (状态)"), col::RED, 0xfdecec, 13.5, &ic::xclose20);
        else if (r.result == "running") pl = pill(rc, tr("运行中"), col::BLUE, 0xe3ecf8, 13.5, nullptr, true);
        else if (r.result == "warn") pl = pill(rc, tr("有警告"), 0xb36b00, 0xfdf0dc, 13.5, &ic::st_alert13);
        else pl = pill(rc, tr(r.result == "leak" ? "通过" : "完成"), 0x14995a, 0xe3f4ea, 13.5, &ic::check16);
        lv_obj_set_pos(pl, 20 + lv_obj_get_width(nm) + 12, 22);
        lv_obj_t* note = tool_btn(rc, 0, 16, tr(r.note.empty() ? "写备注" : "改备注"), &ic::pencil16, col::INK, 40);
        lv_obj_update_layout(note);
        lv_obj_set_x(note, rw - 16 - lv_obj_get_width(note));
        std::string id = r.id, oldn = r.note;
        on_tap(note, [this, id, oldn] {
            TextKbd::open(tr("这次运行的备注"), oldn, 60, [this, id](const std::string& s) { app_->act().run_note(id, s); });
        });
        std::string meta;
        if (r.result == "running") meta = trf("%s 开始 · 已 %s · %s", md_hm(r.start).c_str(), model::fmt_hmin((now - r.start) / 1000).c_str(), chan_list(r.channels).c_str());
        else if (r.result == "abort")
            meta = md_hm(r.start) + " – " + hm(r.end) + " · " +
                   trf("运行了 %s（计划 %s）· %s", model::fmt_hmin((r.end - r.start) / 1000).c_str(), model::fmt_hmin(r.plannedSec).c_str(),
                       chan_list(r.channels).c_str());
        else meta = md_hm(r.start) + " – " + hm(r.end) + " · " + model::fmt_hmin((r.end - r.start) / 1000) + " · " + chan_list(r.channels) +
                    (r.endReason.empty() ? "" : " · " + std::string(tr(r.endReason.c_str())));
        if (!r.note.empty()) meta += "  ·  " + std::string(tr("备注：")) + r.note;
        label_at(rc, 20, 56, meta.c_str(), 14, 400, col::SEC);
        int y = 90;
        if (r.result == "leak") {
            lv_obj_t* l = label_at(rc, 20, y, trf("%s 都通过 · 每一路的压降和曲线在「程序 · 泄漏测试」的结果页", chan_list(r.channels).c_str()).c_str(), 15, 400, col::INK);
            (void)l;
            lv_obj_t* b = blue_btn(rc, 20, rh - 70, tr("去泄漏测试"), &ic::tab_shield18);
            on_tap(b, [this] { app_->go(PageId::Program, 200); });
            return;
        }
        if (r.abort && a_quantity_known(*r.abort)) {
            const auto& a = *r.abort;
            lv_obj_t* ab = box(rc, 20, y, rw - 40, 112, 0xfffafa, 12);
            ring(ab, 0xf0c9c6, 2);
            mini_vessel(ab, 18, 14, 18, chan_color(a.ch));
            label_at(ab, 42, 14, trf("%d 号%s", a.ch, tr(a.quantity == "pressure" ? "压力" : "温度")).c_str(), 14, 400, col::SEC);
            lv_obj_t* v = label_at(ab, 18, 36, model::fmt_num(a.value, 2).c_str(), 34, 700, col::RED);
            lv_obj_update_layout(v);
            label_at(ab, 18 + lv_obj_get_width(v) + 5, 56, a.quantity == "pressure" ? "MPa" : "°C", 13, 400, col::SEC);
            label_at(ab, 18, 82, trf("%s · 超上限 %s", model::fmt_clock(a.at).c_str(), model::fmt_num(a.value - a.limit, 2).c_str()).c_str(), 13, 400, col::SEC);
            int bx = 200;
            box(ab, bx, 14, 1, 84, 0xf0c9c6);
            label_at(ab, bx + 20, 14, tr(a.title.c_str()), 15.5, 700, col::RED);
            int bw = rw - 40 - bx - 40;
            double lf = a.limit / a.rangeHi, vf = a.value / a.rangeHi;
            box(ab, bx + 20, 48, (int)(bw * lf), 8, 0xdfeee5, 4);
            box(ab, bx + 20 + (int)(bw * lf), 48, bw - (int)(bw * lf), 8, 0xfadada, 4);
            lv_obj_t* d = box(ab, bx + 20 + (int)(bw * vf) - 8, 44, 16, 16, col::RED, 8);
            ring(d, 0xffffff, 2);
            label_at(ab, bx + 20, 62, "0", 12, 400, col::SEC);
            lv_obj_t* sl = label(ab, trf("%s 设定", model::fmt_num(a.setpoint, 1).c_str()).c_str(), 12, 400, col::SEC);
            lv_obj_update_layout(sl);
            lv_obj_set_pos(sl, bx + 20 + (int)(bw * a.setpoint / a.rangeHi) - lv_obj_get_width(sl) / 2, 62);
            lv_obj_t* ll = label(ab, trf("上限 %s", model::fmt_num(a.limit, 2).c_str()).c_str(), 12, 600, col::RED);
            lv_obj_update_layout(ll);
            lv_obj_set_pos(ll, bx + 20 + (int)(bw * lf) - lv_obj_get_width(ll) + 8, 62);
            lv_obj_t* hl = label(ab, (model::fmt_num(a.rangeHi, 1) + " MPa").c_str(), 12, 400, col::SEC);
            lv_obj_update_layout(hl);
            lv_obj_set_pos(hl, bx + 20 + bw - lv_obj_get_width(hl), 62);
            label_at(ab, bx + 20, 84, a.did.c_str(), 13, 400, col::SEC);
            y += 124;
        } else if (r.abort) {
            // 手动中止 / 程序故障（不是超温超压联锁）：没有量值和上限，不画刻度条
            const auto& a = *r.abort;
            lv_obj_t* ab = box(rc, 20, y, rw - 40, 84, 0xfffafa, 12);
            ring(ab, 0xf0c9c6, 2);
            mini_vessel(ab, 18, 16, 18, chan_color(a.ch));
            label_at(ab, 42, 14, (trf("%d 号", a.ch) + " · " + tr(a.title.c_str())).c_str(), 15.5, 700, col::RED);
            label_at(ab, 42, 46, (model::fmt_clock(a.at) + " · " + a.did).c_str(), 13, 400, col::SEC);
            y += 96;
        } else {
            int kw = (rw - 40 - 3 * 12) / 4;
            double avg = r.avgMmol, rsd = r.rsdPct;
            if (r.result == "running" && m.x.trend) {
                double sum = 0;
                int n = 0;
                for (int c : r.channels) {
                    const auto& v = m.x.trend->gas[(size_t)c - 1];
                    if (!v.empty() && !std::isnan(v.back())) { sum += v.back(); ++n; }
                }
                avg = n ? sum / n : 0;
            }
            kpi(rc, 20, y, kw, &ic::drop22, tr("平均吸气量"), model::fmt_num(avg, 2), "mmol");
            kpi(rc, 20 + (kw + 12), y, kw, &ic::spread22, tr("各路偏差"), model::fmt_num(rsd, 1), "% RSD");
            kpi(rc, 20 + 2 * (kw + 12), y, kw, &ic::thermo22, tr("温度最大偏差"), model::fmt_num(r.tempDevC, 1), "°C");
            kpi(rc, 20 + 3 * (kw + 12), y, kw, &ic::gauge22, tr("反应压力"), model::fmt_num(r.pressMPa, 3), "MPa");
            y += 84;
        }
        // 图 + 每一路
        int tw = 300;
        int gw = rw - 40 - tw - 20;
        int gh = rh - y - 90;
        label_at(rc, 20, y, tr(r.abort ? "各路吸气量（到中止为止）" : "各路吸气量"), 15, 700, col::INK);
        TimeChart* tc = new TimeChart;
        lv_obj_add_event_cb(rc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
        tc->create(rc, 12, y + 28, gw + 8, gh - 28);
        gas_chart(*tc, r, r.result == "running" ? m.x.trend.get() : r.trend.get(), r.channels, now);
        int tx = 20 + gw + 20;
        label_at(rc, tx, y, tr("每一路"), 15, 700, col::INK);
        const char* H[3] = {"吸气量 mmol", "最快速率", "温度 °C"};
        int cx[3] = {tx + 138, tx + 218, tx + tw};
        for (int i = 0; i < 3; ++i) {
            lv_obj_t* l = label(rc, tr(H[i]), 12.5, 400, col::SEC);
            lv_obj_update_layout(l);
            lv_obj_set_pos(l, cx[i] - lv_obj_get_width(l), y + 30);
        }
        int ry = y + 56;
        for (int c : r.channels) {
            const model::RunChannelStat* st = nullptr;
            for (auto& x : r.perCh) if (x.ch == c) st = &x;
            if (st && st->flagged) box(rc, tx - 4, ry - 4, tw + 8, 36, 0xfdecec, 6);
            mini_vessel(rc, tx, ry + 2, 18, chan_color(c));
            label_at(rc, tx + 22, ry + 2, trf("%d 号", c).c_str(), 15, 400, col::SEC);
            std::string v[3];
            if (st) { v[0] = model::fmt_num(st->mmol, 2); v[1] = model::fmt_num(st->maxRate, 3); v[2] = model::fmt_num(st->tempC, 1); }
            else if (r.result == "running") {
                const auto& ch = m.ch(c);
                v[0] = model::fmt_mmol(ch.gasConsumed); v[1] = ch.gasRate ? model::fmt_num(*ch.gasRate, 3) : "—"; v[2] = model::fmt_temp(ch);
            } else { v[0] = v[1] = v[2] = "—"; }
            for (int i = 0; i < 3; ++i) {
                bool red = st && st->flagged;
                lv_obj_t* l = label(rc, v[i].c_str(), i == 0 ? 15.5 : 15, i == 0 ? 700 : 400, red ? col::RED : i == 0 ? col::DEEP : col::SEC);
                lv_obj_update_layout(l);
                lv_obj_set_pos(l, cx[i] - lv_obj_get_width(l), ry + 1);
            }
            ry += 38;
            if (ry > y + gh) break;
        }
        // 按钮
        int by = rh - 66;
        if (r.result == "running") {
            lv_obj_t* b = blue_btn(rc, 20, by, tr("在主页看"), &ic::nav_overview);
            on_tap(b, [this] { app_->go(PageId::Home, 2); });
            return;
        }
        lv_obj_t* b1 = blue_btn(rc, 20, by, tr("在曲线页打开"), &ic::chart_line16);
        on_tap(b1, [this] { app_->go(PageId::Curve, 3); });
        lv_obj_update_layout(b1);
        int bx = 20 + lv_obj_get_width(b1) + 10;
        lv_obj_t* b2 = tool_btn(rc, bx, by, tr("导出到 U 盘"), &ic::download16, col::INK, 48, 15.5);
        on_tap(b2, [this] { records_open_export(*app_); });
        lv_obj_update_layout(b2);
        bx += lv_obj_get_width(b2) + 10;
        lv_obj_t* b3 = tool_btn(rc, bx, by, tr("把这次的程序存进程序库"), &ic::save17, col::INK, 48, 15.5);
        model::ProgramDoc pd = r.program;
        std::string nm2 = r.programName;
        on_tap(b3, [this, pd, nm2] {
            model::ProgramDoc d = pd;
            d.id.clear(); d.revision = 0; d.builtIn = false;
            d.name = nm2 + tr(" · ") + md(app_->m().snap.ts);
            app_->act().lib_save(false, d);
            toast(trf("已存进程序库：%s", d.name.c_str()));
        });
        if (r.result == "abort") {
            lv_obj_update_layout(b3);
            bx += lv_obj_get_width(b3) + 10;
            lv_obj_t* b4 = tool_btn(rc, bx, by, tr("用同样设置再跑一次"), &ic::rerun18, col::INK, 48, 15.5);
            std::vector<int> chs = r.channels;
            on_tap(b4, [this, pd, chs] {
                int n = 0;
                for (int c : chs) {
                    const auto& ch = app_->m().ch(c);
                    if (!ch.installed || ch.programOwned) continue;
                    app_->act().put_program(c, pd);
                    app_->act().start_program(c);
                    ++n;
                }
                toast(n ? trf("%d 路已重新开始", n) : std::string(tr("这几路都在忙，没法再跑")), n > 0);
            });
        }
    }

    // ---------------------------------------------------------------- 每日数据文件
    void build_files(const Model& m) {
        int BH = H_ - 56;
        RecState& s = rs();
        if (s.day.empty()) s.day = m.x.days.size() > 1 ? m.x.days[1].day : m.x.days.empty() ? ymd(m.snap.ts) : m.x.days.front().day;
        int lw = 440;
        lv_obj_t* lc = card(body_, 12, 0, lw, BH - 12);
        // 月份
        std::time_t tt = (std::time_t)(m.snap.ts / 1000);
        std::tm tm{};
        localtime_r(&tt, &tm);
        tm.tm_mday = 1;
        tm.tm_mon += s.month_ofs;
        tm.tm_hour = 12;
        std::mktime(&tm);
        int year = tm.tm_year + 1900, mon = tm.tm_mon + 1;
        int wd0 = (tm.tm_wday + 6) % 7;  // 周一 = 0
        std::tm t2 = tm;
        t2.tm_mon += 1;
        t2.tm_mday = 0;
        std::mktime(&t2);
        int ndays = t2.tm_mday;
        lv_obj_t* pv = box(lc, 16, 14, 40, 40, col::TILE, 8);
        ring(pv, col::RING, 2);
        lv_obj_center(icon(pv, &ic::chev_left18, col::SEC));
        on_tap(pv, [this] { rs().month_ofs--; refresh(); });
        lv_obj_t* nx = box(lc, lw - 56, 14, 40, 40, col::TILE, 8);
        ring(nx, col::RING, 2);
        lv_obj_center(icon(nx, &ic::chev_right18, s.month_ofs < 0 ? col::SEC : col::DIS));
        if (s.month_ofs < 0) on_tap(nx, [this] { rs().month_ofs++; refresh(); });
        lv_obj_t* tl = label(lc, trf("%d 年 %d 月", year, mon).c_str(), 18, 700, col::INK);
        lv_obj_align(tl, LV_ALIGN_TOP_MID, 0, 23);
        const char* wk[7] = {"一", "二", "三", "四", "五", "六", "日"};
        int cell = 52, gap = 7, gx = (lw - 7 * cell - 6 * gap) / 2;
        for (int i = 0; i < 7; ++i) {
            lv_obj_t* l = label(lc, tr(wk[i]), 13, 400, col::MUT);
            lv_obj_update_layout(l);
            lv_obj_set_pos(l, gx + i * (cell + gap) + (cell - lv_obj_get_width(l)) / 2, 68);
        }
        int64_t maxb = 1;
        for (auto& d : m.x.days) maxb = std::max(maxb, d.bytes);
        char cur_month[8];
        std::snprintf(cur_month, sizeof cur_month, "%04d%02d", year, mon);
        int days_with = 0;
        int64_t month_bytes = 0;
        std::string today = ymd(m.snap.ts);
        for (int d = 1; d <= ndays; ++d) {
            int idx = wd0 + d - 1;
            int x = gx + (idx % 7) * (cell + gap), y = 96 + (idx / 7) * (cell + gap);
            char key[16];
            std::snprintf(key, sizeof key, "%s%02d", cur_month, d);
            const model::DayInfo* di = nullptr;
            for (auto& dd : m.x.days) if (dd.day == key) di = &dd;
            bool sel = s.day == key, istoday = today == key;
            if (di) { ++days_with; month_bytes += di->bytes; }
            lv_obj_t* c = box(lc, x, y, cell, cell, sel ? col::BLUE : di ? 0xffffff : 0, 9, !(sel || di));
            if (di && !sel) ring(c, istoday ? col::ORANGE : 0xd5dde6, istoday ? 2 : 1);
            if (sel && istoday) ring(c, col::ORANGE, 2);
            lv_obj_t* l = label(c, std::to_string(d).c_str(), 17, di ? 600 : 400, sel ? 0xffffff : di ? col::INK : col::DIS);
            lv_obj_align(l, LV_ALIGN_CENTER, 0, di ? -5 : 0);
            if (di) {
                int bw = std::max(8, (int)(34 * double(di->bytes) / maxb));
                box(c, 9, cell - 12, 34, 4, sel ? 0x5b88bd : 0xe6ebf1, 2);
                box(c, 9, cell - 12, bw, 4, sel ? 0xffffff : 0x7fa6d6, 2);
                std::string k = key;
                on_tap(c, [this, k] { rs().day = k; refresh(); });
            }
        }
        int ly = 96 + 6 * (cell + gap) + 4;
        box(lc, 16, ly + 7, 16, 4, 0x7fa6d6, 2);
        label_at(lc, 38, ly, tr("条长 = 当天数据量"), 13, 400, col::SEC);
        lv_obj_t* tb = box(lc, 180, ly + 2, 14, 14, 0xffffff, 3);
        ring(tb, col::ORANGE, 2);
        label_at(lc, 200, ly, tr("今天"), 13, 400, col::SEC);
        lv_obj_t* fb = box(lc, 16, BH - 12 - 56, lw - 32, 40, 0xf3f6f9, 8);
        lv_obj_align(label(fb, trf("本月 %d 天有数据 · 共 %s · 保留最近 %d 天", days_with, fmt_bytes(month_bytes).c_str(),
                                   (int)m.x.prefs.retentionDays).c_str(), 13.5, 400, col::SEC),
                     LV_ALIGN_LEFT_MID, 12, 0);
        // ---- 右：这一天 ----
        int rx = 12 + lw + 12, rw = W_ - rx - 12, rh = BH - 12;
        lv_obj_t* rc = card(body_, rx, 0, rw, rh);
        const model::DayInfo* di = nullptr;
        for (auto& d : m.x.days) if (d.day == s.day) di = &d;
        int64_t d0 = day_start(s.day);
        std::time_t dt = (std::time_t)(d0 / 1000);
        std::tm dtm{};
        localtime_r(&dt, &dtm);
        const char* wn[7] = {"周日", "周一", "周二", "周三", "周四", "周五", "周六"};
        std::string head = md(d0) + " " + tr(wn[dtm.tm_wday]);
        std::string sub = di ? trf("%d 个文件 · %s", (int)di->files.size(), fmt_bytes(di->bytes).c_str()) : std::string(tr("这一天没有数据"));
        card_head(rc, 20, 20, head.c_str(), sub.c_str(), 22);
        if (!di) return;
        int y = 62;
        if (!di->runs.empty()) {
            label_at(rc, 20, y, tr("这一天跑过的程序"), 13.5, 700, col::SEC);
            y += 24;
            for (auto& r : di->runs) {
                lv_obj_t* rb = box(rc, 20, y, rw - 40, 36, 0xf3f6f9, 8);
                bool ab = r.result == "abort";
                icon(rb, ab ? &ic::x_circle16 : &ic::check_circle16, ab ? col::RED : 0x16a05a, 12, 10);
                lv_obj_t* n = label_at(rb, 38, 8, r.name.c_str(), 15, 700, col::INK);
                lv_obj_update_layout(n);
                label_at(rb, 38 + lv_obj_get_width(n) + 10, 9,
                         (hm(r.start) + " – " + (r.end ? hm(r.end) : std::string("…")) + " · " + chan_list(r.channels)).c_str(), 13.5, 400, col::SEC);
                lv_obj_t* go = label(rb, tr("看记录 ›"), 13.5, 600, col::BLUE);
                lv_obj_align(go, LV_ALIGN_RIGHT_MID, -12, 0);
                std::string id = r.runId;
                on_tap(rb, [this, id] { records_select() = id; tab_ = 0; tabs_.select(0); refresh(); });
                y += 42;
            }
            y += 10;
        }
        label_at(rc, 20, y, tr("文件"), 13.5, 700, col::SEC);
        y += 24;
        for (auto& f : di->files) {
            bool ev = f.type == "events";
            file_icon(rc, 20, y + 6, ev ? "JSONL" : "CSV");
            lv_obj_t* n = label_at(rc, 62, y + 6, f.name.c_str(), 15.5, 500, col::INK);
            lv_obj_update_layout(n);
            lv_obj_t* tg = pill(rc, tr(ev ? "事件" : "读数"), ev ? 0xb36b00 : col::BLUE, ev ? 0xfdf0dc : 0xe3ecf8, 12);
            lv_obj_set_pos(tg, 62 + lv_obj_get_width(n) + 8, y + 5);
            label_at(rc, 62, y + 31, tr(f.desc.c_str()), 13, 400, col::SEC);
            lv_obj_t* exb = tool_btn(rc, 0, y + 8, tr("导出"), &ic::download16, col::INK, 38, 14.5);
            lv_obj_update_layout(exb);
            int ex = rw - 20 - lv_obj_get_width(exb);
            lv_obj_set_x(exb, ex);
            std::string fname = f.name;
            on_tap(exb, [this, fname] {
                model::ExportReq q;
                q.file = fname;
                app_->act().export_usb(q);
                toast(trf("已导出 %s", fname.c_str()));
            });
            lv_obj_t* sz = label(rc, fmt_bytes(f.bytes).c_str(), 15.5, 700, col::INK);
            lv_obj_update_layout(sz);
            lv_obj_set_pos(sz, ex - 16 - lv_obj_get_width(sz), y + 17);
            std::string rng = ev ? trf("全天 · %d 条", f.lines) : (hm(f.from.value_or(d0)) + " – " + (f.to && *f.to >= d0 + 86399000LL ? "23:59" : hm(f.to.value_or(d0))));
            lv_obj_t* rl = label(rc, rng.c_str(), 13.5, 400, col::SEC);
            lv_obj_update_layout(rl);
            lv_obj_set_pos(rl, ex - 16 - 90 - lv_obj_get_width(rl), y + 18);
            box(rc, 20, y + 58, rw - 40, 1, col::DIV);
            y += 62;
        }
        label_at(rc, 20, rh - 46, tr("原始数据，Excel 能直接打开；按运行整理好的在「运行记录」里"), 13.5, 400, col::SEC);
        lv_obj_t* all = blue_btn(rc, 0, rh - 66, tr("这一天全部导出"), &ic::download16);
        lv_obj_update_layout(all);
        lv_obj_set_x(all, rw - 20 - lv_obj_get_width(all));
        std::string day = s.day;
        on_tap(all, [this, day] {
            model::ExportReq q;
            q.day = day;
            q.kinds = {"telemetry", "events"};
            app_->act().export_usb(q);
            toast(tr("已开始导出这一天"));
        });
        lv_obj_t* rge = tool_btn(rc, 0, rh - 66, tr("按时间段导出…"), &ic::cal_clock22, col::INK, 48, 15.5);
        lv_obj_update_layout(rge);
        lv_obj_set_x(rge, lv_obj_get_x(all) - 10 - lv_obj_get_width(rge));
        on_tap(rge, [this] { records_open_range_export(*app_); });
    }

    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr; lv_obj_t* body_ = nullptr; lv_obj_t* usb_ = nullptr;
    int W_ = 1280, H_ = 740, tab_ = 0, ver_ = 0;
    std::string sig_;
    TabCard tabs_;
};

std::unique_ptr<Page> make_records_page(int tab) { return std::make_unique<RecordsPage>(tab); }

}  // namespace reactor::hmi
