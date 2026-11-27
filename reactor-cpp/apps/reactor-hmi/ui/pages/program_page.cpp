// 程序页：顶上三个页签（编辑程序 / 程序库 / 泄漏测试）+ 8 路通道条，下面按状态换：
//   编辑（Prog-1 / Prog-Params / Prog-AddStep / Prog-Curve8 / Prog-Curve16）
//   运行中（Prog-Run）、跑完（Prog-Done）、已中止（Prog-Aborted）
//   程序库（Lib-Mine / Lib-Tpl）、泄漏测试（Leak-*，没有通道条）
// 参数区完全按 /program-schema 画：字段、单位、范围、默认值、选项、visibleWhen 都来自后端。
#include "common2.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <ctime>
#include <map>
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;
using model::ProgramDoc;
using model::StepDoc;

// ================================================================ 共用小函数
std::string hm(int64_t ms) { return model::fmt_clock(ms, false); }
std::string md(int64_t ms) {
    std::time_t t = (std::time_t)(ms / 1000);
    std::tm tm{};
    localtime_r(&t, &tm);
    char b[16];
    std::strftime(b, sizeof b, "%m-%d", &tm);
    return b;
}
std::string md_hm(int64_t ms) { return md(ms) + " " + hm(ms); }
int days_between(int64_t a, int64_t b) {
    auto day = [](int64_t ms) {
        std::time_t t = (std::time_t)(ms / 1000);
        std::tm tm{};
        localtime_r(&t, &tm);
        tm.tm_hour = 12; tm.tm_min = 0; tm.tm_sec = 0;
        return (int64_t)std::mktime(&tm) / 86400;
    };
    return (int)(day(b) - day(a));
}
std::string day_word(int64_t ms, int64_t now) {
    int d = days_between(ms, now);
    if (d == 0) return std::string(tr("今天")) + " · " + md(ms);
    if (d == 1) return std::string(tr("昨天")) + " · " + md(ms);
    return md(ms);
}
std::string fmt_bytes(int64_t b) {
    char s[32];
    if (b >= 1073741824LL) std::snprintf(s, sizeof s, "%.1f GB", b / 1073741824.0);
    else if (b >= 1048576) std::snprintf(s, sizeof s, "%.1f MB", b / 1048576.0);
    else std::snprintf(s, sizeof s, "%lld KB", (long long)std::max<int64_t>(1, b / 1024));
    return s;
}
std::string chan_list(const std::vector<int>& chs) {
    if (chs.empty()) return "";
    bool run = chs.size() > 2;
    for (size_t i = 1; i < chs.size(); ++i) run = run && chs[i] == chs[i - 1] + 1;
    if (run) return trf("%s 号", (std::to_string(chs.front()) + "–" + std::to_string(chs.back())).c_str());
    std::string s;
    for (size_t i = 0; i < chs.size(); ++i) s += (i ? " " : "") + std::to_string(chs[i]);
    return trf("%s 号", s.c_str());
}

int card_head(lv_obj_t* p, int x, int y, const char* title, const char* sub, double px) {
    lv_obj_t* t = label_at(p, x, y, title, px, 700, col::INK);
    lv_obj_update_layout(t);
    int w = lv_obj_get_width(t);
    if (sub && *sub) {
        lv_obj_t* s = label_at(p, x + w + 10, y + (int)(px - 13.5) / 2 + 2, sub, 13.5, 400, col::MUT);
        lv_obj_update_layout(s);
        w += 10 + lv_obj_get_width(s);
    }
    return w;
}

void field_label(lv_obj_t* p, int x, int y, const std::string& text, const std::string& hint, bool faded) {
    std::vector<SpanDef> sp = {{text, 13, 400, faded ? col::DIS : col::SEC}};
    if (!hint.empty()) sp.push_back({"  " + hint, 11.5, 400, faded ? 0xc3cad3 : col::MUT2});
    lv_obj_t* r = rich(p, x, y, 300, sp);
    (void)r;
}

lv_obj_t* chan_dot(lv_obj_t* p, int x, int y, int ch, uint32_t bg, uint32_t fg, int d) {
    lv_obj_t* c = box(p, x, y, d, d, bg, d / 2);
    lv_obj_center(label(c, std::to_string(ch).c_str(), d * 0.62, 700, fg));
    return c;
}

// ================================================================ 编辑状态
ProgEdit& prog_edit() {
    static ProgEdit e;
    return e;
}

void reset_curve_state();
void reset_records_state();
void reset_settings_state();
void reset_page_state() {
    prog_edit() = ProgEdit{};
    reset_curve_state();
    reset_records_state();
    reset_settings_state();
}

void prog_sync(const Model& m) {
    ProgEdit& e = prog_edit();
    for (size_t i = 0; i < 8; ++i) {
        if (!e.init[i]) {
            e.doc[i] = m.x.programs[i];
            e.dirty[i] = m.progress[i].editedNotSent;
            e.init[i] = true;
            e.sel[i] = 0;
            for (size_t k = 0; k < e.doc[i].steps.size(); ++k)
                if (e.doc[i].steps[k].type == "heat") e.sel[i] = (int)k;
        } else if (!e.dirty[i]) {
            e.doc[i] = m.x.programs[i];
        }
        if (e.sel[i] >= (int)e.doc[i].steps.size()) e.sel[i] = std::max(0, (int)e.doc[i].steps.size() - 1);
    }
}

const model::RunRecord* last_result(const Model& m, int ch) {
    const auto& c = m.ch(ch);
    if (!c.program || c.program->running) return nullptr;
    if (c.program->state != "completed" && c.program->state != "aborted" && c.program->state != "failed") return nullptr;
    for (auto& r : m.x.runs) {
        if (std::find(r.channels.begin(), r.channels.end(), ch) == r.channels.end()) continue;
        if ((r.result == "done" || r.result == "abort") && r.end) {
            if (prog_edit().result_dismissed[ch - 1] == r.id) return nullptr;
            return &r;
        }
        if (r.result == "running") {
            // 同时下发的几路算一次运行：别的路还在跑时，这一路自己先结束 / 被中止了，
            // 运行记录整体还是「运行中」。给这一路拼一份只含自己的结果（中止时刻取 abort.at）。
            static std::array<model::RunRecord, 8> part;
            auto& p = part[(size_t)ch - 1];
            std::string pid = r.id + "#" + std::to_string(ch);
            if (prog_edit().result_dismissed[ch - 1] == pid) return nullptr;
            bool aborted = c.program->state != "completed";
            p = r;
            p.id = pid;
            p.result = aborted ? "abort" : "done";
            p.channels = {ch};
            p.perCh.erase(std::remove_if(p.perCh.begin(), p.perCh.end(), [ch](const model::RunChannelStat& s) { return s.ch != ch; }),
                          p.perCh.end());
            if (r.abort && r.abort->ch != ch) p.abort.reset();
            int64_t el = m.progress[(size_t)ch - 1].elapsedSec;
            p.end = p.abort ? p.abort->at : el > 0 ? r.start + el * 1000 : m.snap.ts;
            return &p;
        }
    }
    return nullptr;
}

