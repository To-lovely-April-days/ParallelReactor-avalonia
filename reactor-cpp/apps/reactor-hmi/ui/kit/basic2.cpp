// 第二批加的小控件：药丸、通道小块、进度条、步骤图标、关闭钮、工具按钮、小日历、选项方块、toast
#include "i18n.hpp"
#include "widgets.hpp"

#include <cstdio>
#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::hmi::kit {

lv_obj_t* pill(lv_obj_t* parent, const char* text, uint32_t fg, uint32_t bg, double px, const lv_image_dsc_t* im, bool dot,
               int weight) {
    const lv_font_t* f = font(px, weight);
    int tw = text_width(text, f);
    int lead = im ? scale::U((int)im->header.w) + 5 : dot ? 13 : 0;
    int h = (int)(px * 1.75 + 0.5);
    lv_obj_t* p = box(parent, 0, 0, tw + lead + 18, h, bg, h / 2);
    int x = 9;
    if (im) {
        icon(p, im, fg, x, (h - scale::U((int)im->header.h)) / 2);
        x += lead;
    } else if (dot) {
        lv_obj_t* d = box(p, x, (h - 8) / 2, 8, 8, fg, 4);
        lv_obj_set_style_shadow_width(d, 5, 0);
        lv_obj_set_style_shadow_color(d, lv_color_hex(fg), 0);
        lv_obj_set_style_shadow_opa(d, 120, 0);
        x += lead;
    }
    lv_obj_t* l = label(p, text, px, weight, fg);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, x, 0);
    return p;
}

lv_obj_t* chan_chip(lv_obj_t* parent, int x, int y, int ch, bool on, bool disabled, int w, int h, bool lamp) {
    lv_obj_t* b = box(parent, x, y, w, h, on && !disabled ? 0xffffff : col::TILE, 8);
    ring(b, on && !disabled ? 0x8fa6bf : col::RING, 2);
    if (on && !disabled) {
        lv_obj_set_style_shadow_width(b, 4, 0);
        lv_obj_set_style_shadow_offset_y(b, 1, 0);
        lv_obj_set_style_shadow_opa(b, 30, 0);
    }
    mini_vessel(b, 8, (h - 18) / 2, 18, chan_color(ch), disabled);
    char n[8];
    std::snprintf(n, sizeof n, "%d", ch);
    lv_obj_t* l = label(b, n, 15.5, 700, disabled ? col::DIS : col::INK);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, 27, 0);
    if (lamp && !disabled) {
        // 和 LampGroup 的灯一样：选中 = 绿灯 + 3 px 淡绿光晕；没选 = 灰灯
        // 挂在右上角、一半在块外面（不占位，块宽不变）：白边隔开描边
        lv_obj_add_flag(b, LV_OBJ_FLAG_OVERFLOW_VISIBLE);
        lv_obj_t* d = box(b, w - 8, -4, 10, 10, on ? col::GREEN : 0xc3ccd6, 5);
        ring(d, 0xffffff, 2);
        if (on) {
            lv_obj_set_style_outline_width(d, 3, 0);
            lv_obj_set_style_outline_color(d, lv_color_hex(col::GREEN), 0);
            lv_obj_set_style_outline_opa(d, 46, 0);
        }
    }
    if (disabled) lv_obj_set_style_opa(b, 150, 0);
    return b;
}

lv_obj_t* bar(lv_obj_t* parent, int x, int y, int w, int h, double frac, uint32_t color, std::optional<double> marker, uint32_t track) {
    lv_obj_t* t = box(parent, x, y, w, h, track, h / 2);
    frac = std::max(0.0, std::min(1.0, frac));
    if (frac > 0) box(t, 0, 0, std::max(h, (int)(w * frac)), h, color, h / 2);
    if (marker) {
        int mx = (int)(w * std::max(0.0, std::min(1.0, *marker)));
        lv_obj_t* m = box(parent, x + mx - 1, y - 5, 2, h + 10, col::RED);
        (void)m;
    }
    return t;
}

const lv_image_dsc_t* step_icon(const std::string& t, int px) {
    if (px <= 16) {
        if (t == "leak") return &ic::step_leak16;
        if (t == "exchange") return &ic::step_exchange16;
        if (t == "stir") return &ic::step_stir16;
        if (t == "heat") return &ic::step_heat16;
        if (t == "press") return &ic::step_press16;
        if (t == "hold") return &ic::step_hold16;
        if (t == "react") return &ic::step_react16;
        if (t == "vent") return &ic::step_vent16;
        return &ic::step_end16;
    }
    if (px <= 20) {
        if (t == "leak") return &ic::step_leak20;
        if (t == "exchange") return &ic::step_exchange20;
        if (t == "stir") return &ic::step_stir20;
        if (t == "heat") return &ic::step_heat20;
        if (t == "press") return &ic::step_press20;
        if (t == "hold") return &ic::step_hold20;
        if (t == "react") return &ic::step_react20;
        if (t == "vent") return &ic::step_vent20;
        return &ic::step_end20;
    }
    if (t == "leak") return &ic::step_leak28;
    if (t == "exchange") return &ic::step_exchange28;
    if (t == "stir") return &ic::step_stir28;
    if (t == "heat") return &ic::step_heat28;
    if (t == "press") return &ic::step_press28;
    if (t == "hold") return &ic::step_hold28;
    if (t == "react") return &ic::step_react28;
    if (t == "vent") return &ic::step_vent28;
    return &ic::step_end28;
}

