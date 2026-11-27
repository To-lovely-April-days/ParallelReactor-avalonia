// 卡片、圆钮、矩形按钮、带灯选项、开关、浮层、文件图标、小釜图标
#include "i18n.hpp"
#include "widgets.hpp"

#include <cstring>
#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hmi::kit {

lv_obj_t* card(lv_obj_t* parent, int x, int y, int w, int h, int radius) {
    lv_obj_t* c = box(parent, x, y, w, h, 0xffffff, radius);
    card_shadow(c);
    return c;
}

lv_obj_t* popover(lv_obj_t* parent, int x, int y, int w, int h, int radius) {
    lv_obj_t* p = box(parent, x, y, w, h, 0xffffff, radius);
    lv_obj_set_style_border_width(p, 1, 0);
    lv_obj_set_style_border_color(p, lv_color_hex(0x102a52), 0);
    lv_obj_set_style_border_opa(p, 20, 0);
    lv_obj_set_style_shadow_width(p, 40, 0);
    lv_obj_set_style_shadow_offset_y(p, 18, 0);
    lv_obj_set_style_shadow_color(p, lv_color_hex(0x102a52), 0);
    lv_obj_set_style_shadow_opa(p, 72, 0);
    lv_obj_add_flag(p, LV_OBJ_FLAG_CLICKABLE);  // 吃掉点击，不穿透到遮罩
    return p;
}

