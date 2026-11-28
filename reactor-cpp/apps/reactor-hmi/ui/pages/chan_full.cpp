// 全部设定（Chan-Full 定值 / Chan-FullCurve 曲线 / Chan-FullRun 跑程序只读）
#include "../app.hpp"
#include "../kit/draw.hpp"
#include "../kit/i18n.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <memory>
#include <string>
#include <utility>
#include <vector>

namespace reactor::hmi {

using namespace kit;
using model::Model;
using model::VState;

namespace {

const App* g_app = nullptr;
const model::ChanSetExt& local_of(int ch) { return g_app->m().chanset[static_cast<size_t>(ch - 1)]; }

std::string f1(const char* fmt, double v) {
    char b[48];
    std::snprintf(b, sizeof b, fmt, v);
    return b;
}

// 曲线小图（Chan-FullCurve 里 832×96 那张）
struct CurveCtx {
    std::vector<model::CurveSeg> segs;
    double start = 25;
    int sel = 3;
};
void curve_draw(lv_obj_t*, lv_layer_t* L, const lv_area_t& a, void* u) {
    auto* c = static_cast<CurveCtx*>(u);
    int W = lv_area_get_width(&a), H = lv_area_get_height(&a);
    int n = (int)c->segs.size();
    if (!n) return;
    // 列和下面的表格对齐：表格第 i 列在卡片 x = 104 + 117·i（宽 111），图在卡片 x = 20
    float x0 = 84, colw = 117;
    float x1 = std::min((float)W - 6, x0 + colw * n);
    (void)x1;
    float ytop = 30, ybot = (float)H - 10;
    double tmax = c->start;
    for (auto& s : c->segs) tmax = std::max(tmax, s.tempC);
    double lo = c->start, hi = std::max(tmax, lo + 10);
    auto Y = [&](double t) { return (float)(ybot - (ybot - ytop) * (t - lo) / (hi - lo)); };
    // 选中段底色
    lv_draw_rect_dsc_t r;
    lv_draw_rect_dsc_init(&r);
    r.bg_color = lv_color_hex(0xeef4fb);
    r.radius = 6;
    lv_area_t ra{(int32_t)(a.x1 + x0 + colw * c->sel + 1), a.y1 + 2, (int32_t)(a.x1 + x0 + colw * (c->sel + 1) - 1), a.y2 - 2};
    lv_draw_rect(L, &r, &ra);
    // 参考线
    float xe = x0 + colw * n;
    draw_hline(L, a, x0, xe, Y(c->start), 0xe3e9f0, 1, 3, 5);
    draw_text(L, a, 68, (int)Y(c->start) - 8, f1("%.0f°", c->start).c_str(), font(11.5), 0x8a94a3, LV_TEXT_ALIGN_RIGHT, 40);
    double ref = c->segs[static_cast<size_t>(std::min(c->sel, n - 1))].tempC;
    draw_hline(L, a, x0, xe, Y(ref), 0xe3e9f0, 1, 3, 5);
    draw_text(L, a, 68, (int)Y(ref) - 8, f1("%.0f°", ref).c_str(), font(11.5), 0x8a94a3, LV_TEXT_ALIGN_RIGHT, 40);
    // 每段：从上一段温度线性升到目标（前 60% 升，后 40% 平）
    std::vector<Pt> pts;
    double t = c->start;
    pts.push_back({x0, Y(t)});
    for (int i = 0; i < n; ++i) {
        float sx = x0 + colw * i;
        pts.push_back({sx + colw * 0.6f, Y(c->segs[static_cast<size_t>(i)].tempC)});
        pts.push_back({sx + colw, Y(c->segs[static_cast<size_t>(i)].tempC)});
        t = c->segs[static_cast<size_t>(i)].tempC;
    }
    draw_fill_under(L, a, pts, ybot + 4, col::BLUE, 18);
    for (size_t i = 1; i < pts.size(); ++i) {
        double tt = lo + (hi - lo) * (ybot - pts[i].y) / (ybot - ytop);
        draw_polyline(L, a, {pts[i - 1], pts[i]}, liquid_color(tt), 4);
    }
    for (int i = 0; i < n; ++i) {
        const Pt& p = pts[static_cast<size_t>(1 + i * 2)];
        if (i == c->sel) continue;
        draw_circle(L, a, p.x, p.y, 3.5f, liquid_color(c->segs[static_cast<size_t>(i)].tempC), 0xffffff, 2);
    }
    const Pt& sp = pts[static_cast<size_t>(1 + c->sel * 2)];
    uint32_t sc = liquid_color(c->segs[static_cast<size_t>(c->sel)].tempC);
    draw_circle(L, a, sp.x, sp.y, 10, sc, sc, 0, 51);
    draw_circle(L, a, sp.x, sp.y, 6, 0xffffff, sc, 3);
    // 提示框
    std::string tip = f1("%.0f °C · ", c->segs[static_cast<size_t>(c->sel)].tempC) + trf("用时 %.0f min", c->segs[static_cast<size_t>(c->sel)].minutes);
    const lv_font_t* f = font(12.5, 600);
    int tw = text_width(tip.c_str(), f) + 28;
    lv_draw_rect_dsc_t tb;
    lv_draw_rect_dsc_init(&tb);
    tb.bg_color = lv_color_hex(col::DEEP);
    tb.radius = 12;
    // 点靠近图顶（最高温那段）时提示框放到点下面，不然被图的上沿裁掉；左右也收进图里
    bool below = sp.y < 44;
    int cx = std::clamp((int)sp.x, tw / 2 + 2, (int)lv_area_get_width(&a) - tw / 2 - 2);
    int ty0 = below ? (int)sp.y + 14 : (int)sp.y - 38;
    lv_area_t ta{(int32_t)(a.x1 + cx - tw / 2), (int32_t)(a.y1 + ty0), (int32_t)(a.x1 + cx + tw / 2), (int32_t)(a.y1 + ty0 + 24)};
    lv_draw_rect(L, &tb, &ta);
    lv_draw_triangle_dsc_t tri;
    lv_draw_triangle_dsc_init(&tri);
    tri.bg_color = lv_color_hex(col::DEEP);
    if (below) {
        tri.p[0] = pt(a.x1 + sp.x - 5, a.y1 + sp.y + 15);
        tri.p[1] = pt(a.x1 + sp.x + 5, a.y1 + sp.y + 15);
        tri.p[2] = pt(a.x1 + sp.x, a.y1 + sp.y + 9);
    } else {
        tri.p[0] = pt(a.x1 + sp.x - 5, a.y1 + sp.y - 15);
        tri.p[1] = pt(a.x1 + sp.x + 5, a.y1 + sp.y - 15);
        tri.p[2] = pt(a.x1 + sp.x, a.y1 + sp.y - 9);
    }
    lv_draw_triangle(L, &tri);
    draw_text(L, a, cx, ty0 + 3, tip.c_str(), f, 0xffffff, LV_TEXT_ALIGN_CENTER, tw);
}

}  // namespace

class ChanFullPage : public Page {
public:
    explicit ChanFullPage(int ch) : ch_(ch) {}

