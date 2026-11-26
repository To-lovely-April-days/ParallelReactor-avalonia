#include "app.hpp"

#include <algorithm>
#include <chrono>

#include "kit/i18n.hpp"
#include "pages/common2.hpp"
#include <cstdint>
#include <memory>
#include <string>

namespace reactor::hmi {

App::App(model::Actions& act, int w, int h) : act_(act), w_(w), h_(h) {}

void App::build(lv_obj_t* screen) {
    screen_ = screen;
    lv_obj_set_style_bg_color(screen, lv_color_hex(col::BG), 0);
    lv_obj_set_style_bg_opa(screen, LV_OPA_COVER, 0);
    lv_obj_remove_flag(screen, LV_OBJ_FLAG_SCROLLABLE);
    top_.create(screen, w_);
    top_.on_nav = [this](int i) {
        static const PageId P[5] = {PageId::Home, PageId::Program, PageId::Curve, PageId::Records, PageId::Settings};
        go(P[i], i == 0 ? home_tab : 0);
    };
    top_.on_bell = [this] { go(PageId::Alarm); };
    top_.on_lang = [this] { open_lang(); };
    top_.on_remote = [this] {
        kit::DialogSpec s;
        s.w = 560;
        s.color = col::BLUE;
        s.icon = &kit::ic::info22;
        s.title = tr("收回控制权？");
        s.secondary = tr("不收回");
        s.primary = tr("收回");
        s.on_primary = [this] { act_.control_take_back(); };
        std::string holder = model_ && model_->snap.mode.holder ? *model_->snap.mode.holder : std::string();
        s.body = [holder](lv_obj_t* b, int w) {
            std::string t = holder.empty() ? std::string(tr("现在由 PC 控制。收回后 PC 只能看，不能再操作；设备照常运行。"))
                                           : trf("现在由「%s」控制。收回后 PC 只能看，不能再操作；设备照常运行。", holder.c_str());
            lv_obj_t* l = label_at(b, 0, 0, t.c_str(), 15, 400, col::INK);
            lv_obj_set_width(l, w);
            lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
            return 48;
        };
        kit::Dialog::open(s);
    };
    content_ = box(screen, 0, 60, w_, h_ - 60, 0, 0, true);
}

void App::update_topbar() {
    const model::Model& m = *model_;
    top_.set_status(m.connected, m.snap.simulated);
    top_.set_remote(m.snap.mode.control == "remote", m.snap.mode.holder.value_or(""));
    int worst = 3;
    for (auto& a : m.alarms)
        if (a.dto.state == "active" && a.ext.tier && a.ext.tier < worst) worst = a.ext.tier;
    top_.set_bell(m.unacked(), worst, page_ == PageId::Alarm);
    top_.set_time(m.snap.ts);
}

void App::apply(std::shared_ptr<const model::Model> m) {
    if (!m) return;
    model_ = std::move(m);
    if (lang_code(lang()) != model_->languageCode) {
        // 语言由后台保存（PUT /system/language），快照带回来以后才换
        for (Lang l : {Lang::Zh, Lang::En, Lang::Ja, Lang::Ko, Lang::De, Lang::Fr, Lang::Es, Lang::Ru})
            if (model_->languageCode == lang_code(l) && l != lang()) {
                set_lang(l);
                rebuild();
                return;
            }
    }
    if (!cur_) go(page_, arg_);
    update_topbar();
    update_link_overlay();
    update_control_request();
    if (cur_) cur_->update(*model_);
    if (overlay_update) overlay_update(*model_);
    // 新出现的报警按级别弹出。界面启动前就有的不弹（在报警页、铃铛上可见）：连后台时第一份快照里还没有报警表，
    // 要等第一次 GET /alarms 取回来（alarmsLoaded）才算「已有」；这之前只弹启动以后才产生的。
    if (!started_ms_)
        started_ms_ = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
    for (auto it = model_->alarms.rbegin(); it != model_->alarms.rend(); ++it) {
        const auto& a = *it;
        if (seen_alarms_.count(a.dto.id)) continue;
        seen_alarms_.insert(a.dto.id);
        bool fresh = !first_ || a.dto.raisedAt > started_ms_;
        if (fresh && a.dto.state == "active") show_alarm(a);
    }
    if (model_->alarmsLoaded) first_ = false;
}

void App::update_link_overlay() {
    const model::Model& m = *model_;
    if (m.connected) {
        if (link_) {
            lv_obj_delete(link_);
            link_ = link_sub_ = nullptr;
        }
        return;
    }
    if (!link_) {
        // 顶栏透过半透明的底还能看到「未连接」；盖层本身可点，吃掉所有点击，下面的按钮都按不到
        link_ = box(lv_layer_sys(), 0, 0, w_, h_, 0x0a1a2f, 0);
        lv_obj_set_style_bg_opa(link_, LV_OPA_60, 0);
        lv_obj_add_flag(link_, LV_OBJ_FLAG_CLICKABLE);
        lv_obj_remove_flag(link_, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_t* c = box(link_, (w_ - 560) / 2, (h_ - 200) / 2, 560, 200, 0xffffff, 16);
        lv_obj_remove_flag(c, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_t* sp = lv_spinner_create(c);
        lv_obj_set_size(sp, 52, 52);
        lv_obj_set_pos(sp, 32, 40);
        lv_obj_set_style_arc_width(sp, 6, LV_PART_MAIN);
        lv_obj_set_style_arc_width(sp, 6, LV_PART_INDICATOR);
        lv_obj_set_style_arc_color(sp, lv_color_hex(col::DIV), LV_PART_MAIN);
        lv_obj_set_style_arc_color(sp, lv_color_hex(col::BLUE), LV_PART_INDICATOR);
        label_at(c, 108, 40, tr("正在连接后台服务…"), 24, 700, col::INK);
        label_at(c, 108, 78, tr("连上以后自动恢复，操作暂时不能用。"), 15, 400, col::SEC);
        link_sub_ = label_at(c, 32, 128, "", 13, 400, col::MUT);
        lv_obj_set_width(link_sub_, 560 - 64);
        lv_label_set_long_mode(link_sub_, LV_LABEL_LONG_WRAP);
    }
    lv_label_set_text(link_sub_, m.linkStatus.c_str());
}

void App::update_control_request() {
    const auto& pend = model_->controlPending;
    // 申请没了（已处理 / 过期 / 被别处处理）或换了一个：关掉旧弹窗
    if (ctl_dim_ && (!pend || pend->requestId != ctl_id_)) {
        lv_obj_delete(ctl_dim_);   // DELETE 事件里清空 ctl_dim_ / ctl_left_
    }
    if (!pend) { ctl_id_.clear(); return; }
    auto left_text = [this, &pend]() -> std::string {
        std::string who = pend->tokenName.empty() ? std::string() : trf("令牌 %s", pend->tokenName.c_str());
        if (pend->expiresAt <= 0) return who;
        int64_t s = std::max<int64_t>(0, (pend->expiresAt - model_->snap.ts + 999) / 1000);
        std::string t = trf("%lld s 后自动拒绝", (long long)s);
        return who.empty() ? t : who + " · " + t;
    };
    if (ctl_dim_) {  // 同一个申请：只刷新倒计时
        if (ctl_left_) lv_label_set_text(ctl_left_, left_text().c_str());
        return;
    }
    if (pend->requestId == ctl_id_) return;  // 这一个已经点过了（等后台确认）
    ctl_id_ = pend->requestId;
    std::string id = pend->requestId;
    std::string client = pend->clientName.empty() ? pend->tokenName : pend->clientName;
    std::string head = trf("「%s」申请控制这台设备", client.c_str());
    std::string sub = left_text();
    kit::DialogSpec s;
    s.w = 600;
    s.color = col::BLUE;
    s.icon = &kit::ic::info22;
    s.title = tr("PC 申请远程控制");
    s.secondary = tr("拒绝");
    s.primary = tr("允许");
    s.on_primary = [this, id] { act_.control_grant(id); };
    s.on_secondary = [this, id] { act_.control_deny(id); };
    lv_obj_t** left = &ctl_left_;
    s.body = [head, sub, left](lv_obj_t* b, int w) {
        lv_obj_t* h = label_at(b, 0, 0, head.c_str(), 18, 700, col::INK);
        lv_obj_set_width(h, w);
        dot_line(h);
        *left = label_at(b, 0, 28, sub.c_str(), 13.5, 400, col::SEC);
        lv_obj_t* hl = box(b, 0, 60, w, 70, 0xeef4fb, 10);
        lv_obj_t* t = label(hl, tr("允许后，PC 可以启停加热、下发和中止程序。触屏照常显示读数，全部停止始终有效。"), 15, 400, col::INK);
        lv_obj_set_width(t, w - 32);
        lv_label_set_long_mode(t, LV_LABEL_LONG_WRAP);
        lv_obj_align(t, LV_ALIGN_LEFT_MID, 16, 0);
        return 130;
    };
    ctl_dim_ = kit::Dialog::open(s);
    lv_obj_add_event_cb(ctl_dim_, [](lv_event_t* e) {
        App* a = static_cast<App*>(lv_event_get_user_data(e));
        a->ctl_dim_ = nullptr;
        a->ctl_left_ = nullptr;
    }, LV_EVENT_DELETE, this);
}

void App::go(PageId p, int arg) {
    close_overlays();
    page_ = p;
    arg_ = arg;
    if (p == PageId::Home) home_tab = arg;
    lv_obj_clean(content_);
    cur_.reset();
    switch (p) {
        case PageId::Home: cur_ = make_home_page(arg); top_.set_nav(0); break;
        case PageId::ChanFull: cur_ = make_chan_full_page(arg); top_.set_nav(0); break;
        case PageId::Alarm: cur_ = make_alarm_page(); top_.set_nav(-1); break;
        case PageId::Program: cur_ = make_program_page(arg); top_.set_nav(1); break;
        case PageId::Curve: cur_ = make_curve_page(arg); top_.set_nav(2); break;
        case PageId::Records: cur_ = make_records_page(arg); top_.set_nav(3); break;
        case PageId::Settings: cur_ = make_settings_page(arg); top_.set_nav(4); break;
    }
    if (!model_) return;
    cur_->build(content_, *this);
    cur_->update(*model_);
    update_topbar();
}

void App::rebuild() {
    if (link_) {
        lv_obj_delete(link_);
        link_ = link_sub_ = nullptr;
    }
    lv_obj_clean(screen_);
    cur_.reset();
    overlay_ = nullptr;
    overlay_update = nullptr;
    top_ = kit::TopBar{};
    build(screen_);
    go(page_, arg_);
}

void App::close_overlays() {
    overlay_update = nullptr;
    if (overlay_) {
        lv_obj_delete(overlay_);
        overlay_ = nullptr;
    }
    kit::NumPad::close();
    kit::TextKbd::close();
}

void App::open_quick(int ch) {
    close_overlays();
    overlay_ = open_chan_quick(*this, ch);
}

void App::open_lang() {
    close_overlays();
    overlay_ = open_lang_popup(*this);
}

void App::show_alarm(const model::AlarmItem& a) {
    const model::Model& m = *model_;
    switch (a.ext.tier) {
        case 1: {
            int more = 0;
            for (auto& x : m.alarms) more += x.dto.state == "active" && x.dto.id != a.dto.id;
            std::string id = a.dto.id;
            kit::AlarmFull::show(
                a, more, [this, id] { act_.ack_alarm(id); }, [this] { go(PageId::Alarm); }, [this] { go(PageId::Curve); });
            break;
        }
        case 2: {
            std::string id = a.dto.id;
            kit::alarm_dialog(a, m.snap.ts, [this, id] { act_.ack_alarm(id); }, [this] { go(PageId::Alarm); });
            break;
        }
        case 3:
            kit::Bubble::show(a, w_ - 185, [this] { go(PageId::Alarm); });
            break;
        default: break;
    }
}

void App::stop_all_confirm() {
    int n = (int)model_->snap.channels.size();
    bool stir_on = model_->snap.stir.on;
    // 「同时停搅拌」开关，默认关（决定记录 2026-11-26；和 C# stop-all {stopStir?} 一致）
    auto stop_stir = std::make_shared<bool>(false);
    kit::DialogSpec s;
    s.w = 560;
    s.title = tr("全部停止？");
    s.secondary = tr("不停");
    s.primary = tr("全部停止");
    s.on_primary = [this, stop_stir] { act_.stop_all(*stop_stir); };
    s.body = [n, stop_stir, stir_on](lv_obj_t* b, int w) {
        lv_obj_t* l = label_at(b, 0, 0, trf("停止全部 %d 路的加热，中止正在跑的程序和气路，关闭全部阀门。", n).c_str(), 15, 400, col::INK);
        lv_obj_set_width(l, w);
        lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
        lv_obj_t* hl = box(b, 0, 52, w, 45, 0xfdf3f2, 10);
        lv_obj_t* t = label(hl, tr("搅拌照常转，不放空；停完可以正常重新开始。"), 15, 400, col::INK);
        lv_obj_align(t, LV_ALIGN_LEFT_MID, 16, 0);
        // 开关行
        lv_obj_t* row = box(b, 0, 109, w, 46, 0, 0, true);
        label_at(row, 0, 4, tr("同时停搅拌"), 16, 600, col::INK);
        label_at(row, 0, 27, tr(stir_on ? "一台电机带全部釜：打开后 8 个釜都停转" : "搅拌现在没有转"), 12.5, 400, col::MUT);
        auto* sw = new kit::Switch;
        sw->create(row, w - 64, 6, false);
        lv_obj_add_event_cb(sw->obj(), [](lv_event_t* e) { delete static_cast<kit::Switch*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, sw);
        sw->on_change = [sw, stop_stir, t](bool on) {
            *stop_stir = on;
            sw->set(on);
            lv_label_set_text(t, tr(on ? "搅拌也停，不放空；停完可以正常重新开始。" : "搅拌照常转，不放空；停完可以正常重新开始。"));
        };
        return 155;
    };
    kit::Dialog::open(s);
}

void App::abort_confirm(int ch) {
    const model::Model& m = *model_;
    const auto& c = m.ch(ch);
    const auto& p = m.progress[static_cast<size_t>(ch - 1)];
    std::string who = trf("%d 号 · %s", ch, p.programName.c_str());
    std::string step;
    if (c.program && c.program->stepIndex < (int)p.stepNames.size())
        step = trf("第 %d 步 %s", c.program->stepIndex + 1, tr(p.stepNames[static_cast<size_t>(c.program->stepIndex)].c_str()));
    std::string detail = step + " · " + model::fmt_temp(c) + " °C · " + trf("已运行 %s", model::fmt_hmin(p.elapsedSec).c_str());
    std::string c1 = tr("中止后，"), c2 = trf("%d 号停止加热、关闭进气阀", ch), c3 = tr("，其他通道照常运行。");
    kit::DialogSpec s;
    s.w = 560;
    s.title = tr("中止程序？");
    s.secondary = tr("继续运行");
    s.primary = tr("中止");
    s.on_primary = [this, ch] { act_.abort_program(ch); };
    s.body = [who, detail, c1, c2, c3, ch](lv_obj_t* b, int w) {
        kit::mini_vessel(b, 0, 5, 34, chan_color(ch));
        label_at(b, 38, 0, who.c_str(), 18, 700, col::INK);
        label_at(b, 38, 25, detail.c_str(), 13.5, 400, col::SEC);
        lv_obj_t* hl = box(b, 0, 58, w, 45, 0xfdf3f2, 10);
        rich(hl, 16, 12, w - 32, {{c1, 15, 400, col::INK}, {c2, 15, 700, col::RED}, {c3, 15, 400, col::INK}});
        return 103;
    };
    kit::Dialog::open(s);
}

}  // namespace reactor::hmi
