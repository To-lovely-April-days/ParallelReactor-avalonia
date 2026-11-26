// 主页：装置总览（Main-F1）/ 气路图（Main-F2）/ 方案总览（Plan-All、Plan-One）+ 右侧「全部釜」竖栏
#include "../app.hpp"
#include "../kit/draw.hpp"
#include "../kit/i18n.hpp"

#include <algorithm>
#include <cstdint>
#include <cstdlib>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;
using model::VState;

namespace {

std::string chans_text(const std::vector<int>& v) {
    std::string s;
    for (size_t i = 0; i < v.size(); ++i) s += (i ? "、" : "") + std::to_string(v[i]);
    return s;
}

// ============================================================ 右侧竖栏（全部釜：搅拌 / 手动阀控 / 全部停止）
class Column {
public:
    void build(lv_obj_t* parent, int x, int y, int w, int h, App& app) {
        app_ = &app;
        lv_obj_t* c = card(parent, x, y, w, h);
        lv_obj_t* t = label(c, tr("全部釜"), 12, 400, col::MUT);
        lv_obj_align(t, LV_ALIGN_TOP_MID, 0, 17);
        stir_ = col_btn(c, w / 2, 50, &ic::stir29, tr("搅拌"));
        on_tap(stir_.root, [this] {
            const Model& m = app_->m();
            if (m.snap.stir.on) {
                confirm_stop(tr("停止搅拌？"), "", "", tr("8 路的搅拌一起停（共用一个电机）。"), tr("不停"), tr("停止搅拌"),
                             [this] { app_->act().stir_run(false); });
            } else {
                app_->act().stir_run(true);
            }
        });
        rpm_ = box(c, (w - 78) / 2, 175, 78, 38, col::TILE, 8);
        ring(rpm_, col::RING, 2);
        rpm_v_ = label(rpm_, "", 16, 600, col::DEEP);
        rpm_u_ = label(rpm_, "rpm", 12, 400, col::MUT);
        on_tap(rpm_, [this] {
            NumSpec s;
            s.title = tr("搅拌转速 · 全部釜");
            s.unit = "rpm";
            s.value = app_->m().snap.stir.rpmSp;
            s.min = 0; s.max = 450;
            s.last = s.value;
            NumPad::open(s, [this](double v) { app_->act().stir_set_rpm((int)std::lround(v)); });
        });
        box(c, (w - 78) / 2, 230, 78, 1, 0xe3e8ee);
        valve_ = col_btn(c, w / 2, 247, &ic::valve29, tr("手动阀控"));
        // 打开时：实心绿圆 + 两圈呼吸光环（画布定稿 D）
        // 光环挂在竖栏卡片上（不挂在按钮里）：按钮区域从圆钮顶边开始，挂在里面会被裁掉顶部
        for (int i = 0; i < 2; ++i) {
            int d = 62 + 8 + i * 8;  // 70 / 78：不压下面的文字、不贴卡片边
            halo_[i] = box(c, w / 2 - d / 2, 247 + 31 - d / 2, d, d, 0, d / 2, true);
            ring(halo_[i], col::GREEN, 2);
            lv_obj_move_to_index(halo_[i], lv_obj_get_index(valve_.root));  // 放在圆钮后面
            lv_obj_add_flag(halo_[i], LV_OBJ_FLAG_HIDDEN);
            lv_obj_remove_flag(halo_[i], LV_OBJ_FLAG_CLICKABLE);
        }
        on_tap(valve_.root, [this] { app_->act().manual_valve(!app_->m().snap.gas.manualValve); });
        lv_obj_t* st = box(c, (w - 68) / 2, h - 135, 68, 68, col::RED, 34);
        lv_obj_set_style_shadow_width(st, 8, 0);
        lv_obj_set_style_shadow_offset_y(st, 2, 0);
        lv_obj_set_style_shadow_color(st, lv_color_hex(col::RED), 0);
        lv_obj_set_style_shadow_opa(st, 90, 0);
        lv_obj_center(box(st, 0, 0, 17, 17, 0xffffff, 3));
        on_tap(st, [this] { app_->stop_all_confirm(); });
        lv_obj_t* l1 = label(c, tr("全部停止"), 13.5, 400, col::SEC);
        lv_obj_align(l1, LV_ALIGN_TOP_MID, 0, h - 61);
        stop_n_ = label(c, "", 14.5, 700, col::RED);
        stop_y_ = h - 40;
        lv_obj_align(stop_n_, LV_ALIGN_TOP_MID, 0, stop_y_);
    }
    void update(const Model& m) {
        const auto& s = m.snap.stir;
        char b[32];
        std::snprintf(b, sizeof b, "%d rpm", s.rpmSp);
        stir_.set(s.on, s.on ? b : tr("停"), s.on ? col::BLUE : col::SEC, s.on ? (int)(360.0 * s.rpmSp / 450 * 0.28) + 0 : 0);
        if (s.on) lv_arc_set_angles(stir_.arc, 0, 100);
        lv_label_set_text_fmt(rpm_v_, "%d", s.rpmSp);
        lv_obj_update_layout(rpm_);
        int tw = lv_obj_get_width(rpm_v_) + 4 + lv_obj_get_width(rpm_u_);
        lv_obj_align(rpm_v_, LV_ALIGN_LEFT_MID, (78 - tw) / 2, 0);
        lv_obj_align(rpm_u_, LV_ALIGN_LEFT_MID, (78 - tw) / 2 + lv_obj_get_width(rpm_v_) + 4, 2);
        bool mv = m.snap.gas.manualValve;
        valve_.set(mv, tr(mv ? "开" : "关"), mv ? col::ST_GRN : col::SEC, 0);
        set_valve_look(mv);
        // 停止帧对 8 路都发；按钮上写会被停下来的路数（有动作的通道，转「手动停止」），没有就写「无动作」
        int n = (int)model::active_channels(m).size();
        lv_label_set_text(stop_n_, n > 0 ? trf("%d 路", n).c_str() : tr("无动作"));
        lv_obj_set_style_text_color(stop_n_, lv_color_hex(n > 0 ? col::RED : col::MUT), 0);
        lv_obj_set_style_text_font(stop_n_, font(n > 0 ? 14.5 : 13, n > 0 ? 700 : 400), 0);
        lv_obj_align(stop_n_, LV_ALIGN_TOP_MID, 0, stop_y_ + (n > 0 ? 0 : 1));
    }

private:
    // 手动阀控打开：圆填绿、图标变白、右上小点藏掉，两圈光环一明一暗（1.2 s 一个来回，第二圈晚半拍）
    void set_valve_look(bool on) {
        lv_obj_set_style_bg_color(valve_.circle, lv_color_hex(on ? col::GREEN : 0xffffff), 0);
        ring(valve_.circle, on ? col::GREEN : 0xdbe5f0, 3);
        icon_color(valve_.icon, on ? 0xffffff : col::ST_GRY);
        if (on) lv_obj_add_flag(valve_.dot, LV_OBJ_FLAG_HIDDEN);
        else lv_obj_remove_flag(valve_.dot, LV_OBJ_FLAG_HIDDEN);
        if (on == halo_on_) return;
        halo_on_ = on;
        for (int i = 0; i < 2; ++i) {
            lv_obj_t* h = halo_[i];
            lv_anim_delete(h, nullptr);
            if (!on) {
                lv_obj_add_flag(h, LV_OBJ_FLAG_HIDDEN);
                continue;
            }
            lv_obj_remove_flag(h, LV_OBJ_FLAG_HIDDEN);
            lv_anim_t a;
            lv_anim_init(&a);
            lv_anim_set_var(&a, h);
            lv_anim_set_values(&a, i == 0 ? 60 : 20, i == 0 ? 230 : 160);
            lv_anim_set_duration(&a, 600);
            lv_anim_set_playback_duration(&a, 600);
            lv_anim_set_delay(&a, i * 300);
            lv_anim_set_repeat_count(&a, LV_ANIM_REPEAT_INFINITE);
            lv_anim_set_exec_cb(&a, [](void* o, int32_t v) { lv_obj_set_style_border_opa(static_cast<lv_obj_t*>(o), (lv_opa_t)v, 0); });
            lv_anim_start(&a);
        }
    }
    App* app_ = nullptr;
    ColBtn stir_, valve_;
    lv_obj_t* halo_[2] = {nullptr, nullptr};
    bool halo_on_ = false;
    lv_obj_t *rpm_ = nullptr, *rpm_v_ = nullptr, *rpm_u_ = nullptr, *stop_n_ = nullptr;
    int stop_y_ = 0;
};

// ============================================================ 装置总览：一张釜卡片
class ChanCard {
public:
    void build(lv_obj_t* parent, int x, int y, int w, int h, int ch, App& app) {
        ch_ = ch; w_ = w;
        root_ = card(parent, x, y, w, h);
        on_tap(root_, [&app, ch] {
            // 启用的路开快捷面板；停用的路进整页（启用 / 停用在整页圆钮和设置 · 通道与气路）
            if (app.m().ch(ch).installed) app.open_quick(ch);
            else app.go(PageId::ChanFull, ch);
        });
        lv_obj_t* hd = box(root_, 0, 0, w, 54, 0, 0, true);
        box(hd, 0, 53, w, 1, col::DIV);
        title_ = label_at(hd, 14, 9, "", 16, 600, col::BLUE);
        dot_line(title_);
        timer_i_ = icon(hd, &ic::timer14, col::INK, 0, 13);
        timer_ = label(hd, "", 14, 600, col::INK);
        step_ = label_at(hd, 14, 34, "", 12, 400, col::MUT);
        right_ = label(hd, "", 12, 400, col::MUT);
        bar_ = box(hd, 0, 51, 0, 3, col::BLUE);
        int body = (h - 54);
        int vy = 54 + (body - 134) / 2 + 14;
        vessel_.create(root_, 4, vy, Vessel::M);
        stub_ = box(root_, 4 + 59, vy + 126, 3, 8, col::LINE);
        readings_.create(root_, w - 150 - 2, 54 + (body - 182) / 2 - 2, 150, 1.0);
        ring_ = box(root_, -3, -3, w + 6, h + 6, 0, 15, true);
        ring(ring_, col::BLUE, 3);
        lv_obj_add_flag(ring_, LV_OBJ_FLAG_HIDDEN);
    }
    void selected(bool on) {
        lv_obj_set_style_outline_width(root_, on ? 3 : 0, 0);
        lv_obj_set_style_outline_color(root_, lv_color_hex(col::BLUE), 0);
    }
    void update(const Model& m) {
        const auto& c = m.ch(ch_);
        const auto& p = m.progress[static_cast<size_t>(ch_ - 1)];
        VState st = model::vstate(m, ch_);
        bool prog = c.program && (c.program->running || c.program->waitingGas);
        if (prog) {
            lv_label_set_text(title_, p.programName.c_str());
            lv_obj_set_style_text_font(title_, font(16, 600), 0);
            lv_obj_set_style_text_color(title_, lv_color_hex(col::BLUE), 0);
            lv_obj_set_width(title_, w_ - 120);
            lv_label_set_text(timer_, model::fmt_hms(p.elapsedSec).c_str());
            lv_obj_update_layout(timer_);
            int tw = lv_obj_get_width(timer_);
            lv_obj_set_pos(timer_, w_ - 14 - tw, 12);
            lv_obj_set_pos(timer_i_, w_ - 14 - tw - 18, 14);
            lv_obj_remove_flag(timer_, LV_OBJ_FLAG_HIDDEN);
            lv_obj_remove_flag(timer_i_, LV_OBJ_FLAG_HIDDEN);
            lv_label_set_text(step_, trf("第 %d/%d 步", c.program->stepIndex + 1, c.program->stepCount).c_str());
            std::string r;
            if (c.program->waitingGas)
                r = p.waitingFor.empty() ? std::string(tr("等气路")) : trf("等 %s 号用完气路", chans_text(p.waitingFor).c_str());
            else if (p.remainingSec)
                r = trf("剩 %s", model::fmt_hmin(*p.remainingSec).c_str());
            lv_label_set_text(right_, r.c_str());
            lv_obj_update_layout(right_);
            lv_obj_set_pos(right_, w_ - 14 - lv_obj_get_width(right_), 34);
            double tot = (double)p.elapsedSec + (p.remainingSec ? (double)*p.remainingSec : 0);
            lv_obj_set_width(bar_, tot > 0 ? (int)std::lround(w_ * p.elapsedSec / tot) : 0);
        } else {
            // 正在泄漏测试的那几路（多半是停用的路）：别显示「已停用」，釜里其实带着测试压力
            bool leak = false;
            if (m.snap.leakTest.running)
                for (auto& lc : m.snap.leakTest.channels) leak |= lc.id == c.id;
            bool stopped = st == VState::Stopped && !leak;
            lv_label_set_text(title_, tr(leak ? "泄漏测试中" : stopped ? "手动停止" : c.installed ? "没有程序 · 手动" : "这一路已停用"));
            lv_obj_set_style_text_font(title_, font(stopped ? 16 : 15, stopped ? 600 : 400), 0);
            lv_obj_set_style_text_color(title_, lv_color_hex(stopped ? col::INK : col::MUT), 0);
            lv_obj_set_width(title_, w_ - 28);
            lv_obj_set_y(title_, stopped ? 9 : 10);
            lv_obj_add_flag(timer_, LV_OBJ_FLAG_HIDDEN);
            lv_obj_add_flag(timer_i_, LV_OBJ_FLAG_HIDDEN);
            // 手动停止的副行：中止的程序名，或者「手动操作已停止」
            std::string sub;
            if (stopped) sub = !(c.program && c.program->state == "aborted") ? std::string(tr("加热、进气已停 · 可重新开始"))
                               : p.programName.empty()                      ? std::string(tr("程序已中止"))
                                                                             : trf("程序「%s」已中止", p.programName.c_str());
            lv_label_set_text(step_, sub.c_str());
            lv_label_set_text(right_, "");
            lv_obj_set_width(bar_, 0);
        }
        vessel_.set(st, ch_, c.tempC, st == VState::Holding ? p.stepFraction : 0);
        uint32_t sc = st == VState::Pressing ? col::ST_GRN : st == VState::QueuedGas ? col::ST_AMB : col::LINE;
        lv_obj_set_style_bg_color(stub_, lv_color_hex(sc), 0);
        lv_obj_set_style_opa(stub_, st == VState::Disabled ? 120 : 255, 0);
        readings_.set(m, ch_);
    }

private:
    int ch_ = 1, w_ = 271;
    lv_obj_t *root_ = nullptr, *title_ = nullptr, *timer_ = nullptr, *timer_i_ = nullptr, *step_ = nullptr, *right_ = nullptr,
             *bar_ = nullptr, *stub_ = nullptr, *ring_ = nullptr;
    Vessel vessel_;
    Readings readings_;
};

// ============================================================ 气路图：一列
class GasColumn {
public:
    void build(lv_obj_t* card, int cx, int top, int my, int ch, App& app) {
        ch_ = ch; my_ = my; cx_ = cx;
        readings_.create(card, cx - 65, top, 130, 0.9);
        vy_ = top + 176;
        vessel_.create(card, cx - 60, vy_, Vessel::M);
        lv_obj_t* hit = box(card, cx - 64, top, 128, vy_ + 134 - top, 0, 0, true);
        on_tap(hit, [&app, ch] { app.go(PageId::ChanFull, ch); });
        pipe_ = lv_line_create(card);
        pts_[0] = {(lv_value_precise_t)cx, (lv_value_precise_t)(vy_ + 126)};
        pts_[1] = {(lv_value_precise_t)cx, (lv_value_precise_t)my};
        lv_line_set_points(pipe_, pts_, 2);
        chk_bg_ = icon(card, &ic::chk_valve_bg, 0xffffff, cx - 10, vy_ + 150 - 10);
        chk_ = icon(card, &ic::chk_valve, 0x4a505b, cx - 10, vy_ + 150 - 10);
        vfill_ = icon(card, &ic::valve_fill, 0xffffff, cx - 11, vy_ + 182 - 13);
        vline_ = icon(card, &ic::valve_line, 0x4a505b, cx - 11, vy_ + 182 - 13);
        lv_obj_t* vhit = box(card, cx - 18, vy_ + 165, 36, 34, 0, 0, true);
        on_tap(vhit, [&app, ch] {
            const Model& m = app.m();
            if (!m.snap.gas.manualValve || m.ch(ch).programOwned) return;  // 后台也会拒绝；这里只是不发
            app.act().set_valve(ch, !m.ch(ch).valveOpen);
        });
        char sv[8];
        std::snprintf(sv, sizeof sv, "SV%d", ch);
        label_at(card, cx + 15, vy_ + 182 - 8, sv, 11.5, 400, col::MUT);
        hour_ = icon(card, &ic::hourglass, col::ST_AMB, cx - 34, vy_ + 182 - 9);
    }
    void update(const Model& m) {
        const auto& c = m.ch(ch_);
        VState st = model::vstate(m, ch_);
        readings_.set(m, ch_);
        vessel_.set(st, ch_, c.tempC, st == VState::Holding ? m.progress[static_cast<size_t>(ch_ - 1)].stepFraction : 0);
        bool flow = st == VState::Pressing && c.valveOpen;
        bool open = c.valveOpen;
        bool queued = st == VState::QueuedGas;
        uint32_t pc = flow ? col::ST_GRN : queued ? col::ST_AMB : col::LINE;
        lv_obj_set_style_line_color(pipe_, lv_color_hex(pc), 0);
        lv_obj_set_style_line_width(pipe_, flow ? 3 : 3, 0);
        lv_obj_set_style_line_dash_width(pipe_, flow ? 6 : queued ? 3 : 0, 0);
        lv_obj_set_style_line_dash_gap(pipe_, flow ? 6 : queued ? 5 : 0, 0);
        icon_color(chk_, flow ? col::ST_GRN : 0x4a505b);
        icon_color(vfill_, open ? col::GREEN : 0xffffff);
        icon_color(vline_, open ? col::GREEN : 0x4a505b);
        if (queued) lv_obj_remove_flag(hour_, LV_OBJ_FLAG_HIDDEN);
        else lv_obj_add_flag(hour_, LV_OBJ_FLAG_HIDDEN);
    }
    int cx() const { return cx_; }

private:
    int ch_ = 1, my_ = 0, cx_ = 0, vy_ = 0;
    Readings readings_;
    Vessel vessel_;
    lv_obj_t *pipe_ = nullptr, *chk_ = nullptr, *chk_bg_ = nullptr, *vfill_ = nullptr, *vline_ = nullptr, *hour_ = nullptr;
    lv_point_precise_t pts_[2];
};

// ============================================================ 方案总览（甘特图）
const char* step_short(const std::string& kind) {
    if (kind == "leak") return "气密";
    if (kind == "purge" || kind == "exchange") return "置换";
    if (kind == "heat") return "升温";
    if (kind == "stir") return "搅拌";
    if (kind == "press") return "充压";
    if (kind == "hold") return "保持";
    if (kind == "react") return "反应";
    if (kind == "vent") return "放空";
    return "结束";
}

struct Axis {
    int64_t t0 = 0, t1 = 0;
    int x0 = 0, x1 = 0;
    float x(int64_t t) const { return x0 + float(x1 - x0) * float(t - t0) / float(t1 - t0); }
};

int64_t floor_hour(int64_t ms) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    tm.tm_min = 0; tm.tm_sec = 0;
    return static_cast<int64_t>(std::mktime(&tm)) * 1000;
}

}  // namespace