    void build(lv_obj_t* root, App& app) override {
        app_ = &app;
        g_app = &app;
        const Model& m = app.m();
        const auto& c = m.ch(ch_);
        run_ = c.programOwned && c.program && c.program->running;
        curve_ = !run_ && c.spMode == "curve";
        W_ = app.w();
        int H = app.h() - kTopH;
        build_bar(root, m);
        int top = 72, bot = H - 12;
        build_left(root, 12, top, 372, bot - top);
        int rx = 396, rw = W_ - 12 - rx;
        if (run_) build_run(root, rx, top, rw, bot - top);
        else if (curve_) build_curve(root, rx, top, rw, bot - top);
        else build_fixed(root, rx, top, rw, bot - top);
    }

    void update(const Model& m) override {
        const auto& c = m.ch(ch_);
        bool run = c.programOwned && c.program && c.program->running;
        bool curve = !run && c.spMode == "curve";
        if (run != run_ || curve != curve_) {  // 模式变了：整页重建
            App* a = app_;
            int ch = ch_;
            lv_async_call([](void* p) { auto* pr = static_cast<std::pair<App*, int>*>(p); pr->first->go(PageId::ChanFull, pr->second); delete pr; },
                          new std::pair<App*, int>(a, ch));
            return;
        }
        for (int i = 0; i < 8; ++i) {
            const auto& ci = m.snap.channels[static_cast<size_t>(i)];
            lv_label_set_text(tab_t_[i], (model::fmt_temp(ci) + "°").c_str());
            tab_v_[i].set(model::vstate(m, i + 1), i + 1, ci.tempC, 0, m.snap.stir.on);
        }
        VState st = model::vstate(m, ch_);
        const auto& p = m.progress[static_cast<size_t>(ch_ - 1)];
        vessel_.set(st, ch_, c.tempC, st == VState::Holding ? p.stepFraction : 0, m.snap.stir.on);
        std::string chip;
        if (run) chip = p.programName + " · " + tr(model::program_paused(&*c.program) ? "已暂停" : "运行中");
        else if (!c.installed) chip = tr("已停用");
        else if (st == VState::Stopped) chip = std::string(tr("手动停止")) + " · " + tr("没有程序（手动）");
        else chip = std::string(tr("已启用")) + " · " + tr("没有程序（手动）");
        lv_label_set_text(chip_, chip.c_str());
        lv_label_set_text(tv_, model::fmt_temp(c).c_str());
        lv_label_set_text(pv_, model::fmt_mpa(c.pressurePsi).c_str());
        bool hm = c.gasConsumed && *c.gasConsumed > 0;
        lv_label_set_text(mv_, hm ? model::fmt_mmol(c.gasConsumed).c_str() : "—");
        lv_obj_set_style_text_color(mv_, lv_color_hex(hm ? col::DEEP : col::MUT), 0);
        if (run) {
            int64_t s = p.elapsedSec;
            char b[16];
            std::snprintf(b, sizeof b, "%lld:%02lld", (long long)(s / 3600), (long long)(s / 60 % 60));
            lv_label_set_text(xv_, b);
        } else {
            bool heat = c.state == "heating" || local_of(ch_).heatOn;
            lv_label_set_text(xv_, tr(heat ? "开" : "关"));
            lv_obj_set_style_text_color(xv_, lv_color_hex(heat ? col::ST_ORG : col::MUT), 0);
        }
        lv_obj_update_layout(lroot_);
        lv_obj_align_to(tu_, tv_, LV_ALIGN_OUT_RIGHT_BOTTOM, 5, -8);
        lv_obj_align_to(pu_, pv_, LV_ALIGN_OUT_RIGHT_BOTTOM, 5, -6);
        lv_obj_align_to(mu_, mv_, LV_ALIGN_OUT_RIGHT_BOTTOM, 5, -6);
        if (run) update_run(m);
        else update_manual(m);
    }

private:
    // ---------------------------------------------------------- 上面一条：总览 | 1 号 … 8 号
    void build_bar(lv_obj_t* root, const Model& m) {
        lv_obj_t* bar = box(root, 0, 0, W_, 60, 0xffffff);
        box(bar, 0, 59, W_, 1, 0xdde3ea);
        lv_obj_t* back = box(bar, 12, 8, 88, 44, 0xffffff, 10);
        ring(back, 0xcfd9e4, 2);
        icon(back, &ic::chev_left18, col::SEC, 16, 13);
        label_at(back, 40, 11, tr("总览"), 15, 400, col::INK);
        App* app = app_;
        on_tap(back, [app] { app->go(PageId::Home, app->home_tab >= 10 ? 0 : app->home_tab); });
        box(bar, 112, 17, 1, 26, 0xe3e8ee);
        int x = 125;
        for (int i = 0; i < 8; ++i) {
            int ch = i + 1;
            bool on = ch == ch_;
            bool en = m.snap.channels[static_cast<size_t>(i)].installed;
            lv_obj_t* t = box(bar, x, 8, 110, 44, on ? 0xeaf2fb : 0xffffff, 10);
            ring(t, on ? col::BLUE : 0xdfe5ec, on ? 2 : 1);
            tab_v_[i].create(t, 6, 9, Vessel::S);
            lv_obj_t* n = label_at(t, 34, 11, trf("%d 号", ch).c_str(), 15, 600, on ? col::BLUE : en ? col::INK : col::DIS);
            lv_obj_update_layout(n);
            tab_t_[i] = label_at(t, 34 + lv_obj_get_width(n) + 6, 14, "", 12.5, 400, col::MUT);
            on_tap(t, [app, ch] { app->go(PageId::ChanFull, ch); });
            x += 110 + 7;
        }
    }

