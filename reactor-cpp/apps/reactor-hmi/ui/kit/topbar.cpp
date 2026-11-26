// 顶栏 + 页签卡片（family.py topbar() / tabs()，2026-11-26 去掉急停、时间靠右加竖线）
#include "i18n.hpp"
#include "widgets.hpp"

#include <ctime>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hmi::kit {

namespace {
const lv_image_dsc_t* NAV_ICON[5] = {&ic::nav_overview, &ic::nav_program, &ic::nav_curve, &ic::nav_records, &ic::nav_settings};
const char* NAV_TEXT[5] = {"总览", "程序", "曲线", "记录", "设置"};
}  // namespace

lv_obj_t* TopBar::create(lv_obj_t* parent, int width) {
    width_ = width;
    root_ = box(parent, 0, 0, width, 60, col::NAVYBAR);
    lv_obj_t* brand = label_at(root_, 18, 19, "HTLAB", 17, 700, 0xffffff);
    lv_obj_set_style_text_letter_space(brand, 1, 0);
    lv_obj_t* sub = label_at(root_, 96, 21, tr("八通道平行反应仪 · HT-PR8"), 13, 400, 0xffffff);
    lv_obj_set_style_text_opa(sub, 184, 0);
    for (int i = 0; i < 5; ++i) {
        int bx = 345 + i * 60;
        lv_obj_t* b = box(root_, bx, 4, 52, 54, 0, 0, true);
        on_tap(b, [this, i] { if (on_nav) on_nav(i); });
        nav_circle_[i] = box(b, 9, 1, 34, 34, 0xffffff, 17);
        nav_icon_[i] = icon(nav_circle_[i], NAV_ICON[i], 0xffffff);
        lv_obj_center(nav_icon_[i]);
        nav_text_[i] = label(b, tr(NAV_TEXT[i]), 11, 400, 0xffffff);
        lv_obj_align(nav_text_[i], LV_ALIGN_TOP_MID, 0, 38);
    }
    dot_ = box(root_, 0, 26, 8, 8, col::CONN, 4);
    conn_ = label_at(root_, 0, 21, tr("已连接"), 13, 400, 0xffffff);
    sim_ = label(root_, tr("模拟模式"), 12, 400, 0xffd2cf);
    lv_obj_set_style_bg_color(sim_, lv_color_hex(0xff5a4e), 0);
    lv_obj_set_style_bg_opa(sim_, 46, 0);
    lv_obj_set_style_radius(sim_, 6, 0);
    lv_obj_set_style_pad_hor(sim_, 8, 0);
    lv_obj_set_style_pad_ver(sim_, 3, 0);
    remote_ = label(root_, "", 12, 600, 0xffffff);
    lv_obj_set_style_bg_color(remote_, lv_color_hex(col::ORANGE), 0);
    lv_obj_set_style_bg_opa(remote_, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(remote_, 6, 0);
    lv_obj_set_style_pad_hor(remote_, 8, 0);
    lv_obj_set_style_pad_ver(remote_, 3, 0);
    lv_obj_add_flag(remote_, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(remote_, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(remote_, 10);
    on_tap(remote_, [this] { if (on_remote) on_remote(); });

    bell_ = box(root_, width - 205, 10, 40, 40, 0xffffff, 20);
    lv_obj_set_style_bg_opa(bell_, 0, 0);
    bell_icon_ = icon(bell_, &ic::bell, 0xffffff, 8, 8);
    on_tap(bell_, [this] { if (on_bell) on_bell(); });
    bell_badge_ = box(root_, width - 183, 12, 16, 16, col::RED, 8);
    bell_num_ = label(bell_badge_, "0", 10.5, 600, 0xffffff);
    lv_obj_center(bell_num_);

    globe_ = box(root_, width - 151, 10, 40, 40, 0, 20, true);
    icon(globe_, &ic::globe, 0xffffff, 8, 8);
    on_tap(globe_, [this] { if (on_lang) on_lang(); });
    lv_obj_t* bar = box(root_, width - 95, 19, 1, 22, 0xffffff);
    lv_obj_set_style_bg_opa(bar, 66, 0);
    time_ = label(root_, "--:--", 16, 600, 0xffffff);
    date_ = label(root_, "----.--.--", 12, 400, 0xffffff);
    lv_obj_set_style_text_opa(date_, 204, 0);
    set_nav(0);
    relayout();
    return root_;
}

void TopBar::relayout() {
    // 时间右对齐到 width-18；左边依次：竖线、地球、铃铛、模拟模式、已连接
    lv_obj_update_layout(root_);
    lv_obj_align(time_, LV_ALIGN_TOP_RIGHT, -18, 14);
    lv_obj_align(date_, LV_ALIGN_TOP_RIGHT, -18, 33);
    int x = width_ - 205 - 14;  // 铃铛左边
    if (!lv_obj_has_flag(sim_, LV_OBJ_FLAG_HIDDEN)) {
        int sw = lv_obj_get_width(sim_);
        x -= sw;
        lv_obj_set_pos(sim_, x, 19);
        x -= 14;
    }
    if (!lv_obj_has_flag(remote_, LV_OBJ_FLAG_HIDDEN)) {
        int rw = lv_obj_get_width(remote_);
        x -= rw;
        lv_obj_set_pos(remote_, x, 19);
        x -= 14;
    }
    int cw = lv_obj_get_width(conn_);
    lv_obj_set_pos(conn_, x - cw, 21);
    lv_obj_set_pos(dot_, x - cw - 14, 26);
}

void TopBar::set_nav(int sel) {
    for (int i = 0; i < 5; ++i) {
        bool on = i == sel;
        lv_obj_set_style_bg_opa(nav_circle_[i], on ? LV_OPA_COVER : 36, 0);
        lv_obj_set_style_border_width(nav_circle_[i], on ? 0 : 1, 0);
        lv_obj_set_style_border_color(nav_circle_[i], lv_color_white(), 0);
        lv_obj_set_style_border_opa(nav_circle_[i], 56, 0);
        icon_color(nav_icon_[i], on ? col::BLUE : 0xffffff);
        lv_obj_set_style_image_opa(nav_icon_[i], on ? LV_OPA_COVER : 217, 0);
        lv_obj_set_style_text_font(nav_text_[i], font(11, on ? 600 : 400), 0);
        lv_obj_set_style_text_opa(nav_text_[i], on ? LV_OPA_COVER : 217, 0);
    }
}

void TopBar::set_status(bool connected, bool simulated) {
    lv_label_set_text(conn_, tr(connected ? "已连接" : "未连接"));
    lv_obj_set_style_bg_color(dot_, lv_color_hex(connected ? col::CONN : col::RED), 0);
    if (simulated) lv_obj_remove_flag(sim_, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(sim_, LV_OBJ_FLAG_HIDDEN);
    relayout();
}

void TopBar::set_remote(bool remote, const std::string& holder) {
    bool was = !lv_obj_has_flag(remote_, LV_OBJ_FLAG_HIDDEN);
    std::string t = holder.empty() ? std::string(tr("PC 控制中")) : trf("PC 控制中 · %s", holder.c_str());
    if (remote == was && (!remote || t == lv_label_get_text(remote_))) return;
    if (remote) {
        lv_label_set_text(remote_, t.c_str());
        lv_obj_remove_flag(remote_, LV_OBJ_FLAG_HIDDEN);
    } else {
        lv_obj_add_flag(remote_, LV_OBJ_FLAG_HIDDEN);
    }
    relayout();
}

void TopBar::set_bell(int count, int worst_tier, bool selected) {
    lv_obj_set_style_bg_opa(bell_, selected ? LV_OPA_COVER : 0, 0);
    icon_color(bell_icon_, selected ? col::NAVYBAR : 0xffffff);
    if (count <= 0) {
        lv_obj_add_flag(bell_badge_, LV_OBJ_FLAG_HIDDEN);
        return;
    }
    lv_obj_remove_flag(bell_badge_, LV_OBJ_FLAG_HIDDEN);
    lv_label_set_text_fmt(bell_num_, "%d", count);
    lv_obj_set_style_bg_color(bell_badge_, lv_color_hex(worst_tier >= 3 ? col::AMBER : col::RED), 0);
    // 角标在白底铃铛上加白边
    lv_obj_set_style_border_width(bell_badge_, selected ? 2 : 0, 0);
    lv_obj_set_style_border_color(bell_badge_, lv_color_white(), 0);
}

void TopBar::set_time(int64_t ms) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char hm[16], d[16];
    std::strftime(hm, sizeof hm, "%H:%M", &tm);
    std::strftime(d, sizeof d, "%Y.%m.%d", &tm);
    lv_label_set_text(time_, hm);
    lv_label_set_text(date_, d);
}

// ------------------------------------------------------------ 页签卡片
lv_obj_t* TabCard::create(lv_obj_t* parent, int x, int y, int w, const std::vector<TabDef>& tabs, int sel) {
    defs_ = tabs;
    root_ = box(parent, x, y, w, 44, 0xffffff, 10);
    lv_obj_set_style_border_width(root_, 1, 0);
    lv_obj_set_style_border_color(root_, lv_color_hex(0x102a4c), 0);
    lv_obj_set_style_border_opa(root_, 18, 0);
    lv_obj_set_style_shadow_width(root_, 4, 0);
    lv_obj_set_style_shadow_offset_y(root_, 1, 0);
    lv_obj_set_style_shadow_color(root_, lv_color_hex(0x102a4c), 0);
    lv_obj_set_style_shadow_opa(root_, 13, 0);
    lv_obj_set_style_clip_corner(root_, true, 0);
    int tx = 0;
    for (size_t i = 0; i < tabs.size(); ++i) {
        const TabDef& d = tabs[i];
        const lv_font_t* f = font(17, 600);
        int tw = text_width(tr(d.text.c_str()), f);
        int iw = d.icon ? (int)d.icon->header.w : 0;
        int bw = 0;
        if (!d.badge.empty()) bw = text_width(d.badge.c_str(), font(12.5, 700)) + 14 + 8;
        int w_tab = 20 + (iw ? iw + 8 : 0) + tw + bw + 20;
        lv_obj_t* t = box(root_, tx, 0, w_tab, 44, 0xffffff);
        lv_obj_set_style_border_side(t, LV_BORDER_SIDE_RIGHT, 0);
        lv_obj_set_style_border_width(t, 1, 0);
        lv_obj_set_style_border_color(t, lv_color_hex(0xe8ecf1), 0);
        int cx = 20;
        lv_obj_t* im = nullptr;
        if (d.icon) {
            im = icon(t, d.icon, col::MUT2, cx, (44 - (int)d.icon->header.h) / 2);
            cx += iw + 8;
        }
        lv_obj_t* l = label_at(t, cx, 10, tr(d.text.c_str()), 17, 600, col::TABSEC);
        cx += tw + 8;
        lv_obj_t* bd = nullptr;
        if (!d.badge.empty()) {
            bd = label(t, d.badge.c_str(), 12.5, 700, 0xffffff);
            lv_obj_set_style_pad_hor(bd, 7, 0);
            lv_obj_set_style_pad_ver(bd, 2, 0);
            lv_obj_set_style_radius(bd, 10, 0);
            lv_obj_set_style_bg_opa(bd, LV_OPA_COVER, 0);
            lv_obj_set_style_min_width(bd, 22, 0);
            lv_obj_set_style_text_align(bd, LV_TEXT_ALIGN_CENTER, 0);
            lv_obj_set_pos(bd, cx, 12);
        }
        lv_obj_t* u = box(t, 0, 41, w_tab, 3, col::ORANGE);
        tabs_.push_back(t); texts_.push_back(l); icons_.push_back(im); badges_.push_back(bd); unders_.push_back(u);
        int idx = (int)i;
        on_tap(t, [this, idx] {
            if (on_select) on_select(idx);
        });
        tx += w_tab;
    }
    select(sel);
    return root_;
}

void TabCard::style_tab(int i, bool on) {
    lv_obj_t* t = tabs_[static_cast<size_t>(i)];
    lv_obj_set_style_bg_color(t, lv_color_hex(on ? col::BLUE : 0xffffff), 0);
    lv_obj_set_style_border_width(t, on ? 0 : 1, 0);
    lv_obj_set_style_text_color(texts_[static_cast<size_t>(i)], lv_color_hex(on ? 0xffffff : col::TABSEC), 0);
    if (icons_[static_cast<size_t>(i)]) icon_color(icons_[static_cast<size_t>(i)], on ? 0xffffff : col::MUT2);
    if (on) lv_obj_remove_flag(unders_[static_cast<size_t>(i)], LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(unders_[static_cast<size_t>(i)], LV_OBJ_FLAG_HIDDEN);
    if (lv_obj_t* bd = badges_[static_cast<size_t>(i)]) {
        auto st = defs_[static_cast<size_t>(i)].badge_style;
        uint32_t bg = on ? 0xffffff : (st == TabDef::Red ? col::RED : 0xeef1f5);
        lv_obj_set_style_bg_color(bd, lv_color_hex(bg), 0);
        lv_obj_set_style_bg_opa(bd, on ? 56 : LV_OPA_COVER, 0);
        lv_obj_set_style_text_color(bd, lv_color_hex(on || st == TabDef::Red ? 0xffffff : col::SEC), 0);
    }
}

void TabCard::select(int sel) {
    for (size_t i = 0; i < tabs_.size(); ++i) style_tab((int)i, (int)i == sel);
}

}  // namespace reactor::hmi::kit
