// 程序库（Lib-Mine / Lib-Tpl）：左边「我的程序 / 内置模板」两列卡片，右边选中那条的步骤、预计温度和操作。
// 数据来自 GET /library/programs、/library/templates（触屏和 PC 互通，带 revision）。
#include "common2.hpp"

#include <algorithm>
#include <cmath>
#include <array>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;

namespace {

std::string modified_text(int64_t ms, int64_t now) {
    if (!ms) return "";
    int d = days_between(ms, now);
    if (d == 0) return trf("修改于今天 %s", hm(ms).c_str());
    if (d == 1) return trf("修改于昨天 %s", hm(ms).c_str());
    return trf("修改于 %s", md(ms).c_str());
}

std::vector<int> used_by(const Model& m, const std::string& id) {
    std::vector<int> v;
    for (int i = 1; i <= 8; ++i)
        if (!id.empty() && m.x.programSrcId[(size_t)i - 1] == id && m.ch(i).installed) v.push_back(i);
    return v;
}

void load_to(App& app, int ch, const model::ProgramDoc& p) {
    model::ProgramDoc d = p;
    d.builtIn = false;
    if (d.revision == 0 || p.builtIn) d.id.clear();
    ProgEdit& e = prog_edit();
    e.doc[(size_t)ch - 1] = d;
    e.dirty[(size_t)ch - 1] = true;
    e.sel[(size_t)ch - 1] = 0;
    e.ch = ch;
    app.act().put_program(ch, d);
    app.go(PageId::Program, ch);
}

}  // namespace

