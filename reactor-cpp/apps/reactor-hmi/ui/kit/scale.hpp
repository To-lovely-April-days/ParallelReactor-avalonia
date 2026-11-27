// 整体缩放：界面代码一律按画布的「设计像素」（1280×720 为基准）写，屏幕上按 g 倍画。
// 板子的屏是 1920×1080 → g = 1.5：字体按 1.5 倍字号直接生成、图标用 1.5 倍的图，所以是清楚的，不是把小图拉大。
//
// 做法：这个头文件在 lvgl.h 之后被所有界面代码包含（theme.hpp 里），把用到的「带像素参数」的 LVGL 函数
// 换成先乘 g 再调用的版本；读回来的尺寸 / 坐标（lv_obj_get_width、lv_obj_get_coords、触摸点……）先除以 g。
// 这样页面代码看到的永远是设计像素，不用到处改。LVGL 自己内部不受影响（宏只在界面代码里生效）。
//
// 例外（要自己注意的）：
//   * 自绘里的点用 kit::pt()（它直接给出屏幕像素）；lv_draw_line / lv_draw_triangle 的点不再二次缩放。
//   * 字体结构体里的 line_height / base_line 是屏幕像素：用 scale::U() 换回来，或者用 lv_font_get_line_height（已换算）。
//   * 直接改 layer->_clip_area 的地方用 scale::S_area()。
#pragma once
#include "lvgl.h"

#include <cmath>
#include <cstdint>

namespace reactor::hmi::scale {

extern float g;  // 屏幕像素 / 设计像素；1 = 不缩放
void set(float s);

// 设计像素 → 屏幕像素。LV_SIZE_CONTENT、LV_PCT()、LV_RADIUS_CIRCLE、LV_COORD_MAX 这些特殊值原样传。
inline int32_t S(int32_t v) {
    if (g == 1.0f) return v;
    if (LV_COORD_IS_SPEC(v) || v >= LV_RADIUS_CIRCLE || v <= -LV_COORD_MAX) return v;
    return (int32_t)std::lround(v * g);
}
inline float Sf(float v) { return v * g; }
// 屏幕像素 → 设计像素
inline int32_t U(int32_t v) {
    if (g == 1.0f) return v;
    if (LV_COORD_IS_SPEC(v) || v >= LV_COORD_MAX || v <= -LV_COORD_MAX) return v;
    return (int32_t)std::lround(v / g);
}
// 区域（x2 / y2 是含在内的最后一个像素）
inline lv_area_t S_area(const lv_area_t& a) {
    if (g == 1.0f) return a;
    lv_area_t r;
    r.x1 = S(a.x1); r.y1 = S(a.y1);
    r.x2 = (int32_t)std::lround((a.x2 + 1) * g) - 1;
    r.y2 = (int32_t)std::lround((a.y2 + 1) * g) - 1;
    return r;
}
inline lv_area_t U_area(const lv_area_t& a) {
    if (g == 1.0f) return a;
    lv_area_t r;
    r.x1 = U(a.x1); r.y1 = U(a.y1);
    r.x2 = (int32_t)std::lround((a.x2 + 1) / g) - 1;
    r.y2 = (int32_t)std::lround((a.y2 + 1) / g) - 1;
    return r;
}

}  // namespace reactor::hmi::scale

