// 釜「乙」（States-D3 / family.vessel_svg）和读数块（states3.values）
#include "i18n.hpp"
#include "widgets.hpp"

#include <cmath>
#include <algorithm>
#include <cstdint>
#include <optional>
#include <string>

namespace reactor::hmi::kit {

using model::VState;

namespace {

void anim_opa_cb(void* o, int32_t v) { lv_obj_set_style_opa(static_cast<lv_obj_t*>(o), (lv_opa_t)v, 0); }
void anim_img_opa_cb(void* o, int32_t v) { lv_obj_set_style_image_opa(static_cast<lv_obj_t*>(o), (lv_opa_t)v, 0); }
void anim_y_cb(void* o, int32_t v) { lv_obj_set_y(static_cast<lv_obj_t*>(o), v); }
void anim_arc_cb(void* o, int32_t v) { lv_arc_set_end_angle(static_cast<lv_obj_t*>(o), (lv_value_precise_t)v); }

// 桨叶：宽度 22 → 4.4 → 22（绕中心缩放）
struct ImpCtx { int cx, w; };
void anim_imp_cb(void* o, int32_t v) {
    lv_obj_t* obj = static_cast<lv_obj_t*>(o);
    auto* c = static_cast<ImpCtx*>(lv_obj_get_user_data(obj));
    int w = c->w * v / 1000;
    if (w < 2) w = 2;
    lv_obj_set_width(obj, w);
    lv_obj_set_x(obj, c->cx - w / 2);
}

void pingpong(lv_obj_t* o, lv_anim_exec_xcb_t cb, int32_t a, int32_t b, uint32_t ms, uint32_t delay = 0) {
    lv_anim_t an;
    lv_anim_init(&an);
    lv_anim_set_var(&an, o);
    lv_anim_set_exec_cb(&an, cb);
    lv_anim_set_values(&an, a, b);
    lv_anim_set_duration(&an, ms / 2);
    lv_anim_set_playback_duration(&an, ms / 2);
    lv_anim_set_repeat_count(&an, LV_ANIM_REPEAT_INFINITE);
    lv_anim_set_delay(&an, delay);
    lv_anim_set_path_cb(&an, lv_anim_path_ease_in_out);
    lv_anim_start(&an);
}

void loop(lv_obj_t* o, lv_anim_exec_xcb_t cb, int32_t a, int32_t b, uint32_t ms, uint32_t delay = 0) {
    lv_anim_t an;
    lv_anim_init(&an);
    lv_anim_set_var(&an, o);
    lv_anim_set_exec_cb(&an, cb);
    lv_anim_set_values(&an, a, b);
    lv_anim_set_duration(&an, ms);
    lv_anim_set_repeat_count(&an, LV_ANIM_REPEAT_INFINITE);
    lv_anim_set_delay(&an, delay);
    lv_anim_start(&an);
}

lv_obj_t* layer(lv_obj_t* root, const lv_image_dsc_t* img) {
    lv_obj_t* o = lv_image_create(root);
    lv_obj_remove_style_all(o);
    lv_image_set_src(o, img);
    lv_obj_set_pos(o, 0, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    return o;
}

}  // namespace

void Vessel::create(lv_obj_t* parent, int x, int y, Size sz) {
    sz_ = sz;
    s_ = sz == L ? 190.0 / 120.0 : sz == S ? 22.0 / 120.0 : 1.0;
    int W = sz == L ? 190 : sz == S ? 22 : 120, H = sz == L ? 212 : sz == S ? 25 : 134;
    root_ = box(parent, x, y, W, H, 0, 0, true);
    lv_obj_remove_flag(root_, LV_OBJ_FLAG_CLICKABLE);
    body_ = layer(root_, sz == L ? &ic::vessel_body_l : sz == S ? &ic::vessel_body_s : &ic::vessel_body);
    liq_ = layer(root_, sz == L ? &ic::vessel_liq_l : sz == S ? &ic::vessel_liq_s : &ic::vessel_liq);
    lv_obj_set_style_image_opa(liq_, 217, 0);
    if (sz != S) {
        heat_ = layer(root_, sz == L ? &ic::vessel_heat_l : &ic::vessel_heat);
        cool_ = layer(root_, sz == L ? &ic::vessel_cool_l : &ic::vessel_cool);
        icon_color(cool_, col::ST_CYN);
    }
    // 气泡在液体里、玻璃高光下面
    if (sz != S) {
        for (int i = 0; i < 4; ++i) {
            static const int bx[4] = {46, 58, 70, 78};
            int d = std::max(3, (int)std::lround(4 * s_));
            bub_[i] = box(root_, (int)std::lround(bx[i] * s_) - d / 2, (int)std::lround(122 * s_), d, d, 0xffffff, d);
            lv_obj_add_flag(bub_[i], LV_OBJ_FLAG_HIDDEN);
        }
    }
    over_ = layer(root_, sz == L ? &ic::vessel_over_l : sz == S ? &ic::vessel_over_s : &ic::vessel_over);
    if (sz != S) {
        edge_ = layer(root_, sz == L ? &ic::vessel_edge_l : &ic::vessel_edge);
        icon_color(edge_, col::ST_RED);
        int iw = (int)std::lround(22 * s_), ih = std::max(3, (int)std::lround(5 * s_));
        int cx = (int)std::lround(60 * s_);
        imp_ = box(root_, cx - iw / 2, (int)std::lround(118.5 * s_) - ih / 2, iw, ih, 0x7f8a95, ih / 2);
        auto* ctx = new ImpCtx{cx, iw};
        lv_obj_set_user_data(imp_, ctx);
        lv_obj_add_event_cb(imp_, [](lv_event_t* e) { delete static_cast<ImpCtx*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, ctx);
        // 序号标
        int bd = (int)std::lround(31 * s_);
        int bcx = (int)std::lround(90 * s_), bcy = (int)std::lround(36 * s_);
        int rd = (int)std::lround(37 * s_);
        ring_ = lv_arc_create(root_);
        lv_obj_remove_style_all(ring_);
        lv_obj_set_size(ring_, rd, rd);
        lv_obj_set_pos(ring_, bcx - rd / 2, bcy - rd / 2);
        lv_arc_set_bg_angles(ring_, 0, 360);
        lv_arc_set_rotation(ring_, 270);
        lv_obj_set_style_arc_width(ring_, std::max(2, (int)std::lround(2.5 * s_)), LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(ring_, lv_color_hex(col::ST_TEAL), LV_PART_INDICATOR);
        lv_obj_set_style_arc_rounded(ring_, true, LV_PART_INDICATOR);
        lv_obj_set_style_arc_opa(ring_, 0, LV_PART_MAIN);
        lv_obj_remove_flag(ring_, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_add_flag(ring_, LV_OBJ_FLAG_HIDDEN);
        badge_ = box(root_, bcx - bd / 2, bcy - bd / 2, bd, bd, col::ST_BLU, bd / 2);
        ring(badge_, 0xffffff, std::max(2, (int)std::lround(2.5 * s_)));
        num_ = label(badge_, "", 14 * s_, 700, 0xffffff);
        lv_obj_center(num_);
        check_ = icon(badge_, &ic::st_check13, 0xffffff);
        lv_obj_center(check_);
        lv_obj_add_flag(check_, LV_OBJ_FLAG_HIDDEN);
        warn_ring_ = box(root_, bcx - bd / 2, bcy - bd / 2, bd, bd, 0, bd / 2, true);
        ring(warn_ring_, col::ST_AMB, std::max(2, (int)std::lround(2.5 * s_)));
        lv_obj_add_flag(warn_ring_, LV_OBJ_FLAG_HIDDEN);
    }
}

void Vessel::stop_anims() {
    lv_obj_t* objs[] = {heat_, cool_, edge_, imp_, bub_[0], bub_[1], bub_[2], bub_[3], ring_, warn_ring_, liq_};
    for (lv_obj_t* o : objs)
        if (o) lv_anim_delete(o, nullptr);
}

void Vessel::set(VState st, int number, std::optional<double> temp, double hold_fraction, bool stirring) {
    bool off = st == VState::Disabled;
    uint32_t lc = off ? 0xd3d9e0 : liquid_color(temp ? *temp : NAN);
    icon_color(liq_, lc);
    lv_obj_set_style_opa(root_, off ? 140 : LV_OPA_COVER, 0);
    if (sz_ == S) return;
    if (num_) {
        lv_label_set_text_fmt(num_, "%d", number);
        lv_obj_center(num_);
    }
    lv_obj_set_style_bg_color(badge_, lv_color_hex(model::badge_color(st)), 0);
    bool done = st == VState::Done;
    if (done) { lv_obj_add_flag(num_, LV_OBJ_FLAG_HIDDEN); lv_obj_remove_flag(check_, LV_OBJ_FLAG_HIDDEN); }
    else { lv_obj_remove_flag(num_, LV_OBJ_FLAG_HIDDEN); lv_obj_add_flag(check_, LV_OBJ_FLAG_HIDDEN); }
    if (ring_ && st == VState::Holding) {
        lv_obj_remove_flag(ring_, LV_OBJ_FLAG_HIDDEN);
        lv_arc_set_angles(ring_, 0, (lv_value_precise_t)(360 * (1.0 - hold_fraction)));
    } else if (ring_) {
        lv_obj_add_flag(ring_, LV_OBJ_FLAG_HIDDEN);
    }
    bool spin = stirring && st != VState::Disabled;
    int key = (int)st * 2 + (spin ? 1 : 0);
    if (key == last_state_) return;  // 动画只在状态 / 搅拌变化时重建
    last_state_ = key;
    stop_anims();
    // 加热带：升温橙色 2 s 呼吸；超温红色 0.8 s
    if (model::vs_heater(st)) {
        lv_obj_remove_flag(heat_, LV_OBJ_FLAG_HIDDEN);
        icon_color(heat_, st == VState::OverTemp ? col::ST_RED : col::ST_ORG);
        pingpong(heat_, anim_img_opa_cb, 90, 255, st == VState::OverTemp ? 800 : 2000);
    } else {
        lv_obj_add_flag(heat_, LV_OBJ_FLAG_HIDDEN);
    }
    if (st == VState::Cooling) {
        lv_obj_remove_flag(cool_, LV_OBJ_FLAG_HIDDEN);
        pingpong(cool_, anim_img_opa_cb, 110, 255, 1600);
    } else {
        lv_obj_add_flag(cool_, LV_OBJ_FLAG_HIDDEN);
    }
    if (st == VState::OverTemp || st == VState::OverPressure) {
        lv_obj_remove_flag(edge_, LV_OBJ_FLAG_HIDDEN);
        pingpong(edge_, anim_img_opa_cb, 40, 255, 800);
    } else {
        lv_obj_add_flag(edge_, LV_OBJ_FLAG_HIDDEN);
    }
    if (spin) {
        lv_obj_remove_flag(imp_, LV_OBJ_FLAG_HIDDEN);
        pingpong(imp_, anim_imp_cb, 1000, 200, 1200);
    } else {
        lv_obj_set_width(imp_, (int)std::lround(22 * s_));
        lv_obj_set_x(imp_, (int)std::lround(60 * s_) - (int)std::lround(22 * s_) / 2);
        // 不转的时候桨叶就是底部那个灰色块（在 over 图层里），不用单独显示
        lv_obj_add_flag(imp_, LV_OBJ_FLAG_HIDDEN);
    }
    for (int i = 0; i < 4; ++i) {
        if (!bub_[i]) continue;
        if (model::vs_bubbles(st)) {
            static const int delay[4] = {0, 600, 1200, 300};
            lv_obj_remove_flag(bub_[i], LV_OBJ_FLAG_HIDDEN);
            loop(bub_[i], anim_y_cb, (int)std::lround(120 * s_), (int)std::lround(90 * s_), 1800, delay[i]);
            pingpong(bub_[i], anim_opa_cb, 0, 242, 1800, delay[i]);
        } else {
            lv_obj_add_flag(bub_[i], LV_OBJ_FLAG_HIDDEN);
        }
    }
    if (st == VState::Offline) {
        lv_obj_remove_flag(warn_ring_, LV_OBJ_FLAG_HIDDEN);
        pingpong(warn_ring_, anim_opa_cb, 50, 255, 1600);
    } else {
        lv_obj_add_flag(warn_ring_, LV_OBJ_FLAG_HIDDEN);
    }
    if (st == VState::OverTemp || st == VState::OverPressure) {
        // 超温、超压：液体闪白（states4 blink）
        pingpong(liq_, anim_img_opa_cb, 217, 90, 1200);
    } else {
        lv_obj_set_style_image_opa(liq_, 217, 0);
    }
    (void)anim_arc_cb;
}

// ------------------------------------------------------------ 读数块
// 画布 viewBox 宽 150（中心 75）：温度 44px 居中（基线 64）；压力 / 吸气量 22px，左起 x=41（图标 x=19），
// 基线 108 / 144；提示行 12.5px 居中，基线 170。
void Readings::create(lv_obj_t* parent, int x, int y, int w, double s) {
    w_ = w; s_ = s;
    root_ = box(parent, x, y, w, (int)std::lround(182 * s), 0, 0, true);
    tbg_ = box(root_, w / 2 - (int)(64 * s), (int)(20 * s), (int)(128 * s), (int)(54 * s), col::RED, 8);
    lv_obj_add_flag(tbg_, LV_OBJ_FLAG_HIDDEN);
    t_ = label(root_, "", 44 * s, 600, col::DEEP);
    tu_ = label(root_, "°C", 13 * s, 500, col::MUT);
    int l = (int)std::lround((75 - 56) * s * w / (150 * s));
    l = w / 2 - (int)std::lround(56 * s);
    pbg_ = box(root_, w / 2 - (int)(64 * s), (int)(84 * s), (int)(128 * s), (int)(34 * s), col::RED, 8);
    lv_obj_add_flag(pbg_, LV_OBJ_FLAG_HIDDEN);
    pi_ = icon(root_, &ic::st_gauge16, col::MUT, l, (int)std::lround(92 * s));
    p_ = label(root_, "", 22 * s, 600, col::DEEP);
    pu_ = label(root_, "MPa", 12 * s, 500, col::MUT);
    mi_ = icon(root_, &ic::st_drop16, col::MUT, l, (int)std::lround(128 * s));
    mm_ = label(root_, "", 22 * s, 600, col::DEEP);
    mu_ = label(root_, "mmol", 12 * s, 500, col::MUT);
    hi_ = icon(root_, &ic::st_up13, col::ST_GRN, 0, 0);
    ht_ = label(root_, "", 12.5 * s, 600, col::ST_GRN);
}

void Readings::set(const model::Model& m, int id) {
    const model::ChannelSnap& c = m.ch(id);
    VState st = model::vstate(m, id);
    bool off = st == VState::Disabled;
    uint32_t num = off ? 0x9aa4b1 : col::DEEP;
    bool tbad = st == VState::OverTemp, pbad = st == VState::OverPressure, offline = st == VState::Offline;
    std::string t = model::fmt_temp(c);
    lv_label_set_text(t_, t.c_str());
    lv_obj_set_style_text_font(t_, font((t.size() < 5 ? 44 : 38) * s_, 600), 0);
    lv_obj_set_style_text_color(t_, lv_color_hex(tbad ? 0xffffff : num), 0);
    lv_obj_set_style_text_color(tu_, lv_color_hex(tbad ? 0xffd7d3 : col::MUT), 0);
    lv_obj_update_layout(root_);
    int tw = lv_obj_get_width(t_), uw = lv_obj_get_width(tu_);
    int tx = w_ / 2 - (tw + 4 + uw) / 2;
    place_baseline(t_, tx, (int)std::lround(64 * s_));
    place_baseline(tu_, tx + tw + 4, (int)std::lround(64 * s_));
    if (tbad) lv_obj_remove_flag(tbg_, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(tbg_, LV_OBJ_FLAG_HIDDEN);
    if (pbad) lv_obj_remove_flag(pbg_, LV_OBJ_FLAG_HIDDEN); else lv_obj_add_flag(pbg_, LV_OBJ_FLAG_HIDDEN);

    int l = w_ / 2 - (int)std::lround(56 * s_);
    int vx = l + (int)std::lround(22 * s_);
    lv_image_set_src(pi_, offline ? &ic::st_off16 : &ic::st_gauge16);
    icon_color(pi_, offline ? col::ST_AMB : (pbad ? 0xffffff : col::MUT));
    lv_label_set_text(p_, model::fmt_mpa(c.pressurePsi).c_str());
    lv_obj_set_style_text_color(p_, lv_color_hex(pbad ? 0xffffff : (offline ? 0xb4bdc8 : num)), 0);
    lv_obj_set_style_text_color(pu_, lv_color_hex(pbad ? 0xffd7d3 : col::MUT), 0);
    lv_obj_update_layout(root_);
    place_baseline(p_, vx, (int)std::lround(108 * s_));
    place_baseline(pu_, vx + lv_obj_get_width(p_) + 4, (int)std::lround(108 * s_));

    bool has_m = c.gasConsumed.has_value() && *c.gasConsumed > 0;
    uint32_t mcol = st == VState::Reacting ? col::ST_GRN : (has_m ? num : 0xb4bdc8);
    icon_color(mi_, has_m ? mcol : col::MUT);
    lv_label_set_text(mm_, has_m ? model::fmt_mmol(c.gasConsumed).c_str() : "—");
    lv_obj_set_style_text_color(mm_, lv_color_hex(mcol), 0);
    lv_obj_update_layout(root_);
    place_baseline(mm_, vx, (int)std::lround(144 * s_));
    place_baseline(mu_, vx + lv_obj_get_width(mm_) + 4, (int)std::lround(144 * s_));

    model::Hint h = model::hint(m, id);
    if (h.text.empty()) {
        lv_obj_add_flag(hi_, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(ht_, LV_OBJ_FLAG_HIDDEN);
        return;
    }
    lv_obj_remove_flag(hi_, LV_OBJ_FLAG_HIDDEN);
    lv_obj_remove_flag(ht_, LV_OBJ_FLAG_HIDDEN);
    lv_image_set_src(hi_, state_icon(h.icon, 13));
    icon_color(hi_, h.color);
    lv_label_set_text(ht_, h.text.c_str());
    lv_obj_set_style_text_color(ht_, lv_color_hex(h.color), 0);
    lv_obj_update_layout(root_);
    int hw = 13 + 4 + lv_obj_get_width(ht_);
    int hx = w_ / 2 - hw / 2;
    lv_obj_set_pos(hi_, hx, (int)std::lround(159 * s_));
    place_baseline(ht_, hx + 17, (int)std::lround(170 * s_));
}

}  // namespace reactor::hmi::kit
