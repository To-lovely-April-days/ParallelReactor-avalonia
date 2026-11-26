// 自绘小工具：斜纹、折线（实线 / 虚线）、折线下的填充。都在 LV_EVENT_DRAW_MAIN 里画，
// 坐标相对对象左上角。LVGL 软件渲染的虚线只支持横竖线，这里斜线的虚线自己切段。
#pragma once
#include "lvgl.h"

#include <cstdint>
#include <vector>

namespace reactor::hmi::kit {

struct Pt { float x, y; };
lv_point_precise_t pt(float x, float y);  // 浮点 → LVGL 坐标（LV_USE_FLOAT=0 时是整数）

// 斜纹（135°）：a 色条宽 wa，b 色条宽 wb（b 可以透明：opa_b = 0）
void hatch(lv_obj_t* o, uint32_t a, lv_opa_t opa_a, int wa, uint32_t b, lv_opa_t opa_b, int wb);

// 在 layer 上画（给自绘对象的 DRAW_MAIN 回调用）
void draw_polyline(lv_layer_t* layer, const lv_area_t& origin, const std::vector<Pt>& pts, uint32_t color, int width,
                   lv_opa_t opa = LV_OPA_COVER, int dash = 0, int gap = 0);
void draw_fill_under(lv_layer_t* layer, const lv_area_t& origin, const std::vector<Pt>& pts, float base_y, uint32_t color,
                     lv_opa_t opa);
void draw_hline(lv_layer_t* layer, const lv_area_t& origin, float x1, float x2, float y, uint32_t color, int w = 1,
                int dash = 0, int gap = 0);
void draw_vline(lv_layer_t* layer, const lv_area_t& origin, float x, float y1, float y2, uint32_t color, int w = 1);
void draw_text(lv_layer_t* layer, const lv_area_t& origin, int x, int y, const char* txt, const lv_font_t* f,
               uint32_t color, lv_text_align_t align = LV_TEXT_ALIGN_LEFT, int w = 200);
void draw_circle(lv_layer_t* layer, const lv_area_t& origin, float cx, float cy, float r, uint32_t fill, uint32_t border,
                 int bw, lv_opa_t opa = LV_OPA_COVER);

// 矩形（可圆角、可描边）；x/y/w/h 相对 origin
void draw_rect(lv_layer_t* layer, const lv_area_t& origin, float x, float y, float w, float h, uint32_t fill, lv_opa_t opa,
               int radius = 0, uint32_t border = 0, int bw = 0);
// 矩形里的 45° 斜纹（间距 spacing，线宽 w）
void draw_hatch(lv_layer_t* layer, const lv_area_t& origin, float x, float y, float w, float h, uint32_t color, lv_opa_t opa,
                int spacing = 8, int lw = 1);

// 挂一个自绘回调（fn 在对象删除时释放）
using DrawFn = void (*)(lv_obj_t* o, lv_layer_t* layer, const lv_area_t& a, void* user);
void on_draw(lv_obj_t* o, DrawFn fn, void* user);

}  // namespace reactor::hmi::kit