// ---------------------------------------------------------------- 包装（先定义，再用宏顶替原名）
namespace reactor::hmi::scale::wrap {

using reactor::hmi::scale::S;
using reactor::hmi::scale::U;

// 位置 / 尺寸
inline void obj_set_pos(lv_obj_t* o, int32_t x, int32_t y) { ::lv_obj_set_pos(o, S(x), S(y)); }
inline void obj_set_x(lv_obj_t* o, int32_t v) { ::lv_obj_set_x(o, S(v)); }
inline void obj_set_y(lv_obj_t* o, int32_t v) { ::lv_obj_set_y(o, S(v)); }
inline void obj_set_size(lv_obj_t* o, int32_t w, int32_t h) { ::lv_obj_set_size(o, S(w), S(h)); }
inline void obj_set_width(lv_obj_t* o, int32_t v) { ::lv_obj_set_width(o, S(v)); }
inline void obj_set_height(lv_obj_t* o, int32_t v) { ::lv_obj_set_height(o, S(v)); }
inline void obj_align(lv_obj_t* o, lv_align_t al, int32_t x, int32_t y) { ::lv_obj_align(o, al, S(x), S(y)); }
inline void obj_align_to(lv_obj_t* o, const lv_obj_t* base, lv_align_t al, int32_t x, int32_t y) {
    ::lv_obj_align_to(o, base, al, S(x), S(y));
}
inline void obj_set_ext_click_area(lv_obj_t* o, int32_t v) { ::lv_obj_set_ext_click_area(o, S(v)); }

// 读回（屏幕 → 设计）
inline int32_t obj_get_width(const lv_obj_t* o) { return U(::lv_obj_get_width(o)); }
inline int32_t obj_get_height(const lv_obj_t* o) { return U(::lv_obj_get_height(o)); }
inline int32_t obj_get_x(const lv_obj_t* o) { return U(::lv_obj_get_x(o)); }
inline int32_t obj_get_y(const lv_obj_t* o) { return U(::lv_obj_get_y(o)); }
inline int32_t obj_get_content_width(const lv_obj_t* o) { return U(::lv_obj_get_content_width(o)); }
inline int32_t obj_get_content_height(const lv_obj_t* o) { return U(::lv_obj_get_content_height(o)); }
inline void obj_get_coords(const lv_obj_t* o, lv_area_t* a) {
    ::lv_obj_get_coords(o, a);
    *a = U_area(*a);
}
inline void indev_get_point(const lv_indev_t* i, lv_point_t* p) {
    ::lv_indev_get_point(i, p);
    p->x = U(p->x);
    p->y = U(p->y);
}
inline int32_t display_get_horizontal_resolution(const lv_display_t* d) { return U(::lv_display_get_horizontal_resolution(d)); }
inline int32_t display_get_vertical_resolution(const lv_display_t* d) { return U(::lv_display_get_vertical_resolution(d)); }
inline int32_t font_get_line_height(const lv_font_t* f) { return U(::lv_font_get_line_height(f)); }
inline void text_get_size(lv_point_t* out, const char* t, const lv_font_t* f, int32_t ls, int32_t lns, int32_t max_w,
                          lv_text_flag_t flag) {
    ::lv_text_get_size(out, t, f, S(ls), S(lns), S(max_w), flag);
    out->x = U(out->x);
    out->y = U(out->y);
}

// 样式里的像素量
#define REACTOR_SC_STYLE(name)                                                         \
    inline void obj_set_style_##name(lv_obj_t* o, int32_t v, lv_style_selector_t s) { \
        ::lv_obj_set_style_##name(o, S(v), s);                                         \
    }
REACTOR_SC_STYLE(radius)
REACTOR_SC_STYLE(width)
REACTOR_SC_STYLE(height)
REACTOR_SC_STYLE(border_width)
REACTOR_SC_STYLE(outline_width)
REACTOR_SC_STYLE(outline_pad)
REACTOR_SC_STYLE(shadow_width)
REACTOR_SC_STYLE(shadow_offset_x)
REACTOR_SC_STYLE(shadow_offset_y)
REACTOR_SC_STYLE(shadow_spread)
REACTOR_SC_STYLE(pad_all)
REACTOR_SC_STYLE(pad_ver)
REACTOR_SC_STYLE(pad_hor)
REACTOR_SC_STYLE(pad_top)
REACTOR_SC_STYLE(pad_bottom)
REACTOR_SC_STYLE(pad_left)
REACTOR_SC_STYLE(pad_right)
REACTOR_SC_STYLE(pad_row)
REACTOR_SC_STYLE(pad_column)
REACTOR_SC_STYLE(arc_width)
REACTOR_SC_STYLE(line_width)
REACTOR_SC_STYLE(line_dash_width)
REACTOR_SC_STYLE(line_dash_gap)
REACTOR_SC_STYLE(text_line_space)
REACTOR_SC_STYLE(text_letter_space)
REACTOR_SC_STYLE(min_width)
REACTOR_SC_STYLE(min_height)
REACTOR_SC_STYLE(max_width)
REACTOR_SC_STYLE(max_height)
REACTOR_SC_STYLE(transform_pivot_x)
REACTOR_SC_STYLE(transform_pivot_y)
REACTOR_SC_STYLE(translate_x)
REACTOR_SC_STYLE(translate_y)
#undef REACTOR_SC_STYLE

