// 主题：颜色、字体、常用的小工具函数。所有页面和控件都从这里取，不在页面里写死颜色值。
// 颜色照画布定稿（gen/family.py、states.py、states3.py）。
#pragma once
#include "lvgl.h"
#include "scale.hpp"  // 设计像素 → 屏幕像素（1920×1080 屏上 1.5 倍）

#include <cstdint>
#include <functional>
#include <string>
#include <vector>

namespace reactor::hmi {

// ---- 颜色（命名照画布生成脚本里的变量名）----
namespace col {
inline lv_color_t hex(uint32_t v) { return lv_color_hex(v); }
constexpr uint32_t NAVYBAR = 0x0a2a55;  // 顶栏
constexpr uint32_t BLUE = 0x00559c;     // 主色
constexpr uint32_t DEEP = 0x1d3557;     // 读数
constexpr uint32_t ORANGE = 0xfd9005;   // 页签下划线、「现在」线
constexpr uint32_t BG = 0xeef1f5;       // 页面底色
constexpr uint32_t TILE = 0xf3f7fc;     // 输入框 / 小块底色
constexpr uint32_t RING = 0xc9d6e5;     // 输入框描边
constexpr uint32_t INK = 0x1f2a35;      // 正文
constexpr uint32_t SEC = 0x4a5563;      // 次要文字
constexpr uint32_t TABSEC = 0x4f5b67;   // 未选页签文字
constexpr uint32_t MUT = 0x6b7685;      // 说明文字
constexpr uint32_t MUT2 = 0x8792a0;     // 更淡的说明
constexpr uint32_t DIS = 0xa6afba;      // 停用的文字
constexpr uint32_t GREEN = 0x00a862;
constexpr uint32_t RED = 0xe5484d;
constexpr uint32_t AMBER = 0xc27400;
constexpr uint32_t LINE = 0xb9c3cf;     // 管路
constexpr uint32_t DIV = 0xe6ebf1;      // 分隔线
constexpr uint32_t CONN = 0x2fd27a;     // 已连接小绿点
// 状态色（states.py）
constexpr uint32_t ST_RED = 0xd92d20;
constexpr uint32_t ST_ORG = 0xe8590c;
constexpr uint32_t ST_GRN = 0x14995a;
constexpr uint32_t ST_BLU = 0x1d63c4;
constexpr uint32_t ST_CYN = 0x0e8fa8;
constexpr uint32_t ST_TEAL = 0x0f8f80;
constexpr uint32_t ST_GRY = 0x8a95a3;
constexpr uint32_t ST_AMB = 0xb7791f;
constexpr uint32_t ST_OFF = 0xc3cad3;
}  // namespace col

// 每一路的识别色（曲线、小釜图标，curvepage.py COL）
uint32_t chan_color(int ch /*1..8*/);
// 液体颜色按温度（states4.py by_temp：冷蓝 → 暖橙 → 红）
uint32_t liquid_color(double temp_c);

// ---- 字体 ----
// 画布写的是 Inter, Noto Sans SC：主字体 Inter（数字、拉丁），中文回退到 Noto Sans SC。
// 板子上用 FreeType 读 assets/fonts 下的 TTF/OTF；电脑预览（tiny_ttf）读不了 CFF 的 .otf，
// 中文回退改用 macOS 的 STHeiti。
struct FontSet {
    std::string dir;      // 用到的字体目录
    std::string summary;  // 给日志 / 关于页看的说明
};
// 按顺序找字体目录：explicit_dir → <exe 目录>/fonts → /opt/reactor/fonts → 工程里的 assets/fonts。
FontSet fonts_init(const std::string& explicit_dir, const std::string& exe_path);
// px：像素大小（小数四舍五入）；weight：400 / 500 / 600 / 700。结果缓存，可以随便调。
const lv_font_t* font(double px, int weight = 400);

// ---- 小工具 ----
// 去掉默认主题样式、不滚动的空白容器
lv_obj_t* box(lv_obj_t* parent, int x, int y, int w, int h, uint32_t bg = 0, int radius = 0, bool transparent = false);
lv_obj_t* label(lv_obj_t* parent, const char* text, double px, int weight, uint32_t color);
lv_obj_t* label_at(lv_obj_t* parent, int x, int y, const char* text, double px, int weight, uint32_t color);
// 单行、超宽打省略号（LVGL 9 的 LONG_DOT 要固定高度才不折行：高度设成一行字高）。宽度由调用方先设好。
void dot_line(lv_obj_t* label);
// 一段话里几种样式（LVGL 9.2 的 label 没有 recolor，用 spangroup）
struct SpanDef { std::string text; double px; int weight; uint32_t color; };
lv_obj_t* rich(lv_obj_t* parent, int x, int y, int w, const std::vector<SpanDef>& spans);
// 把 label 放在基线 baseline_y 上（同一行数字和单位对齐用）
void place_baseline(lv_obj_t* lbl, int x, int baseline_y);
int text_width(const char* text, const lv_font_t* f);
// 画布的卡片阴影：0 0 0 1px rgba(16,42,82,.06), 0 1px 3px rgba(16,42,82,.08)
void card_shadow(lv_obj_t* o);
// 内描边（CSS inset box-shadow）
void ring(lv_obj_t* o, uint32_t color, int width, lv_opa_t opa = LV_OPA_COVER);
// 弹窗阴影
void pop_shadow(lv_obj_t* o, int blur = 40, int dy = 18, lv_opa_t opa = 70);
// 单色图标（A8）或彩色图标（ARGB）；A8 的颜色由 color 决定
lv_obj_t* icon(lv_obj_t* parent, const lv_image_dsc_t* img, uint32_t color, int x = 0, int y = 0);
void icon_color(lv_obj_t* img, uint32_t color);
// 让对象可点，点了调 cb（user_data 原样带过去）
void on_click(lv_obj_t* o, lv_event_cb_t cb, void* user = nullptr);
// lambda 版：函数对象挂在控件上，控件删除时一起释放
void on_tap(lv_obj_t* o, std::function<void()> fn);
// 带透明度的颜色叠加（rgba 的 a）：返回在白底 / 指定底色上混合后的颜色
uint32_t mix(uint32_t fg, uint32_t bg, double alpha);

// psi（表压）→ MPa
inline double psi_to_mpa(double psi) { return psi * 0.00689475729; }
inline double mpa_to_psi(double mpa) { return mpa / 0.00689475729; }

}  // namespace reactor::hmi