void build_prog_lib(lv_obj_t* body, App& app, const Model& m, std::function<void()> refresh) {
    lv_obj_update_layout(body);
    ProgEdit& e = prog_edit();
    int W = app.w(), BH = lv_obj_get_height(body);
    bool tpl = e.lib_tpl;
    const auto& list = tpl ? m.x.templates : m.x.library;
    if (e.lib_sel.empty() || std::none_of(list.begin(), list.end(), [&](const model::LibEntry& x) { return x.prog.id == e.lib_sel; }))
        e.lib_sel = list.empty() ? "" : list.front().prog.id;
    // ---------- 左：列表 ----------
    int lw = 440;
    lv_obj_t* lc = card(body, 12, 6, lw, BH - 18);
    LampGroup* lg = new LampGroup;
    lv_obj_add_event_cb(lc, [](lv_event_t* ev) { delete static_cast<LampGroup*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, lg);
    lg->create(lc, 14, 12, {trf("我的程序 %d", (int)m.x.library.size()), trf("内置模板 %d", (int)m.x.templates.size())}, {110, 110},
               tpl ? 1 : 0, 44, 14.5, 8);
    lg->on_change = [refresh](int i) {
        prog_edit().lib_tpl = i == 1;
        prog_edit().lib_sel.clear();
        refresh();
    };
    lv_obj_t* grid = box(lc, 0, 66, lw, BH - 18 - 66 - 50, 0, 0, true);
    scroll_list(grid, tpl ? "lib-tpl" : "lib-mine");
    int cwid = 201, ch_ = 117, gx = 15, gy = 0;
    size_t nitems = list.size() + (tpl ? 0 : 1);
    for (size_t i = 0; i < nitems; ++i) {
        int x = gx + (int)(i % 2) * (cwid + 9), y = gy + (int)(i / 2) * (ch_ + 9);
        if (i == list.size()) {
            lv_obj_t* nb = box(grid, x, y, cwid, ch_, 0xffffff, 10);
            ring(nb, col::RING, 2);
            lv_obj_t* pl = icon(nb, &ic::plus26, col::BLUE);
            lv_obj_align(pl, LV_ALIGN_CENTER, 0, -14);
            lv_obj_t* tl = label(nb, tr("新建空白程序"), 15.5, 700, col::BLUE);
            lv_obj_align(tl, LV_ALIGN_CENTER, 0, 16);
            on_tap(nb, [&app] {
                TextKbd::open(tr("给新程序起个名字"), "", 40, [&app](const std::string& s) {
                    model::ProgramDoc d;
                    d.name = s.empty() ? tr("新程序") : s;
                    d.steps = {model::new_step(app.m().x.schema, "end")};
                    app.act().lib_save(false, d);
                    toast(trf("已新建「%s」", d.name.c_str()));
                });
            });
            continue;
        }
        const auto& le = list[i];
        bool on = le.prog.id == e.lib_sel;
        lv_obj_t* cb = box(grid, x, y, cwid, ch_, on ? 0xffffff : 0xf6f8fa, 10);
        ring(cb, on ? col::BLUE : 0xe1e7ee, on ? 2 : 1);
        lv_obj_t* nl = label_at(cb, 12, 10, le.prog.name.c_str(), 16, 700, on ? col::BLUE : col::INK);
        lv_obj_set_width(nl, cwid - 24);
        dot_line(nl);
        model::DurEst d = model::program_minutes(le.prog);
        label_at(cb, 12, 52, (std::string(tr("约 ")) + model::fmt_dur_min(d.minutes)).c_str(), 13.5, 400, col::SEC);
        label_at(cb, 12, 88, trf("%d 步", (int)le.prog.steps.size()).c_str(), 13.5, 400, col::SEC);
        auto ub = used_by(m, le.prog.id);
        for (size_t k = 0; k < ub.size(); ++k) chan_dot(cb, cwid - 12 - 20 - (int)(ub.size() - 1 - k) * 24, 86, ub[k]);
        std::string id = le.prog.id;
        on_tap(cb, [id, refresh] { prog_edit().lib_sel = id; refresh(); });
    }
    restore_scroll(grid, tpl ? "lib-tpl" : "lib-mine");
    box(lc, 0, BH - 18 - 50, lw, 1, col::DIV);
    if (tpl) label_at(lc, 16, BH - 18 - 34, tr("内置模板随软件提供，不能改，也不能删"), 13.5, 400, col::SEC);
    else {
        label_at(lc, 16, BH - 18 - 34, tr("按最近修改排序"), 13.5, 400, col::SEC);
        lv_obj_t* r = label(lc, trf("共 %d 个 · 上限 %d", (int)list.size(), m.x.libraryMax).c_str(), 13.5, 400, col::SEC);
        lv_obj_align(r, LV_ALIGN_BOTTOM_RIGHT, -16, -16);
    }
    // ---------- 右：详情 ----------
    int rx = 12 + lw + 12, rw = W - rx - 12;
    lv_obj_t* rc = card(body, rx, 6, rw, BH - 18);
    const model::LibEntry* sel = nullptr;
    for (auto& le : list) if (le.prog.id == e.lib_sel) sel = &le;
    if (!sel) {
        lv_obj_center(label(rc, tr("程序库是空的"), 16, 400, col::MUT));
        return;
    }
    const model::ProgramDoc& p = sel->prog;
    lv_obj_t* nm = label_at(rc, 16, 18, p.name.c_str(), 22, 700, col::INK);
    lv_obj_update_layout(nm);
    lv_obj_t* tg = tpl ? pill(rc, tr("内置模板"), col::AMBER, 0xfdf0dc, 13) : pill(rc, tr("我的程序"), col::BLUE, 0xe3ecf8, 13);
    lv_obj_set_pos(tg, 16 + lv_obj_get_width(nm) + 10, 22);
    if (tpl) {
        lv_obj_t* r = label(rc, tr("随软件提供 · 不能改"), 13.5, 400, col::SEC);
        lv_obj_align(r, LV_ALIGN_TOP_RIGHT, -16, 24);
    } else {
        auto ub = used_by(m, p.id);
        int x = rw - 16;
        if (!ub.empty()) {
            lv_obj_t* hao = label(rc, tr("号"), 13.5, 400, col::SEC);
            lv_obj_update_layout(hao);
            x -= lv_obj_get_width(hao);
            lv_obj_set_pos(hao, x, 24);
            for (size_t k = ub.size(); k-- > 0;) {
                x -= 24;
                chan_dot(rc, x, 23, ub[k]);
            }
            x -= 4;
        }
        std::string s = modified_text(p.modifiedMs, m.snap.ts) + (ub.empty() ? "" : std::string(tr(" · 正用在")));
        lv_obj_t* l = label(rc, s.c_str(), 13.5, 400, col::SEC);
        lv_obj_update_layout(l);
        lv_obj_set_pos(l, x - lv_obj_get_width(l) - 4, 24);
    }
    lv_obj_t* note = label_at(rc, 16, 58, p.note.c_str(), 14.5, 400, col::SEC);
    lv_obj_set_width(note, rw - 32);
    lv_label_set_long_mode(note, LV_LABEL_LONG_WRAP);
    lv_obj_update_layout(note);
    int y = 58 + lv_obj_get_height(note) + 16;
    label_at(rc, 16, y, trf("%d 个步骤", (int)p.steps.size()).c_str(), 13.5, 700, col::INK);
    y += 28;
    int n = (int)p.steps.size();
    int x0 = 42, x1 = rw - 42;
    double sp = n > 1 ? double(x1 - x0) / (n - 1) : 0;
    if (n > 1) box(rc, x0, y + 17, x1 - x0, 2, col::DIV);
    for (int k = 0; k < n; ++k) {
        int cx = x0 + (int)std::lround(k * sp);
        const auto& st = p.steps[(size_t)k];
        lv_obj_t* c = box(rc, cx - 17, y, 34, 34, 0xffffff, 17);
        ring(c, 0xc9d3de, 2);
        lv_obj_center(icon(c, step_icon(st.type, 16), col::SEC));
        lv_obj_t* l1 = label(rc, tr(model::zh_step_name(st.type).c_str()), 14.5, 700, col::INK);
        lv_obj_update_layout(l1);
        lv_obj_set_pos(l1, cx - lv_obj_get_width(l1) / 2, y + 41);
        std::string v = model::step_value_line(st);
        if (st.type == "exchange") v = (st.code("gas", "inert") == "inert" ? std::string(tr("惰性")) : model::step_value_line(st)) + " ×" +
                                       std::to_string((int)std::lround(st.num("cycles", 3)));
        lv_obj_t* l2 = label(rc, tr(v.c_str()), 12.5, 400, col::MUT);
        lv_obj_update_layout(l2);
        lv_obj_set_pos(l2, cx - lv_obj_get_width(l2) / 2, y + 62);
    }
    y += 92;
    model::DurEst d = model::program_minutes(p);
    std::string press;
    for (auto& st : p.steps)
        if (st.type == "press") press = trf(" · 充压 %s MPa 后%s", model::fmt_press_psi(st.num("pres", 290.1)).c_str(),
                                            tr(st.code("regulate", "on") == "on" ? "稳压" : "不稳压"));
    card_head(rc, 16, y, tr("预计温度"), ((std::string(tr("共约 ")) + model::fmt_dur_min(d.minutes)) + press).c_str(), 14);
    y += 26;
    int btnY = BH - 18 - 100;
    TimeChart* tc = new TimeChart;
    lv_obj_add_event_cb(rc, [](lv_event_t* ev) { delete static_cast<TimeChart*>(lv_event_get_user_data(ev)); }, LV_EVENT_DELETE, tc);
    tc->create(rc, 8, y, rw - 16, btnY - y - 10);
    model::PlanCurve pc = model::plan_curve(p);
    ChartConfig cfg;
    cfg.x0 = 0; cfg.x1 = std::max(60.0, pc.total);
    cfg.left = 52; cfg.bottom = 26; cfg.frame = false; cfg.xgrid = false; cfg.top = 12;
    double hstep = cfg.x1 > 600 ? 120 : 60;
    for (double t = 0; t <= cfg.x1; t += hstep) cfg.xticks.push_back(t);
    cfg.x_label = [](double x) { return std::to_string((int)std::lround(x / 60)) + " h"; };
    ChartPanel cp;
    double tmax = 25;
    for (auto& v : pc.temp) tmax = std::max(tmax, v.second);
    cp.ymin = 10; cp.ymax = tmax + 10; cp.yticks = {25, tmax}; cp.ysuffix = "°";
    ChartSeries ts;
    ts.gradient_by_value = true; ts.width = 3; ts.fill = true; ts.fill_opa = 22; ts.color = 0x9db8dc;
    for (auto& v : pc.temp) { ts.xs.push_back(v.first); ts.y.push_back((float)v.second); }
    cp.series = {ts};
    tc->set(cfg, {cp});
    // 按钮
    box(rc, 16, btnY - 2, rw - 32, 1, col::DIV);
    int ch = e.ch;
    bool can_load = m.ch(ch).installed && !m.ch(ch).programOwned;
    lv_obj_t* ld = box(rc, 16, btnY + 12, 154, 48, can_load ? col::BLUE : 0xb3c2d4, 9);
    icon(ld, &ic::download16, 0xffffff, 26, 16);
    lv_obj_align(label(ld, trf("载入到 %d 号", ch).c_str(), 16, 700, 0xffffff), LV_ALIGN_LEFT_MID, 50, 0);
    model::ProgramDoc pd = p;
    if (can_load) on_tap(ld, [&app, ch, pd] { load_to(app, ch, pd); });
    lv_obj_t* lm = tool_btn(rc, 180, btnY + 12, tr("载入到多个通道…"), nullptr, col::INK, 48, 15.5);
    on_tap(lm, [&app, pd] {
        auto pick = std::make_shared<std::array<bool, 8>>();
        DialogSpec s;
        s.w = 600; s.color = col::BLUE; s.icon = nullptr;
        s.title = tr("载入到多个通道"); s.subtitle = pd.name;
        s.secondary = tr("取消"); s.primary = tr("载入");
        const Model* mm = &app.m();
        s.body = [pick, mm](lv_obj_t* b, int) {
            label_at(b, 0, 0, tr("载入会替换这几路现在的程序（运行中的不能选）"), 14, 400, col::SEC);
            for (int i = 1; i <= 8; ++i) {
                bool dis = !mm->ch(i).installed || mm->ch(i).programOwned;
                lv_obj_t* chip = chan_chip(b, (i - 1) * 64, 34, i, false, dis, 56, 40);
                if (dis) continue;
                on_tap(chip, [pick, i, chip] {
                    bool on = !(*pick)[(size_t)i - 1];
                    (*pick)[(size_t)i - 1] = on;
                    lv_obj_set_style_bg_color(chip, lv_color_hex(on ? 0xffffff : col::TILE), 0);
                    ring(chip, on ? col::BLUE : col::RING, 2);
                });
            }
            return 80;
        };
        s.on_primary = [&app, pick, pd] {
            int n2 = 0, first = 0;
            for (int i = 1; i <= 8; ++i)
                if ((*pick)[(size_t)i - 1]) {
                    model::ProgramDoc d2 = pd;
                    if (pd.builtIn) d2.id.clear();
                    d2.builtIn = false;
                    app.act().put_program(i, d2);
                    prog_edit().doc[(size_t)i - 1] = d2;
                    prog_edit().dirty[(size_t)i - 1] = false;
                    if (!first) first = i;
                    ++n2;
                }
            if (n2) toast(trf("已载入到 %d 路", n2));
        };
        Dialog::open(s);
    });
    lv_obj_update_layout(lm);
    int bx = 180 + lv_obj_get_width(lm) + 14;
    box(rc, bx, btnY + 20, 1, 32, col::DIV);
    bx += 14;
    if (tpl) {
        lv_obj_t* sv = tool_btn(rc, bx, btnY + 12, tr("另存为我的程序"), &ic::save17, col::INK, 48, 15.5);
        on_tap(sv, [&app, pd] {
            model::ProgramDoc d2 = pd;
            d2.id.clear(); d2.builtIn = false; d2.revision = 0;
            app.act().lib_save(false, d2);
            toast(trf("「%s」已存为我的程序", d2.name.c_str()));
        });
        label_at(rc, 16, btnY + 70, trf("载入后改的是 %d 号自己的副本，模板本身不变", ch).c_str(), 13.5, 400, col::SEC);
        return;
    }
    lv_obj_t* cp2 = tool_btn(rc, bx, btnY + 12, tr("复制"), &ic::copy1717, col::INK, 48, 15.5);
    lv_obj_update_layout(cp2);
    bx += lv_obj_get_width(cp2) + 10;
    lv_obj_t* rn = tool_btn(rc, bx, btnY + 12, tr("改名"), &ic::pencil16, col::INK, 48, 15.5);
    lv_obj_update_layout(rn);
    bx += lv_obj_get_width(rn) + 10;
    lv_obj_t* dl = tool_btn(rc, bx, btnY + 12, tr("删除"), &ic::trash18, col::RED, 48, 15.5, 0xf3c3c3);
    on_tap(cp2, [&app, pd] {
        model::ProgramDoc d2 = pd;
        d2.id.clear(); d2.revision = 0;
        d2.name = pd.name + tr(" 副本");
        app.act().lib_save(false, d2);
        toast(trf("已复制为「%s」", d2.name.c_str()));
    });
    on_tap(rn, [&app, pd] {
        TextKbd::open(tr("改名"), pd.name, 40, [&app, pd](const std::string& s) {
            if (s.empty()) return;
            model::ProgramDoc d2 = pd;
            d2.name = s;
            app.act().lib_save(false, d2);
        });
    });
    on_tap(dl, [&app, pd] {
        confirm_stop(trf("删除「%s」？", pd.name.c_str()), "", "", tr("从程序库删掉这一条；已经载入到通道里的副本不受影响。删了不能恢复。"),
                     tr("不删"), tr("删除"), [&app, pd] { app.act().lib_delete(false, pd.id, pd.revision); });
    });
    // 「载入会替换 5 号现在的程序。3、4 号正在用它跑，改这里不影响它们」
    std::string foot = trf("载入会替换 %d 号现在的程序。", ch);
    auto ub = used_by(m, pd.id);
    std::vector<int> run;
    for (int c2 : ub) if (m.ch(c2).programOwned) run.push_back(c2);
    if (!run.empty()) {
        std::string s;
        for (size_t k = 0; k < run.size(); ++k) s += (k ? tr("、") : "") + std::to_string(run[k]);
        foot += trf("%s 号正在用它跑，改这里不影响它们", s.c_str());
    }
    if (!can_load) foot = trf("%d 号正在跑程序，跑完才能载入", ch);
    label_at(rc, 16, btnY + 70, foot.c_str(), 13.5, 400, col::SEC);
}

}  // namespace reactor::hmi