// 自绘：区域、线宽、圆角按设计像素给，这里换成屏幕像素。点（p1/p2、三角形顶点）由 kit::pt() 直接给屏幕像素。
inline void draw_rect(lv_layer_t* l, const lv_draw_rect_dsc_t* d, const lv_area_t* a) {
    lv_draw_rect_dsc_t c = *d;
    c.radius = S(c.radius);
    c.border_width = S(c.border_width);
    c.outline_width = S(c.outline_width);
    c.outline_pad = S(c.outline_pad);
    c.shadow_width = S(c.shadow_width);
    c.shadow_offset_x = S(c.shadow_offset_x);
    c.shadow_offset_y = S(c.shadow_offset_y);
    c.shadow_spread = S(c.shadow_spread);
    lv_area_t sa = S_area(*a);
    ::lv_draw_rect(l, &c, &sa);
}
inline void draw_line(lv_layer_t* l, const lv_draw_line_dsc_t* d) {
    lv_draw_line_dsc_t c = *d;
    c.width = S(c.width);
    c.dash_width = S(c.dash_width);
    c.dash_gap = S(c.dash_gap);
    ::lv_draw_line(l, &c);
}
inline void draw_label(lv_layer_t* l, const lv_draw_label_dsc_t* d, const lv_area_t* a) {
    lv_draw_label_dsc_t c = *d;
    c.letter_space = S(c.letter_space);
    c.line_space = S(c.line_space);
    lv_area_t sa = S_area(*a);
    ::lv_draw_label(l, &c, &sa);
}

}  // namespace reactor::hmi::scale::wrap

