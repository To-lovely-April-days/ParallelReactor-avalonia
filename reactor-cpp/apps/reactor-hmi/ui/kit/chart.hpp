// 时间曲线图（设计文档 §8.4：lv_chart 不够用，自己画）。一个控件里可以竖着叠几块图，共用一条横轴：
//   曲线页（Curve2-*：一块或三块）、程序运行中 / 跑完 / 已中止的「整次运行」（三块：温度 / 压力 / 吸气量）、
//   记录页「各路吸气量」、泄漏测试「测量阶段的压降」、程序页「预计曲线」。
// 不做双纵轴：每一块只有一个量。横轴是任意单位的 double（毫秒时间戳、分钟都行），刻度文字由 x_label 回调给出。
// 只用 LVGL 的 draw API（线、矩形、三角形、文字），在 DRAW_MAIN 里画；数据改了调 set() 再 invalidate。
#pragma once
#include "lvgl.h"

#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>
#include <utility>

namespace reactor::hmi::kit {

struct ChartSeries {
    // 等间隔数据（x0 + i·dx）；也可以给 xs（不等间隔，和 y 一样长）。NaN = 断开。
    double x0 = 0, dx = 1;
    std::vector<double> xs;
    std::vector<float> y;
    uint32_t color = 0x00559c;
    int width = 2;
    int dash = 0, gap = 0;        // 虚线（计划线、阈值线）
    lv_opa_t opa = LV_OPA_COVER;
    bool fill = false;            // 线下面填一层半透明（程序库预览）
    lv_opa_t fill_opa = 40;
    bool end_dot = false;         // 末端画一个点（泄漏测试）
    std::string end_label;        // 末端文字（「7 号」「阈值」）
    uint32_t end_label_color = 0;
    bool gradient_by_value = false;  // 线色按 y 值走「冷蓝 → 暖橙」（升温曲线）
    // 什么时候算「真断了」：两个有效点之间超过这么远（横轴单位）才断开，中间零星的 NaN 直接连过去。
    // 0 = 自动：等间隔数据取 3 × dx；不等间隔（xs）遇到 NaN 就断。负数 = 遇到 NaN 就断（老行为）
    double max_gap = 0;
};

struct ChartPanel {
    double ymin = 0, ymax = 1;
    std::vector<double> yticks;
    int ydec = 0;
    std::string ysuffix;          // 刻度后缀（「°」）
    std::string title, unit;      // 左上角小标签「温度 °C」
    bool title_box = true;        // 白底描边小框（曲线页）；false = 只写字（运行中）
    int weight = 1;               // 竖向分高度的权重
    std::vector<ChartSeries> series;
    // 设定值附近：绿色带 [band_lo, band_hi] + 中线虚线 + 右边写「+2 °C / −2 °C」
    std::optional<std::pair<double, double>> band;
    std::optional<double> band_center;
    std::string band_hi_text, band_lo_text;
    // 斜纹区（x 范围）+ 中间一行字（「反应开始后计量 · 吸气停止就结束」）
    std::optional<std::pair<double, double>> hatch;
    std::string hatch_text;
    std::string unit_top;         // 纵轴顶上的单位（「mmol」「kPa」）
};

struct ChartConfig {
    double x0 = 0, x1 = 1;
    std::vector<double> xticks;
    std::function<std::string(double)> x_label;
    std::optional<double> now;            // 「现在」竖线
    uint32_t now_color = 0x4a5563;
    int now_width = 1;
    bool shade_future = false;            // 「现在」之后浅灰蓝底
    bool grey_future = false;             // 「现在」之后灰底（已中止：后面没有执行）
    std::optional<std::pair<double, double>> highlight;  // 当前步的浅蓝底
    std::vector<double> vlines;           // 步骤分界（很淡的竖线）
    std::optional<double> cursor;         // 读数竖虚线 + 各线上的点 + 底部时间气泡
    std::string cursor_text;
    std::optional<double> marker;         // 一条标记竖虚线（「开始充反应气」）
    std::string marker_text;
    std::optional<double> stop_at;        // 红色竖线 + 红色小签（「11:28 中止」）
    std::string stop_text;
    int left = 44, right = 12, top = 8, bottom = 26, gap = 10;
    bool xgrid = true, ygrid = true;
    bool frame = true;                    // 每块外面一圈细框
    double ytick_px = 12.5;
};

class TimeChart {
public:
    lv_obj_t* create(lv_obj_t* parent, int x, int y, int w, int h);
    void set(const ChartConfig& cfg, const std::vector<ChartPanel>& panels);
    lv_obj_t* obj() const { return root_; }
    // 内容区（第 i 块）在控件里的位置，页面放浮层（读数卡片）用
    lv_area_t panel_area(int i) const;
    int x_to_px(double x) const;
    std::function<void(double x)> on_press;  // 点 / 拖：给出点到的 x（读数用）
    std::function<void()> on_release;        // 手指抬起（或按压被打断）
    // 只改读数竖线（不重建整张图）：拖动时跟手用；nullopt = 不画
    void set_cursor(std::optional<double> x, const std::string& text);

private:
    struct State;
    static void draw_cb(lv_obj_t* o, lv_layer_t* layer, const lv_area_t& a, void* user);
    lv_obj_t* root_ = nullptr;
    State* st_ = nullptr;
};

// 刻度：在 [lo, hi] 里取 n 个左右的「整」数
std::vector<double> nice_ticks(double lo, double hi, int n, double* step_out = nullptr);
// 时钟刻度（UTC 毫秒），间隔 step_min 分钟
std::vector<double> clock_ticks(int64_t from, int64_t to, int step_min);

}  // namespace reactor::hmi::kit
