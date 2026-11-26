// 曲线页（Curve2-Gas / Curve2-Temp / Curve2-Press / Curve2-Three）：
// 工具条（自适应、导出、通道开关、横轴 时钟 / 运行时间、纵轴 全部 / 设定值附近）+ 自绘曲线 + 读数卡片。
// 不做双纵轴：「三项对照」是三块图叠起来共用一条时间轴。数据来自 Model::x.trend（每 10 s 一点，NaN = 没数据）。
#include "common2.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>

namespace reactor::hmi {

using namespace kit;
using model::Model;

namespace {

struct CurveState {
    std::array<bool, 8> hidden{};
    bool runtime_axis = false;
    std::array<int, 4> near{0, 1, 1, 0};   // 纵轴：0 全部 / 1 设定值附近（每个页签各记一份）
    std::optional<double> cursor;          // 读数时刻（UTC 毫秒）；空 = 「现在」前 18 min
    int window_min = 60;                   // 「现在」往前看多久；往后留 30 min 空白
};
CurveState& cs() {
    static CurveState s;
    return s;
}

const std::vector<float>& series_of(const model::Trend& t, int q, int ch) {
    return q == 0 ? t.gas[(size_t)ch - 1] : q == 1 ? t.temp[(size_t)ch - 1] : t.press[(size_t)ch - 1];
}

double value_at(const model::Trend& t, int q, int ch, double at) {
    long i = std::lround((at - (double)t.t0) / (double)t.stepMs);
    const auto& v = series_of(t, q, ch);
    if (i < 0 || i >= (long)v.size()) return NAN;
    return v[(size_t)i];
}

bool has_data(const model::Trend& t, int q, int ch, double from) {
    const auto& v = series_of(t, q, ch);
    for (size_t i = 0; i < v.size(); ++i)
        if (t.t_at((int)i) >= from && !std::isnan(v[i])) return true;
    return false;
}

}  // namespace

class CurvePage : public Page {
public:
    explicit CurvePage(int tab) : tab_(std::max(0, std::min(3, tab))) {}
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        H_ = app.h() - 60;
        std::vector<TabDef> tabs = {{&ic::drop18, "气体消耗", "", TabDef::None}, {&ic::thermo18, "温度", "", TabDef::None}, {&ic::gauge18, "压力", "", TabDef::None}, {&ic::lines18, "三项对照", "", TabDef::None}};
        tabs_.create(root, 8, 4, W_ - 16, tabs, tab_);
        tabs_.on_select = [this](int i) { tab_ = i; sig_.clear(); if (app_) update(app_->m()); };
        body_ = box(root, 0, 0, W_, H_, 0, 0, true);
    }

    void update(const Model& m) override {
        CurveState& s = cs();
        char sig[160];
        std::snprintf(sig, sizeof sig, "%d/%lld/%d/%d/%d/%.0f/%d", tab_, (long long)(m.snap.ts / 5000), s.runtime_axis, s.near[(size_t)tab_],
                      s.window_min, s.cursor.value_or(0), ver_);
        std::string sg = sig;
        for (bool h : s.hidden) sg += h ? '1' : '0';
        if (sg == sig_) return;
        sig_ = sg;
        lv_obj_clean(body_);
        build_body(m);
    }

private:
    void refresh() { ++ver_; if (app_) update(app_->m()); }

