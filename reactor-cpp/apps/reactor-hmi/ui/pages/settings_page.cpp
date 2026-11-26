// 设置（10 个分类，Set-*）：左边菜单，右边一张大卡。所有修改都走 Actions（PATCH /settings、PUT /pid/…、/calib、/network …），
// 界面不直接改 Model，等后端的新快照回来再刷新。压力在界面上一律 MPa，提交时换回 psi。
#include "common2.hpp"

#include "../kit/draw.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;

namespace {

constexpr double kPsi = 0.00689475729;

struct SetState { int calib_ch = 0; int pid_band = 1; };
SetState& ss() {
    static SetState s;
    return s;
}

const char* kMenu[10] = {"通用", "参数与报警", "温控 PID", "容积标定", "通道与气路", "网络", "通讯与模块", "数据存储", "用户与权限", "关于"};
const lv_image_dsc_t* menu_icon(int i) {
    switch (i) {
        case 0: return &ic::m_general20;
        case 1: return &ic::m_params20;
        case 2: return &ic::thermo18;
        case 3: return &ic::m_calib20;
        case 4: return &ic::m_chan20;
        case 5: return &ic::m_net20;
        case 6: return &ic::m_comm20;
        case 7: return &ic::m_data20;
        case 8: return &ic::m_user20;
        default: return &ic::m_about20;
    }
}

// 小卡片（设置页里的分组）
lv_obj_t* group(lv_obj_t* p, int x, int y, int w, int h, const char* title) {
    lv_obj_t* g = box(p, x, y, w, h, 0xffffff, 12);
    ring(g, 0xe1e7ee, 1);
    if (title && *title) label_at(g, 14, 12, tr(title), 15.5, 700, col::INK);
    return g;
}

// 一行：左标题 + 灰说明，右边留给控件；返回行高
int row_text(lv_obj_t* g, int x, int y, int w, const char* title, const char* sub, bool divider = true, int sub_w = 0) {
    lv_obj_t* t = label_at(g, x, y + (sub && *sub ? 4 : 13), tr(title), 15, 400, col::INK);
    (void)t;
    if (sub && *sub) {
        // 行高固定 48：说明只占一行（原来折成两行会压到分隔线 / 下一行）。放不下先换 11 px，再不行打省略号
        int sw = sub_w > 0 ? sub_w : w * 45 / 100;
        double px = text_width(tr(sub), font(12, 400)) <= sw ? 12 : 11;
        lv_obj_t* s = label_at(g, x, y + 26, tr(sub), px, 400, col::MUT);
        lv_obj_set_width(s, sw);
        dot_line(s);
    }
    if (divider) box(g, x, y + 47, w, 1, col::DIV);
    return 48;
}

// 数值行（右对齐的数值框 + 单位）
void num_row(lv_obj_t* g, int x, int y, int w, const char* title, const char* sub, double v, int dec, const char* unit, double lo,
             double hi, std::function<void(double)> apply, bool divider = true, int fw = 104) {
    // 单位列固定宽度，同一组里的数值框才对得齐
    int uw = unit && *unit ? std::max(54, text_width(unit, font(13, 400)) + 10) : 0;
    row_text(g, x, y, w, title, sub, divider, w - uw - fw - 8);
    NumSpec sp;
    sp.title = tr(title);
    sp.unit = unit ? unit : "";
    sp.value = v; sp.min = lo; sp.max = hi; sp.decimals = dec; sp.last = v;
    NumField* nf = new NumField;
    nf->create(g, x + w - uw - fw, y + 4, fw, 40, sp);
    nf->set_align_right(true);
    nf->set(v);
    nf->on_apply = std::move(apply);
    lv_obj_add_event_cb(nf->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, nf);
    if (uw) label_at(g, x + w - uw + 10, y + 15, unit, 13, 400, col::MUT);
}

void kv_row(lv_obj_t* g, int x, int y, int w, const char* k, const std::string& v, const char* sub = nullptr, bool divider = true,
            double vpx = 15, int vw = 400) {
    row_text(g, x, y, w, k, sub, divider);
    lv_obj_t* l = label(g, v.c_str(), vpx, vw, col::INK);
    lv_obj_update_layout(l);
    lv_obj_set_pos(l, x + w - lv_obj_get_width(l), y + (48 - lv_obj_get_height(l)) / 2);
}

lv_obj_t* make_switch(lv_obj_t* g, int x, int y, bool on, std::function<void(bool)> cb) {
    Switch* sw = new Switch;
    sw->create(g, x, y, on, true);
    // 点了先切过去，不等后台来回（后台拒绝时设置会重取，界面回到真实值）
    sw->on_change = [sw, cb = std::move(cb)](bool v) {
        sw->set(v);
        if (cb) cb(v);
    };
    lv_obj_add_event_cb(sw->obj(), [](lv_event_t* e) { delete static_cast<Switch*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, sw);
    return sw->obj();
}

LampGroup* make_lamps(lv_obj_t* g, int right_x, int y, const std::vector<std::string>& opts, int sel, std::function<void(int)> cb,
                      int h = 42) {
    const lv_font_t* f = font(15, 600);
    int w = 0;
    for (auto& o : opts) w += std::max(60, 27 + text_width(o.c_str(), f) + 12) + 6;
    w -= 6;
    LampGroup* lg = new LampGroup;
    lg->create(g, right_x - w, y, opts, std::vector<int>(opts.size(), 60), sel, h, 15, 6);
    lg->on_change = [lg, cb = std::move(cb)](int i) {
        lg->set(i);  // 点了先切过去，不等后台来回
        if (cb) cb(i);
    };
    lv_obj_add_event_cb(g, [](lv_event_t* e) { delete static_cast<LampGroup*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, lg);
    return lg;
}

lv_obj_t* info_box(lv_obj_t* p, int x, int y, int w, const char* text) {
    lv_obj_t* b = box(p, x, y, w, 40, 0xf3f6f9, 8);
    lv_obj_t* l = label(b, text, 13.5, 400, col::SEC);
    lv_obj_set_width(l, w - 28);
    lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
    lv_obj_update_layout(l);
    lv_obj_set_height(b, lv_obj_get_height(l) + 22);
    lv_obj_set_pos(l, 14, 11);
    return b;
}

}  // namespace

void reset_settings_state() { ss() = SetState{}; }

void settings_open_wifi_password(App& app, const std::string& ssid, const std::string& init) {
    TextKbdOpts o;
    o.password = true;
    o.ok_text = tr("连接");
    o.hint = tr("连上后会记住，下次开机自动连接");
    TextKbd::open(trf("输入「%s」的密码", ssid.c_str()), init, 63, o, [&app, ssid](const std::string& pw) {
        app.act().wifi_connect(ssid, pw);
        toast(trf("正在连接 %s…", ssid.c_str()));
    });
}

void settings_confirm_quit(App& app) {
    confirm_stop(tr("退出触屏程序？"), "", "", tr("只退出这个界面，后台服务照常运行：正在跑的程序、加热和记录都不受影响。"), tr("不退出"), tr("退出程序"),
                 [&app] { app.act().quit_app(); });
}

class SettingsPage : public Page {
public:
    explicit SettingsPage(int sec) : sec_(std::max(0, std::min(9, sec))) {}
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        H_ = app.h() - 60;
        menu_ = card(root, 12, 12, 220, H_ - 24);
        body_ = card(root, 244, 12, W_ - 244 - 12, H_ - 24);
        build_menu();
    }
    void update(const Model& m) override {
        char sig[200];
        const auto& L = m.snap;
        std::snprintf(sig, sizeof sig, "%d/%lld/%d/%zu/%d/%d/%d/%lld/%d/%d", sec_, (long long)m.settings.revision, ver_,
                      m.x.net.nearby.size(), (int)m.x.net.wifiOn, (int)L.stir.online, ss().calib_ch,
                      (long long)((sec_ == 2 || sec_ == 3 || sec_ == 6) ? m.snap.ts / 2000 : 0), ss().pid_band,
                      m.x.net.connectedSsid.empty() ? 0 : (int)m.x.net.connectedSsid.size());
        std::string s = sig;
        for (int i = 1; i <= 8; ++i) s += m.ch(i).installed ? '1' : '0';
        if (s == sig_) return;
        sig_ = s;
        lv_obj_clean(body_);
        bw_ = W_ - 244 - 12;
        bh_ = H_ - 24;
        switch (sec_) {
            case 0: general(m); break;
            case 1: params(m); break;
            case 2: pid(m); break;
            case 3: calib(m); break;
            case 4: channels(m); break;
            case 5: network(m); break;
            case 6: comm(m); break;
            case 7: data(m); break;
            case 8: users(m); break;
            default: about(m); break;
        }
    }

private:
    void refresh() { ++ver_; if (app_) update(app_->m()); }
    model::Actions& act() { return app_->act(); }

    void build_menu() {
        lv_obj_clean(menu_);
        for (int i = 0; i < 10; ++i) {
            bool on = i == sec_;
            lv_obj_t* it = box(menu_, 0, 8 + i * 52, 220, 52, on ? 0xe8f0fb : 0xffffff);
            if (on) box(it, 0, 0, 4, 52, col::BLUE);
            icon(it, menu_icon(i), on ? col::BLUE : col::SEC, 18, 16);
            label_at(it, 48, 14, tr(kMenu[i]), 16, on ? 700 : 400, on ? col::BLUE : col::INK);
            on_tap(it, [this, i] {
                sec_ = i;
                build_menu();
                sig_.clear();
                if (app_) update(app_->m());
            });
        }
        int qh = H_ - 24;
        box(menu_, 0, qh - 84, 220, 1, col::DIV);
        lv_obj_t* q = box(menu_, 14, qh - 68, 192, 46, 0xffffff, 10);
        ring(q, 0xf0b4b4, 2);
        icon(q, &ic::logout18, col::RED, 56, 14);
        label_at(q, 82, 12, tr("退出程序"), 15.5, 600, col::RED);
        on_tap(q, [this] { settings_confirm_quit(*app_); });
    }

    void header(const char* title, const char* sub, const std::string& right = "") {
        card_head(body_, 18, 20, tr(title), tr(sub), 22);
        if (!right.empty()) {
            lv_obj_t* r = label(body_, right.c_str(), 13.5, 400, col::SEC);
            lv_obj_align(r, LV_ALIGN_TOP_RIGHT, -18, 26);
        }
    }

    // ---------------------------------------------------------------- 0 通用
    void general(const Model& m) {
        header("通用", "界面、语言、单位");
        const auto& p = m.x.prefs;
        int w1 = 541, w2 = bw_ - 18 * 2 - 13 - w1;
        lv_obj_t* g = group(body_, 18, 56, w1, 232, "显示与单位");
        int x = 14, w = w1 - 28;
        kv_row(g, x, 42, w, "语言", tr("点顶栏右上角的地球切换"), nullptr, true, 14, 400);
        row_text(g, x, 90, w, "压力单位", "内部按 psi 存，只换算显示");
        int ps = p.pressureUnit == "bar" ? 1 : p.pressureUnit == "psi" ? 2 : 0;
        make_lamps(g, x + w, 93, {"MPa", "bar", "psi"}, ps, [this](int i) {
            const char* u[3] = {"MPa", "bar", "psi"};
            act().patch_setting_str("ui.pressureUnit", u[i]);
        });
        row_text(g, x, 138, w, "温度单位", "");
        make_lamps(g, x + w, 141, {"°C", "°F"}, p.tempUnit == "F" ? 1 : 0, [this](int i) { act().patch_setting_str("ui.tempUnit", i ? "F" : "C"); });
        row_text(g, x, 186, w, "界面主题", "新界面默认浅色", false);
        make_lamps(g, x + w, 189, {tr("浅色"), tr("深色")}, p.theme == "dark" ? 1 : 0,
                   [this](int i) { act().patch_setting_str("ui.theme", i ? "dark" : "light"); });
        lv_obj_t* g2 = group(body_, 18 + w1 + 13, 56, w2, 166, "运行");
        int w_ = w2 - 28;
        row_text(g2, 14, 42, w_, "开始前弹出检查清单", trf("每次启动程序前确认 %d 项", 9).c_str());
        make_switch(g2, 14 + w_ - 52, 51, p.preflightChecklist, [this](bool on) { act().patch_setting("ui.preflightChecklist", on); });
        row_text(g2, 14, 90, w_, "屏幕键盘", "点数值框时弹出哪一种", false);
        make_lamps(g2, 14 + w_, 93, {tr("数字"), tr("全键盘")}, p.keyboard == "full" ? 1 : 0,
                   [this](int i) { act().patch_setting_str("ui.keyboard", i ? "full" : "num"); });
        label_at(g2, 14, 140, tr("「参数与报警」里是数值和阈值，这里只放界面和习惯。"), 12.5, 400, col::SEC);
    }

    // ---------------------------------------------------------------- 1 参数与报警
    void params(const Model& m) {
        header("参数与报警", "改完立即生效 · 点数值用屏幕键盘输入", tr("压力按 MPa 显示，单位在「通用」里改"));
        const auto& S = m.settings;
        const auto& X = m.x;
        int cw = (bw_ - 36 - 24) / 3;
        int x1 = 18, x2 = 18 + cw + 12, x3 = 18 + 2 * (cw + 12);
        int w = cw - 28;
        auto P = [this](const char* key, bool pressure) {
            std::string k = key;
            return [this, k, pressure](double v) { act().patch_setting(k, pressure ? v / kPsi : v); };
        };
        // 报警阈值
        lv_obj_t* g = group(body_, x1, 56, cw, 288, "报警阈值");
        num_row(g, 14, 40, w, "超温报警", "", S.alarms.overTempC, 0, "°C", 50, 300, P("alarms.overTempC", false));
        num_row(g, 14, 88, w, "超压报警", "", S.alarms.overPressurePsi * kPsi, 3, "MPa", 0.1, 5, P("alarms.overPressurePsi", true));
        num_row(g, 14, 136, w, "压力偏离设定", "稳压时偏离超过这个值报警", S.alarms.pressDeviationPsi * kPsi, 3, "MPa", 0.005, 1,
                P("alarms.pressDeviationPsi", true));
        num_row(g, 14, 184, w, "升温超时", "", S.alarms.heatTimeoutMin, 0, "min", 1, 600, P("alarms.heatTimeoutMin", false));
        row_text(g, 14, 232, w, "超压时停止加热", "超压联锁关阀的同时停这一路加热", false);
        make_switch(g, 14 + w - 52, 241, S.alarms.stopHeatOnOverPressure, [this](bool on) { act().patch_setting("alarms.stopHeatOnOverPressure", on); });
        lv_obj_t* g2 = group(body_, x1, 356, cw, bh_ - 356 - 18, "可设置的范围");
        num_row(g2, 14, 40, w, "温度设定上限", "", S.limits.tempSpMaxC, 0, "°C", 50, 300, P("limits.tempSpMaxC", false));
        num_row(g2, 14, 88, w, "压力设定上限", "", S.limits.pressureSpMaxPsi * kPsi, 3, "MPa", 0.1, 5, P("limits.pressureSpMaxPsi", true));
        num_row(g2, 14, 136, w, "搅拌转速上限", "釜里的转速", S.limits.rpmMax, 0, "rpm", 0, 1500, P("limits.rpmMax", false));
        num_row(g2, 14, 184, w, "溶液体积上限", "10 mL 釜，要给气相留空间", S.limits.volumeMaxMl, 1, "mL", 1, 10, P("limits.volumeMaxMl", false), false);
        // 泄漏测试
        lv_obj_t* g3 = group(body_, x2, 56, cw, 288, "泄漏测试");
        num_row(g3, 14, 40, w, "测试压力", "惰性气体，低压", X.leakSettings.testPsi * kPsi, 3, "MPa", 0.05, 3, P("leakTest.testPsi", true));
        num_row(g3, 14, 88, w, "平衡时间", "", X.leakSettings.settleMin, 0, "min", 1, 120, P("leakTest.settleMin", false));
        num_row(g3, 14, 136, w, "测量时间", "", X.leakSettings.measureMin, 0, "min", 5, 600, P("leakTest.measureMin", false));
        num_row(g3, 14, 184, w, "压降上限", "", X.leakSettings.rateLimitPsiPerHr * kPsi, 3, "MPa/h", 0.001, 1, P("leakTest.rateLimitPsiPerHr", true));
        num_row(g3, 14, 232, w, "提醒周期", "", X.leakSettings.reminderDays, 0, tr("天"), 1, 90, P("leakTest.reminderDays", false), false);
        lv_obj_t* g4 = group(body_, x2, 356, cw, 190, "搅拌传动");
        row_text(g4, 14, 40, w, "齿轮传动比", "一台电机经齿轮带 8 个釜");
        {
            NumSpec sp;
            sp.title = tr("齿轮传动比");
            NumField* nf = new NumField;
            nf->create(g4, 14 + w - 54 - 104, 44, 104, 40, sp, true);
            nf->set_align_right(true);
            nf->set_text("85 / 38");
            lv_obj_add_event_cb(nf->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, nf);
            label_at(g4, 14 + w - 46, 55, ("= " + model::fmt_num(X.stir.stirGearRatio, 3)).c_str(), 13, 400, col::MUT);
        }
        num_row(g4, 14, 88, w, "电机电流", "写入驱动器", X.stir.peakCurrentA, 1, "A", 0.1, 3, P("stir.peakCurrentA", false));
        num_row(g4, 14, 136, w, "待机电流", "", X.stir.standbyPct, 0, "%", 10, 100, P("stir.standbyPct", false), false);
        // 防超调
        lv_obj_t* g5 = group(body_, x3, 56, cw, 238, "定值加热防超调");
        num_row(g5, 14, 40, w, "预留温差 Δ", "先升到 目标 − Δ 判稳", X.approach.deltaC, 0, "°C", 0, 30, P("approach.deltaC", false));
        num_row(g5, 14, 88, w, "快段速率", "", X.approach.fastRateCPerMin, 1, "°C/min", 0.1, 20, P("approach.fastRateCPerMin", false));
        num_row(g5, 14, 136, w, "末段慢推", "最后 1 °C 慢慢推到目标", X.approach.rateCPerMin, 1, "°C/min", 0.05, 5, P("approach.rateCPerMin", false));
        row_text(g5, 14, 184, w, "判稳", "", false);
        {
            NumSpec a;
            a.title = tr("判稳 · 温度范围"); a.unit = "°C"; a.value = X.approach.settleBandC; a.min = 0.05; a.max = 2; a.decimals = 1;
            NumField* f1 = new NumField;
            f1->create(g5, 14 + w - 196, 188, 78, 40, a);
            f1->set_align_right(true);
            f1->set_text(("±" + model::fmt_num(a.value, 1)).c_str());
            f1->on_apply = P("approach.settleBandC", false);
            lv_obj_add_event_cb(f1->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, f1);
            label_at(g5, 14 + w - 112, 199, "°C", 13, 400, col::MUT);
            NumSpec b;
            b.title = tr("判稳 · 持续"); b.unit = "s"; b.value = X.approach.settleSec; b.min = 10; b.max = 600;
            NumField* f2 = new NumField;
            f2->create(g5, 14 + w - 80, 188, 58, 40, b);
            f2->set_align_right(true);
            f2->set(b.value);
            f2->on_apply = P("approach.settleSec", false);
            lv_obj_add_event_cb(f2->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, f2);
            label_at(g5, 14 + w - 14, 199, "s", 13, 400, col::MUT);
        }
        lv_obj_t* g6 = group(body_, x3, 306, cw, 94, "运行");
        num_row(g6, 14, 40, w, "等待气路上限", "几路程序抢气路时最多等多久", X.prefs.gasWaitMaxMin, 0, "min", 5, 600, P("ui.gasWaitMaxMin", false), false);
    }

    // ---------------------------------------------------------------- 2 温控 PID
    void pid(const Model& m) {
        const auto& P = m.x.pid;
        header("温控 PID", trf("8 路独立 PID · %s", P.controller.c_str()).c_str());
        int& band = ss().pid_band;
        if (band >= (int)P.bands.size()) band = 0;
        // 档位
        std::vector<std::string> bn;
        for (auto& b : P.bands) {
            std::string r = b.index == 0 ? "< " + model::fmt_num(b.hiC, 0) + " °C"
                          : b.index == (int)P.bands.size() - 1 ? "> " + model::fmt_num(b.loC, 0) + " °C"
                                                               : model::fmt_num(b.loC, 0) + " – " + model::fmt_num(b.hiC, 0) + " °C";
            bn.push_back(std::string(tr(b.name.c_str())) + " " + r);
        }
        LampGroup* lg = make_lamps(body_, bw_ - 18, 14, bn, band, [this](int i) { ss().pid_band = i; refresh(); });
        lv_obj_t* tl = label(body_, tr("温度档位"), 14.5, 400, col::SEC);
        lv_obj_update_layout(tl);
        lv_obj_set_pos(tl, bw_ - 18 - lg->width() - 12 - lv_obj_get_width(tl), 26);
        lv_obj_t* expl = label_at(body_, 18, 74, tr("每个档位存一组 PID：在某个档位下自整定一次就存进这个档位，跑反应时按目标温度自动选用。分界"), 13.5, 400, col::SEC);
        lv_obj_update_layout(expl);
        int bx0 = 18 + lv_obj_get_width(expl) + 10;
        if (P.bands.size() == 3) {
            for (int i = 0; i < 2; ++i) {
                NumSpec sp;
                sp.title = tr(i == 0 ? "低温 / 中温 分界" : "中温 / 高温 分界");
                sp.unit = "°C"; sp.value = i == 0 ? P.bands[0].hiC : P.bands[1].hiC; sp.min = 30; sp.max = 280;
                NumField* nf = new NumField;
                nf->create(body_, bx0 + i * 120, 62, 62, 38, sp);
                nf->set_align_right(true);
                nf->set(sp.value);
                double other = i == 0 ? P.bands[1].hiC : P.bands[0].hiC;
                nf->on_apply = [this, i, other](double v) { act().set_pid_bands(i == 0 ? v : other, i == 0 ? other : v); };
                lv_obj_add_event_cb(nf->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, nf);
                label_at(body_, bx0 + i * 120 + 70, 72, "°C", 13, 400, col::MUT);
            }
        }
        // 表
        int tx = 18, tw = bw_ - 36, ty = 112;
        lv_obj_t* tb = box(body_, tx, ty, tw, 36 + 8 * 54, 0xffffff, 10);
        ring(tb, 0xe1e7ee, 1);
        lv_obj_set_style_clip_corner(tb, true, 0);
        box(tb, 0, 0, tw, 36, 0xf3f6f9);
        struct C { const char* t; int x; bool right; };
        const C cols[] = {{"通道", 16, false}, {"测量 °C", 210, true}, {"输出", 318, true}, {"比例带 P", 328, false},
                          {"积分 I（s）", 450, false}, {"微分 D（s）", 572, false}, {"状态", 696, false}};
        for (auto& c : cols) {
            lv_obj_t* l = label(tb, tr(c.t), 13, 400, col::SEC);
            lv_obj_update_layout(l);
            lv_obj_set_pos(l, c.right ? c.x - lv_obj_get_width(l) : c.x, 10);
        }
        for (int ch = 1; ch <= 8; ++ch) {
            const auto& pc = P.channels[(size_t)ch - 1];
            const auto& c = m.ch(ch);
            int y = 36 + (ch - 1) * 54;
            bool tuning = pc.autotuning;
            if (tuning) box(tb, 0, y, tw, 54, 0xfdf8ef);
            if (ch > 1) box(tb, 0, y, tw, 1, col::DIV);
            mini_vessel(tb, 16, y + 16, 21, chan_color(ch), !c.installed);
            label_at(tb, 40, y + 16, trf("%d 号", ch).c_str(), 15, 400, col::INK);
            lv_obj_t* t = label(tb, model::fmt_temp(c).c_str(), 16, 700, col::DEEP);
            lv_obj_update_layout(t);
            lv_obj_set_pos(t, 210 - lv_obj_get_width(t), y + 16);
            lv_obj_t* o = label(tb, (model::fmt_num(c.heatOutputPct.value_or(0), 0) + " %").c_str(), 14.5, 400, col::SEC);
            lv_obj_update_layout(o);
            lv_obj_set_pos(o, 318 - lv_obj_get_width(o), y + 17);
            if (tuning) {
                lv_obj_t* pb = box(tb, 328, y + 9, 357, 36, 0xffffff, 8);
                ring(pb, 0xead9b8, 2);
                label_at(pb, 12, 8, trf("第 %d / %d 次振荡", pc.tuneCycle, pc.tuneCycles).c_str(), 14, 400, col::INK);
                lv_obj_t* tr2 = box(pb, 118, 15, 148, 7, 0xf0ebe2, 4);
                hatch(tr2, 0xe2d6c0, LV_OPA_COVER, 4, 0, 0, 4);
                box(pb, 118, 15, (int)(148 * pc.tuneFraction), 7, 0xc27c1f, 4);
                lv_obj_t* lft = label(pb, trf("约剩 %d min", (int)std::ceil(pc.tuneLeftSec.value_or(0) / 60)).c_str(), 14, 400, col::INK);
                lv_obj_align(lft, LV_ALIGN_RIGHT_MID, -12, 0);
                label_at(tb, 696, y + 8, trf("整定中 · %s °C", model::fmt_num(pc.tuneSpC.value_or(0), 0).c_str()).c_str(), 14, 700, 0xc27c1f);
                label_at(tb, 696, y + 29, tr("完成后自动填入"), 12, 400, col::SEC);
                lv_obj_t* cb = tool_btn(tb, 0, y + 9, tr("取消整定"), nullptr, col::RED, 36, 14.5, 0xf3c3c3);
                lv_obj_update_layout(cb);
                lv_obj_set_x(cb, tw - 16 - lv_obj_get_width(cb));
                on_tap(cb, [this, ch] { act().autotune_cancel(ch); });
                continue;
            }
            const model::PidBandValueSnap* bv = nullptr;
            for (auto& b : pc.bands) if (b.index == band) bv = &b;
            double v3[3] = {bv ? bv->P : 0, bv ? bv->I : 0, bv ? bv->D : 0};
            const char* nm[3] = {"比例带 P", "积分 I", "微分 D"};
            for (int k = 0; k < 3; ++k) {
                NumSpec sp;
                sp.title = trf("%d 号 · %s · %s", ch, P.bands.empty() ? "" : tr(P.bands[(size_t)band].name.c_str()), tr(nm[k]));
                sp.value = v3[k]; sp.min = 0; sp.max = k == 0 ? 200 : 3600; sp.decimals = k == 0 ? 1 : 0;
                NumField* nf = new NumField;
                nf->create(tb, 328 + k * 122, y + 9, 113, 36, sp, !c.installed);
                nf->set_align_right(true);
                nf->set(v3[k]);
                nf->on_apply = [this, ch, k, v3](double v) {
                    double a[3] = {v3[0], v3[1], v3[2]};
                    a[k] = v;
                    act().set_pid(ch, ss().pid_band, a[0], a[1], a[2]);
                };
                lv_obj_add_event_cb(nf->obj(), [](lv_event_t* e) { delete static_cast<NumField*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, nf);
            }
            bool ready = c.installed && c.tempOnline;
            box(tb, 698, y + 22, 10, 10, ready ? 0x16a05a : 0xb3bcc7, 5);
            label_at(tb, 716, y + 17, trc(!c.installed ? "停用 (状态)" : ready ? "就绪" : "离线"), 14, 400, col::SEC);
            if (c.installed) {
                lv_obj_t* ab = tool_btn(tb, 0, y + 9, tr("自整定"), nullptr, col::INK, 36, 14.5);
                lv_obj_update_layout(ab);
                lv_obj_set_x(ab, tw - 16 - lv_obj_get_width(ab));
                double sp_c = P.bands.empty() ? 100 : std::min(P.bands[(size_t)band].hiC - 10, (P.bands[(size_t)band].loC + P.bands[(size_t)band].hiC) / 2);
                on_tap(ab, [this, ch, sp_c] {
                    int b = ss().pid_band;
                    DialogSpec s;
                    s.w = 560; s.color = col::BLUE; s.icon = nullptr;
                    s.title = trf("%d 号 · 自整定", ch);
                    s.secondary = tr("取消"); s.primary = tr("开始整定");
                    std::string txt = trf("在当前档位下按 %s °C 整定，大约 25 min，会来回振荡 3 次。整定期间这一路不要跑程序。", model::fmt_num(sp_c, 0).c_str());
                    s.body = [txt](lv_obj_t* bb, int w) {
                        lv_obj_t* l = label(bb, txt.c_str(), 15, 400, col::INK);
                        lv_obj_set_width(l, w);
                        lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
                        return 48;
                    };
                    s.on_primary = [this, ch, b, sp_c] { act().autotune(ch, b, sp_c); };
                    Dialog::open(s);
                });
            }
        }
    }

    // ---------------------------------------------------------------- 3 容积标定
    void calib(const Model& m) {
        const auto& V = m.x.volume;
        const auto& C = m.x.calib;
        const auto& cs = m.snap.calib;
        bool running = cs && cs->running;
        std::string right = V.manifoldVolume ? trf("总管体积 %s mL", model::fmt_num(*V.manifoldVolume, 2).c_str())
                                             : std::string(tr("总管体积 未标定 · 第一次要加水做两遍"));
        header("容积标定", "用已知体积的气体膨胀算出每一路的气相体积，吸气量更准", right);
        int& sel = ss().calib_ch;
        if (sel == 0) sel = running && cs->channel ? *cs->channel : 1;
        int tw = (bw_ - 36 - 7 * 9) / 8;
        for (int ch = 1; ch <= 8; ++ch) {
            bool on = ch == sel, now = running && cs->channel && *cs->channel == ch;
            lv_obj_t* t = box(body_, 18 + (ch - 1) * (tw + 9), 56, tw, 92, on ? 0xffffff : 0xf6f8fa, 10);
            ring(t, on ? col::BLUE : 0xe1e7ee, on ? 2 : 1);
            mini_vessel(t, 12, 10, 18, chan_color(ch), !m.ch(ch).installed);
            label_at(t, 34, 10, trf("%d 号", ch).c_str(), 15, 400, col::INK);
            const auto& ev = V.effectiveVolume[(size_t)ch - 1];
            if (now) {
                label_at(t, 12, 34, tr("标定中…"), 16, 700, col::AMBER);
                label_at(t, 12, 62, trf("第 %d / %d 次", C.cycle, C.cycles).c_str(), 13, 400, col::SEC);
            } else if (ev) {
                lv_obj_t* v = label_at(t, 12, 32, model::fmt_num(*ev, 2).c_str(), 21, 700, col::DEEP);
                lv_obj_update_layout(v);
                label_at(t, 12 + lv_obj_get_width(v) + 3, 41, "mL", 12.5, 400, col::MUT);
                auto at = V.volumeCalibratedAt[(size_t)ch - 1];
                label_at(t, 12, 64, at ? trf("%s 标定", md(*at).c_str()).c_str() : "", 13, 400, col::SEC);
            } else {
                label_at(t, 12, 34, tr("未标定"), 16, 400, col::SEC);
                label_at(t, 12, 62, trf("按釜 %s mL 估算", model::fmt_num(V.vesselVolume, 0).c_str()).c_str(), 13, 400, col::SEC);
            }
            on_tap(t, [this, ch] { ss().calib_ch = ch; refresh(); });
        }
        box(body_, 18, 164, bw_ - 36, 1, col::DIV);
        int lw = (bw_ - 36) * 54 / 100;
        bool this_running = running && cs->channel && *cs->channel == sel;
        if (!this_running) {
            bool first = !V.manifoldVolume;
            label_at(body_, 18, 182, trf("%d 号 · %s", sel, tr(first ? "第一次标定（同时得出总管体积）" : "标定")).c_str(), 16, 700, col::INK);
            const char* st[4] = {"空釜做一遍", "泄压、加 2.00 mL 水、重新密封", "加水后再做一遍", "算出总管体积和有效容积"};
            int n = first ? 4 : 1;
            for (int i = 0; i < n; ++i) {
                lv_obj_t* c = box(body_, 18, 218 + i * 44, 28, 28, 0xffffff, 14);
                ring(c, 0xc9d3de, 2);
                lv_obj_center(label(c, std::to_string(i + 1).c_str(), 13, 700, col::SEC));
                label_at(body_, 58, 221 + i * 44, tr(st[i]), 16, 400, col::SEC);
            }
            info_box(body_, 18, 218 + n * 44 + 10, lw, tr("请在常温、不加热时标定；一遍里釜温变化超过 1.5 °C 会判失败。之后其他通道只要空釜做一遍。"));
            bool can = m.ch(sel).installed && !m.ch(sel).programOwned && !running;
            lv_obj_t* go = box(body_, 18 + lw + 26, 218, bw_ - 36 - lw - 26, 52, can ? col::BLUE : 0xb3c2d4, 10);
            lv_obj_center(label(go, trf("开始标定 %d 号", sel).c_str(), 16, 700, 0xffffff));
            if (can) on_tap(go, [this, sel] { act().calib_start(sel, 2.0); });
            if (!can)
                label_at(body_, 18 + lw + 26, 282, tr(running ? "另一路正在标定，一次只能标一路" : "这一路正在跑程序或已停用"), 13, 400, col::SEC);
            return;
        }
        label_at(body_, 18, 182, trf("%d 号 · %s", sel, tr(C.passes > 1 ? "第一次标定（同时得出总管体积）" : "标定")).c_str(), 16, 700, col::INK);
        int stage = cs->stage == "first" ? 0 : cs->stage == "waitWater" ? 1 : cs->stage == "second" ? 2 : 3;
        const char* st[4] = {"空釜做一遍", "泄压、加 %s mL 水、重新密封", "加水后再做一遍", "算出总管体积和 %d 号有效容积"};
        for (int i = 0; i < 4; ++i) {
            int y = 218 + i * 44;
            bool done = i < stage, cur = i == stage;
            lv_obj_t* c = box(body_, 18, y, 28, 28, done ? 0x16a05a : cur ? col::BLUE : 0xffffff, 14);
            if (done) lv_obj_center(icon(c, &ic::check16, 0xffffff));
            else {
                if (!cur) ring(c, 0xc9d3de, 2);
                else {
                    lv_obj_set_style_outline_width(c, 4, 0);
                    lv_obj_set_style_outline_color(c, lv_color_hex(col::BLUE), 0);
                    lv_obj_set_style_outline_opa(c, 40, 0);
                }
                lv_obj_center(label(c, std::to_string(i + 1).c_str(), 13, 700, cur ? 0xffffff : col::SEC));
            }
            std::string t = i == 1 ? trf(st[1], model::fmt_num(cs->waterMl.value_or(2.0), 2).c_str()) : i == 3 ? trf(st[3], sel) : std::string(tr(st[i]));
            label_at(body_, 58, y + 3, t.c_str(), 16, cur ? 700 : 400, cur ? col::INK : done ? col::INK : col::SEC);
            std::string r;
            uint32_t rc = col::SEC;
            if (i == 0 && done) r = trf("%d min", C.stepMinutes[0]);
            if (i == 1 && done) r = tr("操作员");
            if (cur) { r = trf("第 %d / %d 次 · 总管充气", C.cycle, C.cycles); rc = col::BLUE; }
            if (!r.empty()) {
                lv_obj_t* l = label(body_, r.c_str(), 13.5, cur ? 600 : 400, rc);
                lv_obj_update_layout(l);
                lv_obj_set_pos(l, 18 + lw - lv_obj_get_width(l), y + 5);
            }
        }
        if (cs->stage == "waitWater") {
            lv_obj_t* cb = box(body_, 18, 400, 220, 48, col::BLUE, 9);
            lv_obj_center(label(cb, tr("水加好了，继续"), 16, 700, 0xffffff));
            on_tap(cb, [this] { act().calib_continue(); });
        }
        info_box(body_, 18, 408 + (cs->stage == "waitWater" ? 56 : 0), lw,
                 tr("请在常温、不加热时标定；一遍里釜温变化超过 1.5 °C 会判失败。之后其他通道只要空釜做一遍。"));
        // 右：每次读数
        int rx = 18 + lw + 26, rw = bw_ - 18 - rx;
        card_head(body_, rx, 182, tr("每次读数"), trf("釜温 %s °C · %s", model::fmt_num(C.vesselTempC, 1).c_str(), tr(C.gas.c_str())).c_str(), 16);
        lv_obj_t* tb = box(body_, rx, 218, rw, 34 + (int)C.readings.size() * 36, 0xffffff, 10);
        ring(tb, 0xe1e7ee, 1);
        lv_obj_set_style_clip_corner(tb, true, 0);
        box(tb, 0, 0, rw, 34, 0xf3f6f9);
        label_at(tb, 16, 9, tr("次"), 13, 400, col::SEC);
        label_at(tb, 60, 9, tr("总管充气 MPa"), 13, 400, col::SEC);
        label_at(tb, rw / 2 + 20, 9, tr("放进釜后 MPa"), 13, 400, col::SEC);
        for (size_t i = 0; i < C.readings.size(); ++i) {
            const auto& r = C.readings[i];
            int y = 34 + (int)i * 36;
            if (i) box(tb, 0, y, rw, 1, col::DIV);
            label_at(tb, 16, y + 9, std::to_string(r.n).c_str(), 15, 400, col::INK);
            label_at(tb, 60, y + 9, (model::fmt_num(r.manifoldFrom, 3) + " → " + model::fmt_num(r.manifoldTo, 3)).c_str(), 15, 400, col::INK);
            if (r.vesselFrom) label_at(tb, rw / 2 + 20, y + 9, (model::fmt_num(*r.vesselFrom, 3) + " → " + model::fmt_num(*r.vesselTo, 3)).c_str(), 15, 400, col::INK);
            else label_at(tb, rw / 2 + 20, y + 9, tr("进行中"), 15, 700, col::BLUE);
        }
        int by = 218 + 34 + (int)C.readings.size() * 36 + 14;
        int bwid = (rw - 10) / 2;
        lv_obj_t* ab = box(body_, rx, by, bwid, 46, 0xffffff, 10);
        ring(ab, 0xf0b4b4, 2);
        icon(ab, &ic::stop_sq14, col::RED, bwid / 2 - 48, 16);
        label_at(ab, bwid / 2 - 26, 12, tr("中止标定"), 15.5, 600, col::RED);
        on_tap(ab, [this] {
            confirm_stop(tr("中止容积标定？"), "", "", tr("中止后排空泄压，这一路的有效容积保持原来的值。"), tr("继续标定"), tr("中止"),
                         [this] { act().calib_abort(); });
        });
        lv_obj_t* cl = box(body_, rx + bwid + 10, by, bwid, 46, 0xffffff, 10);
        ring(cl, col::RING, 2);
        lv_obj_center(label(cl, tr("关掉，后台继续"), 15.5, 600, col::INK));
        on_tap(cl, [this] { app_->go(PageId::Home, 0); });
    }

    // ---------------------------------------------------------------- 4 通道与气路
    void channels(const Model& m) {
        header("通道与气路", "8 路反应釜 · 3 路气源 + 1 路排空");
        const auto& io = m.x.io;
        int lw = 358;
        lv_obj_t* g = group(body_, 18, 56, lw, 262, "气源与排空");
        const char* nm[4] = {"惰性气体", "气体 A", "气体 B", "排空 / 泄压"};
        const char* sub[4] = {"置换、泄漏测试、标定", "按程序选用", "按程序选用", ""};
        for (int i = 0; i < 4; ++i) {
            int coil = i < 3 ? io.inletCoil[(size_t)i] : io.ventCoil;
            kv_row(g, 14, 36 + i * 48, lw - 28, nm[i], io.ioModel + " " + tr("线圈") + " " + std::to_string(coil), sub[i], true, 15, 400);
        }
        lv_obj_t* fn = label_at(g, 14, 236, tr("气源压力由各路减压阀手动设定，软件不测量。"), 12.5, 400, col::SEC);
        (void)fn;
        int rx = 18 + lw + 14, rw = bw_ - 18 - rx;
        lv_obj_t* g2 = group(body_, rx, 56, rw, 34 + 36 + 8 * 46 + 46, "");
        card_head(g2, 14, 12, tr("反应釜"),
                  trf("釜 %s mL · 压力变送器满量程 %s MPa（在「参数与报警」改）", model::fmt_num(m.x.volume.vesselVolume, 0).c_str(),
                      model::fmt_num(m.settings.pressFullScaleMPa, 3).c_str()).c_str(), 15.5);
        lv_obj_t* tb = box(g2, 14, 40, rw - 28, 36 + 8 * 46, 0xffffff, 10);
        ring(tb, 0xe1e7ee, 1);
        lv_obj_set_style_clip_corner(tb, true, 0);
        int tw = rw - 28;
        box(tb, 0, 0, tw, 36, 0xf3f6f9);
        const char* H[5] = {"通道", "进气阀", "压力变送器", "有效容积", "启用"};
        int hx[5] = {14, 100, 234, 368, tw - 50};
        for (int i = 0; i < 5; ++i) label_at(tb, hx[i], 10, tr(H[i]), 13, 400, col::SEC);
        const auto& cs = m.snap.calib;
        for (int ch = 1; ch <= 8; ++ch) {
            int y = 36 + (ch - 1) * 46;
            if (ch > 1) box(tb, 0, y, tw, 1, col::DIV);
            mini_vessel(tb, 14, y + 13, 21, chan_color(ch), !m.ch(ch).installed);
            label_at(tb, 40, y + 13, trf("%d 号", ch).c_str(), 15, 400, col::INK);
            label_at(tb, 100, y + 13, ("SV" + std::to_string(ch) + " · " + tr("线圈") + " " + std::to_string(io.svCoil[(size_t)ch - 1])).c_str(), 15, 400, col::INK);
            label_at(tb, 234, y + 13, (io.aiModel + " " + tr("通道") + " " + std::to_string(io.aiChannel[(size_t)ch - 1])).c_str(), 15, 400, col::INK);
            const auto& ev = m.x.volume.effectiveVolume[(size_t)ch - 1];
            bool now = cs && cs->running && cs->channel && *cs->channel == ch;
            std::string v = now ? tr("标定中") : ev ? model::fmt_num(*ev, 2) + " mL" : std::string(tr("未标定"));
            label_at(tb, 368, y + 13, v.c_str(), 15, ev && !now ? 700 : 400, ev && !now ? col::INK : col::SEC);
            bool inst = m.ch(ch).installed;
            make_switch(tb, tw - 66, y + 8, inst, [this, ch](bool on) {
                if (!on && app_->m().ch(ch).programOwned) {
                    toast(trf("%d 号正在跑程序，先中止才能停用", ch), false);
                    return;
                }
                act().channel_enable(ch, on);
            });
        }
        label_at(g2, 14, 40 + 36 + 8 * 46 + 14, tr("停用的通道在主页和程序里变灰，不能选。有效容积在「容积标定」里做。"), 12.5, 400, col::SEC);
    }

    // ---------------------------------------------------------------- 5 网络
    void network(const Model& m) {
        const auto& N = m.x.net;
        header("网络", "连 WiFi 或网线；电脑远程看数据时用这里的地址");
        int lw = 559;
        int nh = 60 + 86 + 34 + (int)N.nearby.size() * 54 + 50;
        lv_obj_t* g = group(body_, 18, 56, lw, std::min(nh, bh_ - 56 - 18), "");
        label_at(g, 14, 18, "WiFi", 17, 700, col::INK);
        make_switch(g, 66, 14, N.wifiOn, [this](bool on) { act().wifi_enable(on); });
        lv_obj_t* rs = tool_btn(g, 0, 10, tr("重新搜索"), &ic::refresh18, col::INK, 40, 15);
        lv_obj_update_layout(rs);
        lv_obj_set_x(rs, lw - 14 - lv_obj_get_width(rs));
        on_tap(rs, [this] { act().wifi_scan(); toast(tr("正在搜索附近的网络…")); });
        int y = 60;
        if (!N.wifiOn) {
            label_at(g, 14, y, tr("WiFi 已关闭"), 15, 400, col::SEC);
        } else {
            if (!N.connectedSsid.empty()) {
                lv_obj_t* cb = box(g, 14, y, lw - 28, 74, 0xeef8f1, 10);
                ring(cb, 0xcfe9d9, 1);
                lv_obj_t* ci = box(cb, 14, 13, 48, 48, 0xffffff, 24);
                lv_obj_center(icon(ci, &ic::wifi26, 0x16a05a));
                lv_obj_t* n = label_at(cb, 76, 12, N.connectedSsid.c_str(), 19, 700, col::INK);
                lv_obj_update_layout(n);
                lv_obj_t* p = pill(cb, tr("已连接"), 0x14995a, 0xd7f0e1, 13, nullptr, true);
                lv_obj_set_pos(p, 76 + lv_obj_get_width(n) + 10, 14);
                label_at(cb, 76, 42, (N.ip + " · " + tr(N.strength >= 3 ? "信号强" : "信号弱") + " · " + tr(N.autoConnect ? "开机自动连接" : "不自动连接")).c_str(),
                         13.5, 400, col::SEC);
                lv_obj_t* dc = tool_btn(cb, 0, 16, tr("断开"), nullptr, col::INK, 42, 15);
                lv_obj_update_layout(dc);
                lv_obj_set_x(dc, lw - 28 - 14 - lv_obj_get_width(dc));
                lv_obj_t* fg = tool_btn(cb, 0, 16, tr("忘记"), nullptr, col::INK, 42, 15);
                lv_obj_update_layout(fg);
                lv_obj_set_x(fg, lv_obj_get_x(dc) - 10 - lv_obj_get_width(fg));
                std::string ssid = N.connectedSsid;
                on_tap(dc, [this] { act().wifi_disconnect(); });
                on_tap(fg, [this, ssid] { act().wifi_forget(ssid); });
                y += 86;
            }
            label_at(g, 14, y + 4, tr("附近的网络"), 13.5, 400, col::SEC);
            y += 30;
            for (auto& w : N.nearby) {
                lv_obj_t* wi = icon(g, &ic::wifi18, col::INK, 14, y + 18);
                lv_obj_set_style_image_opa(wi, (lv_opa_t)(w.strength >= 4 ? 255 : w.strength == 3 ? 200 : w.strength == 2 ? 140 : 90), 0);
                lv_obj_t* n = label_at(g, 50, y + 15, w.ssid.c_str(), 16, 400, col::INK);
                lv_obj_update_layout(n);
                if (w.secured) icon(g, &ic::lock14, col::SEC, 50 + lv_obj_get_width(n) + 10, y + 18);
                else label_at(g, 50 + lv_obj_get_width(n) + 10, y + 18, tr("没有密码"), 13, 400, col::SEC);
                lv_obj_t* cb = tool_btn(g, 0, y + 8, tr("连接"), nullptr, col::INK, 38, 15);
                lv_obj_update_layout(cb);
                lv_obj_set_x(cb, lw - 14 - lv_obj_get_width(cb));
                std::string ssid = w.ssid;
                bool sec = w.secured && !w.saved;
                on_tap(cb, [this, ssid, sec] {
                    if (sec) settings_open_wifi_password(*app_, ssid);
                    else { act().wifi_connect(ssid, ""); toast(trf("正在连接 %s…", ssid.c_str())); }
                });
                box(g, 14, y + 53, lw - 28, 1, col::DIV);
                y += 54;
                if (y > bh_ - 56 - 18 - 90) break;
            }
            icon(g, &ic::plus18, col::BLUE, 18, y + 16);
            lv_obj_t* hid = label_at(g, 44, y + 14, tr("加入隐藏网络…"), 15.5, 600, col::BLUE);
            on_tap(hid, [this] {
                TextKbd::open(tr("隐藏网络的名字（SSID）"), "", 32, [this](const std::string& s) {
                    if (!s.empty()) settings_open_wifi_password(*app_, s);
                });
            });
        }
        int rx = 18 + lw + 13, rw = bw_ - 18 - rx;
        lv_obj_t* g2 = group(body_, rx, 56, rw, 88, "有线网络");
        kv_row(g2, 14, 36, rw - 28, "以太网", N.ethernetPlugged ? N.ethernetIp : std::string(tr("未插网线")), nullptr, false, 14, 400);
        lv_obj_t* g3 = group(body_, rx, 156, rw, 262, "IP 地址");
        row_text(g3, 14, 36, rw - 28, "获取方式", "一般选自动");
        make_lamps(g3, rw - 14, 40, {tr("自动"), tr("手动")}, N.dhcp ? 0 : 1, [this](int i) { act().set_ip_mode(i == 0); });
        kv_row(g3, 14, 84, rw - 28, "本机地址", N.ip.empty() ? "—" : N.ip, nullptr, true, 15, 400);
        kv_row(g3, 14, 132, rw - 28, "网关", N.gateway.empty() ? "—" : N.gateway, nullptr, true, 15, 400);
        kv_row(g3, 14, 180, rw - 28, "主机名", N.hostname.empty() ? "—" : N.hostname, nullptr, true, 15, 400);
        label_at(g3, 14, 236, tr("手动时可以改本机地址、子网掩码、网关和 DNS。"), 12.5, 400, col::SEC);
    }

    // ---------------------------------------------------------------- 6 通讯与模块
    void comm(const Model& m) {
        const auto& C = m.x.comm;
        int on = 0, off = 0;
        for (auto& mx : C.modules) (mx.online ? on : off)++;
        header("通讯与模块", "4 路独立 RS485 · Modbus RTU", trf("%d 个在线 · %d 个离线", on, off));
        int cw = (bw_ - 36 - 13) / 2;
        for (size_t i = 0; i < C.modules.size() && i < 4; ++i) {
            const auto& mx = C.modules[i];
            int x = 18 + (int)(i % 2) * (cw + 13), y = 56 + (int)(i / 2) * 130;
            lv_obj_t* g = group(body_, x, y, cw, 116, "");
            lv_obj_t* ci = box(g, 14, 16, 44, 44, mx.online ? 0xe8f0fb : 0xfdecec, 22);
            lv_obj_center(icon(ci, mx.online ? &ic::plug20 : &ic::plug_off20, mx.online ? col::BLUE : col::RED));
            lv_obj_t* n = label_at(g, 72, 14, tr(mx.name.c_str()), 18, 700, col::INK);
            lv_obj_update_layout(n);
            if (mx.online) {
                lv_obj_t* p = pill(g, tr("在线"), 0x14995a, 0xe3f4ea, 13, nullptr, true);
                lv_obj_set_pos(p, 72 + lv_obj_get_width(n) + 10, 15);
            } else {
                int64_t since = mx.offlineSince.value_or(m.snap.ts);
                label_at(g, 72 + lv_obj_get_width(n) + 10, 17, trf("离线 %s", model::fmt_hmin((m.snap.ts - since) / 1000).c_str()).c_str(), 15, 700, col::RED);
            }
            lv_obj_t* md = label_at(g, 72, 42, (mx.model + " · " + tr(mx.detail.c_str())).c_str(), 14, 400, col::SEC);
            lv_obj_set_size(md, cw - 72 - 130, 20);
            lv_label_set_long_mode(md, LV_LABEL_LONG_DOT);
            std::string port = mx.port + " · " + std::to_string(mx.baud);
            std::string stats = mx.online ? trf("累计 %lld 次 · 失败 %lld · 最近 %s", (long long)mx.polls, (long long)mx.fails,
                                                mx.lastOkAt ? model::fmt_clock(*mx.lastOkAt).c_str() : "—")
                                          : trf("连续 %d 次通讯失败 · 最近 %s", std::max(3, mx.failStreak), mx.lastOkAt ? model::fmt_clock(*mx.lastOkAt).c_str() : "—");
            label_at(g, 14, 80, port.c_str(), 13, 400, col::SEC);
            label_at(g, mx.online ? 160 : 160, 80, stats.c_str(), 13, 400, col::SEC);
            std::string id = mx.id;
            if (mx.online) {
                lv_obj_t* b = tool_btn(g, 0, 18, tr("测试连接"), nullptr, col::INK, 42, 15);
                lv_obj_update_layout(b);
                lv_obj_set_x(b, cw - 14 - lv_obj_get_width(b));
                on_tap(b, [this, id] { act().comm_test(id); toast(tr("已发出测试")); });
            } else {
                const lv_font_t* f = font(15.5, 700);
                int bw = text_width(tr("重新连接"), f) + 18 + 8 + 32;
                lv_obj_t* b = box(g, cw - 14 - bw, 18, bw, 42, col::BLUE, 9);
                icon(b, &ic::refresh18, 0xffffff, 16, 12);
                lv_obj_align(label(b, tr("重新连接"), 15.5, 700, 0xffffff), LV_ALIGN_LEFT_MID, 42, 0);
                on_tap(b, [this, id] { act().comm_reconnect(id); });
            }
        }
        int y = 56 + 2 * 130;
        lv_obj_t* g1 = group(body_, 18, y, cw, 136, "网络");
        const auto& N = m.x.net;
        kv_row(g1, 14, 36, cw - 28, "WiFi", N.connectedSsid.empty() ? std::string(tr("未连接")) : N.connectedSsid + " · " + N.ip, "在「网络」里连接 / 切换");
        kv_row(g1, 14, 84, cw - 28, "主机名", N.hostname, nullptr, false);
        lv_obj_t* g2 = group(body_, 18 + cw + 13, y, cw, 136, "轮询");
        kv_row(g2, 14, 36, cw - 28, "轮询周期", model::fmt_num(C.pollSec, 1) + " s");
        kv_row(g2, 14, 84, cw - 28, "离线判定", trf("连续 %d 次通讯失败", C.offlineAfter), nullptr, false);
        info_box(body_, 18, y + 150, bw_ - 36,
                 tr("串口在 run.sh 里用环境变量配置（PR_TEMP_PORT / PR_PRESS_PORT / PR_IO_PORT / PR_STIR_PORT）。离线模块的读数显示「—」，依赖它的操作会被拒绝。"));
    }

    // ---------------------------------------------------------------- 7 数据存储
    void data(const Model& m) {
        const auto& S = m.x.storage;
        header("数据存储", "运行记录和设置都存在这台机器上");
        lv_obj_t* g = group(body_, 18, 56, bw_ - 36, 104, "磁盘");
        int gw = bw_ - 36;
        std::vector<SpanDef> sp = {{fmt_bytes(S.diskFree).substr(0, fmt_bytes(S.diskFree).find(' ')), 24, 700, col::DEEP},
                                   {std::string(" GB ") + tr("可用 / 共 ") + fmt_bytes(S.diskTotal), 14, 400, col::SEC}};
        lv_obj_t* r = rich(g, 0, 10, 400, sp);
        lv_obj_update_layout(r);
        lv_obj_set_pos(r, gw - 14 - 300, 12);
        lv_obj_set_width(r, 300);
        lv_obj_set_style_text_align(r, LV_TEXT_ALIGN_RIGHT, 0);
        double tot = (double)std::max<int64_t>(1, S.diskTotal);
        int bw = gw - 28;
        box(g, 14, 54, bw, 10, 0xe6ebf1, 5);
        int w1 = (int)(bw * S.systemBytes / tot), w2 = (int)(bw * S.dataBytes / tot);
        box(g, 14 + w1, 54, std::max(4, w2), 10, 0x7fa6d6, 3);
        box(g, 14, 54, w1, 10, col::BLUE, 5);
        box(g, 14, 77, 10, 10, col::BLUE, 2);
        lv_obj_t* l1 = label_at(g, 30, 73, (std::string(tr("系统和软件 ")) + fmt_bytes(S.systemBytes)).c_str(), 13.5, 400, col::SEC);
        lv_obj_update_layout(l1);
        int lx2 = 30 + lv_obj_get_width(l1) + 20;  // 跟在第一项后面（系统占用到 100 GB 级时原来的固定 x 会叠字）
        box(g, lx2, 77, 10, 10, 0x7fa6d6, 2);
        label_at(g, lx2 + 16, 73, trf("运行记录 %s（%d 天）", fmt_bytes(S.dataBytes).c_str(), (int)m.x.prefs.retentionDays).c_str(), 13.5, 400, col::SEC);
        int lw = 510;
        lv_obj_t* g2 = group(body_, 18, 172, lw, 192, "放在哪里");
        kv_row(g2, 14, 36, lw - 28, "设置与 PID 存档", S.settingsPath, "全局设置、报警阈值、温区边界、8 路 PID", true, 14, 400);
        kv_row(g2, 14, 84, lw - 28, "运行记录目录", S.dataDir, "环境变量 PR_DATA_DIR 可改", true, 14, 400);
        num_row(g2, 14, 132, lw - 28, "保留", "超过的自动删除最旧的", m.x.prefs.retentionDays, 0, tr("天"), 7, 3650,
                [this](double v) { act().patch_setting("ui.retentionDays", v); }, false, 64);
        int rx = 18 + lw + 13, rw = bw_ - 18 - rx;
        lv_obj_t* g3 = group(body_, rx, 172, rw, 250, "记录了什么");
        struct F { const char* ext; const char* name; const char* sub; std::string right; };
        const F fs[3] = {{"CSV", "telemetry-日期.csv", "每 1.2 s 一行：8 路温度、压力、阀门、吸气量", std::string(tr("今天 ")) + fmt_bytes(S.todayTelemetryBytes)},
                         {"JSONL", "events-日期.jsonl", "报警与事件", std::string(tr("今天 ")) + fmt_bytes(S.todayEventsBytes)},
                         {"", "leak/", "泄漏测试逐点数据", trf("共 %d 次", S.leakRuns)}};
        for (int i = 0; i < 3; ++i) {
            int y = 38 + i * 50;
            if (fs[i].ext[0]) file_icon(g3, 14, y + 4, fs[i].ext);
            else icon(g3, &ic::folder30, 0, 14, y + 6);
            label_at(g3, 52, y + 2, tr(fs[i].name), 15.5, 500, col::INK);
            label_at(g3, 52, y + 24, tr(fs[i].sub), 12.5, 400, col::SEC);
            lv_obj_t* rr = label(g3, fs[i].right.c_str(), 14, 400, col::SEC);
            lv_obj_update_layout(rr);
            lv_obj_set_pos(rr, rw - 14 - lv_obj_get_width(rr), y + 12);
            if (i < 2) box(g3, 14, y + 47, rw - 28, 1, col::DIV);
        }
        lv_obj_t* ex = tool_btn(g3, 14, 194, tr("导出到 U 盘…"), &ic::download16, col::INK, 42, 15.5);
        on_tap(ex, [this] { app_->go(PageId::Records, 1); });
    }

    // ---------------------------------------------------------------- 8 用户与权限
    void users(const Model& m) {
        header("用户与权限", "现在是单用户，所有操作都以管理员身份进行");
        int lw = 488;
        lv_obj_t* g = group(body_, 18, 56, lw, 100, "");
        lv_obj_t* av = box(g, 20, 18, 64, 64, 0xe8f0fb, 32);
        lv_obj_center(icon(av, &ic::m_user30, col::BLUE));
        label_at(g, 100, 26, tr(m.x.users.role == "admin" ? "管理员" : "操作员"), 20, 700, col::INK);
        label_at(g, 100, 56, tr(m.x.users.mode == "single" ? "当前模式 · 单用户" : "当前模式 · 多用户"), 13.5, 400, col::SEC);
        int rx = 18 + lw + 13, rw = bw_ - 18 - rx;
        lv_obj_t* g2 = group(body_, rx, 56, rw, 172, "管理员可以");
        const char* it[4] = {"控制阀门（手动阀控）", "改参数与报警阈值、PID", "停用通道", "做容积标定、泄漏测试"};
        for (int i = 0; i < 4; ++i) {
            icon(g2, &ic::check_sm16, 0x16a05a, 16, 44 + i * 30);
            label_at(g2, 42, 42 + i * 30, tr(it[i]), 15, 400, col::INK);
        }
        info_box(body_, 18, 240, bw_ - 36,
                 tr("账户登录与权限分级还没启用。以后启用时，这里加「操作员 / 管理员」两级和登录密码：操作员只能看和运行程序，上面四项要管理员。"));
    }

    // ---------------------------------------------------------------- 9 关于
    void about(const Model& m) {
        header("关于", "本机信息和联系方式");
        lv_obj_t* nv = box(body_, 18, 56, 380, 190, 0x0a2a55, 12);
        label_at(nv, 22, 18, "HTLAB", 26, 700, 0xffffff);
        lv_obj_t* pn = label_at(nv, 22, 56, tr("霍桐仪器多通道平行反应仪控制系统"), 17, 400, 0xffffff);
        lv_obj_set_style_text_opa(pn, 230, 0);
        int64_t up = (m.snap.ts - m.bootAt) / 1000;
        const std::string rows[3][2] = {{tr("硬件型号"), m.x.about.model}, {tr("软件版本"), m.x.about.version},
                                        {tr("本次运行"), trf("%lld 小时 %lld 分钟", (long long)(up / 3600), (long long)(up / 60 % 60))}};
        for (int i = 0; i < 3; ++i) {
            lv_obj_t* k = label_at(nv, 22, 92 + i * 28, rows[i][0].c_str(), 14, 400, 0xffffff);
            lv_obj_set_style_text_opa(k, 180, 0);
            label_at(nv, 118, 91 + i * 28, rows[i][1].c_str(), 15, 500, 0xffffff);
        }
        int rx = 18 + 380 + 13, rw = bw_ - 18 - rx;
        lv_obj_t* g = group(body_, rx, 56, rw, 332, "");
        label_at(g, 20, 18, tr("关于霍桐仪器"), 15.5, 700, col::INK);
        lv_obj_t* p = label_at(g, 20, 46,
                               tr("上海霍桐实验仪器有限公司成立于 2015 年，专注于反应设备及成套化工装配产线的研发、制造与服务。产品涵盖高压、高温、耐腐蚀等特种反应设备，用于医药合成、化工工艺开发、新材料研制等场景。"),
                               14, 400, col::SEC);
        lv_obj_set_width(p, rw - 40);
        lv_label_set_long_mode(p, LV_LABEL_LONG_WRAP);
        lv_obj_set_style_text_line_space(p, 6, 0);
        const char* ks[4] = {"电话", "邮箱", "官网", "地址"};
        const char* vs[4] = {"021-6723-0701", "Htlab@htlab.com", "www.htlab.cn", "上海市闵行区东川路 3966 号 3 幢 2 楼"};
        for (int i = 0; i < 4; ++i) {
            label_at(g, 20, 134 + i * 28, tr(ks[i]), 14, 400, col::SEC);
            label_at(g, 72, 133 + i * 28, tr(vs[i]), 15, 400, col::INK);
        }
        lv_obj_t* qb = box(g, rw - 20 - 142, 118, 142, 146, 0xffffff, 10);
        ring(qb, 0xe1e7ee, 1);
        lv_obj_center(icon(qb, &ic::qr126, col::INK));
        lv_obj_t* qt = label(g, tr("扫码了解更多"), 12.5, 400, col::SEC);
        lv_obj_update_layout(qt);
        lv_obj_set_pos(qt, rw - 20 - 71 - lv_obj_get_width(qt) / 2, 270);
        box(g, 20, 292, rw - 40, 1, col::DIV);
        label_at(g, 20, 302, tr("© 2026 上海霍桐实验仪器有限公司 · Huotong Instruments"), 12.5, 400, col::SEC);
    }

    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr; lv_obj_t* menu_ = nullptr; lv_obj_t* body_ = nullptr;
    int W_ = 1280, H_ = 740, sec_ = 0, ver_ = 0, bw_ = 1024, bh_ = 716;
    std::string sig_;
};

std::unique_ptr<Page> make_settings_page(int sec) { return std::make_unique<SettingsPage>(sec); }

}  // namespace reactor::hmi