    // ---------------------------------------------------------- 左：釜和读数
    void build_left(lv_obj_t* root, int x, int y, int w, int h) {
        lroot_ = card(root, x, y, w, h);
        lv_obj_t* nl = label_at(lroot_, 22, 16, trf("%d 号", ch_).c_str(), 22, 700, col::INK);
        lv_obj_update_layout(nl);
        chip_ = label_at(lroot_, 22 + lv_obj_get_width(nl) + 10, 22, "", 13, run_ ? 700 : 600, run_ ? col::BLUE : col::SEC);
        lv_obj_set_style_bg_color(chip_, lv_color_hex(run_ ? 0xe8f0fb : 0xeef1f5), 0);
        lv_obj_set_style_bg_opa(chip_, LV_OPA_COVER, 0);
        lv_obj_set_style_radius(chip_, 12, 0);
        lv_obj_set_style_pad_hor(chip_, 10, 0);
        lv_obj_set_style_pad_ver(chip_, 3, 0);
        int extra = h - 576;  // 1280×800 多出来的高度：釜下移一半，读数下移
        vessel_.create(lroot_, 91, 61 + extra / 3, Vessel::L);
        int ry = 281 + extra * 2 / 3;
        label_at(lroot_, 22, ry, tr("温度"), 13, 400, col::MUT);
        tv_ = label_at(lroot_, 22, ry + 17, "", 40, 600, run_ ? col::BLUE : col::DEEP);
        tu_ = label(lroot_, "°C", 14, 400, col::MUT);
        label_at(lroot_, 191, ry, tr("压力"), 13, 400, col::MUT);
        pv_ = label_at(lroot_, 191, ry + 19, "", 30, 600, col::DEEP);
        pu_ = label(lroot_, "MPa", 14, 400, col::MUT);
        label_at(lroot_, 22, ry + 81, tr("吸气量"), 13, 400, col::MUT);
        mv_ = label_at(lroot_, 22, ry + 100, "", 30, 600, col::MUT);
        mu_ = label(lroot_, "mmol", 14, 400, col::MUT);
        label_at(lroot_, 191, ry + 81, tr(run_ ? "已运行" : "加热"), 13, 400, col::MUT);
        xv_ = label_at(lroot_, 191, ry + 100, "", 30, 600, col::DEEP);
        box(lroot_, 22, h - 112, w - 44, 1, 0xedf1f5);
        int by = h - 97;
        App* app = app_;
        int ch = ch_;
        if (run_) {
            on_tap(round_btn(lroot_, 72, by, 52, RoundStyle::White, &ic::list22, tr("去程序页"), col::INK, 14.5), [app] { app->go(PageId::Program); });
            on_tap(round_btn(lroot_, 186, by, 52, RoundStyle::White, &ic::chart22, tr("去曲线页"), col::INK, 14.5), [app] { app->go(PageId::Curve, 3); });
            on_tap(round_btn(lroot_, 300, by, 52, RoundStyle::Stop, nullptr, tr("中止程序…"), col::RED, 14.5), [app, ch] { app->abort_confirm(ch); });
        } else {
            on_tap(round_btn(lroot_, 72, by, 52, RoundStyle::White, &ic::chart22, tr("去曲线页"), col::INK, 14.5), [app] { app->go(PageId::Curve, 3); });
            on_tap(round_btn(lroot_, 186, by, 52, RoundStyle::White, &ic::copy22, tr("复制到全部"), col::INK, 14.5), [app, ch] {
                DialogSpec d;
                d.color = col::BLUE;
                d.icon = nullptr;
                d.title = trf("把 %d 号的设定复制到其余各路？", ch);
                d.secondary = tr("取消");
                d.primary = tr("复制");
                d.on_primary = [app, ch] { app->act().copy_to_all(ch); };
                d.body = [](lv_obj_t* b, int w) {
                    lv_obj_t* l = label_at(b, 0, 0, tr("目标温度、升温方式、到位范围、防超调一起复制；正在跑程序和已停用的路不改。"), 15, 400, col::INK);
                    lv_obj_set_width(l, w);
                    lv_label_set_long_mode(l, LV_LABEL_LONG_WRAP);
                    return 44;
                };
                Dialog::open(d);
            });
            bool en = app->m().ch(ch).installed;
            lv_obj_t* dis = round_btn(lroot_, 300, by, 52, en ? RoundStyle::WhiteRed : RoundStyle::White, en ? &ic::ban22 : &ic::play24,
                                      tr(en ? "停用这一路" : "启用这一路"), en ? col::RED : col::BLUE, 14.5);
            on_tap(dis, [app, ch] {
                if (!app->m().ch(ch).installed) { app->act().channel_enable(ch, true); return; }
                confirm_stop(tr("停用这一路？"), "", "", trf("%d 号停止加热、关闭进气阀，从主页和程序里拿掉。", ch), tr("不停用"), tr("停用"),
                             [app, ch] { app->act().channel_enable(ch, false); });
            });
        }
    }