std::string chan_state_text(const Model& m, int ch, uint32_t* color) {
    const auto& c = m.ch(ch);
    auto out = [&](const char* t, uint32_t cc) {
        if (color) *color = cc;
        return std::string(trc(t));
    };
    if (!c.installed) return out("停用 (状态)", col::DIS);
    if (c.program && c.program->running) return c.program->waitingGas ? out("等气路", col::AMBER) : out("运行中", col::ST_GRN);
    if (c.state == "stopped") return out("手动停止", col::SEC);   // 停止 / 全部停止 / 中止程序时正在有动作
    if (c.program && c.program->state == "completed" && last_result(m, ch)) return out("已完成", col::ST_GRN);
    if (c.program && c.program->state == "aborted" && last_result(m, ch)) return out("已中止", col::RED);
    if (prog_edit().dirty[(size_t)ch - 1]) return out("编辑中", col::BLUE);
    return out("空闲", col::MUT);
}

void prog_chan_strip(lv_obj_t* root, const Model& m, int x, int y, int w, int sel, std::function<void(int)> on_pick) {
    int gap = 8, cw = (w - 7 * gap) / 8;
    for (int ch = 1; ch <= 8; ++ch) {
        const auto& c = m.ch(ch);
        uint32_t sc = col::MUT;
        std::string st = chan_state_text(m, ch, &sc);
        bool on = ch == sel;
        lv_obj_t* b = box(root, x + (ch - 1) * (cw + gap), y, cw, 50, c.installed ? 0xffffff : 0xf3f5f8, 10);
        if (on) ring(b, col::BLUE, 2);
        else card_shadow(b);
        uint32_t liq = c.installed ? liquid_color(c.tempC ? *c.tempC : 25) : 0xe3e8ee;
        mini_vessel(b, 8, 12, 24, liq, !c.installed);
        uint32_t nc = !c.installed ? col::DIS : st == tr("编辑中") ? col::BLUE : st == tr("空闲") ? col::INK : sc;
        lv_obj_t* n = label_at(b, 37, 11, std::to_string(ch).c_str(), 21, 700, nc);
        (void)n;
        std::string name = prog_edit().doc[(size_t)ch - 1].steps.empty() ? std::string("—") : prog_edit().doc[(size_t)ch - 1].name;
        if (!c.installed) name = "—";
        lv_obj_t* nl = label_at(b, 56, 7, name.c_str(), 14, 600, c.installed ? col::INK : col::DIS);
        lv_obj_set_width(nl, cw - 62);
        dot_line(nl);
        label_at(b, 56, 27, st.c_str(), 12, 500, sc);
        on_tap(b, [on_pick, ch] { on_pick(ch); });
    }
}

// ================================================================ 程序页本体
class ProgramPage : public Page {
public:
    explicit ProgramPage(int arg) {
        sub_ = arg / 100;
        int ch = arg % 100;
        if (ch >= 1 && ch <= 8) prog_edit().ch = ch;
    }
    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        root_ = root;
        W_ = app.w();
        H_ = app.h() - kTopH;
        std::vector<TabDef> tabs = {{&ic::tab_flask18, "编辑程序", "", TabDef::None}, {&ic::tab_book18, "程序库", "", TabDef::None}, {&ic::tab_shield18, "泄漏测试", "", TabDef::None}};
        tabs_.create(root, 8, 4, W_ - 16, tabs, sub_);
        tabs_.on_select = [this](int i) {
            sub_ = i;
            sig_.clear();
            if (app_) update(app_->m());
        };
        hint_ = label(tabs_.obj(), "", 13.5, 400, col::MUT);
        body_ = box(root, 0, 0, W_, H_, 0, 0, true);
    }

    void update(const Model& m) override {
        prog_sync(m);
        ProgEdit& e = prog_edit();
        int ch = e.ch;
        const auto& c = m.ch(ch);
        bool running = c.program && c.program->running;
        const model::RunRecord* res = last_result(m, ch);
        // 什么时候需要重建：页签、通道、编辑版本、程序状态；运行中每秒一次
        char sig[256];
        std::snprintf(sig, sizeof sig, "%d/%d/%d/%d/%s/%d/%lld/%zu/%zu/%d/%d/%s/%d", sub_, ch, ver_, running,
                      c.program ? c.program->state.c_str() : "-", c.program ? c.program->stepIndex : -1,
                      (long long)((running || sub_ == 2) ? m.snap.ts / 1000 : 0), m.x.library.size(), m.x.templates.size(),
                      (int)m.snap.leakTest.running, (int)m.snap.leakTest.done, res ? res->id.c_str() : "", (int)c.installed);
        std::string s = sig;
        for (int i = 1; i <= 8; ++i) s += chan_state_text(m, i, nullptr)[0];
        if (s == sig_) return;
        sig_ = s;
        lv_obj_clean(body_);
        lv_label_set_text(hint_, sub_ == 2 ? "" : tr(sub_ == 0 && (running || res) ? "运行的是下发时的副本 · 现在改程序，下次下发才生效"
                                                                                   : "每一路独立编辑 · 运行中的程序不能改"));
        lv_obj_align(hint_, LV_ALIGN_RIGHT_MID, -40, 0);
        auto refresh = [this] {
            ++ver_;
            if (app_) update(app_->m());
        };
        if (sub_ == 2) {
            lv_obj_t* b = box(body_, 0, 52, W_, H_ - 52, 0, 0, true);
            build_leak(b, *app_, m, refresh);
            return;
        }
        prog_chan_strip(body_, m, 12, 54, W_ - 24, ch, [this](int k) {
            prog_edit().ch = k;
            sig_.clear();
            if (app_) update(app_->m());
        });
        lv_obj_t* b = box(body_, 0, 112, W_, H_ - 112, 0, 0, true);
        if (sub_ == 1) build_prog_lib(b, *app_, m, refresh);
        else if (running) build_prog_run(b, *app_, m, ch);
        else if (res) build_prog_result(b, *app_, m, ch, *res);
        else build_prog_editor(b, *app_, m, ch, refresh);
    }

private:
    App* app_ = nullptr;
    lv_obj_t* root_ = nullptr; lv_obj_t* body_ = nullptr; lv_obj_t* hint_ = nullptr;
    int W_ = 1280, H_ = 740, sub_ = 0, ver_ = 0;
    std::string sig_;
    TabCard tabs_;
};

std::unique_ptr<Page> make_program_page(int arg) { return std::make_unique<ProgramPage>(arg); }

