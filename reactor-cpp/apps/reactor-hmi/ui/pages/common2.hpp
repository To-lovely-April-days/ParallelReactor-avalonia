// 第二批页面共用的小东西：时间格式、卡片标题、程序页的编辑状态和通道条。
#pragma once
#include "../app.hpp"
#include "../kit/chart.hpp"
#include "../kit/i18n.hpp"

#include <array>
#include <functional>
#include <string>
#include <cstdint>
#include <memory>
#include <vector>

namespace reactor::hmi {

// ---- 时间 / 大小 ----
std::string hm(int64_t ms);                 // 「14:05」
std::string md(int64_t ms);                 // 「11-25」
std::string md_hm(int64_t ms);             // 「11-25 13:05」
std::string day_word(int64_t ms, int64_t now);  // 「今天 · 11-26」「昨天 · 11-25」「11-24」
std::string fmt_bytes(int64_t b);           // 「18.4 MB」「46 KB」「28.4 GB」
std::string chan_list(const std::vector<int>& chs);  // 「1–6 号」「3 4 5 号」
int days_between(int64_t a, int64_t b);     // 按本地日期

// 卡片标题：大字 + 后面一句灰字，返回整行宽
int card_head(lv_obj_t* parent, int x, int y, const char* title, const char* sub, double px = 17);
// 「标题 · 灰色小说明」字段标签（Prog-Params：「目标温度 35-200」）
void field_label(lv_obj_t* parent, int x, int y, const std::string& text, const std::string& hint, bool faded = false);
// 通道号小圆（绿底白字：「正用在 ③④⑤」）
lv_obj_t* chan_dot(lv_obj_t* parent, int x, int y, int ch, uint32_t bg = 0xe3f4ea, uint32_t fg = 0x14995a, int d = 20);

// ---- 程序页：每一路的编辑副本（换页不丢；下发 / 存库后清掉「改动」标记）----
struct ProgEdit {
    std::array<model::ProgramDoc, 8> doc;
    std::array<bool, 8> init{}, dirty{};
    std::array<int, 8> sel{};        // 选中的步骤
    std::array<int, 8> seg{};        // 曲线编辑器里选中的段
    int ch = 5;                      // 当前通道
    bool lib_tpl = false;            // 程序库：看「内置模板」
    std::string lib_sel;             // 程序库选中的 id
    std::array<bool, 8> leak_pick{}; // 泄漏测试勾选
    bool leak_result_seen = false;   // 结果页已经看过（点了「重新选择通道」）
    bool leak_init = false;          // 勾选已按「全部空闲」初始化
    std::string result_dismissed[8];  // 跑完 / 已中止页点了「再跑」等以后，不再显示的运行 id
};
ProgEdit& prog_edit();
// 换会话（预览批量截图）时把各页记住的状态清掉
void reset_page_state();
// 程序页「添加步骤」弹层（预览也用）
void prog_open_add_step(App& app, int ch);
void prog_sync(const model::Model& m);  // 没改动的路跟着后端的程序走

// 通道条（8 张小卡：小釜 + 号码 + 程序名 + 状态）
std::string chan_state_text(const model::Model& m, int ch, uint32_t* color);
void prog_chan_strip(lv_obj_t* root, const model::Model& m, int x, int y, int w, int sel, std::function<void(int)> on_pick);

// ---- 程序页的几块（ui/pages/prog_*.cpp）----
void build_prog_editor(lv_obj_t* body, App& app, const model::Model& m, int ch, std::function<void()> refresh);
void build_prog_run(lv_obj_t* body, App& app, const model::Model& m, int ch);
void build_prog_result(lv_obj_t* body, App& app, const model::Model& m, int ch, const model::RunRecord& r);
void build_prog_lib(lv_obj_t* body, App& app, const model::Model& m, std::function<void()> refresh);
void build_leak(lv_obj_t* body, App& app, const model::Model& m, std::function<void()> refresh);
const model::RunRecord* last_result(const model::Model& m, int ch);  // 这一路刚跑完 / 刚中止的那次运行

// 三块整次运行曲线（温度 / 压力 / 吸气量），运行中、跑完、已中止共用
void run_chart(kit::TimeChart& tc, const model::Model& m, int ch, const model::Trend* trend, int64_t from, int64_t to,
               int64_t now, bool aborted, bool show_plan);

// 预览 / 别的页直接打开的弹层
std::string& records_select();                 // 记录页要选中的运行 id
void records_open_export(App& app);            // Rec-Export
void records_open_range_export(App& app);      // Rec-RangeExport
void settings_open_wifi_password(App& app, const std::string& ssid, const std::string& init = "");  // Set-NetPw
void settings_confirm_quit(App& app);

// 页面工厂
std::unique_ptr<Page> make_program_page(int arg);
std::unique_ptr<Page> make_curve_page(int tab);
// 截图调试：曲线页一进来就在 at_ms 处显示读数线 + 读数卡片（不自动收起）
void curve_debug_cursor(int64_t at_ms);
std::unique_ptr<Page> make_records_page(int tab);
std::unique_ptr<Page> make_settings_page(int section);

}  // namespace reactor::hmi