    // ---------------------------------------------------------- 右：定值
    void temp_head(lv_obj_t* c, const char* sub = nullptr) {
        label_at(c, 20, 15, tr("温度"), 14, 700, col::DEEP);
        if (sub) label_at(c, 58, 16, sub, 13, 400, col::SEC);
    }
    void mode_lamp(lv_obj_t* c, int y, int sel) {
        label_at(c, 20, y - 23, tr("升温方式"), 12.5, 400, col::SEC);
        lamp_.create(c, 20, y, {"定值", "曲线 (升温方式)"}, {69, 69}, sel);
        App* app = app_;
        int ch = ch_;
        lamp_.on_change = [app, ch](int i) { app->act().set_heat_mode(ch, i ? "curve" : "fixed"); };
    }
    void anti_switch(lv_obj_t* c, int x, int y) {
        label_at(c, x, y - 28, tr("防超调"), 12.5, 400, col::SEC);
        anti_.create(c, x, y, local_of(ch_).antiOvershoot);
        App* app = app_;
        int ch = ch_;
        anti_.on_change = [app, ch](bool v) { app->act().set_anti_overshoot(ch, v); };
    }
    void tempctl(lv_obj_t* c, int y, bool curve) {
        App* app = app_;
        int ch = ch_;
        bool heat = app->m().ch(ch).state == "heating" || local_of(ch).heatOn;
        tempctl_ = round_btn_side(c, 20, y, 64, heat ? RoundStyle::Stop : RoundStyle::TempCtl, &ic::tempctl34,
                                     tr(heat ? (curve ? "停止曲线升温" : "停止控温") : (curve ? "启动曲线升温" : "启动控温")),
                                     tr(heat ? "点按停止 · 会先问你" : (curve ? "从现在的温度开始，按段自动下发" : "点按开始")),
                                     heat ? col::RED : 0x0a8a4f);
        on_tap(tempctl_, [app, ch, heat, curve] {
            if (heat) {
                confirm_stop(tr("停止加热？"), "", "", trf("%d 号停止加热，阀门和搅拌不变。", ch), tr("不停"), tr("停止加热"),
                             [app, ch] { app->act().heat(ch, false); });
            } else if (curve) {
                app->act().start_profile(ch);
            } else {
                app->act().heat(ch, true);
            }
        });
    }
    void gas_fields(lv_obj_t* c, int x, int ylab, int yfield) {
        const Model& m = app_->m();
        const auto& ch = m.ch(ch_);
        char sv[32];
        std::snprintf(sv, sizeof sv, "%s SV%d", tr("进气阀"), ch_);
        lv_obj_t* sl = label_at(c, x, ylab, sv, 12.5, 400, col::SEC);
        valve_.create(c, x, yfield + 5, ch.valveOpen);
        lv_obj_update_layout(sl);
        // 各列宽度跟着文字走（英文比中文长）
        int x2 = x + std::max(64, (int)lv_obj_get_width(sl)) + 15;
        App* app = app_;
        int id = ch_;
        valve_.on_change = [app, id](bool v) {
            if (!app->m().snap.gas.manualValve) return;  // 要先开手动阀控（页面底下有说明）
            app->act().set_valve(id, v);
        };
        lv_obj_t* vl = label_at(c, x2, ylab, tr("溶液体积"), 12.5, 400, col::SEC);
        lv_obj_update_layout(vl);
        label_at(c, x2 + lv_obj_get_width(vl) + 6, ylab + 3, f1("≤ %.0f", m.settings.limits.volumeMaxMl).c_str(), 11.5, 400, col::MUT);
        NumSpec s;
        s.title = trf("%d 号 · 溶液体积", ch_);
        s.unit = "mL";
        s.value = ch.solutionVolume ? *ch.solutionVolume : 5.0;
        s.min = 0; s.max = m.settings.limits.volumeMaxMl; s.decimals = 1;
        s.last = s.value;
        vol_.create(c, x2, yfield, 110, 44, s);
        int x3 = x2 + std::max(110, (int)lv_obj_get_width(vl) + 34) + 14;
        vol_.on_apply = [app, id](double v) { app->act().set_solution_volume(id, v); };
        label_at(c, x3, ylab, tr("吸气量"), 12.5, 400, col::SEC);
        int zw = std::max(161, text_width(tr("清零 · 重新计量"), font(15)) + 18 + 8 + 32);
        lv_obj_t* z = rect_btn(c, x3, yfield, zw, 44, tr("清零 · 重新计量"), false, 0xcfd9e4, 15, 10, &ic::refresh18);
        lv_obj_set_style_text_color(lv_obj_get_child(z, 0), lv_color_hex(col::INK), 0);
        lv_obj_set_style_text_font(lv_obj_get_child(z, 0), font(15, 400), 0);
        icon_color(lv_obj_get_child(z, 1), col::SEC);
        on_tap(z, [app, id] { app->act().gas_zero(id); });
    }

