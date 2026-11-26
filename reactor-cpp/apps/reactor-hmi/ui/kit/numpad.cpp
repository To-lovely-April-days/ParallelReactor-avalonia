// 数字键盘（Kbd-Num / Chan-QuickKbd）：左边大数字 + 滑条 + 上次设定 / 当前实测 + 退格 / 应用；右边 3×4 圆键。
// 和数值框（点了弹出）
#include "i18n.hpp"
#include "widgets.hpp"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <algorithm>
#include <functional>
#include <string>

namespace reactor::hmi::kit {

namespace {

struct Pad {
    lv_obj_t* dim = nullptr;
    lv_obj_t* val = nullptr; lv_obj_t* unit = nullptr; lv_obj_t* err = nullptr;
    lv_obj_t* fill = nullptr; lv_obj_t* knob = nullptr; lv_obj_t* track = nullptr;
    NumSpec spec;
    std::string text;
    bool fresh = true;  // 第一次按数字键时替换原值
    std::function<void(double)> on_apply;
};
Pad* g_pad = nullptr;

std::string fmt(double v, int d) {
    if (std::isnan(v)) return "";  // 没有值（例如没设过目标温度）：键盘从空开始
    char b[32];
    std::snprintf(b, sizeof b, "%.*f", d, v);
    return b;
}

void refresh() {
    Pad& p = *g_pad;
    lv_label_set_text(p.val, p.text.empty() ? "—" : p.text.c_str());  // 还没输入（原来没有值时显示成「0」像是设成了 0）
    lv_obj_update_layout(p.val);
    lv_obj_align_to(p.unit, p.val, LV_ALIGN_OUT_RIGHT_BOTTOM, 10, -12);
    double v = std::atof(p.text.c_str());
    double f = (v - p.spec.min) / (p.spec.max - p.spec.min);
    if (f < 0) f = 0;
    if (f > 1) f = 1;
    int w = (int)std::lround(330 * f);
    lv_obj_set_width(p.fill, std::max(6, w));
    lv_obj_set_x(p.knob, 30 + w - 17);
    bool bad = !p.text.empty() && (v < p.spec.min - 1e-9 || v > p.spec.max + 1e-9);
    if (bad) {
        lv_label_set_text(p.err, trf("范围 %s – %s %s", fmt(p.spec.min, p.spec.decimals).c_str(), fmt(p.spec.max, p.spec.decimals).c_str(),
                                     p.spec.unit.c_str()).c_str());
        lv_obj_remove_flag(p.err, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_obj_add_flag(p.err, LV_OBJ_FLAG_HIDDEN);
    }
}

void key(const char* k) {
    Pad& p = *g_pad;
    std::string s = k;
    if (s == "±") {
        if (!p.spec.allow_negative) return;
        if (!p.text.empty() && p.text[0] == '-') p.text.erase(0, 1);
        else p.text = "-" + p.text;
        p.fresh = false;
    } else if (s == ".") {
        if (p.spec.decimals == 0) return;
        if (p.fresh) { p.text = "0"; p.fresh = false; }
        if (p.text.find('.') == std::string::npos) p.text += ".";
    } else {
        if (p.fresh) { p.text.clear(); p.fresh = false; }
        auto dot = p.text.find('.');
        if (dot != std::string::npos && (int)(p.text.size() - dot - 1) >= p.spec.decimals) return;
        if (p.text.size() >= 8) return;
        if (p.text == "0") p.text.clear();
        p.text += s;
    }
    refresh();
}

void slider_event(lv_event_t* e) {
    Pad& p = *g_pad;
    lv_indev_t* in = lv_indev_active();
    if (!in) return;
    lv_point_t pt;
    lv_indev_get_point(in, &pt);
    lv_area_t a;
    lv_obj_get_coords(p.track, &a);
    double f = double(pt.x - a.x1) / double(a.x2 - a.x1);
    f = f < 0 ? 0 : f > 1 ? 1 : f;
    double v = p.spec.min + (p.spec.max - p.spec.min) * f;
    double q = std::pow(10.0, p.spec.decimals);
    v = std::round(v * q) / q;
    p.text = fmt(v, p.spec.decimals);
    p.fresh = true;
    refresh();
    (void)e;
}

}  // namespace

void NumPad::close() {
    if (!g_pad) return;
    lv_obj_delete(g_pad->dim);
    delete g_pad;
    g_pad = nullptr;
}

void NumPad::open(const NumSpec& spec, std::function<void(double)> on_apply) {
    close();
    g_pad = new Pad;
    Pad& p = *g_pad;
    p.spec = spec;
    p.on_apply = std::move(on_apply);
    p.text = fmt(spec.value, spec.decimals);
    p.dim = modal_dim();
    int W = lv_obj_get_width(p.dim), H = lv_obj_get_height(p.dim);
    lv_obj_t* pan = box(p.dim, (W - 900) / 2, (H - 560) / 2, 900, 560, 0xffffff, 20);
    lv_obj_set_style_clip_corner(pan, true, 0);
    pop_shadow(pan, 60, 24, 90);
    lv_obj_add_flag(pan, LV_OBJ_FLAG_CLICKABLE);
    // 左：数字
    lv_obj_t* t = label_at(pan, 30, 30, spec.title.c_str(), 15, 400, col::SEC);
    lv_obj_set_width(t, 330);
    p.val = label_at(pan, 30, 50, "", 64, 700, col::DEEP);
    p.unit = label(pan, spec.unit.c_str(), 22, 400, col::MUT);
    p.track = box(pan, 30, 163, 330, 6, 0xdfe6ee, 3);
    p.fill = box(pan, 30, 163, 89, 6, col::BLUE, 3);
    p.knob = box(pan, 102, 149, 34, 34, 0xffffff, 17);
    ring(p.knob, col::BLUE, 3);
    lv_obj_set_style_shadow_width(p.knob, 6, 0);
    lv_obj_set_style_shadow_offset_y(p.knob, 2, 0);
    lv_obj_set_style_shadow_color(p.knob, lv_color_hex(col::BLUE), 0);
    lv_obj_set_style_shadow_opa(p.knob, 64, 0);
    lv_obj_t* hit = box(pan, 20, 140, 350, 50, 0, 0, true);
    lv_obj_add_flag(hit, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(hit, slider_event, LV_EVENT_PRESSING, nullptr);
    lv_obj_add_event_cb(hit, slider_event, LV_EVENT_PRESSED, nullptr);
    label_at(pan, 30, 189, fmt(spec.min, spec.decimals).c_str(), 12.5, 400, col::MUT);
    lv_obj_t* mx = label(pan, fmt(spec.max, spec.decimals).c_str(), 12.5, 400, col::MUT);
    lv_obj_align(mx, LV_ALIGN_TOP_LEFT, 360 - text_width(fmt(spec.max, spec.decimals).c_str(), font(12.5)), 189);
    int ry = 229;
    if (spec.last) {
        label_at(pan, 30, ry, tr("上次设定"), 15, 400, col::SEC);
        label_at(pan, 108, ry, (fmt(*spec.last, spec.decimals) + " " + spec.unit).c_str(), 15, 700, col::INK);
        ry += 29;
    }
    if (!spec.current.empty()) {
        label_at(pan, 30, ry, tr("当前实测"), 15, 400, col::SEC);
        label_at(pan, 108, ry, spec.current.c_str(), 15, 700, col::INK);
        ry += 29;
    }
    p.err = label_at(pan, 30, ry + 8, "", 14, 600, col::RED);
    lv_obj_t* bs = box(pan, 30, 468, 92, 64, 0xeaf1f9, 32);
    lv_obj_t* bi = icon(bs, &ic::backspace24, col::SEC);
    lv_obj_center(bi);
    on_tap(bs, [] {
        Pad& q = *g_pad;
        if (q.fresh) { q.text.clear(); q.fresh = false; }
        else if (!q.text.empty()) q.text.pop_back();
        refresh();
    });
    lv_obj_t* ap = box(pan, 134, 468, 226, 64, col::BLUE, 32);
    lv_obj_t* al = label(ap, tr("应用"), 20, 700, 0xffffff);
    lv_obj_center(al);
    on_tap(ap, [] {
        Pad& q = *g_pad;
        double v = std::atof(q.text.c_str());
        if (q.text.empty() || v < q.spec.min - 1e-9 || v > q.spec.max + 1e-9) {
            refresh();
            // 没输入就点应用：也提示范围
            lv_label_set_text(q.err, trf("范围 %s – %s %s", fmt(q.spec.min, q.spec.decimals).c_str(), fmt(q.spec.max, q.spec.decimals).c_str(),
                                         q.spec.unit.c_str()).c_str());
            lv_obj_remove_flag(q.err, LV_OBJ_FLAG_HIDDEN);
            return;
        }
        auto cb = q.on_apply;
        NumPad::close();
        if (cb) cb(v);
    });
    // 右：键
    lv_obj_t* right = box(pan, 390, 0, 510, 560, 0xf4f7fb);
    static const char* K[12] = {"7", "8", "9", "4", "5", "6", "1", "2", "3", "±", "0", "."};
    for (int i = 0; i < 12; ++i) {
        int r = i / 3, c = i % 3;
        lv_obj_t* k = box(right, 75 + c * 128, 48 + r * 120, 104, 104, 0xeaf1f9, 52);
        lv_obj_t* kl = label(k, K[i], 30, 400, col::INK);
        lv_obj_center(kl);
        lv_obj_set_style_bg_color(k, lv_color_hex(0xd5e3f2), LV_STATE_PRESSED);
        const char* kk = K[i];
        on_tap(k, [kk] { key(kk); });
    }
    lv_obj_t* cl = box(pan, 838, 18, 44, 44, 0xeef1f5, 22);
    lv_obj_t* ci = icon(cl, &ic::close20, col::SEC);
    lv_obj_center(ci);
    on_tap(cl, [] { NumPad::close(); });
    refresh();
}

// ------------------------------------------------------------ 数值框
void NumField::create(lv_obj_t* parent, int x, int y, int w, int h, const NumSpec& spec, bool readonly) {
    spec_ = spec;
    root_ = box(parent, x, y, w, h, readonly ? 0xf4f6f8 : col::TILE, 8);
    if (!readonly) ring(root_, col::RING, 2);
    val_ = label(root_, "", readonly ? 18 : 19, 600, readonly ? col::SEC : col::DEEP);
    lv_obj_align(val_, LV_ALIGN_LEFT_MID, 10, 0);
    unit_ = label(root_, spec.unit.c_str(), 12, 400, col::MUT);
    lv_obj_align(unit_, LV_ALIGN_RIGHT_MID, -10, 1);
    set(spec.value);
    if (!readonly)
        on_tap(root_, [this] {
            NumSpec s = spec_;
            NumPad::open(s, [this](double v) {
                if (on_apply) on_apply(v);
            });
        });
}

void NumField::set(double v) {
    spec_.value = v;
    set_text(std::isnan(v) ? "—" : fmt(v, spec_.decimals).c_str());
}

void NumField::set_text(const char* t) {
    lv_label_set_text(val_, t);
    if (right_) {
        lv_obj_update_layout(root_);
        lv_obj_align(val_, LV_ALIGN_RIGHT_MID, spec_.unit.empty() ? -12 : -12, 0);
    }
}

void NumField::set_faded(bool f) {
    lv_obj_set_style_opa(root_, f ? 110 : LV_OPA_COVER, 0);
}

void NumField::set_align_right(bool r) {
    right_ = r;
    if (r) {
        lv_obj_add_flag(unit_, LV_OBJ_FLAG_HIDDEN);
        lv_obj_set_style_text_font(val_, font(19, 700), 0);
        lv_obj_align(val_, LV_ALIGN_RIGHT_MID, -12, 0);
    }
}

}  // namespace reactor::hmi::kit