// ============================================================ 主页
class HomePage : public Page {
public:
    explicit HomePage(int tab) : tab_(tab >= 10 ? 2 : tab), one_(tab >= 10 ? tab - 10 : 0) {}
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        BH_ = app.h() - 60 - 56 - 12;
        TabCard* tc = &tabs_;
        tc->create(root, 8, 4, W_ - 16, {{&ic::tab_vessel, "装置总览"}, {&ic::tab_gas, "气路图"}, {&ic::tab_plan, "方案总览"}}, tab_);
        tc->on_select = [this](int i) { app_->go(PageId::Home, i); };
        int colw = 126;
        column_.build(root, W_ - 12 - colw, 56, colw, BH_, app);
        int gw = W_ - 12 - 12 - colw - 12;
        if (tab_ == 0) build_overview(root, gw);
        else if (tab_ == 1) build_gas(root, gw);
        else build_plan(root, gw);
    }
    void update(const Model& m) override {
        column_.update(m);
        for (auto& c : cards_) c.update(m);
        for (auto& g : gas_) g.update(m);
        if (gas_card_) update_gas(m);
        if (plan_card_) update_plan(m);
    }
    void select_card(int ch) {
        for (size_t i = 0; i < cards_.size(); ++i) cards_[i].selected((int)i + 1 == ch);
    }

