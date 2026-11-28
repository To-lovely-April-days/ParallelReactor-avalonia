// 顶栏 + 页签卡片（family.py topbar() / tabs()，2026-11-26 去掉急停、时间靠右加竖线）
#include "i18n.hpp"
#include "widgets.hpp"

#include <ctime>
#include <cstdint>
#include <string>
#include <vector>

namespace reactor::hmi::kit {

namespace {
const lv_image_dsc_t* NAV_ICON[5] = {&ic::nav_overview28, &ic::nav_program28, &ic::nav_curve28, &ic::nav_records28, &ic::nav_settings28};
const char* NAV_TEXT[5] = {"总览", "程序", "曲线", "记录", "设置"};
}  // namespace

lv_obj_t* TopBar::create(lv_obj_t* parent, int width) {
    // 布局（画布 Opt-TopBar2 · A2）：总高 kTopH = 24 空白 + 80 内容；内容行中线 y = 24 + 40
    width_ = width;
    const int T = kTopSafe, MID = kTopSafe + 40;
    root_ = box(parent, 0, 0, width, kTopH, col::NAVYBAR);
    lv_obj_t* brand = label_at(root_, 20, MID - 12, "HTLAB", 18, 700, 0xffffff);
    lv_obj_set_style_text_letter_space(brand, 1, 0);
    lv_obj_update_layout(brand);
    lv_obj_t* sub = label_at(root_, 20 + lv_obj_get_width(brand) + 12, MID - 9, tr("八通道平行反应仪 · HT-PR8"), 13.5, 400, 0xffffff);
    lv_obj_set_style_text_opa(sub, 184, 0);
    for (int i = 0; i < 5; ++i) {
        int bx = 345 + i * 86;
        lv_obj_t* b = box(root_, bx, T + 2, 76, 76, 0, 0, true);
        on_tap(b, [this, i] { if (on_nav) on_nav(i); });
        nav_circle_[i] = box(b, 14, 3, 48, 48, 0xffffff, 24);
        nav_icon_[i] = icon(nav_circle_[i], NAV_ICON[i], 0xffffff);
        lv_obj_center(nav_icon_[i]);
        nav_text_[i] = label(b, tr(NAV_TEXT[i]), 14, 400, 0xffffff);
        lv_obj_align(nav_text_[i], LV_ALIGN_TOP_MID, 0, 55);
    }
    dot_ = box(root_, 0, MID - 4, 9, 9, col::CONN, 5);
    conn_ = label_at(root_, 0, MID - 10, tr("已连接"), 14, 400, 0xffffff);
    sim_ = label(root_, tr("模拟模式"), 13, 400, 0xffd2cf);
    lv_obj_set_style_bg_color(sim_, lv_color_hex(0xff5a4e), 0);
    lv_obj_set_style_bg_opa(sim_, 46, 0);
    lv_obj_set_style_radius(sim_, 6, 0);
    lv_obj_set_style_pad_hor(sim_, 9, 0);
    lv_obj_set_style_pad_ver(sim_, 4, 0);
    remote_ = label(root_, "", 13, 600, 0xffffff);
    lv_obj_set_style_bg_color(remote_, lv_color_hex(col::ORANGE), 0);
    lv_obj_set_style_bg_opa(remote_, LV_OPA_COVER, 0);
    lv_obj_set_style_radius(remote_, 6, 0);
    lv_obj_set_style_pad_hor(remote_, 9, 0);
    lv_obj_set_style_pad_ver(remote_, 4, 0);
    lv_obj_add_flag(remote_, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(remote_, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_ext_click_area(remote_, 12);
    on_tap(remote_, [this] { if (on_remote) on_remote(); });

    // 铃铛 / 地球：52 的圆，平时淡白底（好认出是按钮），铃铛选中时整圆变白
    int bell_x = width - kBellFromRight - 26;
    bell_ = box(root_, bell_x, MID - 26, 52, 52, 0xffffff, 26);
    lv_obj_set_style_bg_opa(bell_, 26, 0);
    bell_icon_ = icon(bell_, &ic::bell26, 0xffffff);
    lv_obj_center(bell_icon_);
    on_tap(bell_, [this] { if (on_bell) on_bell(); });
    bell_badge_ = box(root_, bell_x + 31, MID - 25, 18, 18, col::RED, 9);
    bell_num_ = label(bell_badge_, "0", 11.5, 600, 0xffffff);
    lv_obj_center(bell_num_);

    globe_ = box(root_, bell_x + 64, MID - 26, 52, 52, 0xffffff, 26);
    lv_obj_set_style_bg_opa(globe_, 26, 0);
    lv_obj_center(icon(globe_, &ic::globe26, 0xffffff));
    on_tap(globe_, [this] { if (on_lang) on_lang(); });
    lv_obj_t* bar = box(root_, bell_x + 64 + 52 + 14, MID - 18, 1, 36, 0xffffff);
    lv_obj_set_style_bg_opa(bar, 70, 0);
    time_ = label(root_, "--:--", 20, 600, 0xffffff);
    date_ = label(root_, "----.--.--", 12.5, 400, 0xffffff);
    lv_obj_set_style_text_opa(date_, 204, 0);
    set_nav(0);
    relayout();
    return root_;
}

void TopBar::relayout() {
    // 时间右对齐到 width-18；左边依次：竖线、地球、铃铛、模拟模式 / PC 控制中、已连接
    const int MID = kTopSafe + 40;
    lv_obj_update_layout(root_);
    lv_obj_align(time_, LV_ALIGN_TOP_RIGHT, -18, MID - 23);
    lv_obj_align(date_, LV_ALIGN_TOP_RIGHT, -18, MID + 3);
    int x = width_ - kBellFromRight - 26 - 16;  // 铃铛左边
    if (!lv_obj_has_flag(sim_, LV_OBJ_FLAG_HIDDEN)) {
        int sw = lv_obj_get_width(sim_), sh = lv_obj_get_height(sim_);
        x -= sw;
        lv_obj_set_pos(sim_, x, MID - sh / 2);
        x -= 14;
    }
    if (!lv_obj_has_flag(remote_, LV_OBJ_FLAG_HIDDEN)) {
        int rw = lv_obj_get_width(remote_), rh = lv_obj_get_height(remote_);
        x -= rw;
        lv_obj_set_pos(remote_, x, MID - rh / 2);
        x -= 14;
    }
    int cw = lv_obj_get_width(conn_), chh = lv_obj_get_height(conn_);
    lv_obj_set_pos(conn_, x - cw, MID - chh / 2);
    lv_obj_set_pos(dot_, x - cw - 15, MID - 4);
}

void TopBar::set_nav(int sel) {
    for (int i = 0; i < 5; ++i) {
        bool on = i == sel;
        lv_obj_set_style_bg_opa(nav_circle_[i], on ? LV_OPA_COVER : 36, 0);
        lv_obj_set_style_border_width(nav_circle_[i], on ? 0 : 1, 0);
        lv_obj_set_style_border_color(nav_circle_[i], lv_color_white(), 0);
        lv_obj_set_style_border_opa(nav_circle_[i], 56, 0);
        icon_color(nav_icon_[i], on ? col::BLUE : 0xffffff);
        lv_obj_set_style_image_opa(nav_icon_[i], on ? LV_OPA_COVER : 225, 0);
        lv_obj_set_style_text_font(nav_text_[i], font(14, on ? 600 : 400), 0);
        lv_obj_set_style_text_opa(nav_text_[i], on ? LV_OPA_COVER : 225, 0);
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
    // 「Coalesce · 实验室电脑」→「Coalesce 控制中 · 实验室电脑」；只有一个名字时「PC 控制中 · 名字」
    std::string t;
    size_t dot = holder.find(" · ");
    if (holder.empty()) t = tr("PC 控制中");
    else if (dot != std::string::npos) t = trf("%s 控制中 · %s", holder.substr(0, dot).c_str(), holder.substr(dot + std::string(" · ").size()).c_str());
    else t = trf("PC 控制中 · %s", holder.c_str());
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
    lv_obj_set_style_bg_opa(bell_, selected ? LV_OPA_COVER : 26, 0);
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
        int iw = d.icon ? scale::U((int)d.icon->header.w) : 0;
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
            im = icon(t, d.icon, col::MUT2, cx, (44 - scale::U((int)d.icon->header.h)) / 2);
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
            // 先把高亮切过去：曲线 / 程序 / 记录页只重建下面的内容、不重建页签（原来高亮一直停在第一次的页签上）。
            // 必须在 on_select 之前：主页的 on_select 会换页，换完 this 已经不在了
            select(idx);
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