lv_obj_t* close_btn(lv_obj_t* parent, int x, int y, int size) {
    lv_obj_t* b = box(parent, x, y, size, size, 0xeef1f5, size / 2);
    lv_obj_center(icon(b, &ic::close20, col::SEC));
    lv_obj_add_flag(b, LV_OBJ_FLAG_CLICKABLE);
    return b;
}

lv_obj_t* tool_btn(lv_obj_t* parent, int x, int y, const char* text, const lv_image_dsc_t* im, uint32_t color, int h, double px,
                   uint32_t ring_color) {
    const lv_font_t* f = font(px, 500);
    int tw = text_width(text, f);
    int iw = im ? scale::U((int)im->header.w) + 8 : 0;
    int w = tw + iw + 32;
    lv_obj_t* b = box(parent, x, y, w, h, 0xffffff, 9);
    ring(b, ring_color, 2);
    int cx = 16;
    if (im) {
        icon(b, im, color, cx, (h - scale::U((int)im->header.h)) / 2);
        cx += iw;
    }
    lv_obj_t* l = label(b, text, px, 500, color);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, cx, 0);
    lv_obj_add_flag(b, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_color(b, lv_color_hex(0xeef3f9), LV_STATE_PRESSED);
    return b;
}

lv_obj_t* cal_badge(lv_obj_t* parent, int x, int y, int month, int day, uint32_t color, int w, int h) {
    lv_obj_t* c = box(parent, x, y, w, h, 0xffffff, 8);
    lv_obj_set_style_border_width(c, 2, 0);
    lv_obj_set_style_border_color(c, lv_color_hex(color), 0);
    lv_obj_set_style_clip_corner(c, true, 0);
    int hh = h * 30 / 100;
    lv_obj_t* top = box(c, 0, 0, w, hh, color);
    char b[16];
    std::snprintf(b, sizeof b, "%d %s", month, tr("月"));
    lv_obj_center(label(top, b, 10.5, 700, 0xffffff));
    std::snprintf(b, sizeof b, "%02d", day);
    lv_obj_t* d = label(c, b, h * 0.36, 700, 0x1d3557);
    lv_obj_align(d, LV_ALIGN_CENTER, 0, hh / 2);
    // 两个挂环
    box(parent, x + w / 4 - 2, y - 4, 4, 9, 0x1d3557, 2);
    box(parent, x + w * 3 / 4 - 2, y - 4, 4, 9, 0x1d3557, 2);
    return c;
}

void LampTile::create(lv_obj_t* parent, int x, int y, int w, int h, const char* ext, const char* title, const char* sub, bool on) {
    root_ = box(parent, x, y, w, h, col::TILE, 10);
    dot_ = box(root_, 14, (h - 10) / 2, 10, 10, 0xc3ccd6, 5);
    file_icon(root_, 36, (h - 36) / 2, ext);
    label_at(root_, 76, h / 2 - 22, title, 16, 700, col::INK);
    label_at(root_, 76, h / 2 + 2, sub, 13, 400, col::MUT);
    on_tap(root_, [this] {
        if (on_change) on_change(!on_);
    });
    set(on);
}

void LampTile::set(bool on) {
    on_ = on;
    lv_obj_set_style_bg_color(root_, lv_color_hex(on ? 0xffffff : col::TILE), 0);
    ring(root_, on ? 0x8fa6bf : col::RING, 2);
    lv_obj_set_style_bg_color(dot_, lv_color_hex(on ? col::GREEN : 0xc3ccd6), 0);
    lv_obj_set_style_shadow_width(dot_, on ? 8 : 0, 0);
    lv_obj_set_style_shadow_color(dot_, lv_color_hex(col::GREEN), 0);
    lv_obj_set_style_shadow_opa(dot_, 150, 0);
}

// ------------------------------------------------------------ toast
namespace {
lv_obj_t* g_toast = nullptr;
lv_timer_t* g_toast_timer = nullptr;
}  // namespace

void toast(const std::string& text, bool ok) {
    if (g_toast) { lv_obj_delete(g_toast); g_toast = nullptr; }
    if (g_toast_timer) { lv_timer_delete(g_toast_timer); g_toast_timer = nullptr; }
    lv_display_t* d = lv_display_get_default();
    int W = lv_display_get_horizontal_resolution(d), H = lv_display_get_vertical_resolution(d);
    const lv_font_t* f = font(15, 600);
    int w = text_width(text.c_str(), f) + 64;
    g_toast = box(lv_layer_top(), (W - w) / 2, H - 84, w, 46, ok ? 0x1d3557 : col::RED, 23);
    pop_shadow(g_toast, 24, 8, 60);
    icon(g_toast, ok ? &ic::check18 : &ic::alarm_tri22, 0xffffff, 18, ok ? 14 : 12);
    lv_obj_t* l = label(g_toast, text.c_str(), 15, 600, 0xffffff);
    lv_obj_align(l, LV_ALIGN_LEFT_MID, 44, 0);
    g_toast_timer = lv_timer_create(
        [](lv_timer_t*) {
            if (g_toast) lv_obj_delete(g_toast);
            g_toast = nullptr;
            lv_timer_delete(g_toast_timer);
            g_toast_timer = nullptr;
        },
        3000, nullptr);
}

}  // namespace reactor::hmi::kit