private:
    // ---------------------------------------------------------- 装置总览
    void build_overview(lv_obj_t* root, int gw) {
        int cw = (gw - 3 * 12) / 4, ch = (BH_ - 12) / 2;
        cards_.resize(8);
        for (int i = 0; i < 8; ++i)
            cards_[static_cast<size_t>(i)].build(root, 12 + (i % 4) * (cw + 12), 56 + (i / 4) * (ch + 12), cw, ch, i + 1, *app_);
    }

    // ---------------------------------------------------------- 气路图
    void build_gas(lv_obj_t* root, int gw) {
        gas_card_ = card(root, 12, 56, gw, BH_);
        int top = 66, my = top + 176 + 234;
        my_ = my;
        // 总管
        main_ = box(gas_card_, 18, my - 2, gw - 32, 5, col::LINE, 3);
        main_g_ = box(gas_card_, 32, my - 2, 10, 5, col::GREEN, 3);
        gas_.resize(8);
        for (int i = 0; i < 8; ++i) {
            int cx = (int)std::lround((122 + 146 * i) * gw / 1256.0);
            if (i > 0) box(gas_card_, cx - (int)std::lround(73 * gw / 1256.0), top - 6, 1, my - top - 40, 0xeef1f5);
            gas_[static_cast<size_t>(i)].build(gas_card_, cx, top, my, i + 1, *app_);
        }
        lv_obj_t* tl = label(gas_card_, tr("共用气路总管"), 12, 400, col::MUT);
        lv_obj_update_layout(tl);
        lv_obj_set_pos(tl, (gas_[3].cx() + gas_[4].cx()) / 2 - lv_obj_get_width(tl) / 2, my + 10);
        struct Src { int x; const char* name; };
        int xs[4] = {32, 82, 132, gw - 28};
        const char* names[4] = {"惰性气体", "气体 A", "气体 B", "排空"};
        for (int k = 0; k < 4; ++k) {
            int x = xs[k];
            src_pipe_[k] = box(gas_card_, x - 1, my, 3, 34, col::LINE);
            src_fill_[k] = icon(gas_card_, &ic::valve_fill, 0xffffff, x - 11, my + 46 - 13);
            src_line_[k] = icon(gas_card_, &ic::valve_line, 0x4a505b, x - 11, my + 46 - 13);
            src_pipe2_[k] = box(gas_card_, x - 1, my + 57, 3, 11, col::LINE);
            src_lbl_[k] = label(gas_card_, tr(names[k]), 12, 400, col::MUT);
            lv_obj_update_layout(src_lbl_[k]);
            lv_obj_set_pos(src_lbl_[k], x - lv_obj_get_width(src_lbl_[k]) / 2, my + 76);
            // 点阀门开 / 关（照原 C# 版：要先打开主页右边的「手动阀控」；气源互斥、气源和排空不同时开由后台把关）
            lv_obj_t* hit = box(gas_card_, x - 24, my + 20, 48, 76, 0, 0, true);
            App* app = app_;
            on_tap(hit, [app, k] {
                const Model& mm = app->m();
                if (!mm.snap.gas.manualValve) {
                    DialogSpec d;
                    d.color = col::BLUE;
                    d.icon = nullptr;
                    d.title = tr("要先打开手动阀控");
                    d.primary = tr("知道了");
                    d.body = [](lv_obj_t* b, int w) {
                        lv_obj_t* l = label_at(b, 0, 0, tr("气源阀和排空阀要先在主页右边打开「手动阀控」才能开关。"), 15, 400, col::INK);
                        lv_obj_set_width(l, w);
                        lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
                        return 44;
                    };
                    Dialog::open(d);
                    return;
                }
                bool now = false;
                if (k == 3) now = mm.snap.gas.ventOpen;
                else {
                    static const char* ids[3] = {"inert", "a", "b"};
                    for (auto& in : mm.snap.gas.inlets)
                        if (in.id == ids[k]) now = in.open;
                }
                app->act().set_gas_source(k, !now);
            });
        }
        lv_obj_t* f = label(gas_card_, tr("点反应釜进入这一路 · 点阀门开关，要先打开手动阀控"), 12.5, 400, col::MUT);
        lv_obj_align(f, LV_ALIGN_BOTTOM_MID, 0, -14);
    }
    void update_gas(const Model& m) {
        bool open[4] = {false, false, false, m.snap.gas.ventOpen};
        for (auto& in : m.snap.gas.inlets) {
            if (in.id == "inert") open[0] = in.open;
            if (in.id == "a") open[1] = in.open;
            if (in.id == "b") open[2] = in.open;
        }
        for (int k = 0; k < 4; ++k) {
            uint32_t c = open[k] ? col::GREEN : col::LINE;
            lv_obj_set_style_bg_color(src_pipe_[k], lv_color_hex(c), 0);
            lv_obj_set_style_bg_color(src_pipe2_[k], lv_color_hex(c), 0);
            icon_color(src_fill_[k], open[k] ? col::GREEN : 0xffffff);
            icon_color(src_line_[k], open[k] ? col::GREEN : 0x4a505b);
            lv_obj_set_style_text_color(src_lbl_[k], lv_color_hex(open[k] ? col::INK : col::MUT), 0);
            lv_obj_set_style_text_font(src_lbl_[k], font(12, open[k] ? 600 : 400), 0);
        }
        // 总管上有气的一段：从开着的气源到最远一个开着进气阀的通道
        int src = open[0] ? 32 : open[1] ? 82 : open[2] ? 132 : -1;
        int far = -1;
        for (int i = 0; i < 8; ++i)
            if (m.snap.channels[static_cast<size_t>(i)].valveOpen) far = gas_[static_cast<size_t>(i)].cx();
        if (src >= 0 && far > src) {
            lv_obj_remove_flag(main_g_, LV_OBJ_FLAG_HIDDEN);
            lv_obj_set_pos(main_g_, src, my_ - 2);
            lv_obj_set_width(main_g_, far - src + 2);
        } else {
            lv_obj_add_flag(main_g_, LV_OBJ_FLAG_HIDDEN);
        }
    }

    // ---------------------------------------------------------- 方案总览
    void build_plan(lv_obj_t* root, int gw) {
        plan_card_ = card(root, 12, 56, gw, BH_);
        gw_ = gw;
    }
    void update_plan(const Model& m) {
        // 每次快照整卡重画（数据量小：8 行 × 9 段）
        lv_obj_clean(plan_card_);
        if (one_) draw_plan_one(m);
        else draw_plan_all(m);
    }

    Axis axis_for(const Model& m, int x0, int x1, int hours_min) const {
        int64_t lo = m.snap.ts, hi = m.snap.ts;
        for (auto& p : m.plans)
            for (auto& s : p.segs) { lo = std::min(lo, s.start); hi = std::max(hi, s.end); }
        Axis a;
        a.t0 = floor_hour(lo);
        int64_t span = std::max<int64_t>((int64_t)hours_min * 3600000, ((hi - a.t0) / 3600000 + 1) * 3600000);
        a.t1 = a.t0 + span;
        a.x0 = x0; a.x1 = x1;
        return a;
    }

    void draw_plan_all(const Model& m) {
        lv_obj_t* c = plan_card_;
        int W = gw_;
        label_at(c, 16, 15, tr("8 路程序"), 17, 700, col::INK);
        int running = 0;
        int64_t last_end = 0;
        std::vector<int> earliest;
        int64_t first_end = INT64_MAX;
        for (int i = 0; i < 8; ++i) {
            const auto& ch = m.snap.channels[static_cast<size_t>(i)];
            if (ch.program && ch.program->running && !ch.program->waitingGas) {
                ++running;
                const auto& sg = m.plans[static_cast<size_t>(i)].segs;
                if (!sg.empty()) {
                    int64_t e = sg.back().end;
                    last_end = std::max(last_end, e);
                    if (e < first_end - 60000) { first_end = e; earliest = {i + 1}; }
                    else if (std::llabs(e - first_end) <= 60000) earliest.push_back(i + 1);
                }
            }
        }
        std::string sum = trf("%d 路在跑", running);
        if (!earliest.empty()) sum += " · " + trf("%s 号最早", chans_text(earliest).c_str());
        if (last_end) sum += std::string(tr("，")) + trf("最晚 %s 结束", model::fmt_clock(last_end, false).c_str());
        label_at(c, 96, 19, sum.c_str(), 13.5, 400, col::SEC);
        // 图例
        struct Lg { const char* t; int kind; };
        Lg lg[5] = {{"做完", 0}, {"正在做", 1}, {"还没做", 2}, {"反应（时长看吸气）", 3}, {"等气路", 4}};
        int lx = W - 16;
        for (int k = 4; k >= 0; --k) {
            int tw = text_width(tr(lg[k].t), font(12.5));
            lx -= tw;
            label_at(c, lx, 19, tr(lg[k].t), 12.5, 400, col::SEC);
            lx -= 24;
            lv_obj_t* sw = box(c, lx, 23, 18, 10, 0xffffff, 3);
            switch (lg[k].kind) {
                case 0: lv_obj_set_style_bg_color(sw, lv_color_hex(0xc9d6e5), 0); break;
                case 1: lv_obj_set_style_bg_color(sw, lv_color_hex(col::BLUE), 0); break;
                case 2: ring(sw, 0xb8c7d8, 2); break;
                case 3: ring(sw, 0x9fb8d4, 2); break;
                case 4: lv_obj_set_style_bg_color(sw, lv_color_hex(0xfdf1e0), 0); hatch(sw, 0xf3d9a9, 255, 4, 0, 0, 4); break;
            }
            lx -= 14;
        }
        // 时间轴：8 行，程序行 rh，空行 40
        int top = 78, x0 = 268, x1 = W - 8;
        Axis ax = axis_for(m, x0, x1, 14);
        int nprog = 0;
        for (int i = 0; i < 8; ++i) nprog += m.plans[static_cast<size_t>(i)].segs.empty() ? 0 : 1;
        int empty_rows = 8 - nprog;
        int avail = BH_ - top - 52;
        int rh = nprog ? std::min(80, (avail - empty_rows * 40) / std::max(1, nprog)) : 64;
        int rows_h = nprog * rh + empty_rows * 40;
        for (int64_t t = ax.t0; t <= ax.t1; t += 3600000) {
            int hx = (int)std::lround(ax.x(t));
            bool even = ((t - ax.t0) / 3600000) % 2 == 0;
            box(c, hx, top, 1, rows_h, even ? 0xe9eef3 : 0xf4f6f9);
            if (even) {
                lv_obj_t* tl = label(c, model::fmt_clock(t, false).c_str(), 12, 400, 0x8a94a3);
                lv_obj_update_layout(tl);
                std::string s = model::fmt_clock(t, false);
                if (s[0] == '0') lv_label_set_text(tl, s.c_str() + 1);
                lv_obj_update_layout(tl);
                lv_obj_set_pos(tl, hx - lv_obj_get_width(tl), top + rows_h + 6);
            }
        }
        int y = top;
        for (int i = 0; i < 8; ++i) {
            int ch = i + 1;
            const auto& pe = m.plans[static_cast<size_t>(i)];
            const auto& pg = m.progress[static_cast<size_t>(i)];
            const auto& cs = m.snap.channels[static_cast<size_t>(i)];
            if (pe.segs.empty()) {
                lv_obj_t* row = box(c, 16, y, W - 32, 40, 0, 0, true);
                box(row, 0, 39, W - 32, 1, 0xf0f3f6);
                mini_vessel(row, 4, 11, 19, chan_color(ch), true);
                std::string nt = trf("%d 号 · 没有程序", ch);
                if (cs.state == "stopped") nt += std::string(" · ") + tr("手动停止");
                label_at(row, 26, 10, nt.c_str(), 14, 400, col::MUT);
                // 虚线（6 / 6）
                for (int dx = 252; dx < W - 32; dx += 12) box(row, dx, 20, std::min(6, W - 32 - dx), 1, 0xe3e8ee);
                y += 40;
                continue;
            }
            lv_obj_t* row = box(c, 16, y, W - 32, rh, 0, 0, true);
            box(row, 0, rh - 1, W - 32, 1, 0xf0f3f6);
            App* app = app_;
            on_tap(row, [app, ch] { app->go(PageId::Home, 10 + ch); });
            int ty = rh / 2 - 21;
            mini_vessel(row, 4, ty + 1, 21, chan_color(ch));
            lv_obj_t* nl = label_at(row, 27, ty, trf("%d 号", ch).c_str(), 15, 700, col::INK);
            lv_obj_update_layout(nl);
            int nx = 27 + lv_obj_get_width(nl) + 8;
            lv_obj_t* pn = label_at(row, nx, ty + 1, pg.programName.c_str(), 14, 400, col::INK);
            lv_obj_update_layout(pn);
            nx += lv_obj_get_width(pn) + 8;
            bool waiting = cs.program && cs.program->waitingGas;
            bool run = cs.program && cs.program->running && !waiting;
            bool stopped = !run && !waiting && cs.state == "stopped";
            const char* chip = run ? "运行中" : waiting ? "等气路" : stopped ? "手动停止" : "未下发";
            uint32_t cbg = run ? 0xe8f0fb : waiting ? 0xfdf1e0 : 0xeef1f5;
            uint32_t cfg = run ? col::BLUE : waiting ? col::AMBER : col::SEC;
            lv_obj_t* cp = label_at(row, nx, ty, tr(chip), 12, 700, cfg);
            lv_obj_set_style_bg_color(cp, lv_color_hex(cbg), 0);
            lv_obj_set_style_bg_opa(cp, LV_OPA_COVER, 0);
            lv_obj_set_style_radius(cp, 11, 0);
            lv_obj_set_style_pad_hor(cp, 8, 0);
            lv_obj_set_style_pad_ver(cp, 3, 0);
            std::string l2;
            if (run && cs.program) {
                int si = cs.program->stepIndex;
                std::string sn = si < (int)pg.stepNames.size() ? pg.stepNames[static_cast<size_t>(si)] : "";
                l2 = trf("第 %d/%d 步 · %s", si + 1, cs.program->stepCount, tr(sn.c_str()));
                if (!pg.stepTarget.empty() && sn != "保持") l2 += " " + pg.stepTarget;
                l2 += " · " + trf("已 %s", model::fmt_hmin(pg.elapsedSec).c_str());
            } else if (waiting) {
                l2 = trf("等 %s 号放开气路", chans_text(pg.waitingFor).c_str()) + " · " + trf("已等 %s", model::fmt_hmin(pg.waitedSec).c_str());
            } else if (stopped && cs.program && cs.program->state == "aborted") {
                l2 = std::string(tr("程序已中止")) + " · " + trf("%d 步 约 %s", pg.stepCountDraft, model::fmt_hmin(pg.draftTotalSec).c_str());
            } else {
                l2 = std::string(tr("已编辑，还没下发")) + " · " + trf("%d 步 约 %s", pg.stepCountDraft, model::fmt_hmin(pg.draftTotalSec).c_str());
            }
            lv_obj_t* l2l = label_at(row, 4, ty + 25, l2.c_str(), 12.5, 400, col::MUT);
            lv_obj_set_width(l2l, 244);
            dot_line(l2l);
            // 条
            int by = rh / 2 - 14;
            int cur = cs.program ? cs.program->stepIndex : -1;
            if (waiting) {
                int wx = (int)std::lround(ax.x(m.snap.ts)) - 16 - 20;
                lv_obj_t* hb = box(row, wx, by, 8, 28, 0xfdf1e0, 6);
                hatch(hb, 0xf3d9a9, 255, 5, 0, 0, 5);
            }
            for (size_t k = 0; k < pe.segs.size(); ++k) {
                const auto& s = pe.segs[k];
                int sx = (int)std::lround(ax.x(s.start)) - 16, ex = (int)std::lround(ax.x(s.end)) - 16;
                if (ex > W - 32) ex = W - 32;
                int bw = std::max(2, ex - sx);
                bool done = run && (int)k < cur;
                bool now = run && (int)k == cur;
                lv_obj_t* b = box(row, sx, by, bw, 28, 0xffffff, 6);
                if (done) {
                    lv_obj_set_style_bg_color(b, lv_color_hex(0xc9d6e5), 0);
                } else if (now) {
                    if (s.openEnded) {
                        // 正在反应：蓝色到「现在」，后面斜纹到最晚结束
                        int nx2 = (int)std::lround(ax.x(m.snap.ts)) - 16;
                        lv_obj_set_width(b, std::max(4, nx2 - sx));
                        lv_obj_set_style_bg_color(b, lv_color_hex(col::BLUE), 0);
                        lv_obj_set_style_border_width(b, 2, 0);
                        lv_obj_set_style_border_color(b, lv_color_white(), 0);
                        lv_obj_set_style_outline_width(b, 2, 0);
                        lv_obj_set_style_outline_color(b, lv_color_hex(col::BLUE), 0);
                        lv_obj_t* il = icon(b, &ic::flask15, 0xffffff, 8, 5);
                        (void)il;
                        label_at(b, 27, 4, tr("反应 · 吸气中"), 13, 600, 0xffffff);
                        lv_obj_t* rest = box(row, nx2 + 4, by, std::max(4, ex - nx2 - 4), 28, 0xffffff, 6);
                        ring(rest, 0x9fb8d4, 2);
                        hatch(rest, col::BLUE, 13, 6, 0, 0, 6);
                        label_at(rest, 7, 5, trf("吸气停止就结束 · 最晚 %s", model::fmt_clock(s.end, false).c_str()).c_str(), 12, 400, col::SEC);
                        continue;
                    }
                    lv_obj_set_style_bg_color(b, lv_color_hex(0xcfe0f3), 0);
                    box(b, 0, 0, (int)std::lround(bw * pg.stepFraction), 28, col::BLUE);
                    lv_obj_set_style_clip_corner(b, true, 0);
                    lv_obj_set_style_outline_width(b, 2, 0);
                    lv_obj_set_style_outline_color(b, lv_color_hex(col::BLUE), 0);
                    lv_obj_set_style_outline_pad(b, 1, 0);
                } else if (s.openEnded) {
                    ring(b, 0xb8c7d8, 2);
                    lv_obj_t* il = icon(b, &ic::flask14, col::MUT, 8, 7);
                    (void)il;
                    label_at(b, 27, 5, tr("反应 · 最长 8 h"), 12.5, 400, col::SEC);
                } else {
                    ring(b, run ? 0xb8c7d8 : waiting ? 0xcfd8e2 : 0xdfe5ec, 2);
                    if (!run && !waiting) lv_obj_set_style_opa(b, 204, 0);
                }
            }
            y += rh;
        }
        // 现在
        int nxp = (int)std::lround(ax.x(m.snap.ts));
        box(c, nxp - 1, top - 6, 2, rows_h + 6, col::ORANGE);
        lv_obj_t* pill = label(c, trf("现在 %s", model::fmt_clock(m.snap.ts, false).c_str()).c_str(), 12, 700, 0xffffff);
        lv_obj_set_style_bg_color(pill, lv_color_hex(col::ORANGE), 0);
        lv_obj_set_style_bg_opa(pill, LV_OPA_COVER, 0);
        lv_obj_set_style_radius(pill, 10, 0);
        lv_obj_set_style_pad_hor(pill, 8, 0);
        lv_obj_set_style_pad_ver(pill, 2, 0);
        lv_obj_update_layout(pill);
        lv_obj_set_pos(pill, nxp - lv_obj_get_width(pill) / 2, top - 26);
        lv_obj_t* f = label(c, tr("点任意一路看这一路的温度、压力计划"), 12.5, 400, col::MUT);
        lv_obj_align(f, LV_ALIGN_BOTTOM_LEFT, 16, -14);
    }

    // ---------------------------------------------------------- 方案总览 · 点开一路
    struct ChartCtx {
        std::vector<Pt> act, plan;
        std::vector<float> gridy;
        std::vector<std::string> ylab;
        std::vector<float> vx;  // 步骤分界
        float nowx = 0, base = 0;
        std::string title;
        std::vector<std::pair<float, std::string>> xlab;
    };
    static void chart_draw(lv_obj_t*, lv_layer_t* L, const lv_area_t& a, void* u) {
        auto* c = static_cast<ChartCtx*>(u);
        for (size_t i = 0; i < c->gridy.size(); ++i) {
            draw_hline(L, a, 46, (float)lv_area_get_width(&a) - 10, c->gridy[i], 0xeef1f5);
            draw_text(L, a, 38, (int)c->gridy[i] - 8, c->ylab[i].c_str(), font(11.5), 0x8a94a3, LV_TEXT_ALIGN_RIGHT, 40);
        }
        for (float x : c->vx) draw_vline(L, a, x, 8, c->base, 0xe3e8ee);
        draw_fill_under(L, a, c->act, c->base, col::BLUE, 26);
        draw_polyline(L, a, c->act, col::BLUE, 2);
        draw_polyline(L, a, c->plan, 0x7fa6cf, 2, LV_OPA_COVER, 5, 4);
        draw_vline(L, a, c->nowx, 4, c->base, col::ORANGE, 2);
        draw_text(L, a, 50, 10, c->title.c_str(), font(12, 700), col::DEEP);
        for (auto& xl : c->xlab) draw_text(L, a, (int)xl.first, (int)c->base + 8, xl.second.c_str(), font(11.5), 0x8a94a3, LV_TEXT_ALIGN_CENTER, 60);
    }

    void draw_plan_one(const Model& m) {
        lv_obj_t* c = plan_card_;
        int W = gw_;
        int ch = one_;
        const auto& pe = m.plans[static_cast<size_t>(ch - 1)];
        const auto& pg = m.progress[static_cast<size_t>(ch - 1)];
        const auto& cs = m.ch(ch);
        int boxh = 336;
        lv_obj_t* top = box(c, 16, 10, W - 32, boxh, 0xffffff, 12);
        ring(top, col::BLUE, 2);
        mini_vessel(top, 16, 18, 22, chan_color(ch));
        lv_obj_t* nl = label_at(top, 42, 18, trf("%d 号", ch).c_str(), 16, 700, col::INK);
        lv_obj_update_layout(nl);
        int x = 42 + lv_obj_get_width(nl) + 10;
        lv_obj_t* pn = label_at(top, x, 17, pg.programName.c_str(), 17, 700, col::BLUE);
        lv_obj_update_layout(pn);
        x += lv_obj_get_width(pn) + 10;
        std::string info;
        if (!pe.segs.empty()) {
            int64_t react_end = 0;
            for (auto& s : pe.segs) if (s.kind == "react") react_end = s.end;
            info = trf("%d 步 · %s 开始 · 已 %s · 反应最晚到 %s", (int)pe.segs.size(), model::fmt_clock(pe.segs.front().start, false).c_str(),
                       model::fmt_hmin(pg.elapsedSec).c_str(), model::fmt_clock(react_end, false).c_str());
        }
        label_at(top, x, 20, info.c_str(), 13, 400, col::MUT);
        if (cs.program) {
            int si = cs.program->stepIndex;
            std::string sn = si < (int)pg.stepNames.size() ? tr(pg.stepNames[static_cast<size_t>(si)].c_str()) : "";
            lv_obj_t* pill = label(top, trf("运行中 · 第 %d / %d 步 %s", si + 1, cs.program->stepCount, sn.c_str()).c_str(), 13, 700, 0xffffff);
            lv_obj_set_style_bg_color(pill, lv_color_hex(col::BLUE), 0);
            lv_obj_set_style_bg_opa(pill, LV_OPA_COVER, 0);
            lv_obj_set_style_radius(pill, 13, 0);
            lv_obj_set_style_pad_hor(pill, 10, 0);
            lv_obj_set_style_pad_ver(pill, 4, 0);
            lv_obj_update_layout(pill);
            lv_obj_set_pos(pill, W - 32 - 58 - lv_obj_get_width(pill), 15);
        }
        lv_obj_t* up = box(top, W - 32 - 50, 10, 36, 36, 0, 8, true);
        lv_obj_center(icon(up, &ic::chev_up20, col::SEC));
        App* app = app_;
        on_tap(up, [app] { app->go(PageId::Home, 2); });
        // 时间轴：从程序开始前一点到结束
        int cw = W - 32 - 32;
        Axis ax;
        if (!pe.segs.empty()) {
            ax.t0 = pe.segs.front().start - 10 * 60000;
            ax.t1 = pe.segs.back().end + 5 * 60000;
        } else {
            ax.t0 = m.snap.ts; ax.t1 = m.snap.ts + 3600000;
        }
        ax.x0 = 62; ax.x1 = cw - 14;
        // 步骤名一行
        int cur = cs.program ? cs.program->stepIndex : -1;
        int lastx = -100;
        for (size_t k = 0; k < pe.segs.size(); ++k) {
            const auto& s = pe.segs[k];
            int sx = 16 + (int)std::lround(ax.x(s.start)) + 2;
            int ex = 16 + (int)std::lround(ax.x(s.end));
            std::string t = std::to_string(k + 1);
            if (ex - sx > 50 || k == 0) t += std::string(" ") + tr(step_short(s.kind));
            if (sx < lastx + 4) sx = lastx + 4;
            lv_obj_t* l = label_at(top, sx, 58, t.c_str(), 12.5, (int)k == cur ? 700 : 400,
                                   (int)k == cur ? col::BLUE : (ex - sx > 50 ? col::INK : col::MUT));
            lv_obj_update_layout(l);
            lastx = sx + lv_obj_get_width(l);
        }
        // 两张小图
        int chh = (boxh - 84) / 2;
        for (int g = 0; g < 2; ++g) {
            auto* cc = new ChartCtx;
            bool temp = g == 0;
            cc->title = temp ? tr("温度 °C") : tr("压力 MPa");
            float top_v = temp ? 80.f : 3.f, bot_v = temp ? 20.f : 0.f;
            float y_top = 12.f, base = (float)chh - (temp ? 6.f : 24.f);
            cc->base = base;
            auto Y = [&](double v) { return (float)(base - (base - y_top) * (v - bot_v) / (top_v - bot_v)); };
            int steps = temp ? 4 : 4;
            for (int k = 0; k < steps; ++k) {
                double v = bot_v + (top_v - bot_v) * k / (steps - 1);
                cc->gridy.push_back(Y(v));
                char b[16];
                std::snprintf(b, sizeof b, "%.0f", v);
                cc->ylab.push_back(b);
            }
            for (auto& s : pe.segs) cc->vx.push_back(ax.x(s.start));
            const auto& act = temp ? pe.tempActual : pe.pressActual;
            const auto& plan = temp ? pe.tempPlan : pe.pressPlan;
            for (auto& p : act) cc->act.push_back({ax.x(p.t), Y(p.v)});
            for (auto& p : plan)
                if (p.t >= m.snap.ts) cc->plan.push_back({ax.x(p.t), Y(p.v)});
            if (!act.empty() && !cc->plan.empty()) cc->plan.insert(cc->plan.begin(), {ax.x(m.snap.ts), Y(act.back().v)});
            cc->nowx = ax.x(m.snap.ts);
            if (!temp)
                for (int64_t t = floor_hour(ax.t0) + 3600000; t < ax.t1; t += 3600000)
                    cc->xlab.push_back({ax.x(t), model::fmt_clock(t, false)});
            lv_obj_t* o = box(top, 16, 80 + g * chh, cw, chh, 0, 0, true);
            on_draw(o, chart_draw, cc);
            lv_obj_add_event_cb(o, [](lv_event_t* e) { delete static_cast<ChartCtx*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, cc);
        }
        // 其余几路：一行一条
        int y = 10 + boxh + 10;
        lv_obj_t* list = box(c, 16, y, W - 32, BH_ - y - 14, 0xffffff, 12);
        ring(list, 0xe3e8ee, 1);
        int ly = 0;
        int rows = 0;
        for (int i = 1; i <= 8; ++i) rows += i != ch;
        int rh = std::min(44, (BH_ - y - 14) / rows);
        for (int i = 1; i <= 8; ++i) {
            if (i == ch) continue;
            const auto& p2 = m.progress[static_cast<size_t>(i - 1)];
            const auto& c2 = m.ch(i);
            const auto& pe2 = m.plans[static_cast<size_t>(i - 1)];
            lv_obj_t* r = box(list, 0, ly, W - 32, rh, 0, 0, true);
            if (i != 8 && !(ch == 8 && i == 7)) box(r, 0, rh - 1, W - 32, 1, 0xf0f3f6);
            int cy = rh / 2;
            if (pe2.segs.empty()) {
                mini_vessel(r, 12, cy - 8, 17, chan_color(i), true);
                std::string nt = trf("%d 号 · 没有程序", i);
                if (c2.state == "stopped") nt += std::string(" · ") + tr("手动停止");
                label_at(r, 36, cy - 10, nt.c_str(), 13.5, 400, col::MUT);
            } else {
                mini_vessel(r, 12, cy - 9, 18, chan_color(i));
                label_at(r, 37, cy - 11, trf("%d 号", i).c_str(), 14.5, 700, col::INK);
                label_at(r, 89, cy - 10, p2.programName.c_str(), 14, 400, col::INK);
                bool waiting = c2.program && c2.program->waitingGas;
                bool run = c2.program && c2.program->running && !waiting;
                bool stopped = !run && !waiting && c2.state == "stopped";
                label_at(r, 211, cy - 9, tr(run ? "运行中" : waiting ? "等气路" : stopped ? "手动停止" : "未下发"), 12.5, 700,
                         run ? col::BLUE : waiting ? col::AMBER : stopped ? col::SEC : col::MUT);
                std::string d;
                if (run) {
                    int si = c2.program->stepIndex;
                    std::string sn = si < (int)p2.stepNames.size() ? p2.stepNames[static_cast<size_t>(si)] : "";
                    d = trf("第 %d/%d 步 · %s · 已 %s", si + 1, c2.program->stepCount, tr(sn.c_str()), model::fmt_hmin(p2.elapsedSec).c_str());
                } else if (waiting) {
                    d = trf("等 %s 号放开气路", chans_text(p2.waitingFor).c_str()) + " · " + trf("已等 %s", model::fmt_hmin(p2.waitedSec).c_str());
                } else if (stopped && c2.program && c2.program->state == "aborted") {
                    d = tr("程序已中止");
                } else {
                    d = tr("已编辑，还没下发");
                }
                label_at(r, 279, cy - 10, d.c_str(), 13.5, 400, col::SEC);
                lv_obj_t* tr_ = box(r, W - 32 - 222, cy - 3, 180, 6, 0xe9eef3, 3);
                double tot = (double)p2.elapsedSec + (p2.remainingSec ? (double)*p2.remainingSec : 0);
                if (run && tot > 0) box(tr_, 0, 0, (int)std::lround(180 * p2.elapsedSec / tot), 6, col::BLUE, 3);
                icon(r, &ic::chev_down18, col::MUT, W - 32 - 30, cy - 9);
                on_tap(r, [app, i] { app->go(PageId::Home, 10 + i); });
            }
            ly += rh;
        }
    }

    int tab_ = 0, one_ = 0;
    int W_ = 1280, BH_ = 672, gw_ = 0, my_ = 0;
    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr;
    TabCard tabs_;
    Column column_;
    std::vector<ChanCard> cards_;
    std::vector<GasColumn> gas_;
    lv_obj_t *gas_card_ = nullptr, *plan_card_ = nullptr, *main_ = nullptr, *main_g_ = nullptr;
    lv_obj_t *src_pipe_[4]{}, *src_pipe2_[4]{}, *src_fill_[4]{}, *src_line_[4]{}, *src_lbl_[4]{};
};

std::unique_ptr<Page> make_home_page(int tab) { return std::make_unique<HomePage>(tab); }

// 点釜面板打开时，把那张卡片加蓝框（chan_quick.cpp 用）
void home_select_card(Page* p, int ch) {
    if (auto* h = dynamic_cast<HomePage*>(p)) h->select_card(ch);
}

}  // namespace reactor::hmi