    void build_fixed(lv_obj_t* root, int x, int y, int w, int h) {
        int ch2 = (h - 12) / 2;
        lv_obj_t* t = card(root, x, y, w, ch2);
        temp_head(t);
        const auto& c = app_->m().ch(ch_);
        mode_lamp(t, 69, 0);
        lv_obj_t* tl = label_at(t, 178, 46, tr("目标温度"), 12.5, 400, col::SEC);
        lv_obj_update_layout(tl);
        label_at(t, 178 + lv_obj_get_width(tl) + 6, 49, f1("35–%.0f", app_->m().settings.limits.tempSpMaxC).c_str(), 11.5, 400, col::MUT);
        NumSpec s;
        s.title = trf("%d 号 · 目标温度", ch_);
        s.unit = "°C";
        s.value = model::temp_sp(c);
        s.min = 35; s.max = app_->m().settings.limits.tempSpMaxC;
        if (!std::isnan(s.value)) s.last = c.tempSpC;
        s.current = model::fmt_temp(c) + " °C";
        sp_.create(t, 178, 69, 120, 44, s);
        App* app = app_;
        int id = ch_;
        sp_.on_apply = [app, id](double v) { app->act().set_temp_sp(id, v); };
        label_at(t, 312, 46, tr("到位范围 ±"), 12.5, 400, col::SEC);
        NumSpec b;
        b.title = trf("%d 号 · 到位范围 ±", ch_);
        b.unit = "°C";
        b.value = local_of(ch_).band;
        b.min = 1; b.max = 20; b.decimals = 0;
        b.last = b.value;
        band_.create(t, 312, 69, 100, 44, b);
        band_.on_apply = [app, id](double v) { app->act().set_band(id, v); };
        anti_switch(t, 426, 74);
        tempctl(t, ch2 - 88, false);
        lv_obj_update_layout(tempctl_);
        label_at(t, 20 + lv_obj_get_width(tempctl_) + 36, ch2 - 65, tr("选「曲线」时在这里编最多 16 段"), 13, 400, col::MUT);

        lv_obj_t* g = card(root, x, y + ch2 + 12, w, ch2);
        label_at(g, 20, 15, tr("气体"), 14, 700, col::DEEP);
        gas_fields(g, 20, 46, 69);
        lv_obj_t* n = label_at(g, 20, ch2 - 34, tr("进气阀要先在主页打开「手动阀控」才能开；溶液体积用来算气相体积和吸气量。"), 13, 400, col::MUT);
        lv_obj_set_width(n, w - 40);
    }

