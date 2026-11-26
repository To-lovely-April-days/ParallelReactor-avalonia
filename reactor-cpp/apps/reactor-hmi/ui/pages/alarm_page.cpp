// 报警页（Alarm-Page）：左边报警与事件列表，右边选中那条的详情；顶上分 全部 / 未处理 / 报警 / 警告 / 事件
#include "../app.hpp"
#include "../kit/i18n.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <cstdlib>
#include <map>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;

namespace {

struct Row {
    int64_t ts = 0;
    const model::AlarmItem* alarm = nullptr;
    const model::EventRecord* event = nullptr;
    std::string level;  // err / warn / info
};

std::string md_hms(int64_t ms) {
    std::time_t t = static_cast<std::time_t>(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char b[32];
    std::strftime(b, sizeof b, "%m-%d %H:%M:%S", &tm);
    return b;
}

}  // namespace

class AlarmPage : public Page {
public:
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        H_ = app.h() - 60;
    }

    void update(const Model& m) override {
        // 数据变了才重建（报警条数 / 状态）
        std::string sig;
        for (auto& a : m.alarms) sig += a.dto.id + a.dto.state;
        sig += std::to_string(m.events.size()) + "/" + std::to_string(filter_) + "/" + sel_id_;
        if (sig == sig_) return;
        sig_ = sig;
        lv_obj_clean(root_);
        rows_.clear();
        // 报警表（/alarms）里的每一条后台也记成了事件（/events）：同一件事只列一行。
        // 报警 / 警告用报警那一条（能确认、有状态）；普通事件（info）用事件那一条。
        std::multimap<std::string, int64_t> alarm_keys;
        for (auto& a : m.alarms) {
            if (a.dto.level == "info") continue;
            rows_.push_back({a.dto.raisedAt, &a, nullptr, a.dto.level});
            alarm_keys.emplace(a.dto.title + "\x1f" + a.dto.message, a.dto.raisedAt);
        }
        for (auto& e : m.events) {
            bool dup = false;
            auto rg = alarm_keys.equal_range(e.title + "\x1f" + e.message);
            for (auto it = rg.first; it != rg.second && !dup; ++it) dup = std::llabs(it->second - e.ts) < 3000;
            if (!dup) rows_.push_back({e.ts, nullptr, &e, e.level == "warn" ? "warn" : e.level == "err" ? "err" : "info"});
        }
        std::stable_sort(rows_.begin(), rows_.end(), [](const Row& a, const Row& b) { return a.ts > b.ts; });
        int n_all = (int)rows_.size(), n_open = 0, n_err = 0, n_warn = 0, n_ev = 0;
        for (auto& r : rows_) {
            if (r.alarm && r.alarm->dto.state == "active") ++n_open;
            if (r.alarm && r.level == "err") ++n_err;
            else if (r.level == "warn") ++n_warn;
            if (r.event && r.level == "info") ++n_ev;
        }
        std::vector<TabDef> tabs = {{nullptr, "全部", std::to_string(n_all), TabDef::Grey},
                                    {nullptr, "未处理", std::to_string(n_open), TabDef::Red},
                                    {nullptr, "报警", std::to_string(n_err), TabDef::Grey},
                                    {nullptr, "警告", std::to_string(n_warn), TabDef::Grey},
                                    {nullptr, "事件", std::to_string(n_ev), TabDef::Grey}};
        tabs_ = TabCard{};
        tabs_.create(root_, 8, 4, W_ - 16, tabs, filter_);
        tabs_.on_select = [this](int i) { filter_ = i; refresh_later(); };
        std::string boot = trf("本次开机以来 · %s 开机", md_hms(m.bootAt).substr(0, 11).c_str());
        lv_obj_t* bl = label(tabs_.obj(), boot.c_str(), 13.5, 400, col::MUT);
        lv_obj_align(bl, LV_ALIGN_RIGHT_MID, -16, 0);

        std::vector<const Row*> shown;
        for (auto& r : rows_) {
            bool ok = filter_ == 0 || (filter_ == 1 && r.alarm && r.alarm->dto.state == "active") ||
                      (filter_ == 2 && r.alarm && r.level == "err") || (filter_ == 3 && r.level == "warn") ||
                      (filter_ == 4 && r.event && r.level == "info");
            if (ok) shown.push_back(&r);
        }
        // 默认选第一条未处理的报警
        const Row* sel = nullptr;
        for (auto* r : shown)
            if (r->alarm && r->alarm->dto.id == sel_id_) sel = r;
        if (!sel)
            for (auto* r : shown)
                if (r->alarm && r->alarm->dto.state == "active") { sel = r; break; }
        if (!sel)
            for (auto* r : shown)
                if (r->alarm) { sel = r; break; }
        if (sel) sel_id_ = sel->alarm->dto.id;

        int lw = 800, top = 52, ch = H_ - top - 12;
        lv_obj_t* L = card(root_, 12, top, lw, ch);
        lv_obj_t* tl = label_at(L, 16, 16, tr("报警与事件"), 17, 700, col::INK);
        lv_obj_update_layout(tl);
        int x = 16 + lv_obj_get_width(tl) + 12;
        lv_obj_t* cnt = label_at(L, x, 19, trf("共 %d 条 ·", n_all).c_str(), 13.5, 400, col::MUT);
        lv_obj_update_layout(cnt);
        label_at(L, x + lv_obj_get_width(cnt) + 4, 19, trf("未处理 %d", n_open).c_str(), 13.5, 700, col::RED);
        lv_obj_t* ack = box(L, lw - 150, 9, 140, 38, 0, 0, true);
        lv_obj_t* ac = box(ack, 0, 0, 38, 38, 0xffffff, 19);
        ring(ac, col::BLUE, 2);
        lv_obj_center(icon(ac, &ic::check20b, col::BLUE));
        label_at(ack, 46, 9, tr("全部确认"), 14.5, 600, col::INK);
        if (n_open) {
            lv_obj_t* b = label_at(ack, 112, 9, std::to_string(n_open).c_str(), 12.5, 700, 0xffffff);
            lv_obj_set_style_bg_color(b, lv_color_hex(col::RED), 0);
            lv_obj_set_style_bg_opa(b, LV_OPA_COVER, 0);
            lv_obj_set_style_radius(b, 10, 0);
            lv_obj_set_style_pad_hor(b, 7, 0);
            lv_obj_set_style_pad_ver(b, 1, 0);
        }
        App* app = app_;
        on_tap(ack, [app] { app->act().ack_all(); });
        // 表
        int tw = lw - 32, th = ch - 64 - 18;
        lv_obj_t* T = box(L, 16, 64, tw, th, 0xffffff, 10);
        ring(T, 0xdfe6ee, 1);
        lv_obj_set_style_clip_corner(T, true, 0);
        lv_obj_t* hd = box(T, 0, 0, tw, 34, col::TILE);
        box(hd, 0, 33, tw, 1, 0xe3e9f0);
        label_at(hd, 16, 8, tr("时间"), 12.5, 400, col::MUT);
        label_at(hd, 150, 8, tr("来源"), 12.5, 400, col::MUT);
        label_at(hd, 242, 8, tr("内容"), 12.5, 400, col::MUT);
        label_at(hd, 660, 8, tr("状态"), 12.5, 400, col::MUT);
        lv_obj_t* list = box(T, 0, 34, tw, th - 34, 0, 0, true);
        lv_obj_add_flag(list, LV_OBJ_FLAG_SCROLLABLE);
        lv_obj_set_scroll_dir(list, LV_DIR_VER);
        lv_obj_set_style_pad_bottom(list, 0, 0);
        int y = 0;
        for (auto* r : shown) {
            build_row(list, y, tw, *r, sel == r);
            y += 58;
        }
        build_detail(m, W_ - 12 - (W_ - 12 - 12 - lw - 12), top, W_ - 12 - 12 - lw - 12, ch, sel ? sel->alarm : nullptr);
    }

private:
    // 在点击回调里不能删自己：推迟到下一轮
    void refresh_later() {
        sig_.clear();
        update(app_->m());  // on_tap 的回调本来就是推迟执行的，这里可以直接重建
    }
    void build_row(lv_obj_t* list, int y, int w, const Row& r, bool selected) {
        bool active = r.alarm && r.alarm->dto.state == "active";
        uint32_t bg = 0xffffff, bar = 0;
        if (selected) { bg = 0xeaf2fb; bar = col::BLUE; }
        else if (active && r.level == "warn") { bg = 0xfdf8ef; bar = col::AMBER; }
        else if (active) { bg = 0xfdf3f2; bar = col::RED; }
        lv_obj_t* row = box(list, 0, y, w, 58, bg);
        box(row, 0, 57, w, 1, 0xedf1f5);
        if (bar) box(row, 0, 0, 4, 58, bar);
        if (r.alarm) {
            std::string id = r.alarm->dto.id;
            on_tap(row, [this, id] { sel_id_ = id; refresh_later(); });
        }
        label_at(row, 16, 20, model::fmt_clock(r.ts).c_str(), 14, active ? 600 : 400, active ? col::INK : col::SEC);
        if (r.level == "err") icon(row, &ic::alarm_tri22, 0, 110, 18);
        else if (r.level == "warn") icon(row, &ic::warn_circle22, 0, 110, 18);
        else icon(row, &ic::info22, 0x5b6672, 110, 18);
        std::optional<int> ch = r.alarm ? r.alarm->dto.channel : (r.event ? r.event->channel : std::nullopt);
        std::string src = r.alarm ? r.alarm->ext.source : "system";
        if (ch) {
            mini_vessel(row, 150, 20, 18, chan_color(*ch));
            label_at(row, 168, 19, trf("%d 号", *ch).c_str(), 14, 400, col::SEC);
        } else {
            icon(row, src == "comm" ? &ic::comm16 : &ic::system16, col::MUT, 150, 21);
            label_at(row, 172, 19, tr(src == "comm" ? "通讯" : "系统"), 14, 400, col::SEC);
        }
        const std::string& title = r.alarm ? r.alarm->dto.title : r.event->title;
        const std::string& msg = r.alarm ? r.alarm->dto.message : r.event->message;
        uint32_t tc = active ? (r.level == "warn" ? col::AMBER : col::RED) : col::INK;
        std::string ttl = kit::alarm_title_body(ch, title);  // 来源列已经写了「n 号」
        lv_obj_t* t = label_at(row, 242, msg.empty() ? 19 : 9, ttl.c_str(), 15, active ? 700 : 500, tc);
        lv_obj_set_width(t, 408);
        dot_line(t);
        if (!msg.empty()) {
            lv_obj_t* ms = label_at(row, 242, 32, msg.c_str(), 12.5, 400, col::MUT);
            lv_obj_set_width(ms, 408);
            dot_line(ms);
        }
        if (!r.alarm) return;
        const auto& d = r.alarm->dto;
        if (active) {
            lv_obj_t* b = box(row, w - 87, 11, 80, 36, 0, 0, true);
            lv_obj_t* c = box(b, 0, 0, 36, 36, col::BLUE, 18);
            lv_obj_set_style_outline_width(c, 4, 0);
            lv_obj_set_style_outline_color(c, lv_color_hex(col::BLUE), 0);
            lv_obj_set_style_outline_opa(c, 36, 0);
            lv_obj_center(icon(c, &ic::check20b, 0xffffff));
            label_at(b, 43, 8, tr("确认"), 14, 600, col::BLUE);
            App* app = app_;
            std::string id = d.id;
            on_tap(b, [app, id] { app->act().ack_alarm(id); });
        } else {
            bool cleared = d.state == "cleared";
            label_at(row, w - 74, 12, tr(cleared ? "已解除" : "已确认"), 13, 400, cleared ? col::ST_GRN : col::SEC);
            if (d.clearedAt) label_at(row, w - 74, 30, model::fmt_clock(*d.clearedAt).c_str(), 13, 400, col::MUT);
        }
    }