#define lv_obj_set_pos ::reactor::hmi::scale::wrap::obj_set_pos
#define lv_obj_set_x ::reactor::hmi::scale::wrap::obj_set_x
#define lv_obj_set_y ::reactor::hmi::scale::wrap::obj_set_y
#define lv_obj_set_size ::reactor::hmi::scale::wrap::obj_set_size
#define lv_obj_set_width ::reactor::hmi::scale::wrap::obj_set_width
#define lv_obj_set_height ::reactor::hmi::scale::wrap::obj_set_height
#define lv_obj_align ::reactor::hmi::scale::wrap::obj_align
#define lv_obj_align_to ::reactor::hmi::scale::wrap::obj_align_to
#define lv_obj_set_ext_click_area ::reactor::hmi::scale::wrap::obj_set_ext_click_area
#define lv_obj_get_width ::reactor::hmi::scale::wrap::obj_get_width
#define lv_obj_get_height ::reactor::hmi::scale::wrap::obj_get_height
#define lv_obj_get_x ::reactor::hmi::scale::wrap::obj_get_x
#define lv_obj_get_y ::reactor::hmi::scale::wrap::obj_get_y
#define lv_obj_get_content_width ::reactor::hmi::scale::wrap::obj_get_content_width
#define lv_obj_get_content_height ::reactor::hmi::scale::wrap::obj_get_content_height
#define lv_obj_get_coords ::reactor::hmi::scale::wrap::obj_get_coords
#define lv_indev_get_point ::reactor::hmi::scale::wrap::indev_get_point
#define lv_display_get_horizontal_resolution ::reactor::hmi::scale::wrap::display_get_horizontal_resolution
#define lv_display_get_vertical_resolution ::reactor::hmi::scale::wrap::display_get_vertical_resolution
#define lv_font_get_line_height ::reactor::hmi::scale::wrap::font_get_line_height
#define lv_text_get_size ::reactor::hmi::scale::wrap::text_get_size
#define lv_obj_set_style_radius ::reactor::hmi::scale::wrap::obj_set_style_radius
#define lv_obj_set_style_width ::reactor::hmi::scale::wrap::obj_set_style_width
#define lv_obj_set_style_height ::reactor::hmi::scale::wrap::obj_set_style_height
#define lv_obj_set_style_border_width ::reactor::hmi::scale::wrap::obj_set_style_border_width
#define lv_obj_set_style_outline_width ::reactor::hmi::scale::wrap::obj_set_style_outline_width
#define lv_obj_set_style_outline_pad ::reactor::hmi::scale::wrap::obj_set_style_outline_pad
#define lv_obj_set_style_shadow_width ::reactor::hmi::scale::wrap::obj_set_style_shadow_width
#define lv_obj_set_style_shadow_offset_x ::reactor::hmi::scale::wrap::obj_set_style_shadow_offset_x
#define lv_obj_set_style_shadow_offset_y ::reactor::hmi::scale::wrap::obj_set_style_shadow_offset_y
#define lv_obj_set_style_shadow_spread ::reactor::hmi::scale::wrap::obj_set_style_shadow_spread
#define lv_obj_set_style_pad_all ::reactor::hmi::scale::wrap::obj_set_style_pad_all
#define lv_obj_set_style_pad_ver ::reactor::hmi::scale::wrap::obj_set_style_pad_ver
#define lv_obj_set_style_pad_hor ::reactor::hmi::scale::wrap::obj_set_style_pad_hor
#define lv_obj_set_style_pad_top ::reactor::hmi::scale::wrap::obj_set_style_pad_top
#define lv_obj_set_style_pad_bottom ::reactor::hmi::scale::wrap::obj_set_style_pad_bottom
#define lv_obj_set_style_pad_left ::reactor::hmi::scale::wrap::obj_set_style_pad_left
#define lv_obj_set_style_pad_right ::reactor::hmi::scale::wrap::obj_set_style_pad_right
#define lv_obj_set_style_pad_row ::reactor::hmi::scale::wrap::obj_set_style_pad_row
#define lv_obj_set_style_pad_column ::reactor::hmi::scale::wrap::obj_set_style_pad_column
#define lv_obj_set_style_arc_width ::reactor::hmi::scale::wrap::obj_set_style_arc_width
#define lv_obj_set_style_line_width ::reactor::hmi::scale::wrap::obj_set_style_line_width
#define lv_obj_set_style_line_dash_width ::reactor::hmi::scale::wrap::obj_set_style_line_dash_width
#define lv_obj_set_style_line_dash_gap ::reactor::hmi::scale::wrap::obj_set_style_line_dash_gap
#define lv_obj_set_style_text_line_space ::reactor::hmi::scale::wrap::obj_set_style_text_line_space
#define lv_obj_set_style_text_letter_space ::reactor::hmi::scale::wrap::obj_set_style_text_letter_space
#define lv_obj_set_style_min_width ::reactor::hmi::scale::wrap::obj_set_style_min_width
#define lv_obj_set_style_min_height ::reactor::hmi::scale::wrap::obj_set_style_min_height
#define lv_obj_set_style_max_width ::reactor::hmi::scale::wrap::obj_set_style_max_width
#define lv_obj_set_style_max_height ::reactor::hmi::scale::wrap::obj_set_style_max_height
#define lv_obj_set_style_transform_pivot_x ::reactor::hmi::scale::wrap::obj_set_style_transform_pivot_x
#define lv_obj_set_style_transform_pivot_y ::reactor::hmi::scale::wrap::obj_set_style_transform_pivot_y
#define lv_obj_set_style_translate_x ::reactor::hmi::scale::wrap::obj_set_style_translate_x
#define lv_obj_set_style_translate_y ::reactor::hmi::scale::wrap::obj_set_style_translate_y
#define lv_draw_rect ::reactor::hmi::scale::wrap::draw_rect
#define lv_draw_line ::reactor::hmi::scale::wrap::draw_line
#define lv_draw_label ::reactor::hmi::scale::wrap::draw_label