    // ---------------------------------------------------------- 右：曲线
    void build_curve(lv_obj_t* root, int x, int y, int w, int h) {
        int gh = 95;
        int th = h - gh - 12;
        lv_obj_t* t = card(root, x, y, w, th);
        auto lc = local_of(ch_);
        double total = 0;
        for (auto& s : lc.curve) total += s.minutes;
        std::string sum = trf("%d 段 · 终温 %.0f °C · 共 %s · 每段从上一段的温度线性升到目标", (int)lc.curve.size(),
                              lc.curve.empty() ? 0.0 : lc.curve.back().tempC, model::fmt_hmin((int64_t)(total * 60)).c_str());
        temp_head(t, sum.c_str());
        mode_lamp(t, 65, 1);
        anti_switch(t, 178, 70);
        // 曲线图
        auto* cc = new CurveCtx;
        cc->segs = lc.curve;
        cc->start = 25;
        cc->sel = std::min(3, (int)lc.curve.size() - 1);
        sel_ = cc->sel;
        int chart_h = 96 + (th - 469) * 3 / 4;
        lv_obj_t* chart = box(t, 20, 115, w - 40, chart_h, 0, 0, true);
        on_draw(chart, curve_draw, cc);
        lv_obj_add_event_cb(chart, [](lv_event_t* e) { delete static_cast<CurveCtx*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, cc);
        curve_ctx_ = cc;
        chart_ = chart;
        // 表格：段 / 目标温度 / 这一段用时
        int ty = 115 + chart_h + 3;
        label_at(t, 20, ty, tr("段"), 12.5, 400, col::SEC);
        label_at(t, 20, ty + 25, tr("目标温度"), 12.5, 400, col::SEC);
        label_at(t, 20, ty + 42, "°C", 11, 400, col::MUT);
        label_at(t, 20, ty + 62, tr("这一段用时"), 12.5, 400, col::SEC);
        label_at(t, 20, ty + 79, "min", 11, 400, col::MUT);
        int cx0 = 104, cw = 111, gap = 6;
        int ncol = std::min(6, (int)lc.curve.size());
        for (int i = 0; i < ncol; ++i) {
            int cx = cx0 + i * (cw + gap);
            bool on = i == cc->sel;
            lv_obj_t* num = box(t, cx + cw / 2 - 10, ty - 1, 20, 20, on ? col::BLUE : 0xffffff, 10);
            lv_obj_set_style_bg_opa(num, on ? LV_OPA_COVER : 0, 0);
            lv_obj_center(label(num, std::to_string(i + 1).c_str(), 11.5, 700, on ? 0xffffff : col::MUT));
            for (int r = 0; r < 2; ++r) {
                lv_obj_t* cell = box(t, cx, ty + 24 + r * 37, cw, 32, on ? 0xffffff : 0xeef2f7, 7);
                if (on) ring(cell, col::BLUE, 2);
                double v = r == 0 ? lc.curve[static_cast<size_t>(i)].tempC : lc.curve[static_cast<size_t>(i)].minutes;
                lv_obj_center(label(cell, f1("%.0f", v).c_str(), 15, 600, col::DEEP));
                App* app = app_;
                int id = ch_, seg = i, row = r;
                on_tap(cell, [app, id, seg, row] {
                    auto cv = local_of(id).curve;
                    NumSpec s;
                    s.title = trf(row == 0 ? "%d 号 · 第 %d 段目标温度" : "%d 号 · 第 %d 段用时", id, seg + 1);
                    s.unit = row == 0 ? "°C" : "min";
                    s.value = row == 0 ? cv[static_cast<size_t>(seg)].tempC : cv[static_cast<size_t>(seg)].minutes;
                    s.min = row == 0 ? 20 : 1;
                    s.max = row == 0 ? app->m().settings.limits.tempSpMaxC : 999;
                    s.last = s.value;
                    NumPad::open(s, [app, id, seg, row](double v) {
                        auto c2 = local_of(id).curve;
                        if (row == 0) c2[static_cast<size_t>(seg)].tempC = v;
                        else c2[static_cast<size_t>(seg)].minutes = v;
                        app->act().set_profile(id, c2);
                    });
                });
            }
        }
        lv_obj_t* add = box(t, w - 20 - 40, ty + 21, 40, 76, 0xffffff, 8);
        ring(add, col::RING, 2);
        icon(add, &ic::plus16, col::BLUE, 12, 10);
        lv_obj_t* al = label_at(add, 12, 30, tr("加一段"), 12, 600, col::BLUE);
        lv_obj_set_width(al, 17);
        lv_label_set_long_mode(al, LV_LABEL_LONG_WRAP);
        App* app = app_;
        int id = ch_;
        on_tap(add, [app, id] {
            auto c2 = local_of(id).curve;
            if (c2.size() >= 16) return;
            c2.push_back({c2.empty() ? 80.0 : c2.back().tempC, 10});
            app->act().set_profile(id, c2);
        });
        tempctl(t, th - 78, true);

        lv_obj_t* g = card(root, x, y + th + 12, w, gh);
        label_at(g, 20, 37, tr("气体"), 14, 700, col::DEEP);
        gas_fields(g, 74, 14, 37);
    }

    // ---------------------------------------------------------- 右：跑程序（只能看）
    lv_obj_t* lock_chip(lv_obj_t* c, int x, int y) {
        lv_obj_t* chip = box(c, x, y, 135, 26, 0xfff3e0, 13);
        icon(chip, &ic::lock13, col::AMBER, 10, 7);
        label_at(chip, 28, 4, tr("程序控制 · 只能看"), 12.5, 700, col::AMBER);
        lv_obj_update_layout(chip);
        lv_obj_set_width(chip, 28 + lv_obj_get_width(lv_obj_get_child(chip, 1)) + 12);
        return chip;
    }
    void build_run(lv_obj_t* root, int x, int y, int w, int h) {
        lv_obj_t* p = card(root, x, y, w, 99);
        label_at(p, 20, 16, tr("程序"), 14, 700, col::DEEP);
        prog_line_ = label_at(p, 58, 17, "", 13, 400, col::SEC);
        int sw = (w - 40 - 8 * 3) / 9;
        for (int k = 0; k < 9; ++k) {
            lv_obj_t* s = box(p, 20 + k * (sw + 3), 50, sw, 10, 0xe3e8ee, 3);
            lv_obj_set_style_clip_corner(s, true, 0);
            seg_fill_[k] = box(s, 0, 0, 0, 10, 0xf08c00);
            seg_[k] = s;
            seg_t_[k] = label(p, "", 12, 400, col::MUT);
            lv_obj_set_width(seg_t_[k], sw);
            lv_obj_set_style_text_align(seg_t_[k], LV_TEXT_ALIGN_CENTER, 0);
            lv_obj_set_pos(seg_t_[k], 20 + k * (sw + 3), 66);
        }
        int rest = h - 99 - 24;
        int ch2 = rest / 2;
        lv_obj_t* t = card(root, x, y + 99 + 12, w, ch2);
        label_at(t, 20, 19, tr("温度"), 14, 700, col::DEEP);
        lock_chip(t, 58, 16);
        temp_vals_ = t;
        lv_obj_t* g = card(root, x, y + 99 + 12 + ch2 + 12, w, rest - ch2);
        label_at(g, 20, 19, tr("气体"), 14, 700, col::DEEP);
        lock_chip(g, 58, 16);
        gas_vals_ = g;
    }
    void update_run(const Model& m) {
        const auto& c = m.ch(ch_);
        const auto& p = m.progress[static_cast<size_t>(ch_ - 1)];
        int cur = c.program ? c.program->stepIndex : 0;
        std::string sn = cur < (int)p.stepNames.size() ? tr(p.stepNames[static_cast<size_t>(cur)].c_str()) : "";
        std::string line = trf("第 %d/%d 步 · %s", cur + 1, c.program->stepCount, sn.c_str());
        if (!p.stepTarget.empty()) line += trf("到 %s", p.stepTarget.c_str());
        int64_t step_el = (int64_t)(p.stepFraction * 30 * 60);
        line += " · " + trf("已 %s", model::fmt_hmin(step_el).c_str());
        if (p.remainingSec) line += "，" + trf("约剩 %s", model::fmt_hmin(*p.remainingSec).c_str());
        lv_label_set_text(prog_line_, line.c_str());
        for (int k = 0; k < 9; ++k) {
            lv_obj_set_style_bg_color(seg_[k], lv_color_hex(k < cur ? col::GREEN : 0xe3e8ee), 0);
            int sw = lv_obj_get_width(seg_[k]);
            lv_obj_set_width(seg_fill_[k], k == cur ? (int)std::lround(sw * std::max(0.1, p.stepFraction)) : 0);
            lv_label_set_text(seg_t_[k], k < (int)p.stepNames.size() ? tr(p.stepNames[static_cast<size_t>(k)].c_str()) : "");
            lv_obj_set_style_text_color(seg_t_[k], lv_color_hex(k < cur ? col::GREEN : k == cur ? col::BLUE : col::MUT), 0);
            lv_obj_set_style_text_font(seg_t_[k], font(12, k == cur ? 700 : 400), 0);
        }
        // 值块：每次重建（文字会变）
        auto lc = local_of(ch_);
        if (vals_t_) lv_obj_delete(vals_t_);
        vals_t_ = box(temp_vals_, 0, 50, lv_obj_get_width(temp_vals_), 90, 0, 0, true);
        struct V { const char* lab; std::string v; const char* u; int w; };
        std::vector<V> tv = {{"升温方式", tr(c.spMode == "curve" ? "曲线" : "定值"), "", 96},
                             {"目标温度", std::isnan(model::temp_sp(c)) ? std::string("—") : f1("%.0f", c.tempSpC), "°C", 96},
                             {"到位范围 ±", f1("%.0f", lc.band), "°C", 96},
                             {"防超调", tr(lc.antiOvershoot ? "开" : "关"), "", 96},
                             {"加热", tr(lc.heatOn ? "开" : "关"), "", 96}};
        int x = 20;
        for (auto& v : tv) {
            label_at(vals_t_, x, 0, tr(v.lab), 12.5, 400, col::SEC);
            value_block(vals_t_, x, 23, v.w, 44, v.v.c_str(), v.u);
            x += v.w + 14;
        }
        if (vals_g_) lv_obj_delete(vals_g_);
        vals_g_ = box(gas_vals_, 0, 50, lv_obj_get_width(gas_vals_), 90, 0, 0, true);
        char sv[32];
        std::snprintf(sv, sizeof sv, "%s SV%d", tr("进气阀"), ch_);
        std::vector<V> gv = {{sv, tr(c.valveOpen ? "开" : "关"), "", 96},
                             {"溶液体积", f1("%.1f", c.solutionVolume ? *c.solutionVolume : 5.0), "mL", 96},
                             {"吸气量", c.state == "react" ? model::fmt_mmol(c.gasConsumed) : std::string(tr("反应开始后计量")),
                              c.state == "react" ? "mmol" : "", 156}};
        x = 20;
        for (auto& v : gv) {
            label_at(vals_g_, x, 0, tr(v.lab), 12.5, 400, col::SEC);
            value_block(vals_g_, x, 23, v.w, 44, v.v.c_str(), v.u);
            x += v.w + 14;
        }
    }
    void update_manual(const Model& m) {
        const auto& c = m.ch(ch_);
        valve_.set(c.valveOpen);
        if (sp_.obj()) sp_.set(model::temp_sp(c));
        if (vol_.obj() && c.solutionVolume) vol_.set(*c.solutionVolume);
        anti_.set(local_of(ch_).antiOvershoot);
        if (band_.obj()) band_.set(local_of(ch_).band);
        if (curve_ctx_) {
            auto lc = local_of(ch_);
            if (lc.curve.size() != curve_ctx_->segs.size()) {
                App* a = app_;
                int ch = ch_;
                lv_async_call([](void* p) { auto* pr = static_cast<std::pair<App*, int>*>(p); pr->first->go(PageId::ChanFull, pr->second); delete pr; },
                              new std::pair<App*, int>(a, ch));
                return;
            }
            curve_ctx_->segs = lc.curve;
            lv_obj_invalidate(chart_);
        }
        bool heat = c.state == "heating" || local_of(ch_).heatOn;
        if (heat != heat_shown_) {
            heat_shown_ = heat;
            if (built_once_) {
                App* a = app_;
                int ch = ch_;
                lv_async_call([](void* p) { auto* pr = static_cast<std::pair<App*, int>*>(p); pr->first->go(PageId::ChanFull, pr->second); delete pr; },
                              new std::pair<App*, int>(a, ch));
            }
        }
        built_once_ = true;
    }

    int ch_;
    int W_ = 1280;
    bool run_ = false, curve_ = false, heat_shown_ = false, built_once_ = false;
    App* app_ = nullptr;
    Vessel tab_v_[8];
    lv_obj_t* tab_t_[8]{};
    lv_obj_t *lroot_ = nullptr, *chip_ = nullptr, *tv_ = nullptr, *tu_ = nullptr, *pv_ = nullptr, *pu_ = nullptr, *mv_ = nullptr,
             *mu_ = nullptr, *xv_ = nullptr;
    Vessel vessel_;
    LampGroup lamp_;
    Switch anti_, valve_;
    NumField sp_, band_, vol_;
    CurveCtx* curve_ctx_ = nullptr;
    lv_obj_t* tempctl_ = nullptr;
    lv_obj_t* chart_ = nullptr;
    int sel_ = 3;
    lv_obj_t *prog_line_ = nullptr, *seg_[9]{}, *seg_fill_[9]{}, *seg_t_[9]{};
    lv_obj_t *temp_vals_ = nullptr, *gas_vals_ = nullptr, *vals_t_ = nullptr, *vals_g_ = nullptr;
};

std::unique_ptr<Page> make_chan_full_page(int ch) { return std::make_unique<ChanFullPage>(ch); }

}  // namespace reactor::hmi