    void build_detail(const Model& m, int x, int y, int w, int h, const model::AlarmItem* a) {
        lv_obj_t* D = card(root_, x, y, w, h);
        if (!a) {
            lv_obj_t* l = label(D, tr("没有报警"), 15, 400, col::MUT);
            lv_obj_center(l);
            return;
        }
        const auto& d = a->dto;
        const auto& e = a->ext;
        bool err = d.level == "err";
        icon(D, err ? &ic::alarm_tri28 : &ic::warn_circle22, 0, 20, 18);
        std::string title = kit::alarm_title(d.channel, d.title);
        lv_obj_t* tl = label_at(D, 58, 17, title.c_str(), 20, 700, col::INK);
        lv_obj_set_width(tl, w - 78);
        dot_line(tl);
        std::string meta = md_hms(d.raisedAt) + (e.context.empty() ? std::string() : " · " + e.context);
        lv_obj_t* ml = label_at(D, 20, 50, meta.c_str(), 13.5, 400, col::MUT);
        lv_obj_set_width(ml, w - 40);
        int yy = 83;
        if (e.value) {
            bool pres = e.quantity == "pressure";
            char b[32];
            std::snprintf(b, sizeof b, pres ? "%.2f" : "%.1f", *e.value);
            lv_obj_t* v = label_at(D, 20, yy, b, 36, 600, col::RED);
            lv_obj_update_layout(v);
            label_at(D, 20 + lv_obj_get_width(v) + 6, yy + 22, pres ? "MPa" : "°C", 14, 400, col::MUT);
            if (e.limit) {
                std::snprintf(b, sizeof b, pres ? "%.2f" : "%.1f", *e.value - *e.limit);
                lv_obj_t* ov = label(D, (std::string(tr("超上限")) + " " + b).c_str(), 13, 400, col::MUT);
                lv_obj_update_layout(ov);
                lv_obj_set_pos(ov, w - 20 - lv_obj_get_width(ov), yy + 21);
            }
            if (e.rangeLo && e.rangeHi && e.limit) {
                int bx = 20, bw = w - 40, by = yy + 52;
                double lo = *e.rangeLo, hi = *e.rangeHi;
                auto fx = [&](double xv) { return bx + (int)std::lround(bw * (xv - lo) / (hi - lo)); };
                box(D, bx, by, bw, 10, 0xe6f4ec, 5);
                box(D, fx(*e.limit), by, bx + bw - fx(*e.limit), 10, 0xfdeceb, 5);
                int vx = fx(std::min(hi, std::max(lo, *e.value)));
                box(D, vx - 1, by - 4, 2, 18, col::RED);
                lv_obj_t* k = box(D, vx - 7, by - 2, 14, 14, col::RED, 7);
                lv_obj_set_style_outline_width(k, 3, 0);
                lv_obj_set_style_outline_color(k, lv_color_white(), 0);
                std::snprintf(b, sizeof b, "%.1f", lo);
                label_at(D, bx, by + 13, b, 11.5, 400, col::MUT);
                std::snprintf(b, sizeof b, pres ? " %.3f" : " %.1f", *e.limit);
                lv_obj_t* ll = label(D, (tr("上限") + std::string(b)).c_str(), 11.5, 400, col::RED);
                lv_obj_update_layout(ll);
                lv_obj_set_pos(ll, fx(*e.limit) - lv_obj_get_width(ll), by + 13);
                if (e.setpoint) {
                    std::snprintf(b, sizeof b, pres ? "%.3f " : "%.1f ", *e.setpoint);
                    lv_obj_t* s = label(D, (b + std::string(tr("设定"))).c_str(), 11.5, 400, col::MUT);
                    lv_obj_update_layout(s);
                    int sw = lv_obj_get_width(s), sx = std::clamp(fx(*e.setpoint) - sw / 2, bx, bx + bw - sw);
                    // 设定值离上限很近时两个字会叠在一起：设定挪到「上限」字的右边（再放不下才放到条上面）
                    int lx = lv_obj_get_x(ll), lw2 = lv_obj_get_width(ll);
                    bool hit = sx < lx + lw2 + 6 && sx + sw + 6 > lx;
                    if (hit && lx + lw2 + 10 + sw <= bx + bw) { sx = lx + lw2 + 10; hit = false; }
                    lv_obj_set_pos(s, sx, hit ? by - 19 : by + 13);
                }
                std::snprintf(b, sizeof b, "%.1f %s", hi, pres ? "MPa" : "°C");
                lv_obj_t* hl = label(D, b, 11.5, 400, col::MUT);
                lv_obj_update_layout(hl);
                lv_obj_set_pos(hl, bx + bw - lv_obj_get_width(hl), by + 13);
            }
            yy = 178;
        } else {
            lv_obj_t* msg = label_at(D, 20, yy, d.message.c_str(), 15, 400, col::INK);
            lv_obj_set_width(msg, w - 40);
            lv_label_set_long_mode(msg, LV_LABEL_LONG_WRAP);
            lv_obj_update_layout(msg);
            yy += lv_obj_get_height(msg) + 20;
        }
        if (!e.did.empty()) {
            label_at(D, 20, yy, tr("系统已经做了"), 13, 600, col::MUT);
            for (size_t i = 0; i < e.did.size(); ++i) {
                icon(D, &ic::check16, col::ST_GRN, 20, yy + 29 + (int)i * 27);
                label_at(D, 44, yy + 24 + (int)i * 27, e.did[i].c_str(), 14, 400, col::INK);
            }
            yy += 24 + (int)e.did.size() * 27 + 15;
        }
        if (!e.next.empty()) {
            label_at(D, 20, yy, tr("接下来"), 13, 600, col::MUT);
            for (size_t i = 0; i < e.next.size(); ++i) {
                lv_obj_t* n = box(D, 20, yy + 25 + (int)i * 25, 20, 20, 0xe8f0fb, 10);
                lv_obj_center(label(n, std::to_string(i + 1).c_str(), 12, 700, col::BLUE));
                lv_obj_t* t = label_at(D, 48, yy + 24 + (int)i * 25, e.next[i].c_str(), 14, 400, col::INK);
                lv_obj_set_width(t, w - 68);
            }
        }
        App* app = app_;
        std::string id = d.id;
        int bx2 = 227;
        if (d.state == "active") {
            std::string sub = d.channel && e.tier == 1 ? trf("%d 号恢复加热、进气", *d.channel) : std::string(tr("这条报警标为已处理"));
            lv_obj_t* ok = round_btn_side(D, 20, h - 80, 60, RoundStyle::Blue, &ic::check30b, tr("确认"), sub.c_str(), col::BLUE);
            on_tap(ok, [app, id] { app->act().ack_alarm(id); });
            lv_obj_update_layout(ok);
            bx2 = std::max(bx2, 20 + (int)lv_obj_get_width(ok) + 24);
        }
        lv_obj_t* cv = round_btn_side(D, bx2, h - 80, 60, RoundStyle::White, &ic::chart26, tr("看曲线"),
                                      tr(e.quantity == "pressure" ? "报警前后的压力" : "报警前后的读数"), col::INK);
        on_tap(cv, [app] { app->go(PageId::Curve, 3); });
        (void)m;
    }

    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr;
    int W_ = 1280, H_ = 740;
    int filter_ = 0;
    std::string sel_id_, sig_;
    std::vector<Row> rows_;
    TabCard tabs_;
};

std::unique_ptr<Page> make_alarm_page() { return std::make_unique<AlarmPage>(); }

// ------------------------------------------------------------ 第二批的页面先占位
class Placeholder : public Page {
public:
    Placeholder(const char* t, const char* w) : t_(t), w_(w) {}
    void build(lv_obj_t* root, App& app) override {
        lv_obj_t* c = card(root, 12, 12, app.w() - 24, app.h() - 60 - 24);
        lv_obj_t* l = label(c, tr(t_), 24, 700, col::INK);
        lv_obj_align(l, LV_ALIGN_CENTER, 0, -20);
        lv_obj_t* s = label(c, (std::string(tr(w_)) + " · " + tr("第二批")).c_str(), 15, 400, col::MUT);
        lv_obj_align(s, LV_ALIGN_CENTER, 0, 16);
    }
    void update(const Model&) override {}

private:
    const char *t_, *w_;
};

std::unique_ptr<Page> make_placeholder_page(const char* title, const char* what) { return std::make_unique<Placeholder>(title, what); }

}  // namespace reactor::hmi
