// 点釜快捷面板（Chan-Quick 手动 / Chan-QuickRun 跑程序）和语言弹窗（Lang-Pop）
#include "../app.hpp"
#include "../kit/i18n.hpp"
#include "../kit/text_gen.hpp"

#include <cmath>
#include <cstdio>

namespace reactor::hmi {

using namespace kit;
using model::Model;
using model::VState;

void home_select_card(Page* p, int ch);

namespace {

// 和主页卡片同一套算法（home.cpp build_overview）
lv_area_t card_rect(const App& app, int ch) {
    int colw = 126, W = app.w();
    int gw = W - 12 - 12 - colw - 12;
    int BH = app.h() - 60 - 56 - 12;
    int cw = (gw - 36) / 4, chh = (BH - 12) / 2;
    int i = ch - 1;
    lv_area_t a;
    a.x1 = 12 + (i % 4) * (cw + 12);
    a.y1 = 56 + (i / 4) * (chh + 12);
    a.x2 = a.x1 + cw - 1;
    a.y2 = a.y1 + chh - 1;
    return a;
}

lv_obj_t* tile(lv_obj_t* p, int x, int y, const lv_image_dsc_t* im, const char* text, lv_obj_t** lbl = nullptr) {
    lv_obj_t* t = box(p, x, y, 157, 76, col::TILE, 12);
    ring(t, col::RING, 2);
    lv_obj_set_style_bg_color(t, lv_color_hex(0xe4edf7), LV_STATE_PRESSED);
    lv_obj_t* i = icon(t, im, col::BLUE);
    lv_obj_align(i, LV_ALIGN_TOP_MID, 0, 13);
    lv_obj_t* l = label(t, text, 14, 400, col::INK);
    lv_obj_align(l, LV_ALIGN_TOP_MID, 0, 45);
    if (lbl) *lbl = l;
    return t;
}

// 气泡旁边的小三角（转 45° 的白方块，露一半）
void pointer(lv_obj_t* dim, int x, int y) {
    lv_obj_t* a = box(dim, x, y, 20, 20, 0xffffff);
    lv_obj_set_style_transform_rotation(a, 450, 0);
    lv_obj_set_style_transform_pivot_x(a, 10, 0);
    lv_obj_set_style_transform_pivot_y(a, 10, 0);
}

}  // namespace

lv_obj_t* open_chan_quick(App& app, int ch) {
    const Model& m = app.m();
    const auto& c = m.ch(ch);
    bool run = c.programOwned && c.program && c.program->running;
    lv_obj_t* dim = modal_dim(0);
    on_tap(dim, [&app] { app.close_overlays(); });
    home_select_card(app.current(), ch);
    lv_area_t cr = card_rect(app, ch);
    // 遮罩挖掉被点的那张卡片（画布上那张卡片不变暗）：四块半透明矩形围住它
    {
        int DW = lv_obj_get_width(dim), DH = lv_obj_get_height(dim);
        auto shade = [&](int x, int y, int w, int h) {
            if (w <= 0 || h <= 0) return;
            lv_obj_t* r = box(dim, x, y, w, h, 0x283241);
            lv_obj_set_style_bg_opa(r, 46, 0);
        };
        shade(0, 0, DW, cr.y1);
        shade(0, cr.y2 + 1, DW, DH - cr.y2 - 1);
        shade(0, cr.y1, cr.x1, cr.y2 - cr.y1 + 1);
        shade(cr.x2 + 1, cr.y1, DW - cr.x2 - 1, cr.y2 - cr.y1 + 1);
    }
    int pw = run ? 500 : 360, ph = run ? 309 : 291;
    bool right_side = (ch - 1) % 4 < 2;
    int px = right_side ? cr.x2 + 18 : cr.x1 - 18 - pw;
    int py = (ch - 1) / 4 == 0 ? cr.y1 + 30 : cr.y2 - ph - 39;
    // 选中的卡片外加蓝框（遮罩会盖住卡片，这里在遮罩上画一圈蓝边）
    lv_obj_t* sel = box(dim, cr.x1 - 1, cr.y1 - 1, cr.x2 - cr.x1 + 3, cr.y2 - cr.y1 + 3, 0, 13, true);
    ring(sel, col::BLUE, 3);
    int ay = (cr.y1 + cr.y2) / 2 - 10;
    if (ay < py + 20) ay = py + 20;
    if (ay > py + ph - 40) ay = py + ph - 40;
    pointer(dim, right_side ? px - 10 : px + pw - 10, ay);
    lv_obj_t* p = popover(dim, px, py, pw, ph);
    // 盖住三角的另一半
    box(dim, right_side ? px : px + pw - 12, ay - 2, 12, 24, 0xffffff);
    label_at(p, 18, 15, trf("%d 号", ch).c_str(), 18, 700, col::INK);
    if (!run) {
        char sub[64];
        std::snprintf(sub, sizeof sub, "%s · %s °C", tr(c.state == "heating" ? "加热中" : "手动"), model::fmt_temp(c).c_str());
        lv_obj_t* s = label_at(p, 61, 19, sub, 13, 400, col::MUT);
        lv_obj_t* heat_lbl = nullptr;
        bool heating = c.state == "heating";
        lv_obj_t* t1 = tile(p, 18, 55, heating ? &ic::play24 : &ic::play24, tr(heating ? "停止加热" : "启动加热"), &heat_lbl);
        on_tap(t1, [&app, ch] {
            if (app.m().ch(ch).state == "heating") {
                confirm_stop(tr("停止加热？"), trf("%d 号", ch), "", tr("这一路停止加热，阀门和搅拌不变。"), tr("不停"), tr("停止加热"),
                             [&app, ch] { app.act().heat(ch, false); });
            } else {
                app.act().heat(ch, true);
            }
            app.close_overlays();
        });
        char sp[48];
        if (std::isnan(model::temp_sp(c))) std::snprintf(sp, sizeof sp, "%s — °C", tr("目标"));  // 还没设过
        else std::snprintf(sp, sizeof sp, "%s %.0f °C", tr("目标"), c.tempSpC);
        lv_obj_t* t2 = tile(p, 185, 55, &ic::thermo24, sp);
        on_tap(t2, [&app, ch] {
            const auto& cc = app.m().ch(ch);
            NumSpec s;
            s.title = trf("%d 号 · 目标温度", ch);
            s.unit = "°C";
            s.value = model::temp_sp(cc);
            s.min = 35; s.max = app.m().settings.limits.tempSpMaxC;
            if (!std::isnan(s.value)) s.last = cc.tempSpC;
            s.current = model::fmt_temp(cc) + " °C";
            NumPad::open(s, [&app, ch](double v) { app.act().set_temp_sp(ch, v); });
        });
        std::string vt = std::string(tr("进气阀")) + " · " + tr(c.valveOpen ? "开" : "关");
        lv_obj_t* t3 = tile(p, 18, 141, &ic::valve24, vt.c_str());
        on_tap(t3, [&app, ch] {
            const Model& mm = app.m();
            if (!mm.snap.gas.manualValve) {
                DialogSpec d;
                d.color = col::BLUE;
                d.icon = nullptr;
                d.title = tr("要先打开手动阀控");
                d.primary = tr("知道了");
                d.body = [](lv_obj_t* b, int w) {
                    lv_obj_t* l = label_at(b, 0, 0, tr("进气阀要先在主页右边打开「手动阀控」才能开。"), 15, 400, col::INK);
                    lv_obj_set_width(l, w);
                    return 24;
                };
                Dialog::open(d);
                return;
            }
            app.act().set_valve(ch, !mm.ch(ch).valveOpen);
        });
        lv_obj_t* t4 = tile(p, 185, 141, &ic::refresh24, tr("吸气量清零"));
        on_tap(t4, [&app, ch] { app.act().gas_zero(ch); });
        lv_obj_t* full = box(p, 18, 229, 324, 46, 0xffffff, 10);
        ring(full, 0x8fb1e3, 2);
        lv_obj_center(label(full, trf("全部设定 · 进入 %d 号 ›", ch).c_str(), 15, 400, col::BLUE));
        on_tap(full, [&app, ch] { app.go(PageId::ChanFull, ch); });
        (void)s;
        return dim;
    }
    // —— 跑程序的一路：只给看，能去程序页 / 曲线 / 设定(只看) / 中止 ——
    const auto& pg = m.progress[static_cast<size_t>(ch - 1)];
    label_at(p, 60, 19, pg.programName.c_str(), 14, 600, col::BLUE);
    lv_obj_t* chip = box(p, pw - 18 - 87, 14, 87, 26, 0xfff3e0, 13);
    icon(chip, &ic::lock13, col::AMBER, 10, 7);
    label_at(chip, 28, 4, tr("程序控制"), 12.5, 700, col::AMBER);
    Vessel* v = new Vessel;
    v->create(p, 18, 52, Vessel::M);
    lv_obj_add_event_cb(v->obj(), [](lv_event_t* e) { delete static_cast<Vessel*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, v);
    label_at(p, 156, 71, tr("温度"), 12.5, 400, col::MUT);
    lv_obj_t* tv = label_at(p, 156, 85, "", 28, 600, col::BLUE);
    lv_obj_t* tsp = label(p, "", 13, 400, col::MUT);
    label_at(p, 323, 71, tr("压力"), 12.5, 400, col::MUT);
    lv_obj_t* pv = label_at(p, 323, 85, "", 28, 600, col::INK);
    lv_obj_t* pu = label(p, "MPa", 13, 400, col::MUT);
    std::vector<lv_obj_t*> segs, fills;
    for (int k = 0; k < 9; ++k) {
        lv_obj_t* s = box(p, 156 + (int)std::lround(k * 36.6), 131, 34, 8, 0xe3e8ee, 3);
        lv_obj_set_style_clip_corner(s, true, 0);
        fills.push_back(box(s, 0, 0, 0, 8, 0xf08c00));
        segs.push_back(s);
    }
    lv_obj_t* stp = label_at(p, 156, 149, "", 13, 700, col::INK);
    lv_obj_t* stn = label(p, "", 13, 400, col::SEC);
    lv_obj_t* rem = label(p, "", 13, 400, col::SEC);
    box(p, 18, 200, pw - 36, 1, 0xedf1f5);
    lv_obj_t* b1 = round_btn(p, 76, 215, 52, RoundStyle::White, &ic::list22, tr("程序页"), col::INK);
    on_tap(b1, [&app] { app.go(PageId::Program); });
    lv_obj_t* b2 = round_btn(p, 192, 215, 52, RoundStyle::White, &ic::chart22, tr("曲线"), col::INK);
    on_tap(b2, [&app] { app.go(PageId::Curve); });
    lv_obj_t* b3 = round_btn(p, 308, 215, 52, RoundStyle::White, &ic::gear22, tr("设定(只看)"), col::INK);
    on_tap(b3, [&app, ch] { app.go(PageId::ChanFull, ch); });
    lv_obj_t* b4 = round_btn(p, 424, 215, 52, RoundStyle::Stop, nullptr, tr("中止…"), col::RED);
    on_tap(b4, [&app, ch] { app.close_overlays(); app.abort_confirm(ch); });
    auto upd = [ch, v, tv, tsp, pv, pu, segs, fills, stp, stn, rem, pw](const Model& mm) {
        const auto& cc = mm.ch(ch);
        const auto& pp = mm.progress[static_cast<size_t>(ch - 1)];
        VState st = model::vstate(mm, ch);
        v->set(st, ch, cc.tempC, st == VState::Holding ? pp.stepFraction : 0);
        lv_label_set_text(tv, model::fmt_temp(cc).c_str());
        char b[32];
        std::snprintf(b, sizeof b, "→ %.0f °C", cc.tempSpC);
        lv_label_set_text(tsp, b);
        lv_obj_update_layout(tv);
        lv_obj_align_to(tsp, tv, LV_ALIGN_OUT_RIGHT_BOTTOM, 4, -4);
        lv_label_set_text(pv, model::fmt_mpa(cc.pressurePsi).c_str());
        lv_obj_update_layout(pv);
        lv_obj_align_to(pu, pv, LV_ALIGN_OUT_RIGHT_BOTTOM, 4, -4);
        int cur = cc.program ? cc.program->stepIndex : 0;
        for (int k = 0; k < 9; ++k) {
            lv_obj_set_style_bg_color(segs[static_cast<size_t>(k)], lv_color_hex(k < cur ? col::GREEN : 0xe3e8ee), 0);
            lv_obj_set_width(fills[static_cast<size_t>(k)], k == cur ? (int)std::lround(34 * std::max(0.1, pp.stepFraction)) : 0);
        }
        std::string sn = cur < (int)pp.stepNames.size() ? tr(pp.stepNames[static_cast<size_t>(cur)].c_str()) : "";
        lv_label_set_text(stp, trf("第 %d/%d 步", cur + 1, cc.program ? cc.program->stepCount : 9).c_str());
        lv_label_set_text(stn, sn.c_str());
        lv_obj_update_layout(stp);
        lv_obj_align_to(stn, stp, LV_ALIGN_OUT_RIGHT_MID, 5, 0);
        lv_label_set_text(rem, pp.remainingSec ? trf("约剩 %s", model::fmt_hmin(*pp.remainingSec).c_str()).c_str() : "");
        lv_obj_update_layout(rem);
        lv_obj_set_pos(rem, pw - 18 - lv_obj_get_width(rem), 149);
    };
    upd(m);
    app.overlay_update = upd;
    return dim;
}

// ------------------------------------------------------------ 语言弹窗
lv_obj_t* open_lang_popup(App& app) {
    lv_obj_t* dim = modal_dim();
    int W = lv_obj_get_width(dim), H = lv_obj_get_height(dim);
    lv_obj_t* p = box(dim, (W - 600) / 2, (H - 608) / 2, 600, 608, 0xffffff, 14);
    lv_obj_set_style_clip_corner(p, true, 0);
    pop_shadow(p, 60, 24, 90);
    lv_obj_add_flag(p, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_t* hd = box(p, 0, 0, 600, 62, col::BLUE);
    icon(hd, &ic::globe, 0xffffff, 24, 19);
    label_at(hd, 60, 17, "语言", 20, 700, 0xffffff);
    label_at(hd, 120, 19, "Language", 20, 700, 0xffffff);
    struct L { Lang l; const char* native; const char* en; const lv_image_dsc_t* flag; };
    static const L langs[8] = {{Lang::Zh, "简体中文", "Chinese", &ic::flag_zh}, {Lang::En, "English", "English", &ic::flag_en},
                               {Lang::Ja, "日本語", "Japanese", &ic::flag_ja},  {Lang::Ko, "한국어", "Korean", &ic::flag_ko},
                               {Lang::De, "Deutsch", "German", &ic::flag_de},  {Lang::Fr, "Français", "French", &ic::flag_fr},
                               {Lang::Es, "Español", "Spanish", &ic::flag_es}, {Lang::Ru, "Русский", "Russian", &ic::flag_ru}};
    for (int i = 0; i < 8; ++i) {
        const L& l = langs[i];
        bool on = lang() == l.l;
        lv_obj_t* r = box(p, 24, 76 + i * 56, 552, 54, on ? 0xeef4fb : 0xffffff, 10);
        lv_obj_t* fl = lv_image_create(r);
        lv_obj_remove_style_all(fl);
        lv_image_set_src(fl, l.flag);
        lv_obj_set_pos(fl, 14, 11);
        // 「한국어」：随包字体没有韩文字形，用预渲染的图（assets/text2a8.py）
        lv_obj_t* n = l.l == Lang::Ko ? icon(r, &ic::txt_korean18, on ? col::BLUE : col::INK, 76, 13)
                                      : label_at(r, 76, 14, l.native, 18, 600, on ? col::BLUE : col::INK);
        lv_obj_update_layout(n);
        label_at(r, 76 + lv_obj_get_width(n) + 14, 18, l.en, 13.5, 400, col::MUT);
        bool full = lang_complete(l.l);
        lv_obj_t* chip = label_at(r, 459, 15, tr(full ? "完整" : "未齐"), 12.5, 700, full ? col::ST_GRN : col::AMBER);
        lv_obj_set_style_bg_color(chip, lv_color_hex(full ? 0xe6f4ec : 0xfdf1e0), 0);
        lv_obj_set_style_bg_opa(chip, LV_OPA_COVER, 0);
        lv_obj_set_style_radius(chip, 6, 0);
        lv_obj_set_style_pad_hor(chip, 6, 0);
        lv_obj_set_style_pad_ver(chip, 3, 0);
        lv_obj_t* rd = box(r, 514, 15, 24, 24, 0xffffff, 12);
        ring(rd, on ? col::BLUE : 0xb8c4d1, 2);
        if (on) lv_obj_center(box(rd, 0, 0, 14, 14, col::BLUE, 7));
        Lang ll = l.l;
        on_tap(r, [&app, ll] {
            if (ll == lang()) return;
            set_lang(ll);
            app.act().set_language(lang_code(ll));
            app.rebuild();
            app.open_lang();
        });
    }
    lv_obj_t* note = label_at(p, 24, 543, tr("点一下马上就换，不合适再点回来。标「未齐」的，没译到的条目显示英文"), 12.5, 400, col::MUT);
    lv_obj_set_width(note, 386);
    lv_label_set_long_mode(note, LV_LABEL_LONG_WRAP);
    lv_obj_t* ok = rect_btn(p, 426, 536, 150, 52, tr("完成"), true, col::BLUE, 17);
    on_tap(ok, [&app] { app.close_overlays(); });
    return dim;
}

}  // namespace reactor::hmi
