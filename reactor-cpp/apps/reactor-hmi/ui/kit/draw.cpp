#include "draw.hpp"
#include "scale.hpp"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

namespace reactor::hmi::kit {

namespace {

struct HatchCtx { uint32_t a; lv_opa_t oa; int wa; uint32_t b; lv_opa_t ob; int wb; };

void line(lv_layer_t* layer, float x1, float y1, float x2, float y2, uint32_t c, int w, lv_opa_t opa) {
    lv_draw_line_dsc_t d;
    lv_draw_line_dsc_init(&d);
    d.color = lv_color_hex(c);
    d.width = w;
    d.opa = opa;
    d.p1 = pt(x1, y1); d.p2 = pt(x2, y2);
    lv_draw_line(layer, &d);
}

// 135° 线段 x + y = k 和矩形求交
bool clip_diag(float k, const lv_area_t& a, float& x1, float& y1, float& x2, float& y2) {
    // 线：x = k - y；y 从 a.y1 到 a.y2+1
    float ya = (float)a.y1, yb = (float)a.y2 + 1;
    float xa = k - ya, xb = k - yb;
    float lo = (float)a.x1, hi = (float)a.x2 + 1;
    if (std::max(xa, xb) < lo || std::min(xa, xb) > hi) return false;
    if (xa > hi) { ya = k - hi; xa = hi; }
    if (xb < lo) { yb = k - lo; xb = lo; }
    x1 = xa; y1 = ya; x2 = xb; y2 = yb;
    return true;
}

void hatch_draw(lv_event_t* e) {
    auto* c = static_cast<HatchCtx*>(lv_event_get_user_data(e));
    lv_obj_t* o = lv_event_get_target_obj(e);
    lv_layer_t* layer = lv_event_get_layer(e);
    lv_area_t a;
    lv_obj_get_coords(o, &a);
    int period = c->wa + c->wb;
    // 线宽按斜向投影换算
    float wa = c->wa / 1.4142f, wb = c->wb / 1.4142f;
    float k0 = (float)(a.x1 + a.y1);
    float k1 = (float)(a.x2 + a.y2 + 2);
    for (float k = k0; k < k1 + period; k += (float)period) {
        float x1, y1, x2, y2;
        if (c->ob && clip_diag(k + c->wa + c->wb / 2.0f, a, x1, y1, x2, y2)) line(layer, x1, y1, x2, y2, c->b, (int)std::ceil(wb), c->ob);
        if (c->oa && clip_diag(k + c->wa / 2.0f, a, x1, y1, x2, y2)) line(layer, x1, y1, x2, y2, c->a, (int)std::ceil(wa), c->oa);
    }
}

struct DrawCtx { DrawFn fn; void* user; };

}  // namespace

lv_point_precise_t pt(float x, float y) {
    // 输入是设计像素，输出直接是屏幕像素（先乘再取整：1.5 倍屏上曲线不会因为先取整而发抖）
    lv_point_precise_t p;
    p.x = (lv_value_precise_t)std::lround(scale::Sf(x));
    p.y = (lv_value_precise_t)std::lround(scale::Sf(y));
    return p;
}

namespace {

}  // namespace

void hatch(lv_obj_t* o, uint32_t a, lv_opa_t oa, int wa, uint32_t b, lv_opa_t ob, int wb) {
    auto* c = new HatchCtx{a, oa, wa, b, ob, wb};
    lv_obj_add_event_cb(o, hatch_draw, LV_EVENT_DRAW_MAIN, c);
    lv_obj_add_event_cb(o, [](lv_event_t* e) { delete static_cast<HatchCtx*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, c);
}

void on_draw(lv_obj_t* o, DrawFn fn, void* user) {
    auto* c = new DrawCtx{fn, user};
    lv_obj_add_event_cb(
        o,
        [](lv_event_t* e) {
            auto* d = static_cast<DrawCtx*>(lv_event_get_user_data(e));
            lv_obj_t* obj = lv_event_get_target_obj(e);
            lv_area_t a;
            lv_obj_get_coords(obj, &a);
            d->fn(obj, lv_event_get_layer(e), a, d->user);
        },
        LV_EVENT_DRAW_MAIN, c);
    lv_obj_add_event_cb(o, [](lv_event_t* e) { delete static_cast<DrawCtx*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, c);
}

void draw_polyline(lv_layer_t* layer, const lv_area_t& o, const std::vector<Pt>& pts, uint32_t color, int width, lv_opa_t opa,
                   int dash, int gap) {
    if (pts.size() < 2) return;
    if (!dash) {
        lv_draw_line_dsc_t d;
        lv_draw_line_dsc_init(&d);
        d.color = lv_color_hex(color);
        d.width = width;
        d.opa = opa;
        d.round_start = 1;
        d.round_end = 1;
        for (size_t i = 1; i < pts.size(); ++i) {
            d.p1 = pt(o.x1 + pts[i - 1].x, o.y1 + pts[i - 1].y);
            d.p2 = pt(o.x1 + pts[i].x, o.y1 + pts[i].y);
            lv_draw_line(layer, &d);
        }
        return;
    }
    // 虚线：沿折线走，按 dash / gap 切
    float phase = 0;
    bool on = true;
    float left = (float)dash;
    for (size_t i = 1; i < pts.size(); ++i) {
        float x0 = pts[i - 1].x, y0 = pts[i - 1].y, x1 = pts[i].x, y1 = pts[i].y;
        float len = std::hypot(x1 - x0, y1 - y0);
        float t = 0;
        while (t < len) {
            float step = std::min(left, len - t);
            if (on) {
                float ax = x0 + (x1 - x0) * t / len, ay = y0 + (y1 - y0) * t / len;
                float bx = x0 + (x1 - x0) * (t + step) / len, by = y0 + (y1 - y0) * (t + step) / len;
                line(layer, o.x1 + ax, o.y1 + ay, o.x1 + bx, o.y1 + by, color, width, opa);
            }
            t += step;
            left -= step;
            if (left <= 0.001f) {
                on = !on;
                left = (float)(on ? dash : gap);
            }
        }
    }
    (void)phase;
}

void draw_fill_under(lv_layer_t* layer, const lv_area_t& o, const std::vector<Pt>& pts, float base, uint32_t color, lv_opa_t opa) {
    lv_draw_triangle_dsc_t d;
    lv_draw_triangle_dsc_init(&d);
    d.bg_color = lv_color_hex(color);
    d.bg_opa = opa;
    for (size_t i = 1; i < pts.size(); ++i) {
        float xa = o.x1 + pts[i - 1].x, ya = o.y1 + pts[i - 1].y, xb = o.x1 + pts[i].x, yb = o.y1 + pts[i].y, yy = o.y1 + base;
        if (xb - xa < 0.01f) continue;
        d.p[0] = pt(xa, ya); d.p[1] = pt(xb, yb); d.p[2] = pt(xb, yy);
        lv_draw_triangle(layer, &d);
        d.p[0] = pt(xa, ya); d.p[1] = pt(xb, yy); d.p[2] = pt(xa, yy);
        lv_draw_triangle(layer, &d);
    }
}

void draw_hline(lv_layer_t* layer, const lv_area_t& o, float x1, float x2, float y, uint32_t c, int w, int dash, int gap) {
    lv_draw_line_dsc_t d;
    lv_draw_line_dsc_init(&d);
    d.color = lv_color_hex(c);
    d.width = w;
    d.dash_width = dash;
    d.dash_gap = gap;
    d.p1 = pt(o.x1 + x1, o.y1 + y); d.p2 = pt(o.x1 + x2, o.y1 + y);
    lv_draw_line(layer, &d);
}

void draw_vline(lv_layer_t* layer, const lv_area_t& o, float x, float y1, float y2, uint32_t c, int w) {
    line(layer, o.x1 + x, o.y1 + y1, o.x1 + x, o.y1 + y2, c, w, LV_OPA_COVER);
}

void draw_text(lv_layer_t* layer, const lv_area_t& o, int x, int y, const char* txt, const lv_font_t* f, uint32_t color,
               lv_text_align_t align, int w) {
    lv_draw_label_dsc_t d;
    lv_draw_label_dsc_init(&d);
    d.text = txt;
    d.text_local = 1;  // 绘制是延后执行的：把文字拷一份（调用方常传临时字符串）
    d.font = f;
    d.color = lv_color_hex(color);
    d.align = align;
    lv_area_t a;
    int x0 = align == LV_TEXT_ALIGN_RIGHT ? x - w : align == LV_TEXT_ALIGN_CENTER ? x - w / 2 : x;
    a.x1 = o.x1 + x0; a.x2 = a.x1 + w - 1;
    a.y1 = o.y1 + y; a.y2 = a.y1 + lv_font_get_line_height(f);
    lv_draw_label(layer, &d, &a);
}

void draw_circle(lv_layer_t* layer, const lv_area_t& o, float cx, float cy, float r, uint32_t fill, uint32_t border, int bw,
                 lv_opa_t opa) {
    lv_draw_rect_dsc_t d;
    lv_draw_rect_dsc_init(&d);
    d.radius = LV_RADIUS_CIRCLE;
    d.bg_color = lv_color_hex(fill);
    d.bg_opa = opa;
    d.border_color = lv_color_hex(border);
    d.border_width = bw;
    d.border_opa = bw ? LV_OPA_COVER : 0;
    lv_area_t a;
    a.x1 = (int32_t)std::lround(o.x1 + cx - r); a.y1 = (int32_t)std::lround(o.y1 + cy - r);
    a.x2 = (int32_t)std::lround(o.x1 + cx + r); a.y2 = (int32_t)std::lround(o.y1 + cy + r);
    lv_draw_rect(layer, &d, &a);
}

void draw_rect(lv_layer_t* layer, const lv_area_t& o, float x, float y, float w, float h, uint32_t fill, lv_opa_t opa, int radius,
               uint32_t border, int bw) {
    if (w <= 0 || h <= 0) return;
    lv_draw_rect_dsc_t d;
    lv_draw_rect_dsc_init(&d);
    d.radius = radius;
    d.bg_color = lv_color_hex(fill);
    d.bg_opa = opa;
    d.border_color = lv_color_hex(border);
    d.border_width = bw;
    d.border_opa = bw ? LV_OPA_COVER : 0;
    lv_area_t a;
    a.x1 = (int32_t)std::lround(o.x1 + x); a.y1 = (int32_t)std::lround(o.y1 + y);
    a.x2 = (int32_t)std::lround(o.x1 + x + w) - 1; a.y2 = (int32_t)std::lround(o.y1 + y + h) - 1;
    lv_draw_rect(layer, &d, &a);
}

void draw_hatch(lv_layer_t* layer, const lv_area_t& o, float x, float y, float w, float h, uint32_t color, lv_opa_t opa, int spacing,
                int lw) {
    if (w <= 0 || h <= 0) return;
    lv_area_t a;
    a.x1 = (int32_t)std::lround(o.x1 + x); a.y1 = (int32_t)std::lround(o.y1 + y);
    a.x2 = (int32_t)std::lround(o.x1 + x + w) - 1; a.y2 = (int32_t)std::lround(o.y1 + y + h) - 1;
    float k0 = (float)(a.x1 + a.y1), k1 = (float)(a.x2 + a.y2 + 2);
    for (float k = k0; k < k1; k += (float)spacing) {
        float x1, y1, x2, y2;
        if (clip_diag(k, a, x1, y1, x2, y2)) line(layer, x1, y1, x2, y2, color, lw, opa);
    }
}

}  // namespace reactor::hmi::kit
