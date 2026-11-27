#include "chart.hpp"

#include "draw.hpp"
#include "theme.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <cstdint>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi::kit {

struct TimeChart::State {
    ChartConfig cfg;
    std::vector<ChartPanel> panels;
    int w = 0, h = 0;
    // 每块的 y 范围（相对控件）
    std::vector<std::pair<int, int>> py;
    void layout() {
        py.clear();
        int n = (int)panels.size();
        if (!n) return;
        int tw = 0;
        for (auto& p : panels) tw += std::max(1, p.weight);
        int avail = h - cfg.top - cfg.bottom - cfg.gap * (n - 1);
        int y = cfg.top;
        for (int i = 0; i < n; ++i) {
            int ph = i == n - 1 ? (h - cfg.bottom - y) : avail * std::max(1, panels[(size_t)i].weight) / tw;
            py.push_back({y, y + ph});
            y += ph + cfg.gap;
        }
    }
    float xp(double x) const {
        double f = (x - cfg.x0) / (cfg.x1 - cfg.x0 == 0 ? 1 : cfg.x1 - cfg.x0);
        return (float)(cfg.left + f * (w - cfg.left - cfg.right));
    }
    float yp(const ChartPanel& p, int i, double y) const {
        double f = (y - p.ymin) / (p.ymax - p.ymin == 0 ? 1 : p.ymax - p.ymin);
        return (float)(py[(size_t)i].second - f * (py[(size_t)i].second - py[(size_t)i].first));
    }
};

namespace {

std::string fmt_tick(double v, int dec, const std::string& suf) {
    char b[32];
    std::snprintf(b, sizeof b, "%.*f", dec, v);
    std::string s = b;
    if (s == "-0") s = "0";
    return s + suf;
}

// 升温曲线的颜色：冷蓝 → 黄 → 橙（Prog-Curve8）
uint32_t warm(double t) {
    struct S { double t; uint32_t c; };
    static const S st[] = {{25, 0x5b8fd6}, {60, 0xe8b830}, {100, 0xf0a030}, {150, 0xf07020}};
    if (t <= st[0].t) return st[0].c;
    for (int i = 1; i < 4; ++i)
        if (t <= st[i].t) {
            double f = (t - st[i - 1].t) / (st[i].t - st[i - 1].t);
            return mix(st[i].c, st[i - 1].c, f);
        }
    return st[3].c;
}

}  // namespace