    void build_body(const Model& m) {
        CurveState& s = cs();
        int cw = W_ - 24, chh = H_ - 56 - 12;
        lv_obj_t* c = card(body_, 12, 56, cw, chh);
        // ---- 工具条 ----
        lv_obj_t* fit = tool_btn(c, 16, 10, tr("自适应"), &ic::fit16);
        on_tap(fit, [this] { cs().cursor.reset(); cs().window_min = 60; cs().hidden = {}; refresh(); });
        lv_obj_update_layout(fit);
        int x = 16 + lv_obj_get_width(fit) + 10;
        lv_obj_t* ex = tool_btn(c, x, 10, tr("导出到 U 盘"), &ic::download16);
        lv_obj_update_layout(ex);
        x += lv_obj_get_width(ex) + 18;
        int64_t now = m.snap.ts;
        double x0 = (double)(now - s.window_min * 60000LL), x1 = (double)(now + 30 * 60000LL);
        on_tap(ex, [this, x0, now] {
            model::ExportReq r;
            r.from = (int64_t)x0; r.to = now; r.kinds = {"telemetry"}; r.stepSec = 15;
            app_->act().export_usb(r);
            toast(tr("已导出这一段到 U 盘"));
        });
        label_at(c, x, 19, tr("通道"), 14, 400, col::SEC);
        x += 38;
        for (int ch = 1; ch <= 8; ++ch) {
            bool inst = m.ch(ch).installed;
            lv_obj_t* chip = chan_chip(c, x, 13, ch, inst && !s.hidden[(size_t)ch - 1], !inst, 46, 34);
            if (inst) on_tap(chip, [this, ch] { cs().hidden[(size_t)ch - 1] = !cs().hidden[(size_t)ch - 1]; refresh(); });
            x += 52;
        }
        int rxp = cw - 16;
        if (tab_ != 3) {
            const lv_font_t* f = font(14.5, 600);
            int w = std::max(70, 27 + text_width(tr("全部"), f) + 12) + 6 + std::max(70, 27 + text_width(tr("设定值附近"), f) + 12);
            rxp -= w;
            LampGroup* ny = new LampGroup;
            lv_obj_add_event_cb(c, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, ny);
            ny->create(c, rxp, 10, {tr("全部"), tr("设定值附近")}, {70, 70}, s.near[(size_t)tab_], 40, 14.5, 6);
            int t = tab_;
            ny->on_change = [this, t](int i) { cs().near[(size_t)t] = i; refresh(); };
            lv_obj_t* l = label(c, tr("纵轴"), 14, 400, col::SEC);
            lv_obj_update_layout(l);
            rxp -= lv_obj_get_width(l) + 10;
            lv_obj_set_pos(l, rxp, 20);
            rxp -= 14;
            box(c, rxp, 14, 1, 32, col::DIV);
            rxp -= 14;
        }
        {
            const lv_font_t* f = font(14.5, 600);
            int w = std::max(60, 27 + text_width(tr("时钟"), f) + 12) + 6 + std::max(80, 27 + text_width(tr("运行时间"), f) + 12);
            rxp -= w;
            LampGroup* ax = new LampGroup;
            lv_obj_add_event_cb(c, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, ax);
            ax->create(c, rxp, 10, {tr("时钟"), tr("运行时间")}, {60, 80}, s.runtime_axis ? 1 : 0, 40, 14.5, 6);
            ax->on_change = [this](int i) { cs().runtime_axis = i == 1; refresh(); };
            lv_obj_t* l = label(c, tr("横轴"), 14, 400, col::SEC);
            lv_obj_update_layout(l);
            rxp -= lv_obj_get_width(l) + 10;
            lv_obj_set_pos(l, rxp, 20);
        }
        // ---- 数据 ----
        const model::Trend* tr_ = m.x.trend.get();
        if (!tr_) {
            lv_obj_center(label(c, tr("还没有数据"), 16, 400, col::MUT));
            return;
        }
        std::vector<int> shown;
        for (int ch = 1; ch <= 8; ++ch)
            if (m.ch(ch).installed && !s.hidden[(size_t)ch - 1]) shown.push_back(ch);
        // 默认读数时刻 = 「现在」前 18 min；数据还不到 18 min（刚开机）时放在最早有数据的那一刻之后，不然读数框一排「—」
        double first = (double)now;
        for (int i = 0; i < tr_->size() && first == (double)now; ++i)
            for (int ch : shown)
                if (!std::isnan(tr_->temp[(size_t)ch - 1][(size_t)i])) { first = (double)tr_->t_at(i); break; }
        double cur = s.cursor.value_or(std::max((double)(now - 18 * 60000LL), std::min((double)now, first + 60000.0)));
        cur = std::max(x0, std::min((double)now, cur));
        // 运行时间轴：从显示中最早开始的程序算起
        int64_t run0 = now;
        for (int ch : shown) {
            const auto& p = m.ch(ch).program;
            if (p && p->startedAt) run0 = std::min(run0, *p->startedAt);
        }
        ChartConfig cfg;
        cfg.x0 = x0; cfg.x1 = x1;
        cfg.xticks = clock_ticks((int64_t)x0, (int64_t)x1, 15);
        if (s.runtime_axis) cfg.x_label = [run0](double v) {
            int64_t sec = std::max<int64_t>(0, ((int64_t)v - run0) / 1000);
            char b[16];
            std::snprintf(b, sizeof b, "%lld:%02lld", (long long)(sec / 3600), (long long)(sec / 60 % 60));
            return std::string(b);
        };
        else cfg.x_label = [](double v) { return hm((int64_t)v); };
        cfg.now = (double)now; cfg.shade_future = true; cfg.now_color = 0x4a5563;
        cfg.cursor = cur; cfg.cursor_text = hm((int64_t)cur);
        cfg.left = 56; cfg.right = 16; cfg.top = tab_ == 3 ? 10 : 22; cfg.bottom = 30; cfg.gap = 18;
        cfg.ytick_px = 13;
        std::vector<int> qs = tab_ == 3 ? std::vector<int>{0, 1, 2} : std::vector<int>{tab_ == 0 ? 0 : tab_ == 1 ? 1 : 2};
        std::vector<ChartPanel> panels;
        std::string chip_text;
        uint32_t chip_fg = 0x1d3557, chip_bg = 0xe8f0fb;
        for (int q : qs) {
            ChartPanel p;
            static const char* T[3] = {"气体消耗", "温度", "压力"};
            static const char* U[3] = {"mmol", "°C", "MPa"};
            if (tab_ == 3) { p.title = tr(T[q]); p.unit = U[q]; }
            else p.unit_top = U[q] == std::string("°C") ? "°C" : U[q];
            double lo = 1e9, hi = -1e9;
            int nd = 0;
            for (int ch : shown) {
                if (!has_data(*tr_, q, ch, x0)) continue;
                ChartSeries se;
                se.x0 = (double)tr_->t0; se.dx = (double)tr_->stepMs;
                se.y = series_of(*tr_, q, ch);
                se.color = chan_color(ch);
                se.width = 2;
                for (size_t i = 0; i < se.y.size(); ++i)
                    if ((double)tr_->t_at((int)i) >= x0 && !std::isnan(se.y[i])) { lo = std::min(lo, (double)se.y[i]); hi = std::max(hi, (double)se.y[i]); }
                p.series.push_back(se);
                ++nd;
            }
            if (lo > hi) { lo = 0; hi = 1; }
            bool near = tab_ != 3 && s.near[(size_t)tab_] == 1 && q != 0;
            if (near) {
                // 设定值附近：中心 = 显示中各路最常见的设定值，带宽 = 到位范围 / 压力偏离阈值
                std::map<long, int> votes;
                for (int ch : shown) {
                    double sp = q == 1 ? m.ch(ch).tempSpC : psi_to_mpa(m.ch(ch).pressureSpPsi);
                    if (sp > 0) votes[std::lround(sp * 1000)]++;
                }
                double center = q == 1 ? 80 : 2.0;
                int best = 0;
                for (auto& v : votes) if (v.second > best) { best = v.second; center = v.first / 1000.0; }
                double band = q == 1 ? m.chanset[0].band : psi_to_mpa(m.settings.alarms.pressDeviationPsi);
                if (q == 2 && band <= 0) band = 0.05;
                p.band = std::make_pair(center - band, center + band);
                p.band_center = center;
                p.band_hi_text = q == 1 ? "+" + model::fmt_num(band, 0) + " °C" : "+" + model::fmt_num(band, 3);
                p.band_lo_text = q == 1 ? "−" + model::fmt_num(band, 0) + " °C" : "−" + model::fmt_num(band, 3);
                double span = band * 1.5;
                p.ymin = center - span; p.ymax = center + span;
                double step = 0;
                p.yticks = nice_ticks(p.ymin, p.ymax, 6, &step);
                p.ydec = step < 0.01 ? 3 : step < 0.1 ? 2 : step < 1 ? 1 : 0;
                // 都在带里多久了
                int inn = 0;
                int64_t since = now;
                for (int ch : shown) {
                    const auto& v = series_of(*tr_, q, ch);
                    if (v.empty() || std::isnan(v.back())) continue;
                    bool in = std::fabs(v.back() - center) <= band;
                    inn += in;
                    if (in) {
                        for (size_t i = v.size(); i-- > 0;) {
                            if (std::isnan(v[i]) || std::fabs(v[i] - center) > band) { since = std::min(since, tr_->t_at((int)i + 1)); break; }
                        }
                    }
                }
                int total = 0;
                for (int ch : shown) total += has_data(*tr_, q, ch, x0) && !std::isnan(series_of(*tr_, q, ch).back());
                std::string ctr = q == 1 ? model::fmt_num(center, 0) + " ± " + model::fmt_num(band, 0) + " °C"
                                         : model::fmt_num(center, 3) + " ± " + model::fmt_num(band, 3) + " MPa";
                if (inn == total && total > 0) {
                    chip_text = trf("%d 路都在 %s 内 · 已 %s", total, ctr.c_str(), model::fmt_hmin((now - since) / 1000).c_str());
                    chip_fg = 0xffffff; chip_bg = 0x14995a;
                } else {
                    chip_text = trf("%d 路里 %d 路在 %s 内", total, inn, ctr.c_str());
                    chip_fg = 0xffffff; chip_bg = col::AMBER;
                }
            } else {
                double step = 0;
                double pad = (hi - lo) * 0.1 + (q == 2 ? 0.01 : q == 1 ? 1 : 0.1);
                double ylo = q == 0 ? 0 : lo - pad, yhi = hi + pad;
                p.yticks = nice_ticks(ylo, yhi, tab_ == 3 ? 5 : 10, &step);
                p.ymin = p.yticks.empty() ? ylo : std::min(ylo, p.yticks.front());
                p.ymax = p.yticks.empty() ? yhi : std::max(yhi, p.yticks.back());
                p.ydec = step < 0.01 ? 3 : step < 0.1 ? 2 : step < 1 ? 1 : 0;
                if (q == 0 && tab_ == 0) {
                    // 平均吸气速率（最近 10 min）和各路偏差
                    double sum = 0, sq = 0, rs = 0;
                    int n = 0;
                    for (int ch : shown) {
                        const auto& v = series_of(*tr_, 0, ch);
                        if (v.size() < 2 || std::isnan(v.back())) continue;
                        // 最近 10 min 里最早有读数的那一点（刚开始计量不到 10 min 时按实际时长算；原来直接跳过 → 显示 0）
                        size_t k0 = v.size() > 61 ? v.size() - 61 : 0;
                        while (k0 + 1 < v.size() && std::isnan(v[k0])) ++k0;
                        double a = v.back(), b = v[k0];
                        double mins = (double)(v.size() - 1 - k0) * tr_->stepMs / 60000.0;
                        if (std::isnan(b) || mins <= 0) continue;
                        rs += (a - b) / mins;
                        sum += a; sq += a * a; ++n;
                    }
                    double mean = n ? sum / n : 0, rsd = n && mean > 0 ? std::sqrt(std::max(0.0, sq / n - mean * mean)) / mean * 100 : 0;
                    chip_text = trf("显示中的 %d 路 · 平均吸气速率 %s mmol/min · 各路偏差 RSD %s %%", nd,
                                    model::fmt_num(n ? rs / n : 0, 3).c_str(), model::fmt_num(rsd, 1).c_str());
                }
            }
            panels.push_back(p);
        }
        TimeChart* tc = new TimeChart;
        lv_obj_add_event_cb(c, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
        int gy = 60;
        tc->create(c, 0, gy, cw, chh - gy - 4);
        tc->set(cfg, panels);
        tc->on_press = [this](double xv) {
            cs().cursor = xv;
            refresh();
        };
        if (!chip_text.empty()) {
            lv_area_t a = tc->panel_area(0);
            lv_obj_t* chip = pill(c, chip_text.c_str(), chip_fg, chip_bg, 13.5, nullptr, false, 600);
            lv_obj_set_style_radius(chip, 6, 0);
            lv_obj_set_pos(chip, a.x1 + 10, gy + a.y1 + 10);
        }
        // ---- 读数卡片 ----
        if (!shown.empty()) {
            int cx = tc->x_to_px(cur);
            int pw = 300, rows = (int)shown.size();
            int ph = 50 + rows * 30 + 8;
            int px = cx - pw - 26 > 60 ? cx - pw - 26 : cx + 26;
            lv_area_t a0 = tc->panel_area(0);
            lv_obj_t* pop = popover(c, px, gy + a0.y1 + 50, pw, ph, 14);
            lv_obj_remove_flag(pop, LV_OBJ_FLAG_CLICKABLE);
            label_at(pop, 14, 14, hm((int64_t)cur).c_str(), 15, 700, col::INK);
            const char* H[3] = {"气体 mmol", "温度 °C", "压力 MPa (读数表头)"};
            int colx[3] = {150, 214, 284};
            int hq = tab_ == 3 ? -1 : (tab_ == 0 ? 0 : tab_ == 1 ? 1 : 2);
            for (int q = 0; q < 3; ++q) {
                lv_obj_t* l = label(pop, trc(H[q]), 12.5, q == hq ? 700 : 400, q == hq ? col::BLUE : col::MUT);
                lv_obj_update_layout(l);
                lv_obj_set_pos(l, colx[q] - lv_obj_get_width(l), 16);
            }
            int y = 44;
            for (int ch : shown) {
                mini_vessel(pop, 14, y + 2, 18, chan_color(ch));
                label_at(pop, 36, y + 2, trf("%d 号", ch).c_str(), 14.5, 400, col::SEC);
                for (int q = 0; q < 3; ++q) {
                    double v = value_at(*tr_, q, ch, cur);
                    std::string t = std::isnan(v) ? std::string("—") : model::fmt_num(v, q == 0 ? 2 : q == 1 ? 1 : 3);
                    lv_obj_t* l = label(pop, t.c_str(), 15, q == hq ? 700 : 400, q == hq ? col::INK : col::SEC);
                    lv_obj_update_layout(l);
                    lv_obj_set_pos(l, colx[q] - lv_obj_get_width(l), y + 1);
                }
                y += 30;
            }
        }
    }

    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr; lv_obj_t* body_ = nullptr;
    int W_ = 1280, H_ = 740, tab_ = 0, ver_ = 0;
    std::string sig_;
    TabCard tabs_;
};

std::unique_ptr<Page> make_curve_page(int tab) { return std::make_unique<CurvePage>(tab); }

void reset_curve_state() { cs() = CurveState{}; }

}  // namespace reactor::hmi