lv_obj_t* modal_dim(lv_opa_t opa) {
    lv_display_t* d = lv_display_get_default();
    int W = lv_display_get_horizontal_resolution(d), H = lv_display_get_vertical_resolution(d);
    lv_obj_t* dim = box(lv_layer_top(), 0, 60, W, H - 60, 0x283241);
    lv_obj_set_style_bg_opa(dim, opa, 0);
    lv_obj_add_flag(dim, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_update_layout(dim);  // 调用方马上要用 lv_obj_get_width/height 居中
    return dim;
}

// ------------------------------------------------------------ 圆钮
static void style_round(lv_obj_t* c, RoundStyle st, int size) {
    lv_obj_set_style_radius(c, size / 2, 0);
    switch (st) {
        case RoundStyle::White:
            lv_obj_set_style_bg_color(c, lv_color_white(), 0);
            ring(c, 0xdbe5f0, 2);
            break;
        case RoundStyle::WhiteRed:
            lv_obj_set_style_bg_color(c, lv_color_white(), 0);
            ring(c, col::RED, 2, 140);
            break;
        case RoundStyle::Stop:
            lv_obj_set_style_bg_color(c, lv_color_hex(col::RED), 0);
            lv_obj_set_style_shadow_width(c, 8, 0);
            lv_obj_set_style_shadow_offset_y(c, 2, 0);
            lv_obj_set_style_shadow_color(c, lv_color_hex(col::RED), 0);
            lv_obj_set_style_shadow_opa(c, 90, 0);
            break;
        case RoundStyle::Blue:
            lv_obj_set_style_bg_color(c, lv_color_hex(col::BLUE), 0);
            lv_obj_set_style_outline_width(c, 5, 0);
            lv_obj_set_style_outline_color(c, lv_color_hex(col::BLUE), 0);
            lv_obj_set_style_outline_opa(c, 40, 0);
            break;
        case RoundStyle::TempCtl:
            lv_obj_set_style_bg_color(c, lv_color_white(), 0);
            ring(c, 0x00b368, 2);
            lv_obj_set_style_outline_width(c, 5, 0);
            lv_obj_set_style_outline_color(c, lv_color_hex(0x00b368), 0);
            lv_obj_set_style_outline_opa(c, 31, 0);
            break;
    }
}

static lv_obj_t* round_core(lv_obj_t* parent, int x, int y, int size, RoundStyle st, const lv_image_dsc_t* im) {
    lv_obj_t* c = box(parent, x, y, size, size, 0xffffff, size / 2);
    style_round(c, st, size);
    if (st == RoundStyle::Stop) {
        int q = size * 12 / 52;
        lv_obj_t* sq = box(c, 0, 0, q, q, 0xffffff, q / 5);
        lv_obj_center(sq);
    } else if (im) {
        uint32_t ic_col = st == RoundStyle::WhiteRed ? col::RED
                        : st == RoundStyle::Blue     ? 0xffffff
                        : st == RoundStyle::TempCtl  ? 0x00b368
                                                     : col::SEC;
        lv_obj_t* i = icon(c, im, ic_col);
        lv_obj_center(i);
    }
    return c;
}

lv_obj_t* round_btn(lv_obj_t* parent, int cx, int y, int size, RoundStyle st, const lv_image_dsc_t* im, const char* text,
                    uint32_t text_color, double px) {
    int w = 120;
    lv_obj_t* hit = box(parent, cx - w / 2, y, w, size + 32, 0, 0, true);
    round_core(hit, (w - size) / 2, 0, size, st, im);
    lv_obj_t* l = label(hit, text, px, 600, text_color);
    lv_obj_align(l, LV_ALIGN_TOP_MID, 0, size + 6);
    lv_obj_add_flag(hit, LV_OBJ_FLAG_CLICKABLE);
    return hit;
}

lv_obj_t* round_btn_side(lv_obj_t* parent, int x, int y, int size, RoundStyle st, const lv_image_dsc_t* im, const char* title,
                         const char* sub, uint32_t title_color) {
    lv_obj_t* hit = box(parent, x, y, 300, size, 0, 0, true);
    round_core(hit, 0, 0, size, st, im);
    lv_obj_t* t = label_at(hit, size + 12, size / 2 - 22, title, 18, 600, title_color);
    lv_obj_t* s = label_at(hit, size + 12, size / 2 + 3, sub, 13, 400, col::MUT2);
    lv_obj_update_layout(hit);
    int tw = std::max(lv_obj_get_width(t), lv_obj_get_width(s));
    lv_obj_set_width(hit, size + 12 + tw + 4);
    lv_obj_add_flag(hit, LV_OBJ_FLAG_CLICKABLE);
    return hit;
}

ColBtn col_btn(lv_obj_t* parent, int cx, int y, const lv_image_dsc_t* im, const char* text) {
    ColBtn b;
    b.root = box(parent, cx - 50, y, 100, 112, 0, 0, true);
    lv_obj_t* c = box(b.root, 19, 0, 62, 62, 0xffffff, 31);
    ring(c, 0xdbe5f0, 3);
    b.circle = c;
    b.arc = lv_arc_create(b.root);
    lv_obj_remove_style_all(b.arc);
    lv_obj_set_pos(b.arc, 19, 0);
    lv_obj_set_size(b.arc, 62, 62);
    lv_arc_set_bg_angles(b.arc, 0, 360);
    lv_arc_set_rotation(b.arc, 270);
    lv_obj_set_style_arc_width(b.arc, 3, LV_PART_INDICATOR);
    lv_obj_set_style_arc_color(b.arc, lv_color_hex(col::BLUE), LV_PART_INDICATOR);
    lv_obj_set_style_arc_rounded(b.arc, true, LV_PART_INDICATOR);
    lv_obj_set_style_arc_opa(b.arc, 0, LV_PART_MAIN);
    lv_obj_remove_flag(b.arc, LV_OBJ_FLAG_CLICKABLE);
    b.icon = icon(c, im, col::DEEP);
    lv_obj_center(b.icon);
    b.dot = box(b.root, 67, -1, 15, 15, col::GREEN, 8);
    ring(b.dot, 0xffffff, 2);
    b.label = label(b.root, text, 13.5, 400, col::SEC);
    lv_obj_align(b.label, LV_ALIGN_TOP_MID, 0, 68);
    b.value = label(b.root, "", 14.5, 700, col::BLUE);
    lv_obj_align(b.value, LV_ALIGN_TOP_MID, 0, 89);
    lv_obj_add_flag(b.root, LV_OBJ_FLAG_CLICKABLE);
    return b;
}

void ColBtn::set(bool on, const char* v, uint32_t vc, int arc_deg) {
    lv_obj_set_style_bg_color(dot, lv_color_hex(on ? col::GREEN : 0xb3bcc7), 0);
    icon_color(icon, on ? col::DEEP : col::ST_GRY);
    lv_label_set_text(value, v);
    lv_obj_set_style_text_color(value, lv_color_hex(vc), 0);
    lv_obj_align(value, LV_ALIGN_TOP_MID, 0, 89);
    if (arc_deg > 0) {
        lv_obj_remove_flag(arc, LV_OBJ_FLAG_HIDDEN);
        lv_arc_set_angles(arc, 0, arc_deg);
    } else {
        lv_obj_add_flag(arc, LV_OBJ_FLAG_HIDDEN);
    }
}

lv_obj_t* rect_btn(lv_obj_t* parent, int x, int y, int w, int h, const char* text, bool filled, uint32_t color, double px,
                   int radius, const lv_image_dsc_t* im) {
    lv_obj_t* b = box(parent, x, y, w, h, filled ? color : 0xffffff, radius);
    if (!filled) ring(b, color, 2, color == col::RED ? LV_OPA_COVER : LV_OPA_COVER);
    lv_obj_t* l = label(b, text, px, filled ? 700 : 600, filled ? 0xffffff : color);
    if (im) {
        lv_obj_t* i = icon(b, im, filled ? 0xffffff : color);
        lv_obj_update_layout(b);
        int tw = lv_obj_get_width(l), iw = scale::U((int)im->header.w);
        int x0 = (w - tw - iw - 8) / 2;
        lv_obj_set_pos(i, x0, (h - scale::U((int)im->header.h)) / 2);
        lv_obj_align(l, LV_ALIGN_LEFT_MID, x0 + iw + 8, 0);
    } else {
        lv_obj_center(l);
    }
    lv_obj_add_flag(b, LV_OBJ_FLAG_CLICKABLE);
    return b;
}

// ------------------------------------------------------------ 带灯选项
void LampGroup::create(lv_obj_t* parent, int x, int y, const std::vector<std::string>& opts, const std::vector<int>& widths,
                       int sel, int h, double px, int gap) {
    px_ = px;
    int cx = x;
    for (size_t i = 0; i < opts.size(); ++i) {
        const char* txt = trc(opts[i].c_str());
        int w = i < widths.size() ? widths[i] : 69;
        w = std::max(w, 27 + text_width(txt, font(px, 600)) + 12);
        lv_obj_t* b = box(parent, cx, y, w, h, col::TILE, 8);
        lv_obj_t* d = box(b, 11, (h - 9) / 2, 9, 9, 0xc3ccd6, 5);
        lv_obj_t* t = label(b, txt, px, 400, col::SEC);
        lv_obj_align(t, LV_ALIGN_LEFT_MID, 27, 0);
        int idx = (int)i;
        on_tap(b, [this, idx] {
            if (idx == sel_ && !allow_reselect) return;
            if (on_change) on_change(idx);
        });
        btns_.push_back(b); dots_.push_back(d); texts_.push_back(t);
        cx += w + gap;
    }
    width_ = cx - gap - x;
    set(sel);
}

void LampGroup::set_faded(bool f) {
    for (auto* b : btns_) lv_obj_set_style_opa(b, f ? 110 : LV_OPA_COVER, 0);
}

void LampGroup::set(int sel) {
    sel_ = sel;
    for (size_t i = 0; i < btns_.size(); ++i) {
        bool on = (int)i == sel;
        lv_obj_t* b = btns_[i];
        lv_obj_set_style_bg_color(b, lv_color_hex(on ? 0xffffff : col::TILE), 0);
        ring(b, on ? 0x8fa6bf : col::RING, 2);
        lv_obj_set_style_shadow_width(b, on ? 4 : 0, 0);
        lv_obj_set_style_shadow_offset_y(b, 1, 0);
        lv_obj_set_style_shadow_color(b, lv_color_hex(0x102a52), 0);
        lv_obj_set_style_shadow_opa(b, 30, 0);
        lv_obj_t* d = dots_[i];
        lv_obj_set_style_bg_color(d, lv_color_hex(on ? col::GREEN : 0xc3ccd6), 0);
        lv_obj_set_style_outline_width(d, on ? 3 : 0, 0);
        lv_obj_set_style_outline_color(d, lv_color_hex(col::GREEN), 0);
        lv_obj_set_style_outline_opa(d, 46, 0);
        lv_obj_set_style_shadow_width(d, on ? 8 : 0, 0);
        lv_obj_set_style_shadow_color(d, lv_color_hex(col::GREEN), 0);
        lv_obj_set_style_shadow_opa(d, 150, 0);
        lv_obj_set_style_text_color(texts_[i], lv_color_hex(on ? col::INK : col::SEC), 0);
        lv_obj_set_style_text_font(texts_[i], font(px_, on ? 600 : 400), 0);
    }
}

// ------------------------------------------------------------ 开关
void Switch::create(lv_obj_t* parent, int x, int y, bool on, bool compact) {
    compact_ = compact;
    root_ = compact ? box(parent, x, y, 52, 30, col::BLUE, 15) : box(parent, x, y, 64, 34, col::BLUE, 17);
    text_ = label(root_, "", 12.5, 600, 0xffffff);
    if (compact) lv_obj_add_flag(text_, LV_OBJ_FLAG_HIDDEN);
    knob_ = compact ? box(root_, 3, 3, 24, 24, 0xffffff, 12) : box(root_, 3, 3, 28, 28, 0xffffff, 14);
    lv_obj_set_style_shadow_width(knob_, 5, 0);
    lv_obj_set_style_shadow_offset_y(knob_, 1, 0);
    lv_obj_set_style_shadow_opa(knob_, 70, 0);
    on_tap(root_, [this] { if (on_change) on_change(!on_); });
    set(on);
}

void Switch::set(bool on) {
    on_ = on;
    lv_obj_set_style_bg_color(root_, lv_color_hex(on ? col::BLUE : 0xdbe2ea), 0);
    lv_label_set_text(text_, tr(on ? "开" : "关"));
    lv_obj_set_style_text_color(text_, lv_color_hex(on ? 0xffffff : col::MUT), 0);
    if (compact_) {
        lv_obj_set_pos(knob_, on ? 25 : 3, 3);
        return;
    }
    if (on) {
        lv_obj_set_pos(knob_, 33, 3);
        lv_obj_align(text_, LV_ALIGN_LEFT_MID, 11, 0);
    } else {
        lv_obj_set_pos(knob_, 3, 3);
        lv_obj_align(text_, LV_ALIGN_RIGHT_MID, -10, 0);
    }
}

// ------------------------------------------------------------ 只读值块
lv_obj_t* value_block(lv_obj_t* parent, int x, int y, int w, int h, const char* value, const char* unit) {
    lv_obj_t* b = box(parent, x, y, w, h, 0xf4f6f8, 8);
    lv_obj_t* v = label(b, value, 18, 600, col::SEC);
    lv_obj_align(v, LV_ALIGN_LEFT_MID, 12, 0);
    if (unit && *unit) {
        lv_obj_update_layout(b);
        lv_obj_t* u = label(b, unit, 12, 400, col::MUT);
        lv_obj_align_to(u, v, LV_ALIGN_OUT_RIGHT_BOTTOM, 6, -2);
    }
    return b;
}

// ------------------------------------------------------------ 文件图标
lv_obj_t* file_icon(lv_obj_t* parent, int x, int y, const char* ext, bool large) {
    int w = large ? 40 : 30;
    double s = w / 30.0;
    lv_obj_t* root = box(parent, x, y, w, (int)(36 * s), 0, 0, true);
    lv_obj_t* paper = lv_image_create(root);
    lv_obj_remove_style_all(paper);
    lv_image_set_src(paper, large ? &ic::file_paper40 : &ic::file_paper30);
    uint32_t c = !std::strcmp(ext, "CSV") ? 0x1f8a4c : (!std::strcmp(ext, "JSON") || !std::strcmp(ext, "JSONL")) ? 0xc27400
               : !std::strcmp(ext, "PDF") ? 0xd93a3a : 0x5b6672;
    lv_obj_t* band = box(root, 0, (int)(19.5 * s), (int)(27 * s), (int)(12 * s), c, (int)(2 * s));
    lv_obj_t* t = label(band, ext, (std::strlen(ext) <= 3 ? 7.6 : 6.4) * s, 700, 0xffffff);
    lv_obj_center(t);
    return root;
}

// ------------------------------------------------------------ 小釜图标
lv_obj_t* mini_vessel(lv_obj_t* parent, int x, int y, int h, uint32_t liquid, bool disabled) {
    const lv_image_dsc_t *b, *l, *f;
    switch (h) {
        case 17: b = &ic::mv_body17; l = &ic::mv_liq17; f = &ic::mv_frame17; break;
        case 18: b = &ic::mv_body18; l = &ic::mv_liq18; f = &ic::mv_frame18; break;
        case 19: b = &ic::mv_body19; l = &ic::mv_liq19; f = &ic::mv_frame19; break;
        case 21: b = &ic::mv_body21; l = &ic::mv_liq21; f = &ic::mv_frame21; break;
        case 22: b = &ic::mv_body22; l = &ic::mv_liq22; f = &ic::mv_frame22; break;
        case 24: b = &ic::mv_body24; l = &ic::mv_liq24; f = &ic::mv_frame24; break;
        default: b = &ic::mv_body34; l = &ic::mv_liq34; f = &ic::mv_frame34; break;
    }
    lv_obj_t* root = box(parent, x, y, scale::U((int)b->header.w), scale::U((int)b->header.h), 0, 0, true);
    icon(root, b, 0xffffff);
    icon(root, l, disabled ? 0xe3e8ee : liquid);
    icon(root, f, disabled ? 0xb3bcc7 : 0x5b6672);
    return root;
}

const lv_image_dsc_t* state_icon(const char* n, int px) {
    std::string s = n ? n : "";
    if (px >= 16) {
        if (s == "gauge") return &ic::st_gauge16;
        if (s == "drop") return &ic::st_drop16;
        if (s == "off") return &ic::st_off16;
    }
    if (s == "up") return &ic::st_up13;
    if (s == "clock") return &ic::st_clock13;
    if (s == "rot") return &ic::st_rot13;
    if (s == "snow") return &ic::st_snow13;
    if (s == "queue") return &ic::st_queue13;
    if (s == "check") return &ic::st_check13;
    if (s == "alert") return &ic::st_alert13;
    if (s == "stop") return &ic::stop_sq14;   // 手动停止（方块）
    if (s == "off") return &ic::st_off13;
    if (s == "drop") return &ic::st_drop13;
    return &ic::st_up13;
}

}  // namespace reactor::hmi::kit
