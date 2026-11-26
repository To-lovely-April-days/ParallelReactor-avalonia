// L 型弹窗（abortask2.py：彩色标题条 + 内容 + 右下两个 150×52 按钮）、停止确认、
// 报警三级：一级全屏（Alarm-L1）、二级弹窗（Alarm-L2）、三级铃铛气泡（Alarm-L3）
#include "i18n.hpp"
#include "widgets.hpp"

#include <cmath>
#include <cstdio>

namespace reactor::hmi::kit {

lv_obj_t* Dialog::open(const DialogSpec& s) {
    lv_obj_t* dim = modal_dim();
    int W = lv_obj_get_width(dim), H = lv_obj_get_height(dim);
    lv_obj_t* pan = box(dim, (W - s.w) / 2, 0, s.w, 100, 0xffffff, 14);
    lv_obj_set_style_clip_corner(pan, true, 0);
    pop_shadow(pan, 50, 20, 77);
    lv_obj_add_flag(pan, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_t* head = box(pan, 0, 0, s.w, 54, s.color);
    if (s.icon) icon(head, s.icon, 0xffffff, 20, 16);
    int tx = s.icon ? 52 : 20;
    lv_obj_t* t = label_at(head, tx, 15, s.title.c_str(), 18, 700, 0xffffff);
    if (!s.subtitle.empty()) {
        lv_obj_update_layout(t);
        lv_obj_t* st = label_at(head, tx + lv_obj_get_width(t) + 10, 18, s.subtitle.c_str(), 13, 400, 0xffffff);
        lv_obj_set_style_text_opa(st, 204, 0);
    }
    lv_obj_t* body = box(pan, 22, 54 + 20, s.w - 44, 10, 0, 0, true);
    int bh = s.body ? s.body(body, s.w - 44) : 0;
    lv_obj_set_height(body, bh);
    int by = 54 + 20 + bh + 20;
    lv_obj_t* dimp = dim;
    if (!s.primary.empty()) {
        lv_obj_t* p = rect_btn(pan, s.w - 22 - 150, by, 150, 52, s.primary.c_str(), true, s.color, 16);
        auto cb = s.on_primary;
        on_tap(p, [dimp, cb] {
            lv_obj_delete_async(dimp);
            if (cb) cb();
        });
    }
    if (!s.secondary.empty()) {
        lv_obj_t* q = rect_btn(pan, s.w - 22 - 150 - 12 - 150, by, 150, 52, s.secondary.c_str(), false, s.color, 16);
        auto cb = s.on_secondary;
        on_tap(q, [dimp, cb] {
            lv_obj_delete_async(dimp);
            if (cb) cb();
        });
    }
    int ph = by + 52 + 22;
    lv_obj_set_height(pan, ph);
    lv_obj_set_y(pan, (H - ph) / 2 - 20);
    return dim;
}

void Dialog::close(lv_obj_t* dim) {
    if (dim) lv_obj_delete(dim);
}

void confirm_stop(const std::string& title, const std::string& who, const std::string& detail, const std::string& consequence,
                  const std::string& keep, const std::string& stop, std::function<void()> on_stop) {
    DialogSpec s;
    s.w = 560;
    s.color = col::RED;
    s.title = title;
    s.secondary = keep;
    s.primary = stop;
    s.on_primary = std::move(on_stop);
    s.body = [who, detail, consequence](lv_obj_t* b, int w) {
        int y = 0;
        if (!who.empty()) {
            mini_vessel(b, 0, 5, 34, chan_color(0), false);
            label_at(b, 38, 0, who.c_str(), 18, 700, col::INK);
            lv_obj_t* d = label_at(b, 38, 25, detail.c_str(), 13.5, 400, col::SEC);
            lv_obj_set_width(d, w - 38);
            y = 58;
        }
        lv_obj_t* hl = box(b, 0, y, w, 45, 0xfdf3f2, 10);
        lv_obj_t* l = label(hl, consequence.c_str(), 15, 400, col::INK);
        lv_obj_set_width(l, w - 32);
        lv_obj_align(l, LV_ALIGN_LEFT_MID, 16, 0);
        lv_obj_update_layout(l);
        int lh = lv_obj_get_height(l);
        if (lh + 24 > 45) lv_obj_set_height(hl, lh + 24);
        return y + std::max(45, lh + 24);
    };
    Dialog::open(s);
}

std::string alarm_title_body(std::optional<int> ch, const std::string& title) {
    if (ch) {
        std::string pre = std::to_string(*ch) + " 号";
        if (title.compare(0, pre.size(), pre) == 0) {
            size_t k = pre.size();
            while (k < title.size() && title[k] == ' ') ++k;
            return tr(title.substr(k).c_str());
        }
    }
    return tr(title.c_str());
}

std::string alarm_title(std::optional<int> ch, const std::string& title) {
    return ch ? trf("%d 号", *ch) + " " + alarm_title_body(ch, title) : std::string(tr(title.c_str()));
}

// ------------------------------------------------------------ 二级：弹窗
lv_obj_t* alarm_dialog(const model::AlarmItem& a, int64_t now_ms, std::function<void()> on_ack, std::function<void()> on_page) {
    DialogSpec s;
    s.w = 600;
    s.color = col::RED;
    s.title = alarm_title(a.dto.channel, a.dto.title);
    s.subtitle = trf("二级 · %s", model::fmt_clock(a.dto.raisedAt).c_str());
    s.secondary = tr("去报警页");
    s.primary = tr("确认");
    s.on_primary = std::move(on_ack);
    s.on_secondary = std::move(on_page);
    int64_t down = a.ext.downSince ? (now_ms - a.ext.downSince) / 1000 : 0;
    model::AlarmItem it = a;
    s.body = [it, down](lv_obj_t* b, int w) {
        int y = 0;
        if (it.ext.downSince) {
            lv_obj_t* l1 = label_at(b, 0, 11, tr("已断开"), 17, 700, col::INK);
            lv_obj_update_layout(l1);
            int x = lv_obj_get_width(l1) + 10;
            char buf[32];
            std::snprintf(buf, sizeof buf, "%lld s", (long long)down);
            lv_obj_t* v = label_at(b, x, 0, buf, 30, 600, col::RED);
            lv_obj_update_layout(v);
            x += lv_obj_get_width(v) + 10;
            if (!it.ext.did.empty()) label_at(b, x, 15, it.ext.did[0].c_str(), 13.5, 400, col::MUT);
            y = 46;
        }
        std::string hl = it.ext.highlight.empty() ? it.dto.message : it.ext.highlight;
        lv_obj_t* bx = box(b, 0, y, w, 41, 0xfdf3f2, 10);
        lv_obj_t* hlab = label(bx, hl.c_str(), 14.5, 400, col::INK);
        lv_obj_set_width(hlab, w - 28);
        lv_obj_align(hlab, LV_ALIGN_LEFT_MID, 14, 0);
        y += 51;
        if (!it.ext.hint.empty()) {
            lv_obj_t* h = label_at(b, 0, y, it.ext.hint.c_str(), 13.5, 400, col::SEC);
            lv_obj_set_width(h, w);
            lv_label_set_long_mode(h, LV_LABEL_LONG_WRAP);
            lv_obj_update_layout(h);
            y += lv_obj_get_height(h);
        }
        return y;
    };
    return Dialog::open(s);
}

// ------------------------------------------------------------ 三级：铃铛气泡
namespace {
lv_obj_t* g_bubble = nullptr;
lv_timer_t* g_bubble_timer = nullptr;
int g_bubble_left = 5;
lv_obj_t* g_bubble_foot = nullptr;
std::string g_bubble_time;
}  // namespace

void Bubble::hide() {
    if (g_bubble_timer) { lv_timer_delete(g_bubble_timer); g_bubble_timer = nullptr; }
    if (g_bubble) { lv_obj_delete(g_bubble); g_bubble = nullptr; }
}

void Bubble::show(const model::AlarmItem& a, int bell_cx, std::function<void()> on_view) {
    hide();
    // 右边留在铃铛右侧（画布：气泡 881..1221，铃铛 1095）；外面留 40 px 给阴影（父对象会裁掉子对象的阴影）
    const int M = 40;
    int x = bell_cx + 66 - 340;
    g_bubble = box(lv_layer_top(), x - M, 52, 340 + 2 * M, 140 + M, 0, 0, true);
    x -= M;
    lv_obj_t* arrow = box(g_bubble, bell_cx - x - 10, 2, 20, 20, 0xffffff);
    lv_obj_set_style_transform_rotation(arrow, 450, 0);
    lv_obj_set_style_transform_pivot_x(arrow, 10, 0);
    lv_obj_set_style_transform_pivot_y(arrow, 10, 0);
    ring(arrow, col::AMBER, 2);
    lv_obj_t* p = box(g_bubble, M, 9, 340, 125, 0xffffff, 12);
    ring(p, col::AMBER, 2);
    lv_obj_set_style_outline_width(p, 4, 0);
    lv_obj_set_style_outline_color(p, lv_color_hex(col::AMBER), 0);
    lv_obj_set_style_outline_opa(p, 41, 0);
    lv_obj_set_style_shadow_width(p, 28, 0);
    lv_obj_set_style_shadow_offset_y(p, 12, 0);
    lv_obj_set_style_shadow_color(p, lv_color_hex(0x102a52), 0);
    lv_obj_set_style_shadow_opa(p, 51, 0);
    // 盖住箭头的下半截，让箭头和气泡连在一起
    box(g_bubble, bell_cx - x - 12, 11, 24, 6, 0xffffff);
    icon(p, &ic::warn_circle20, 0, 14, 13);
    std::string title = alarm_title(a.dto.channel, a.dto.title);
    label_at(p, 42, 11, title.c_str(), 15, 700, col::INK);
    lv_obj_t* m = label_at(p, 42, 35, a.dto.message.c_str(), 13, 400, col::SEC);
    lv_obj_set_width(m, 284);
    lv_label_set_long_mode(m, LV_LABEL_LONG_WRAP);
    g_bubble_time = model::fmt_clock(a.dto.raisedAt);
    g_bubble_left = 5;
    g_bubble_foot = label_at(p, 42, 89, "", 12, 400, col::MUT);
    lv_label_set_text(g_bubble_foot, trf("%s · %d s 后收起", g_bubble_time.c_str(), g_bubble_left).c_str());
    lv_obj_t* v = box(p, 274, 80, 52, 34, 0, 8, true);
    lv_obj_center(label(v, tr("查看"), 14, 600, col::BLUE));
    on_tap(v, [on_view] {
        Bubble::hide();
        if (on_view) on_view();
    });
    g_bubble_timer = lv_timer_create(
        [](lv_timer_t*) {
            if (--g_bubble_left <= 0) { Bubble::hide(); return; }
            lv_label_set_text(g_bubble_foot, trf("%s · %d s 后收起", g_bubble_time.c_str(), g_bubble_left).c_str());
        },
        1000, nullptr);
}

// ------------------------------------------------------------ 一级：全屏
namespace {
lv_obj_t* g_full = nullptr;

void stripes_draw(lv_event_t* e) {
    lv_obj_t* o = lv_event_get_target_obj(e);
    lv_layer_t* layer = lv_event_get_layer(e);
    lv_area_t a;
    lv_obj_get_coords(o, &a);
    lv_draw_triangle_dsc_t d;
    lv_draw_triangle_dsc_init(&d);
    d.bg_color = lv_color_hex(0xffd2cf);
    d.bg_opa = 140;
    int h = lv_area_get_height(&a);
    for (int x = a.x1 - 28; x < a.x2 + 28; x += 28) {
        // 135° 斜纹：平行四边形 = 两个三角形
        lv_point_precise_t p0 = {(lv_value_precise_t)x, a.y1}, p1 = {(lv_value_precise_t)(x + 14), a.y1}, p2 = {(lv_value_precise_t)(x + 14 - h), a.y2 + 1}, p3 = {(lv_value_precise_t)(x - h), a.y2 + 1};
        d.p[0] = p0; d.p[1] = p1; d.p[2] = p2;
        lv_draw_triangle(layer, &d);
        d.p[0] = p0; d.p[1] = p2; d.p[2] = p3;
        lv_draw_triangle(layer, &d);
    }
}
}  // namespace

bool AlarmFull::showing() { return g_full != nullptr; }
void AlarmFull::hide() {
    if (g_full) { lv_obj_delete(g_full); g_full = nullptr; }
}

void AlarmFull::show(const model::AlarmItem& a, int more, std::function<void()> on_ack, std::function<void()> on_page,
                     std::function<void()> on_curve) {
    hide();
    lv_display_t* disp = lv_display_get_default();
    int W = lv_display_get_horizontal_resolution(disp), H = lv_display_get_vertical_resolution(disp);
    g_full = box(lv_layer_top(), 0, 60, W, H - 60, 0x9c1420);
    lv_obj_add_flag(g_full, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_set_style_bg_grad_color(g_full, lv_color_hex(0x700a12), 0);
    lv_obj_set_style_bg_grad_dir(g_full, LV_GRAD_DIR_VER, 0);
    lv_obj_set_style_bg_main_stop(g_full, 60, 0);
    lv_obj_t* st = box(g_full, 0, 0, W, 6, 0, 0, true);
    lv_obj_add_event_cb(st, stripes_draw, LV_EVENT_DRAW_MAIN, nullptr);
    // 画布内容区 660 高，这里多出的高度上下平分
    // 画布上这一块在 108..534（720 高的屏）；800 高时整块往下挪，让上下留白差不多
    int oy = (H - 60 - 660) / 2 - 60 + 40;  // 下面的坐标照画布的绝对 y 写
    int L = 150, R = W - 150;
    icon(g_full, &ic::tri_white52, 0, L, 115 + oy);
    label_at(g_full, 218, 108 + oy, tr("一级报警 · 安全联锁已动作"), 13.5, 700, 0xffb4ae);
    std::string title = alarm_title(a.dto.channel, a.dto.title);
    label_at(g_full, 218, 129 + oy, title.c_str(), 32, 700, 0xffffff);
    lv_obj_t* tm = label(g_full, model::fmt_clock(a.dto.raisedAt).c_str(), 16, 400, 0xffc9c6);
    lv_obj_update_layout(tm);
    lv_obj_set_pos(tm, R - lv_obj_get_width(tm), 131 + oy);

    const auto& e = a.ext;
    bool pres = e.quantity == "pressure";
    const char* unit = pres ? "MPa" : "°C";
    if (e.value) {
        std::string q = a.dto.channel ? trf(pres ? "%d 号压力" : "%d 号温度", *a.dto.channel) : std::string(tr(pres ? "压力" : "温度"));
        mini_vessel(g_full, L, 196 + oy, 24, 0xffffff, false);
        label_at(g_full, L + 24, 197 + oy, q.c_str(), 15, 400, 0xffd2cf);
        char v[32];
        std::snprintf(v, sizeof v, pres ? "%.2f" : "%.1f", *e.value);
        lv_obj_t* big = label_at(g_full, L, 212 + oy, v, 72, 400, 0xffffff);
        lv_obj_update_layout(big);
        lv_obj_t* u = label(g_full, unit, 20, 400, 0xffc9c6);
        place_baseline(u, L + lv_obj_get_width(big) + 8, 212 + oy + (lv_obj_get_style_text_font(big, 0)->line_height -
                                                                         lv_obj_get_style_text_font(big, 0)->base_line));
        if (e.rangeLo && e.rangeHi && e.limit) {
            int bx = 399, bw = R - bx, by = 252 + oy;
            double lo = *e.rangeLo, hi = *e.rangeHi;
            auto fx = [&](double x) { return bx + (int)std::lround(bw * (x - lo) / (hi - lo)); };
            lv_obj_t* base = box(g_full, bx, by, bw, 10, 0xffffff, 5);
            lv_obj_set_style_bg_opa(base, 71, 0);
            lv_obj_t* over = box(g_full, fx(*e.limit), by, R - fx(*e.limit), 10, 0xffb4ae, 5);
            (void)over;
            int vx = fx(std::min(hi, std::max(lo, *e.value)));
            box(g_full, vx - 4, by - 4, 2, 18, 0xffffff);
            lv_obj_t* k = box(g_full, vx, by - 3, 16, 16, 0xffffff, 8);
            lv_obj_set_style_outline_width(k, 4, 0);
            lv_obj_set_style_outline_color(k, lv_color_white(), 0);
            lv_obj_set_style_outline_opa(k, 77, 0);
            char b[48];
            std::snprintf(b, sizeof b, "%.1f", lo);
            label_at(g_full, bx, by + 14, b, 12, 400, 0xffc9c6);
            bool pres = a.ext.quantity == "pressure";
            std::snprintf(b, sizeof b, pres ? "%.3f" : "%.1f", *e.limit);
            lv_obj_t* ll = label(g_full, (std::string(tr("上限")) + " " + b).c_str(), 12, 700, 0xffffff);
            lv_obj_update_layout(ll);
            lv_obj_set_pos(ll, fx(*e.limit) - lv_obj_get_width(ll), by + 14);
            if (e.setpoint) {
                std::snprintf(b, sizeof b, pres ? "%.3f " : "%.1f ", *e.setpoint);
                std::string s = b + std::string(tr("设定"));
                lv_obj_t* sl = label(g_full, s.c_str(), 12, 400, 0xffc9c6);
                lv_obj_update_layout(sl);
                int sw = lv_obj_get_width(sl), sx = std::clamp(fx(*e.setpoint) - sw / 2, bx, bx + bw - sw);
                // 设定值离上限很近时两个字会叠在一起：设定挪到「上限」字的右边（再放不下才放到条上面）
                int lx = lv_obj_get_x(ll), lw2 = lv_obj_get_width(ll);
                bool hit = sx < lx + lw2 + 6 && sx + sw + 6 > lx;
                if (hit && lx + lw2 + 10 + sw <= bx + bw) { sx = lx + lw2 + 10; hit = false; }
                lv_obj_set_pos(sl, sx, hit ? by - 20 : by + 14);
            }
            std::snprintf(b, sizeof b, "%.1f %s", hi, unit);
            lv_obj_t* hl = label(g_full, b, 12, 400, 0xffc9c6);
            lv_obj_update_layout(hl);
            lv_obj_set_pos(hl, R - lv_obj_get_width(hl), by + 14);
        }
    }
    lv_obj_t* div = box(g_full, L, 314 + oy, R - L, 1, 0xffffff);
    lv_obj_set_style_bg_opa(div, 46, 0);
    int mid = L + (R - L) / 2 + 20;
    label_at(g_full, L, 332 + oy, tr("系统已经做了"), 13, 700, 0xffb4ae);
    for (size_t i = 0; i < e.did.size(); ++i) {
        int y = 357 + oy + (int)i * 31;
        icon(g_full, &ic::check18, 0xffd2cf, L, y + 6);
        label_at(g_full, L + 26, y + 4, e.did[i].c_str(), 15.5, 400, 0xffffff);
    }
    label_at(g_full, mid, 332 + oy, tr("接下来"), 13, 700, 0xffb4ae);
    std::vector<std::string> nx = e.next;
    nx.push_back(tr("情况不对就点主页的「全部停止」"));
    for (size_t i = 0; i < nx.size(); ++i) {
        int y = 357 + oy + (int)i * 29;
        lv_obj_t* n = box(g_full, mid, y + 1, 22, 22, 0xffffff, 11);
        lv_obj_set_style_bg_opa(n, 51, 0);
        lv_obj_center(label(n, std::to_string(i + 1).c_str(), 12.5, 700, 0xffffff));
        label_at(g_full, mid + 32, y, nx[i].c_str(), 15.5, 400, 0xffffff);
    }
    if (more > 0) label_at(g_full, L, 494 + oy, trf("还有 %d 条未处理", more).c_str(), 14.5, 400, 0xffc9c6);
    // 按钮：看这一刻的曲线 / 去报警页 / 确认
    int bx = R;
    lv_obj_t* ok = box(g_full, bx - 152, 476 + oy, 152, 58, 0xffffff, 12);
    lv_obj_t* okl = label(ok, tr("确认"), 18, 700, col::RED);
    lv_obj_update_layout(okl);
    int ow = 22 + 8 + lv_obj_get_width(okl);
    icon(ok, &ic::check22, col::RED, (152 - ow) / 2, 18);
    lv_obj_set_pos(okl, (152 - ow) / 2 + 30, 17);
    on_tap(ok, [on_ack] { AlarmFull::hide(); if (on_ack) on_ack(); });
    bx -= 152 + 12;
    lv_obj_t* pg = box(g_full, bx - 110, 476 + oy, 110, 58, 0, 12, true);
    ring(pg, 0xffffff, 2, 140);
    lv_obj_center(label(pg, tr("去报警页"), 16.5, 400, 0xffffff));
    on_tap(pg, [on_page] { AlarmFull::hide(); if (on_page) on_page(); });
    bx -= 110 + 11;
    lv_obj_t* cv = box(g_full, bx - 188, 476 + oy, 188, 58, 0, 12, true);
    ring(cv, 0xffffff, 2, 140);
    icon(cv, &ic::chart20, 0xffffff, 22, 19);
    label_at(cv, 52, 17, tr("看这一刻的曲线"), 16.5, 400, 0xffffff);
    on_tap(cv, [on_curve] { AlarmFull::hide(); if (on_curve) on_curve(); });
}

}  // namespace reactor::hmi::kit