lv_obj_t* TimeChart::create(lv_obj_t* parent, int x, int y, int w, int h) {
    root_ = box(parent, x, y, w, h, 0, 0, true);
    st_ = new State;
    st_->w = w; st_->h = h;
    on_draw(root_, draw_cb, st_);
    lv_obj_add_event_cb(root_, [](lv_event_t* e) { delete static_cast<State*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, st_);
    lv_obj_add_flag(root_, LV_OBJ_FLAG_CLICKABLE);
    auto press = [](lv_event_t* e) {
        auto* self = static_cast<TimeChart*>(lv_event_get_user_data(e));
        if (!self->on_press) return;
        lv_point_t p;
        lv_indev_get_point(lv_indev_active(), &p);
        lv_area_t a;
        lv_obj_get_coords(self->root_, &a);
        const State& s = *self->st_;
        double f = double(p.x - a.x1 - s.cfg.left) / double(s.w - s.cfg.left - s.cfg.right);
        f = std::max(0.0, std::min(1.0, f));
        self->on_press(s.cfg.x0 + f * (s.cfg.x1 - s.cfg.x0));
    };
    lv_obj_add_event_cb(root_, press, LV_EVENT_PRESSED, this);
    lv_obj_add_event_cb(root_, press, LV_EVENT_PRESSING, this);
    auto release = [](lv_event_t* e) {
        auto* self = static_cast<TimeChart*>(lv_event_get_user_data(e));
        if (self->on_release) self->on_release();
    };
    lv_obj_add_event_cb(root_, release, LV_EVENT_RELEASED, this);
    lv_obj_add_event_cb(root_, release, LV_EVENT_PRESS_LOST, this);
    // 横向拖读数时别把外面的容器带着滚走（滚动一开始，这边就收到 PRESS_LOST，读数线不再跟手）
    lv_obj_remove_flag(root_, LV_OBJ_FLAG_SCROLL_CHAIN_HOR);
    lv_obj_remove_flag(root_, LV_OBJ_FLAG_SCROLL_CHAIN_VER);
    return root_;
}

void TimeChart::set(const ChartConfig& cfg, const std::vector<ChartPanel>& panels) {
    st_->cfg = cfg;
    st_->panels = panels;
    lv_obj_update_layout(root_);
    if (lv_obj_get_width(root_) > 0) {
        st_->w = lv_obj_get_width(root_);
        st_->h = lv_obj_get_height(root_);
    }
    st_->layout();
    lv_obj_invalidate(root_);
}

void TimeChart::set_cursor(std::optional<double> x, const std::string& text) {
    st_->cfg.cursor = x;
    st_->cfg.cursor_text = text;
    lv_obj_invalidate(root_);
}

lv_area_t TimeChart::panel_area(int i) const {
    lv_area_t a{};
    if (i < 0 || i >= (int)st_->py.size()) return a;
    a.x1 = st_->cfg.left; a.x2 = st_->w - st_->cfg.right;
    a.y1 = st_->py[(size_t)i].first; a.y2 = st_->py[(size_t)i].second;
    return a;
}

int TimeChart::x_to_px(double x) const { return (int)std::lround(st_->xp(x)); }

void TimeChart::draw_cb(lv_obj_t*, lv_layer_t* L, const lv_area_t& o, void* user) {
    State& s = *static_cast<State*>(user);
    const ChartConfig& c = s.cfg;
    if (s.panels.empty()) return;
    const float X0 = (float)c.left, X1 = (float)(s.w - c.right);
    const lv_font_t* ft = font(c.ytick_px, 400);
    const float nowx = c.now ? s.xp(*c.now) : X1;
    for (size_t pi = 0; pi < s.panels.size(); ++pi) {
        const ChartPanel& p = s.panels[pi];
        const float Y0 = (float)s.py[pi].first, Y1 = (float)s.py[pi].second;
        draw_rect(L, o, X0, Y0, X1 - X0, Y1 - Y0, 0xffffff, LV_OPA_COVER);
        if (c.now && (c.shade_future || c.grey_future) && nowx < X1)
            draw_rect(L, o, nowx, Y0, X1 - nowx, Y1 - Y0, c.grey_future ? 0xf1f3f6 : 0xf2f5fa, LV_OPA_COVER);
        if (c.highlight) {
            float a = std::max(X0, s.xp(c.highlight->first)), b = std::min(X1, s.xp(c.highlight->second));
            draw_rect(L, o, a, Y0, b - a, Y1 - Y0, 0xe3ecf8, LV_OPA_COVER);
        }
        if (p.band) {
            float ya = s.yp(p, (int)pi, std::min(p.ymax, p.band->second)), yb = s.yp(p, (int)pi, std::max(p.ymin, p.band->first));
            draw_rect(L, o, X0, ya, X1 - X0, yb - ya, 0x14995a, 26);
        }
        if (p.hatch) {
            float a = std::max(X0, s.xp(p.hatch->first)), b = std::min(X1, s.xp(p.hatch->second));
            draw_rect(L, o, a, Y0, b - a, Y1 - Y0, 0xf6f8fa, LV_OPA_COVER);
            draw_hatch(L, o, a, Y0, b - a, Y1 - Y0, 0xdde3ea, LV_OPA_COVER, 9, 1);
        }
        // 网格
        if (c.ygrid)
            for (double t : p.yticks) {
                float y = s.yp(p, (int)pi, t);
                if (y < Y0 - 0.5f || y > Y1 + 0.5f) continue;
                draw_hline(L, o, X0, X1, y, 0xe8ecf1, 1);
            }
        if (c.xgrid)
            for (double t : c.xticks) {
                float x = s.xp(t);
                if (x < X0 || x > X1) continue;
                draw_vline(L, o, x, Y0, Y1, 0xeef1f5, 1);
            }
        for (double v : c.vlines) {
            float x = s.xp(v);
            if (x >= X0 && x <= X1) draw_vline(L, o, x, Y0, Y1, 0xd6dde6, 1);
        }
        if (p.band_center) draw_hline(L, o, X0, X1, s.yp(p, (int)pi, *p.band_center), 0x8792a0, 1, 5, 4);
        if (p.hatch && !p.hatch_text.empty()) {
            float a = std::max(X0, s.xp(p.hatch->first)), b = std::min(X1, s.xp(p.hatch->second));
            draw_text(L, o, (int)((a + b) / 2), (int)((Y0 + Y1) / 2 - 9), p.hatch_text.c_str(), font(13, 400), col::MUT,
                      LV_TEXT_ALIGN_CENTER, (int)(b - a));
        }
        // 曲线（裁在这一块里面）
        lv_area_t clip_old = L->_clip_area;
        lv_area_t clip;
        clip.x1 = o.x1 + (int)X0; clip.x2 = o.x1 + (int)X1;
        clip.y1 = o.y1 + (int)Y0 - 2; clip.y2 = o.y1 + (int)Y1 + 2;
        clip = scale::S_area(clip);  // layer 的裁剪区是屏幕像素
        lv_area_t inter;
        inter.x1 = std::max(clip_old.x1, clip.x1); inter.x2 = std::min(clip_old.x2, clip.x2);
        inter.y1 = std::max(clip_old.y1, clip.y1); inter.y2 = std::min(clip_old.y2, clip.y2);
        if (inter.x1 <= inter.x2 && inter.y1 <= inter.y2) {
            L->_clip_area = inter;
            for (const ChartSeries& se : p.series) {
                std::vector<Pt> pts;
                auto flush = [&] {
                    if (pts.size() >= 2) {
                        if (se.fill) draw_fill_under(L, o, pts, Y1, se.color, se.fill_opa);
                        if (se.gradient_by_value) {
                            for (size_t k = 1; k < pts.size(); ++k) {
                                double yv = p.ymin + (Y1 - (pts[k].y + pts[k - 1].y) / 2) / (Y1 - Y0) * (p.ymax - p.ymin);
                                draw_polyline(L, o, {pts[k - 1], pts[k]}, warm(yv), se.width, se.opa);
                            }
                        } else {
                            draw_polyline(L, o, pts, se.color, se.width, se.opa, se.dash, se.gap);
                        }
                    }
                    pts.clear();
                };
                size_t n = se.y.size();
                // 断线门限：零星缺的点（取数时刻正好落在桶边上、某一帧没读到）连过去，只有真的缺一大段才断开
                double gap = se.max_gap > 0 ? se.max_gap : se.max_gap < 0 || !se.xs.empty() ? -1 : 3.0 * std::fabs(se.dx);
                double last_valid_x = 0;
                bool any_valid = false;
                // 太密时按像素抽稀（每像素保留最小和最大，噪声的样子不丢）
                float lastx = -1e9f;
                float mn = 0, mx = 0;
                bool have = false;
                for (size_t k = 0; k < n; ++k) {
                    double xv = se.xs.empty() ? se.x0 + se.dx * (double)k : se.xs[k];
                    float yv = se.y[k];
                    if (std::isnan(yv)) {
                        if (gap < 0) { flush(); have = false; }
                        continue;
                    }
                    if (gap >= 0 && any_valid && std::fabs(xv - last_valid_x) > gap * (1 + 1e-9)) { flush(); have = false; }
                    last_valid_x = xv;
                    any_valid = true;
                    float xpx = s.xp(xv), ypx = s.yp(p, (int)pi, yv);
                    if (have && se.xs.empty() && xpx - lastx < 1.0f && n > 600) {
                        mn = std::min(mn, ypx); mx = std::max(mx, ypx);
                        continue;
                    }
                    if (have && n > 600 && mx - mn > 1.5f) {
                        pts.push_back({lastx, mn});
                        pts.push_back({lastx, mx});
                    }
                    pts.push_back({xpx, ypx});
                    lastx = xpx; mn = mx = ypx; have = true;
                }
                flush();
            }
            L->_clip_area = clip_old;
        }
        // 末端的点和文字
        for (const ChartSeries& se : p.series) {
            if (!se.end_dot && se.end_label.empty()) continue;
            for (size_t k = se.y.size(); k-- > 0;) {
                if (std::isnan(se.y[k])) continue;
                double xv = se.xs.empty() ? se.x0 + se.dx * (double)k : se.xs[k];
                float x = s.xp(xv), y = s.yp(p, (int)pi, se.y[k]);
                if (se.end_dot) draw_circle(L, o, x, y, 3.5f, se.color, 0xffffff, 0);
                if (!se.end_label.empty())
                    draw_text(L, o, (int)x + 8, (int)y - 9, se.end_label.c_str(), font(13, 700),
                              se.end_label_color ? se.end_label_color : se.color, LV_TEXT_ALIGN_LEFT, 120);
                break;
            }
        }
        if (p.band && !p.band_hi_text.empty()) {
            float x = std::min(X1 - 60, nowx + 8);
            draw_text(L, o, (int)x, (int)s.yp(p, (int)pi, p.band->second) - 18, p.band_hi_text.c_str(), font(12, 400), 0x14995a);
            draw_text(L, o, (int)x, (int)s.yp(p, (int)pi, p.band->first) + 4, p.band_lo_text.c_str(), font(12, 400), 0x14995a);
        }
        // 「现在」线、标记线、中止线
        if (c.now && nowx >= X0 && nowx <= X1) draw_vline(L, o, nowx, Y0, Y1, c.now_color, c.now_width);
        if (c.marker) {
            float x = s.xp(*c.marker);
            for (float y = Y0; y < Y1; y += 6) draw_vline(L, o, x, y, std::min(Y1, y + 3), 0x9aa5b2, 1);
            if (pi == 0 && !c.marker_text.empty()) draw_text(L, o, (int)x + 5, (int)Y0 + 3, c.marker_text.c_str(), font(12, 400), col::MUT);
        }
        if (c.stop_at) {
            float x = s.xp(*c.stop_at);
            draw_vline(L, o, x, Y0, Y1, col::RED, 2);
            if (pi == 0 && !c.stop_text.empty()) {
                int tw = text_width(c.stop_text.c_str(), font(13, 700)) + 18;
                draw_rect(L, o, x - tw + 1, Y0 + 4, tw, 22, col::RED, LV_OPA_COVER, 4);
                draw_text(L, o, (int)x - tw / 2, (int)Y0 + 7, c.stop_text.c_str(), font(13, 700), 0xffffff, LV_TEXT_ALIGN_CENTER, tw);
            }
        }
        if (c.frame) draw_rect(L, o, X0, Y0, X1 - X0, Y1 - Y0 + 1, 0xffffff, 0, 0, 0xdfe5ec, 1);
        // 纵轴刻度。给的小数位放不下刻度时（量程小、刻度 0.5 / 0.25…）自动多显示几位，免得出现两个一样的「0」「2」
        int ydec = p.ydec;
        for (double t : p.yticks)
            while (ydec < 3 && std::fabs(t * std::pow(10.0, ydec) - std::round(t * std::pow(10.0, ydec))) > 1e-6) ++ydec;
        for (double t : p.yticks) {
            float y = s.yp(p, (int)pi, t);
            if (y < Y0 - 1 || y > Y1 + 1) continue;
            // 上下叠放的几块图：边上的刻度字收进本块以内，不和相邻那块的刻度挤在一起
            float ty = y - c.ytick_px * 0.62f;
            if (s.panels.size() > 1) ty = std::clamp(ty, (float)Y0 - 2.0f, (float)(Y1 - c.ytick_px * 1.3));
            draw_text(L, o, (int)X0 - 7, (int)ty, fmt_tick(t, ydec, p.ysuffix).c_str(), ft, col::MUT, LV_TEXT_ALIGN_RIGHT, 60);
        }
        if (!p.unit_top.empty()) draw_text(L, o, std::max(2, (int)X0 - 44), (int)Y0 - 18, p.unit_top.c_str(), font(12, 400), col::MUT);
        // 左上角的小标签
        if (!p.title.empty()) {
            const lv_font_t* f1 = font(13.5, 700);
            const lv_font_t* f2 = font(13, 400);
            int w1 = text_width(p.title.c_str(), f1), w2 = p.unit.empty() ? 0 : text_width(p.unit.c_str(), f2) + 6;
            float bx = X0 + 8, by = Y0 + 6;
            if (p.title_box) draw_rect(L, o, bx, by, (float)(w1 + w2 + 16), 22, 0xffffff, LV_OPA_COVER, 5, 0xdfe5ec, 1);
            draw_text(L, o, (int)bx + 8, (int)by + 2, p.title.c_str(), f1, col::INK);
            if (w2) draw_text(L, o, (int)bx + 8 + w1 + 6, (int)by + 3, p.unit.c_str(), f2, col::MUT);
        }
    }
    // 读数竖线（跨所有块）+ 线上的点 + 底部时间气泡
    float ytop = (float)s.py.front().first, ybot = (float)s.py.back().second;
    if (c.cursor) {
        float x = s.xp(*c.cursor);
        for (float y = ytop; y < ybot; y += 6) draw_vline(L, o, x, y, std::min(ybot, y + 3), 0x1d3557, 1);
        for (size_t pi = 0; pi < s.panels.size(); ++pi) {
            const ChartPanel& p = s.panels[pi];
            for (const ChartSeries& se : p.series) {
                if (!se.xs.empty() || se.y.empty() || se.dash) continue;
                long k = std::lround((*c.cursor - se.x0) / se.dx);
                if (k < 0 || k >= (long)se.y.size() || std::isnan(se.y[(size_t)k])) continue;
                draw_circle(L, o, x, s.yp(p, (int)pi, se.y[(size_t)k]), 3.5f, se.color, 0xffffff, 1);
            }
        }
    }
    // 横轴刻度
    // 字挨得太近（步骤名、刻度密）时后一个不画；贴边的收进图里，不被右边裁掉
    float last_right = -1e9f;
    for (double t : c.xticks) {
        float x = s.xp(t);
        if (x < X0 - 1 || x > X1 + 1 || !c.x_label) continue;
        std::string txt = c.x_label(t);
        if (txt.empty()) continue;
        int tw = text_width(txt.c_str(), ft) + 4;
        float cx = std::min(x, (float)(X1 + 30) - tw / 2.0f);
        if (cx - tw / 2.0f < last_right + 6) continue;
        last_right = cx + tw / 2.0f;
        draw_text(L, o, (int)cx, (int)ybot + 7, txt.c_str(), ft, col::MUT, LV_TEXT_ALIGN_CENTER, std::max(80, tw));
    }
    if (c.cursor && !c.cursor_text.empty()) {
        float x = s.xp(*c.cursor);
        const lv_font_t* f = font(13.5, 700);
        int tw = text_width(c.cursor_text.c_str(), f) + 22;
        draw_rect(L, o, x - tw / 2.0f, ybot + 3, (float)tw, 22, 0x1d3557, LV_OPA_COVER, 11);
        draw_text(L, o, (int)x, (int)ybot + 5, c.cursor_text.c_str(), f, 0xffffff, LV_TEXT_ALIGN_CENTER, tw);
    }
}

std::vector<double> nice_ticks(double lo, double hi, int n, double* step_out) {
    std::vector<double> v;
    if (!(hi > lo)) { v.push_back(lo); return v; }
    double raw = (hi - lo) / std::max(1, n);
    double mag = std::pow(10.0, std::floor(std::log10(raw)));
    double step = mag;
    for (double m : {1.0, 2.0, 2.5, 5.0, 10.0})
        if (raw <= m * mag) { step = m * mag; break; }
    for (double t = std::ceil(lo / step - 1e-9) * step; t <= hi + 1e-9; t += step) v.push_back(std::fabs(t) < 1e-12 ? 0 : t);
    if (step_out) *step_out = step;
    return v;
}

std::vector<double> clock_ticks(int64_t from, int64_t to, int step_min) {
    std::vector<double> v;
    std::time_t t = (std::time_t)(from / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    tm.tm_sec = 0;
    tm.tm_min = tm.tm_min / step_min * step_min;
    int64_t x = (int64_t)std::mktime(&tm) * 1000;
    for (; x <= to; x += step_min * 60000LL)
        if (x >= from) v.push_back((double)x);
    return v;
}

}  // namespace reactor::hmi::kit