// ================================================================ 编辑
namespace {

// 选项在灯按钮上的短名（schema 的 label 是完整说法）
std::string lamp_text(const std::string& type, const std::string& key, const model::SchemaOption& o) {
    if (o.label == "停止这一路") return tr("停这一路");
    if (o.label == "惰性气体") return tr("惰性");
    if (type == "heat" && key == "mode" && o.code == "curve") return tr("分段曲线");
    return tr(o.label.c_str());
}

std::string field_hint(const std::string& type, const model::FieldSchema& f) {
    char b[64];
    auto R = [&](int dec) {
        double lo = model::to_display(f, f.min.value_or(0)), hi = model::to_display(f, f.max.value_or(0));
        std::snprintf(b, sizeof b, "%s–%s", model::fmt_num(lo, dec).c_str(), model::fmt_num(hi, dec).c_str());
        return std::string(b);
    };
    std::string k = type + "." + f.key;
    if (k == "leak.pres" || k == "vent.to") return "≤ " + model::fmt_num(model::to_display(f, f.max.value_or(0)), 3);
    if (k == "leak.maxDrop") return tr("设置里的阈值");
    if (k == "heat.temp") return R(0);
    if (k == "heat.rate") return tr("0 = 尽快");
    if (k == "exchange.cycles") return R(0);
    if (k == "exchange.maxSec" || k == "vent.maxSec") return tr("超时报警");
    if (k == "stir.rpm") return R(0) + tr(" · 0 = 停");
    return "";
}

void open_add_step(App& app, int ch, std::function<void()> refresh) {
    ProgEdit& e = prog_edit();
    ProgramDoc& d = e.doc[(size_t)ch - 1];
    const Model& m = app.m();
    int n = (int)d.steps.size();
    bool has_end = n > 0 && d.steps.back().type == "end";
    int ins = std::min(e.sel[(size_t)ch - 1] + 1, has_end ? n - 1 : n);
    if (n == 0) ins = 0;
    lv_obj_t* dim = modal_dim();
    app.set_overlay(dim);
    int W = lv_obj_get_width(dim);
    lv_obj_t* p = box(dim, (W - 700) / 2, 36, 700, 476, 0xffffff, 20);
    pop_shadow(p, 60, 24, 90);
    lv_obj_add_flag(p, LV_OBJ_FLAG_CLICKABLE);
    std::string title = trf("第 %d 步做什么？", ins + 1);
    std::string sub;
    if (ins > 0) {
        char b[96];
        std::snprintf(b, sizeof b, "%02d %s", ins, tr(model::zh_step_name(d.steps[(size_t)ins - 1].type).c_str()));
        sub = trf("加在「%s」后面", b);
        if (has_end) sub += std::string(tr("、「结束」前面"));
    }
    card_head(p, 28, 26, title.c_str(), sub.c_str(), 22);
    lv_obj_t* cl = close_btn(p, 700 - 64, 18);
    on_tap(cl, [&app] { app.close_overlays(); });
    struct G { const char* name; std::vector<const char*> types; int x, y; };
    const G groups[] = {{"准备", {"leak", "exchange"}, 28, 76}, {"加热和搅拌", {"heat", "stir"}, 294, 76},
                        {"加气和反应", {"press", "hold", "react"}, 28, 268}, {"收尾", {"vent", "end"}, 412, 268}};
    static const std::map<std::string, const char*> desc = {
        {"leak", "保压看压降"}, {"exchange", "惰性气体 / 气体 A"}, {"heat", "到目标温度再平衡"}, {"stir", "转速"},
        {"press", "升到设定压力"}, {"hold", "等一段时间"}, {"react", "吸气停止就结束"}, {"vent", "泄到常压附近"},
        {"end", "程序已经有了"}};
    box(p, 28, 240, 644, 1, col::DIV);
    std::string cur = n ? d.steps[(size_t)e.sel[(size_t)ch - 1]].type : "";
    for (auto& g : groups) {
        label_at(p, g.x, g.y, tr(g.name), 14, 400, col::MUT);
        int x = g.x;
        for (const char* t : g.types) {
            bool disabled = std::string(t) == "end" && has_end;
            bool hi = cur == t && !disabled;
            lv_obj_t* it = box(p, x, g.y + 26, 118, 150, 0, 0, true);
            lv_obj_t* c = box(it, 21, 0, 76, 76, hi ? col::BLUE : disabled ? 0xf1f3f6 : 0xeaf1f9, 38);
            if (hi) {
                lv_obj_set_style_outline_width(c, 5, 0);
                lv_obj_set_style_outline_color(c, lv_color_hex(col::BLUE), 0);
                lv_obj_set_style_outline_opa(c, 40, 0);
            }
            lv_obj_center(icon(c, step_icon(t, 28), hi ? 0xffffff : disabled ? 0xc3cad3 : col::BLUE));
            lv_obj_t* nl = label(it, tr(model::zh_step_name(t).c_str()), 17, 700, disabled ? col::DIS : hi ? col::BLUE : col::INK);
            lv_obj_align(nl, LV_ALIGN_TOP_MID, 0, 86);
            lv_obj_t* dl = label(it, tr(desc.at(t)), 12.5, 400, disabled ? col::DIS : col::MUT);
            lv_obj_align(dl, LV_ALIGN_TOP_MID, 0, 112);
            if (!disabled) {
                std::string type = t;
                on_tap(it, [&app, ch, ins, type, refresh] {
                    ProgEdit& e2 = prog_edit();
                    ProgramDoc& doc = e2.doc[(size_t)ch - 1];
                    if (doc.steps.empty() && doc.name.empty()) doc.name = tr("新程序");
                    doc.steps.insert(doc.steps.begin() + ins, model::new_step(app.m().x.schema, type));
                    e2.sel[(size_t)ch - 1] = ins;
                    e2.dirty[(size_t)ch - 1] = true;
                    app.close_overlays();
                    refresh();
                });
            }
            x += 118;
        }
    }
    label_at(p, 28, 440, tr("没有「降温」步骤：程序结束后自然冷却。点一下就加上，参数在下面改。"), 13.5, 400, col::MUT);
    (void)m;
}

// 一行参数（标题 + 控件），返回用掉的宽度
int param_field(lv_obj_t* card, App& app, int ch, int k, const model::StepSchema& ss, const model::FieldSchema& f, int x, int y,
                std::function<void()> refresh) {
    ProgEdit& e = prog_edit();
    StepDoc& st = e.doc[(size_t)ch - 1].steps[(size_t)k];
    model::FieldShow show = model::field_show(ss, f, st);
    if (show == model::FieldShow::Hidden) return -1;
    bool faded = show == model::FieldShow::Faded;
    std::string lbl = tr(f.label.c_str());
    field_label(card, x, y, lbl, field_hint(ss.type, f), faded);
    if (f.is_sel() && f.is_onoff()) {
        Switch* sw = new Switch;
        sw->create(card, x, y + 21, st.code(f.key, f.defaultCode) == "on");
        lv_obj_add_event_cb(sw->obj(), [](lv_event_t* ev) { delete static_cast<Switch*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, sw);
        std::string key = f.key;
        sw->on_change = [ch, k, key, refresh](bool on) {
            ProgEdit& e2 = prog_edit();
            e2.doc[(size_t)ch - 1].steps[(size_t)k].set_code(key, on ? "on" : "off");
            e2.dirty[(size_t)ch - 1] = true;
            refresh();
        };
        return std::max(64, text_width(lbl.c_str(), font(13, 400)));
    }
    if (f.is_sel()) {
        std::vector<std::string> opts;
        int sel = 0;
        std::string cur = st.code(f.key, f.defaultCode.empty() && !f.options.empty() ? f.options[0].code : f.defaultCode);
        for (size_t i = 0; i < f.options.size(); ++i) {
            opts.push_back(lamp_text(ss.type, f.key, f.options[i]));
            if (f.options[i].code == cur) sel = (int)i;
        }
        LampGroup* lg = new LampGroup;
        lg->create(card, x, y + 16, opts, std::vector<int>(opts.size(), 69), sel, 44, 14, 6);
        lg->set_faded(faded);
        lv_obj_add_event_cb(card, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, lg);
        std::string key = f.key;
        std::vector<std::string> codes;
        for (auto& o : f.options) codes.push_back(o.code);
        lg->on_change = [ch, k, key, codes, refresh](int i) {
            ProgEdit& e2 = prog_edit();
            e2.doc[(size_t)ch - 1].steps[(size_t)k].set_code(key, codes[(size_t)i]);
            e2.dirty[(size_t)ch - 1] = true;
            refresh();
        };
        return lg->width();
    }
    // 数值
    int dec = model::field_decimals(f);
    double shown = model::to_display(f, st.num(f.key, f.def.value_or(0)));
    std::string unit = model::unit_label(f);
    // 整数就不带小数（「0」「2」），键盘仍按 dec 位
    std::string vtxt = std::fabs(shown - std::round(shown)) < 1e-9 && !f.is_pressure() ? model::fmt_num(shown, 0) : model::fmt_num(shown, dec);
    int w = std::max(92, text_width(vtxt.c_str(), font(19, 600)) + text_width(unit.c_str(), font(12, 400)) + 44);
    NumSpec sp;
    sp.title = trf("%d 号 · 第 %d 步 %s · %s", ch, k + 1, tr(model::zh_step_name(ss.type).c_str()), lbl.c_str());
    sp.unit = unit;
    sp.value = shown;
    sp.min = model::to_display(f, f.min.value_or(0));
    sp.max = model::to_display(f, f.max.value_or(9999));
    sp.decimals = dec;
    sp.last = shown;
    NumField* nf = new NumField;
    nf->create(card, x, y + 16, w, 44, sp);
    nf->set_faded(faded);
    nf->set_text(vtxt.c_str());
    lv_obj_add_event_cb(nf->obj(), [](lv_event_t* ev) { delete static_cast<NumField*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, nf);
    model::FieldSchema fc = f;
    nf->on_apply = [ch, k, fc, refresh](double v) {
        ProgEdit& e2 = prog_edit();
        e2.doc[(size_t)ch - 1].steps[(size_t)k].set_num(fc.key, model::from_display(fc, v));
        e2.dirty[(size_t)ch - 1] = true;
        refresh();
    };
    (void)app;
    return w;
}

// 升温曲线编辑（Prog-Curve8 / Prog-Curve16）：左边方式 / 到位范围 / 最长等待，右边曲线 + 每段三行
void curve_editor(lv_obj_t* card, App& app, int ch, int k, const model::StepSchema& ss, int cw, int chh,
                  std::function<void()> refresh) {
    ProgEdit& e = prog_edit();
    StepDoc& st = e.doc[(size_t)ch - 1].steps[(size_t)k];
    int y = 56;
    for (const char* key : {"mode", "band", "maxWait", "onTimeout"}) {
        const model::FieldSchema* f = ss.field(key);
        if (!f) continue;
        if (y + 70 > chh) break;
        param_field(card, app, ch, k, ss, *f, 16, y, refresh);
        y += 76;
    }
    auto& segs = st.segments;
    if (segs.empty()) segs.push_back({80, 1, 15});
    int n = (int)segs.size();
    int& sel = e.seg[(size_t)ch - 1];
    if (sel >= n) sel = n - 1;
    int X0 = 186, addw = n < 16 ? 52 : 0;
    int lw = 84;
    int avail = cw - X0 - 16 - addw - lw;
    int colw = std::min(113, avail / n);
    int tableH = 24 + 3 * 38;
    int ty = chh - 16 - tableH;
    // 曲线
    TimeChart* tc = new TimeChart;
    lv_obj_add_event_cb(card, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
    int chartY = 50, chartH = ty - chartY - 8;
    tc->create(card, X0 + lw - 44, chartY, colw * n + 44 + 8, chartH);
    ChartConfig cfg;
    cfg.left = 44; cfg.right = 8; cfg.top = 34; cfg.bottom = 4; cfg.frame = false; cfg.xgrid = false;
    // x：段号空间（每段占一列，和下面的表格对齐）：0 = 起点，i+1 = 第 i 段结束
    cfg.x0 = -0.5; cfg.x1 = n - 0.5;
    ChartPanel p;
    double tmax = 25;
    for (auto& s : segs) tmax = std::max(tmax, s.temp);
    p.ymin = 15; p.ymax = tmax + 15;
    p.yticks = {25, std::round(tmax * 0.55 / 10) * 10, tmax};
    p.ysuffix = "°";
    ChartSeries line;
    line.width = 3; line.gradient_by_value = true; line.fill = true; line.fill_opa = 18; line.color = 0x7fa7d9;
    line.xs.push_back(-0.5); line.y.push_back(25);
    for (int i = 0; i < n; ++i) {
        // 每段：前 60% 是升温斜坡，后 40% 保温平台（示意，表格才是准数）
        line.xs.push_back(i - 0.5 + 0.55); line.y.push_back((float)segs[(size_t)i].temp);
        line.xs.push_back(i + 0.5); line.y.push_back((float)segs[(size_t)i].temp);
    }
    p.series.push_back(line);
    for (int i = 0; i < n; ++i) {
        ChartSeries d;
        d.xs = {i - 0.5 + 0.55};
        d.y = {(float)segs[(size_t)i].temp};
        d.color = i == sel ? 0xffffff : 0xe9a23b;
        d.end_dot = true;
        p.series.push_back(d);
    }
    cfg.highlight = std::make_pair(sel - 0.5, sel + 0.5);
    tc->set(cfg, {p});
    // 选中那段的提示框
    {
        const auto& s = segs[(size_t)sel];
        char b[96];
        std::snprintf(b, sizeof b, "%s °C · %s °C/min · %s %s min", model::fmt_num(s.temp, 0).c_str(), model::fmt_num(s.rate, 1).c_str(),
                      tr("保温"), model::fmt_num(s.hold, 0).c_str());
        lv_obj_t* tip = pill(card, b, 0xffffff, 0x1d3557, 13.5, nullptr, false, 700);
        lv_obj_update_layout(tip);
        int px = X0 + lw - 44 + tc->x_to_px(sel - 0.5 + 0.55);
        lv_obj_set_pos(tip, std::max(X0, std::min(cw - 16 - (int)lv_obj_get_width(tip), px - (int)lv_obj_get_width(tip) / 2)), chartY + 2);
        // 选中点：白芯橙圈
        lv_area_t a = tc->panel_area(0);
        double frac = (s.temp - p.ymin) / (p.ymax - p.ymin);
        int py = chartY + a.y2 - (int)std::lround(frac * (a.y2 - a.y1));
        lv_obj_t* dot = box(card, px - 7, py - 7, 14, 14, 0xffffff, 7);
        ring(dot, 0xf08a24, 3);
    }
    // 表格
    label_at(card, X0, ty + 2, tr("段"), 13, 400, col::SEC);
    const char* rows[3] = {"目标温度", "升温速率", "到了保温"};
    const char* units[3] = {"°C", "°C/min", "min"};
    for (int r = 0; r < 3; ++r) {
        label_at(card, X0, ty + 24 + r * 38 + 2, tr(rows[r]), 13, 400, col::SEC);
        label_at(card, X0, ty + 24 + r * 38 + 18, units[r], 11.5, 400, col::MUT2);
    }
    int tx = X0 + lw;
    for (int i = 0; i < n; ++i) {
        bool on = i == sel;
        lv_obj_t* hd = box(card, tx + i * colw + colw / 2 - 11, ty, 22, 22, on ? col::BLUE : 0, 11, !on);
        lv_obj_center(label(hd, std::to_string(i + 1).c_str(), 13, on ? 700 : 500, on ? 0xffffff : col::SEC));
        on_tap(hd, [ch, i, refresh] { prog_edit().seg[(size_t)ch - 1] = i; refresh(); });
        for (int r = 0; r < 3; ++r) {
            const auto& s = segs[(size_t)i];
            double v = r == 0 ? s.temp : r == 1 ? s.rate : s.hold;
            int dec = r == 1 ? 1 : 0;
            lv_obj_t* cbx = box(card, tx + i * colw + 3, ty + 24 + r * 38, colw - 6, 32, on ? 0xffffff : col::TILE, 7);
            ring(cbx, on ? col::BLUE : col::RING, 2);
            lv_obj_center(label(cbx, model::fmt_num(v, dec).c_str(), colw < 60 ? 15 : 16.5, 600, col::DEEP));
            on_tap(cbx, [ch, k, i, r, refresh, &app, dec] {
                ProgEdit& e2 = prog_edit();
                e2.seg[(size_t)ch - 1] = i;
                auto& sg = e2.doc[(size_t)ch - 1].steps[(size_t)k].segments[(size_t)i];
                NumSpec sp;
                static const char* nm[3] = {"目标温度", "升温速率", "到了保温"};
                sp.title = trf("%d 号 · 曲线第 %d 段 · %s", ch, i + 1, tr(nm[r]));
                sp.unit = r == 0 ? "°C" : r == 1 ? "°C/min" : "min";
                sp.value = r == 0 ? sg.temp : r == 1 ? sg.rate : sg.hold;
                sp.min = 0;
                sp.max = r == 0 ? app.m().settings.limits.tempSpMaxC : r == 1 ? 50 : 600;
                sp.decimals = dec;
                sp.last = sp.value;
                NumPad::open(sp, [ch, k, i, r, refresh](double v) {
                    ProgEdit& e3 = prog_edit();
                    auto& g = e3.doc[(size_t)ch - 1].steps[(size_t)k].segments[(size_t)i];
                    (r == 0 ? g.temp : r == 1 ? g.rate : g.hold) = v;
                    e3.dirty[(size_t)ch - 1] = true;
                    refresh();
                });
                refresh();
            });
        }
    }
    if (n < 16) {
        lv_obj_t* add = box(card, tx + n * colw + 8, ty + 24, 42, 3 * 38 - 6, 0xffffff, 8);
        ring(add, col::RING, 2);
        icon(add, &ic::plus18, col::BLUE, 12, 12);
        lv_obj_t* al = label(add, tr("加\n一\n段"), 13.5, 600, col::BLUE);
        lv_obj_set_style_text_align(al, LV_TEXT_ALIGN_CENTER, 0);
        lv_obj_align(al, LV_ALIGN_TOP_MID, 0, 36);
        on_tap(add, [ch, k, refresh] {
            ProgEdit& e2 = prog_edit();
            auto& sg = e2.doc[(size_t)ch - 1].steps[(size_t)k].segments;
            model::HeatSeg s = sg.back();
            s.temp = std::min(200.0, s.temp + 10);
            sg.push_back(s);
            e2.seg[(size_t)ch - 1] = (int)sg.size() - 1;
            e2.dirty[(size_t)ch - 1] = true;
            refresh();
        });
    }
}

}  // namespace

void prog_open_add_step(App& app, int ch) {
    open_add_step(app, ch, [&app] { app.go(PageId::Program, prog_edit().ch); });
}

void build_prog_editor(lv_obj_t* body, App& app, const Model& m, int ch, std::function<void()> refresh) {
    lv_obj_update_layout(body);
    ProgEdit& e = prog_edit();
    ProgramDoc& d = e.doc[(size_t)ch - 1];
    const auto& c = m.ch(ch);
    int W = app.w(), BH = lv_obj_get_height(body);
    int cw = W - 24;
    // ---------- 程序卡 ----------
    lv_obj_t* pc = card(body, 12, 6, cw, 222);
    if (!c.installed) {
        lv_obj_t* l = label(pc, trf("%d 号已停用：在「设置 · 通道与气路」里启用后才能编辑、运行程序。", ch).c_str(), 16, 400, col::MUT);
        lv_obj_center(l);
        return;
    }
    if (d.steps.empty()) {
        // 这一路还没有程序（没保存过就是没有，不再自动塞模板）：给两条路——从程序库选，或者自己从第一步加
        label_at(pc, 24, 34, trf("%d 号还没有程序", ch).c_str(), 22, 700, col::INK);
        label_at(pc, 24, 72, tr("从程序库选一个，或者自己一步一步加。没有程序也可以在主页手动加热、进气、搅拌。"), 14.5, 400, col::MUT);
        lv_obj_t* lib = tool_btn(pc, 24, 128, tr("从程序库选…"), &ic::tab_book18, col::INK, 52, 16);
        on_tap(lib, [&app, ch] { app.go(PageId::Program, 100 + ch); });
        lv_obj_update_layout(lib);
        lv_obj_t* add = rect_btn(pc, 24 + lv_obj_get_width(lib) + 12, 128, 170, 52, tr("添加第一步"), true, col::BLUE, 16, 10, &ic::plus18);
        on_tap(add, [&app, ch, refresh] { open_add_step(app, ch, refresh); });
        return;
    }
    model::DurEst est = model::program_minutes(d);
    lv_obj_t* nm = label_at(pc, 16, 16, d.name.c_str(), 22, 700, col::BLUE);
    lv_obj_update_layout(nm);
    std::string sum = trf("%d 步 · 预计 %s", (int)d.steps.size(), model::fmt_dur_min(est.minutes).c_str());
    if (e.dirty[(size_t)ch - 1]) sum += std::string(" · ") + tr("已改动未保存");
    label_at(pc, 16 + lv_obj_get_width(nm) + 12, 23, sum.c_str(), 14, 400, col::MUT);
    on_tap(nm, [&app, ch, refresh] {
        TextKbd::open(tr("给这个程序起个名字"), prog_edit().doc[(size_t)ch - 1].name, 40, [ch, refresh](const std::string& s) {
            if (s.empty()) return;
            prog_edit().doc[(size_t)ch - 1].name = s;
            prog_edit().dirty[(size_t)ch - 1] = true;
            refresh();
        });
        (void)app;
    });
    int playW = 150;
    box(pc, cw - playW, 0, 1, 222, col::DIV);
    // 右上三个按钮（从右往左排）
    {
        int bx = cw - playW - 16;
        struct B { const char* t; const lv_image_dsc_t* i; int id; };
        const B bs[] = {{"用到其他通道…", &ic::copy1717, 2}, {"存到程序库", &ic::save17, 1}, {"从程序库选…", &ic::tab_book18, 0}};
        for (auto& b : bs) {
            lv_obj_t* btn = tool_btn(pc, 0, 10, tr(b.t), b.i);
            lv_obj_update_layout(btn);
            bx -= lv_obj_get_width(btn);
            lv_obj_set_x(btn, bx);
            bx -= 10;
            int id = b.id;
            on_tap(btn, [&app, ch, id, refresh] {
                ProgEdit& e2 = prog_edit();
                ProgramDoc doc = e2.doc[(size_t)ch - 1];
                if (id == 0) {
                    app.go(PageId::Program, 100 + ch);
                } else if (id == 1) {
                    TextKbd::open(tr("存到程序库 · 名字"), doc.name, 40, [&app, ch, refresh](const std::string& s) {
                        ProgEdit& e3 = prog_edit();
                        ProgramDoc dd = e3.doc[(size_t)ch - 1];
                        dd.name = s.empty() ? dd.name : s;
                        // 同名同 id 的条目就覆盖（带 revision），否则新建
                        dd.revision = 0;
                        bool found = false;
                        for (auto& le : app.m().x.library)
                            if (le.prog.id == dd.id && le.prog.name == dd.name) { dd.revision = le.prog.revision; found = true; }
                        if (!found) dd.id.clear();
                        app.act().lib_save(false, dd);
                        e3.doc[(size_t)ch - 1].name = dd.name;
                        e3.dirty[(size_t)ch - 1] = false;
                        toast(trf("「%s」已存到程序库", dd.name.c_str()));
                        refresh();
                    });
                } else {
                    // 用到其他通道：选几路，逐路 PUT（运行中的路不能选）
                    auto pick = std::make_shared<std::array<bool, 8>>();
                    DialogSpec s;
                    s.w = 600; s.color = col::BLUE; s.icon = nullptr;
                    s.title = tr("用到其他通道"); s.subtitle = doc.name;
                    s.secondary = tr("取消"); s.primary = tr("用到这几路");
                    const Model* mm = &app.m();
                    s.body = [pick, mm, ch](lv_obj_t* b, int w) {
                        label_at(b, 0, 0, tr("选要换成这个程序的通道（运行中的不能换）"), 14, 400, col::SEC);
                        for (int i = 1; i <= 8; ++i) {
                            bool dis = i == ch || !mm->ch(i).installed || mm->ch(i).programOwned;
                            lv_obj_t* chip = chan_chip(b, (i - 1) * 64, 34, i, (*pick)[(size_t)i - 1], dis, 56, 40);
                            if (dis) continue;
                            on_tap(chip, [pick, i, chip] {
                                (*pick)[(size_t)i - 1] = !(*pick)[(size_t)i - 1];
                                bool on = (*pick)[(size_t)i - 1];
                                lv_obj_set_style_bg_color(chip, lv_color_hex(on ? 0xffffff : col::TILE), 0);
                                ring(chip, on ? col::BLUE : col::RING, 2);
                            });
                        }
                        (void)w;
                        return 80;
                    };
                    s.on_primary = [&app, pick, doc, refresh] {
                        int n = 0;
                        for (int i = 1; i <= 8; ++i)
                            if ((*pick)[(size_t)i - 1]) {
                                app.act().put_program(i, doc);
                                prog_edit().doc[(size_t)i - 1] = doc;
                                prog_edit().dirty[(size_t)i - 1] = false;
                                ++n;
                            }
                        if (n) toast(trf("已用到 %d 路", n));
                        refresh();
                    };
                    Dialog::open(s);
                }
            });
        }
    }
    // 下发并运行
    {
        lv_obj_t* col = box(pc, cw - playW, 0, playW, 222, 0, 0, true);
        lv_obj_t* pb = box(col, (playW - 80) / 2, 46, 80, 80, col::BLUE, 40);
        lv_obj_set_style_outline_width(pb, 6, 0);
        lv_obj_set_style_outline_color(pb, lv_color_hex(col::BLUE), 0);
        lv_obj_set_style_outline_opa(pb, 40, 0);
        lv_obj_set_style_shadow_width(pb, 14, 0);
        lv_obj_set_style_shadow_offset_y(pb, 4, 0);
        lv_obj_set_style_shadow_color(pb, lv_color_hex(col::BLUE), 0);
        lv_obj_set_style_shadow_opa(pb, 90, 0);
        lv_obj_t* pi = icon(pb, &ic::play_fill30, 0xffffff);
        lv_obj_align(pi, LV_ALIGN_CENTER, 3, 0);
        lv_obj_t* t1 = label(col, tr("下发并运行"), 16, 700, col::BLUE);
        lv_obj_align(t1, LV_ALIGN_TOP_MID, 0, 140);
        lv_obj_t* t2 = label(col, trf("只运行 %d 号", ch).c_str(), 13, 400, col::MUT);
        lv_obj_align(t2, LV_ALIGN_TOP_MID, 0, 165);
        on_tap(col, [&app, ch, refresh] {
            ProgEdit& e2 = prog_edit();
            ProgramDoc doc = e2.doc[(size_t)ch - 1];
            DialogSpec s;
            s.w = 560; s.color = col::BLUE; s.icon = nullptr;
            s.title = trf("%d 号 · 下发并运行？", ch);
            s.subtitle = doc.name;
            s.secondary = tr("先不运行"); s.primary = tr("下发并运行");
            model::DurEst est2 = model::program_minutes(doc);
            std::string line = trf("%d 步 · 预计 %s。运行的是现在这份的副本，运行中改程序不影响它。", (int)doc.steps.size(),
                                   model::fmt_dur_min(est2.minutes).c_str());
            s.body = [line](lv_obj_t* b, int w) {
                lv_obj_t* l = label(b, line.c_str(), 15, 400, col::INK);
                lv_obj_set_width(l, w);
                lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
                return 48;
            };
            s.on_primary = [&app, ch, doc, refresh] {
                app.act().put_program(ch, doc);
                app.act().start_program(ch);
                prog_edit().dirty[(size_t)ch - 1] = false;
                refresh();
            };
            Dialog::open(s);
        });
    }
    // 步骤流
    int n = (int)d.steps.size();
    int sel = e.sel[(size_t)ch - 1];
    int x0 = 60, sp = std::min(103, (cw - playW - x0 - 30) / std::max(1, n));
    int cy = 106;  // 序号在圆圈上方，圆圈和连线整体下移，避免序号压住圆圈
    box(pc, x0, cy - 1, sp * n, 2, col::DIV);
    for (int k = 0; k <= n; ++k) {
        int cx = x0 + k * sp;
        lv_obj_t* it = box(pc, cx - sp / 2, 60, sp, 150, 0, 0, true);
        if (k == n) {
            lv_obj_t* c2 = box(it, sp / 2 - 22, cy - 60 - 22, 44, 44, 0xffffff, 22);
            ring(c2, col::BLUE, 2, 150);
            lv_obj_center(icon(c2, &ic::plus26, col::BLUE));
            lv_obj_t* l = label(it, tr("添加"), 16, 700, col::BLUE);
            lv_obj_align(l, LV_ALIGN_TOP_MID, 0, 82);
            on_tap(it, [&app, ch, refresh] { open_add_step(app, ch, refresh); });
            continue;
        }
        const StepDoc& st = d.steps[(size_t)k];
        bool on = k == sel;
        char num[8];
        std::snprintf(num, sizeof num, "%02d", k + 1);
        lv_obj_t* nl = label(it, num, 12, 500, on ? col::BLUE : col::MUT);
        lv_obj_align(nl, LV_ALIGN_TOP_MID, 0, 0);
        lv_obj_t* c2 = box(it, sp / 2 - 22, cy - 60 - 22, 44, 44, on ? col::BLUE : 0xffffff, 22);
        if (on) {
            lv_obj_set_style_outline_width(c2, 5, 0);
            lv_obj_set_style_outline_color(c2, lv_color_hex(col::BLUE), 0);
            lv_obj_set_style_outline_opa(c2, 40, 0);
        } else {
            ring(c2, 0xc9d3de, 2);
        }
        lv_obj_center(icon(c2, step_icon(st.type, 20), on ? 0xffffff : col::SEC));
        lv_obj_t* l1 = label(it, tr(model::zh_step_name(st.type).c_str()), 17, 700, on ? col::BLUE : col::INK);
        lv_obj_align(l1, LV_ALIGN_TOP_MID, 0, 80);
        lv_obj_t* l2 = label(it, tr(model::step_value_line(st).c_str()), 14.5, 700, col::INK);
        lv_obj_align(l2, LV_ALIGN_TOP_MID, 0, 103);
        lv_obj_t* l3 = label(it, tr(model::step_sub_line(st).c_str()), 12.5, 400, col::MUT);
        lv_obj_align(l3, LV_ALIGN_TOP_MID, 0, 125);
        on_tap(it, [ch, k, refresh] { prog_edit().sel[(size_t)ch - 1] = k; refresh(); });
    }
    if (n == 0) return;
    // ---------- 参数卡 ----------
    const StepDoc& st = d.steps[(size_t)sel];
    const model::StepSchema* ss = m.x.schema.step(st.type);
    bool curve = st.type == "heat" && st.code("mode", "fixed") == "curve";
    int py = 240, ph = curve ? BH - py - 12 : 133;
    lv_obj_t* pk = card(body, 12, py, cw, ph);
    lv_obj_t* bd = box(pk, 16, 14, 30, 30, col::BLUE, 15);
    lv_obj_center(icon(bd, step_icon(st.type, 16), 0xffffff));
    std::string tt = trf("第 %d 步 · %s", sel + 1, tr(model::zh_step_name(st.type).c_str()));
    std::string brief = tr(model::step_brief(st.type).c_str());
    if (curve && !st.segments.empty())
        brief = trf("%d 段 · 终温 %s °C · 共 %s", (int)st.segments.size(), model::fmt_num(st.segments.back().temp, 0).c_str(),
                    model::fmt_dur_min(model::curve_minutes(st.segments)).c_str());
    int hw = card_head(pk, 56, 18, tt.c_str(), brief.c_str(), 17);
    lv_obj_t* q = box(pk, 56 + hw + 12, 18, 24, 24, 0xe6ebf1, 12);
    lv_obj_center(label(q, "?", 13, 700, col::SEC));
    if (ss) {
        std::string help = tr(ss->help.c_str()), name =tr(model::zh_step_name(st.type).c_str());
        on_tap(q, [help, name] {
            DialogSpec s;
            s.w = 640; s.color = col::BLUE; s.icon = &ic::info22;
            s.title = name; s.subtitle = tr("这一步怎么做");
            s.primary = tr("知道了");
            s.body = [help](lv_obj_t* b, int w) {
                lv_obj_t* l = label(b, help.c_str(), 15, 400, col::INK);
                lv_obj_set_width(l, w);
                lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
                lv_obj_set_style_text_line_space(l, 6, 0);
                lv_obj_update_layout(l);
                return (int)lv_obj_get_height(l);
            };
            Dialog::open(s);
        });
    }
    bool is_end = st.type == "end";
    if (!is_end) {
        lv_obj_t* del = tool_btn(pk, 0, 14, tr("删除"), &ic::trash15, col::RED, 36, 15, 0xf3c3c3);
        lv_obj_update_layout(del);
        int bx = cw - 16 - lv_obj_get_width(del);
        lv_obj_set_x(del, bx);
        lv_obj_t* dn = tool_btn(pk, 0, 14, tr("后移"), &ic::chev_right18, col::INK, 36);
        lv_obj_update_layout(dn);
        bx -= 10 + lv_obj_get_width(dn);
        lv_obj_set_x(dn, bx);
        lv_obj_t* up = tool_btn(pk, 0, 14, tr("前移"), &ic::chev_left18, col::INK, 36);
        lv_obj_update_layout(up);
        bx -= 10 + lv_obj_get_width(up);
        lv_obj_set_x(up, bx);
        auto move = [ch, refresh](int dir) {
            ProgEdit& e2 = prog_edit();
            auto& v = e2.doc[(size_t)ch - 1].steps;
            int s0 = e2.sel[(size_t)ch - 1], s1 = s0 + dir;
            int last = (int)v.size() - (v.back().type == "end" ? 2 : 1);
            if (s1 < 0 || s1 > last) return;
            std::swap(v[(size_t)s0], v[(size_t)s1]);
            e2.sel[(size_t)ch - 1] = s1;
            e2.dirty[(size_t)ch - 1] = true;
            refresh();
        };
        on_tap(up, [move] { move(-1); });
        on_tap(dn, [move] { move(1); });
        on_tap(del, [ch, refresh] {
            ProgEdit& e2 = prog_edit();
            auto& v = e2.doc[(size_t)ch - 1].steps;
            int s0 = e2.sel[(size_t)ch - 1];
            v.erase(v.begin() + s0);
            e2.sel[(size_t)ch - 1] = std::max(0, s0 - 1);
            e2.dirty[(size_t)ch - 1] = true;
            refresh();
        });
    }
    if (is_end) {
        label_at(pk, 16, 78, tr("没有参数。这一步不能删、不能移动。结束时釜可能还是热的，要等凉了再放空，可以在放空前加「保持 · 温度 ≤ x」。"),
                 14, 400, col::SEC);
    } else if (ss && curve) {
        curve_editor(pk, app, ch, sel, *ss, cw, ph, refresh);
    } else if (ss) {
        int x = 16;
        for (auto& f : ss->fields) {
            if (f.kind == "array") continue;
            int w = param_field(pk, app, ch, sel, *ss, f, x, 62, refresh);
            if (w < 0) continue;
            x += w + 14;
        }
        if (st.type == "stir") {
            lv_obj_t* t = label_at(pk, x + 6, 86, tr("一台电机带全部釜：同时运行的\n几路转速应一致"), 12.5, 400, col::SEC);
            (void)t;
        }
    }
    if (curve) return;
    // ---------- 预计曲线 ----------
    int gy = py + ph + 12, gh = BH - gy - 12;
    lv_obj_t* gc = card(body, 12, gy, cw, gh);
    int lw = card_head(gc, 16, 12, tr("预计曲线"), "", 15);
    int lx = 16 + lw + 18;
    box(gc, lx, 22, 18, 3, 0xe8590c);
    lv_obj_t* l1 = label_at(gc, lx + 24, 13, tr("温度"), 13.5, 400, col::SEC);
    lv_obj_update_layout(l1);
    lx += 24 + lv_obj_get_width(l1) + 18;
    for (int i = 0; i < 3; ++i) box(gc, lx + i * 7, 22, 4, 3, col::BLUE);
    lv_obj_t* l2 = label_at(gc, lx + 26, 13, tr("压力"), 13.5, 400, col::SEC);
    lv_obj_update_layout(l2);
    lx += 26 + lv_obj_get_width(l2) + 14;
    label_at(gc, lx, 13, trf("按每一步的设定估算 · 蓝底是正在编辑的第 %d 步", sel + 1).c_str(), 13.5, 400, col::MUT);
    model::PlanCurve pcv = model::plan_curve(d);
    TimeChart* tc = new TimeChart;
    lv_obj_add_event_cb(gc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
    tc->create(gc, 8, 40, cw - 16, gh - 64);
    ChartConfig cfg;
    cfg.x0 = 0; cfg.x1 = std::max(1.0, pcv.total * 1.01);
    cfg.left = 44; cfg.bottom = 22; cfg.frame = false; cfg.xgrid = false;
    ChartPanel p;
    double tmax = 25, pmax = 0.1;
    for (auto& v : pcv.temp) tmax = std::max(tmax, v.second);
    for (auto& v : pcv.press) pmax = std::max(pmax, v.second);
    p.ymin = 0; p.ymax = tmax * 1.12; p.yticks = {25, tmax}; p.ysuffix = "°";
    ChartSeries ts;
    ts.color = 0xe8590c; ts.width = 2;
    for (auto& v : pcv.temp) { ts.xs.push_back(v.first); ts.y.push_back((float)v.second); }
    ChartSeries ps;  // 压力只看形状（没有自己的纵轴：放在同一块里按比例画，见报告）
    ps.color = col::BLUE; ps.width = 2; ps.dash = 5; ps.gap = 4;
    for (auto& v : pcv.press) { ps.xs.push_back(v.first); ps.y.push_back((float)(v.second / pmax * tmax * 1.05)); }
    p.series = {ts, ps};
    if (sel < (int)pcv.stepAt.size()) cfg.highlight = pcv.stepAt[(size_t)sel];
    // 横轴：第一步、选中步、反应步的名字
    std::vector<std::pair<double, std::string>> marks;
    for (size_t k = 0; k < pcv.stepAt.size() && k < d.steps.size(); ++k) {
        bool want = k == 0 || (int)k == sel || d.steps[k].type == "react";
        if (!want) continue;
        char b[48];
        std::snprintf(b, sizeof b, "%02d %s", (int)k + 1, tr(model::zh_step_name(d.steps[k].type).c_str()));
        marks.push_back({pcv.stepAt[k].first, b});
        cfg.xticks.push_back(pcv.stepAt[k].first + (cfg.x1 - cfg.x0) * 0.02);
    }
    // 刻度文字：离得最近的那个步骤名
    cfg.x_label = [marks](double x) {
        std::string best;
        double bd2 = 1e18;
        for (auto& mk : marks)
            if (std::fabs(mk.first - x) < bd2) { bd2 = std::fabs(mk.first - x); best = mk.second; }
        return best;
    };
    tc->set(cfg, {p});
    lv_obj_t* tl = label(gc, (std::string(est.approx ? tr("约 ") : tr("约 ")) + model::fmt_dur_min(est.minutes)).c_str(), 13, 400, col::MUT);
    lv_obj_align(tl, LV_ALIGN_BOTTOM_RIGHT, -16, -6);
}

}  // namespace reactor::hmi
