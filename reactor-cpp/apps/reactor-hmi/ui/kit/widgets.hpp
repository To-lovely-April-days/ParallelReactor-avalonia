// 公共控件（设计文档 §8.1 ui/kit）。尺寸、颜色照画布定稿；坐标一律相对父对象。
#pragma once
#include "../../model/derive.hpp"
#include "icons_gen.hpp"
#include "theme.hpp"

#include <functional>
#include <optional>
#include <string>
#include <vector>
#include <cstdint>

namespace reactor::hmi::kit {

namespace ic = reactor::hmi::icons;

// ============================================================ 顶栏（60 高）
// 左：HTLAB + 型号；中：5 个圆形导航（总览 / 程序 / 曲线 / 记录 / 设置）；
// 右：● 已连接 · 模拟模式 · 铃铛（带数字）· 地球（语言）| 时间 / 日期。不做急停。
class TopBar {
public:
    lv_obj_t* create(lv_obj_t* parent, int width);
    void set_nav(int sel);  // 0..4；-1 = 都不选（报警页）
    void set_status(bool connected, bool simulated);
    void set_remote(bool remote, const std::string& holder);  // PC 拿着控制权：顶栏挂「PC 控制中」，点它收回
    void set_bell(int count, int worst_tier, bool selected);
    void set_time(int64_t utc_ms);
    void relayout();
    std::function<void(int)> on_nav;
    std::function<void()> on_bell, on_lang, on_remote;

private:
    lv_obj_t* root_ = nullptr;
    lv_obj_t* nav_circle_[5]{}; lv_obj_t* nav_icon_[5]{}; lv_obj_t* nav_text_[5]{};
    lv_obj_t* dot_ = nullptr; lv_obj_t* conn_ = nullptr; lv_obj_t* sim_ = nullptr; lv_obj_t* remote_ = nullptr;
    lv_obj_t* bell_ = nullptr; lv_obj_t* bell_icon_ = nullptr; lv_obj_t* bell_badge_ = nullptr; lv_obj_t* bell_num_ = nullptr;
    lv_obj_t* globe_ = nullptr; lv_obj_t* time_ = nullptr; lv_obj_t* date_ = nullptr;
    int width_ = 1280;
};

// ============================================================ 页签卡片（44 高，白底圆角 10）
struct TabDef {
    const lv_image_dsc_t* icon = nullptr;  // A8
    std::string text;
    std::string badge;                     // 空 = 没有角标
    enum Badge { None, Grey, Red } badge_style = None;
};
class TabCard {
public:
    lv_obj_t* create(lv_obj_t* parent, int x, int y, int w, const std::vector<TabDef>& tabs, int sel);
    void select(int i);
    lv_obj_t* obj() const { return root_; }
    std::function<void(int)> on_select;

private:
    void style_tab(int i, bool on);
    lv_obj_t* root_ = nullptr;
    std::vector<lv_obj_t*> tabs_, texts_, icons_, badges_, unders_;
    std::vector<TabDef> defs_;
};

// ============================================================ 卡片容器（白底圆角 12 + 浅阴影）
lv_obj_t* card(lv_obj_t* parent, int x, int y, int w, int h, int radius = 12);

// ============================================================ 圆钮
enum class RoundStyle {
    White,      // 白底 + 浅灰圈（#dbe5f0）
    WhiteRed,   // 白底 + 浅红圈（停用这一路）
    Stop,       // 红底白方块（停止 / 中止，带红色投影）
    Blue,       // 蓝底 + 外发光（确认）
    TempCtl,    // 绿圈 + 外发光（启动控温）
};
// 圆钮 + 下方一行字（cx：圆心 x；y：圆顶 y）。返回可点的整体。
lv_obj_t* round_btn(lv_obj_t* parent, int cx, int y, int size, RoundStyle st, const lv_image_dsc_t* icon,
                    const char* text, uint32_t text_color, double text_px = 14);
// 圆钮 + 右边两行字（标题 / 说明），如「启动控温 · 点按开始」「确认 · 7 号恢复加热、进气」
lv_obj_t* round_btn_side(lv_obj_t* parent, int x, int y, int size, RoundStyle st, const lv_image_dsc_t* icon,
                         const char* title, const char* sub, uint32_t title_color);
// 竖栏里的大圆钮（62）：白圈 + 中间图标 + 右上角状态点 + 可选蓝色进度弧
struct ColBtn {
    lv_obj_t* root = nullptr; lv_obj_t* arc = nullptr; lv_obj_t* dot = nullptr; lv_obj_t* icon = nullptr;
    lv_obj_t* label = nullptr; lv_obj_t* value = nullptr;
    void set(bool on, const char* value_text, uint32_t value_color, int arc_deg);
};
ColBtn col_btn(lv_obj_t* parent, int cx, int y, const lv_image_dsc_t* icon, const char* label);

// 普通矩形按钮（描边 / 实心）
lv_obj_t* rect_btn(lv_obj_t* parent, int x, int y, int w, int h, const char* text, bool filled, uint32_t color,
                   double px = 16, int radius = 10, const lv_image_dsc_t* icon = nullptr);

// ============================================================ 带灯选项（定值 / 曲线 …）
class LampGroup {
public:
    void create(lv_obj_t* parent, int x, int y, const std::vector<std::string>& opts, const std::vector<int>& widths,
                int sel, int h = 44, double px = 14, int gap = 6);
    void set(int sel);
    int get() const { return sel_; }
    void set_faded(bool f);               // 不起作用时变淡（还能点）
    int width() const { return width_; }  // 整组宽度（排版用）
    bool allow_reselect = false;          // 点已选中的那个也回调（单个开关用）
    std::function<void(int)> on_change;

private:
    std::vector<lv_obj_t*> btns_, dots_, texts_;
    int width_ = 0;
    int sel_ = 0;
    double px_ = 14;
};

// ============================================================ 开关（64×34，里面写开 / 关）
class Switch {
public:
    // compact = 设置页的小开关（52×30，没有字）
    void create(lv_obj_t* parent, int x, int y, bool on, bool compact = false);
    void set(bool on);
    bool get() const { return on_; }
    std::function<void(bool)> on_change;  // 用户点了（新值）；不自动改显示，等快照回来
    lv_obj_t* obj() const { return root_; }

private:
    lv_obj_t* root_ = nullptr; lv_obj_t* knob_ = nullptr; lv_obj_t* text_ = nullptr;
    bool on_ = false, compact_ = false;
};

// ============================================================ 数字键盘（Kbd-Num）
struct NumSpec {
    std::string title;       // 「5 号 · 目标温度」
    std::string unit;        // 「°C」
    double value = 0, min = 0, max = 100;
    int decimals = 0;
    std::optional<double> last;     // 上次设定
    std::string current;            // 当前实测（文字）
    bool allow_negative = false;
};
class NumPad {
public:
    static void open(const NumSpec& spec, std::function<void(double)> on_apply);
    static void close();
};

// ============================================================ 数值框（点了弹数字键盘）
class NumField {
public:
    void create(lv_obj_t* parent, int x, int y, int w, int h, const NumSpec& spec, bool readonly = false);
    void set(double v);
    void set_text(const char* t);          // 不是单个数的值（「85 / 38」）
    void set_faded(bool f);
    void set_align_right(bool r);          // 数字靠右（设置页）
    const NumSpec& spec() const { return spec_; }
    std::function<void(double)> on_apply;
    lv_obj_t* obj() const { return root_; }

private:
    lv_obj_t* root_ = nullptr; lv_obj_t* val_ = nullptr; lv_obj_t* unit_ = nullptr;
    NumSpec spec_;
    bool right_ = false;
};
// 只读的值块（程序控制时）：灰底，值 + 单位
lv_obj_t* value_block(lv_obj_t* parent, int x, int y, int w, int h, const char* value, const char* unit);

// ============================================================ 文字键盘（Kbd-Text，带拼音候选）
struct TextKbdOpts {
    bool password = false;     // 密码：显示成 ●●●，右边「显示」；不出拼音、默认英文
    std::string ok_text;       // 确定键的字（「连接」）；空 = 「确定」
    std::string hint;          // 输入框下面一行说明
};
class TextKbd {
public:
    static void open(const std::string& title, const std::string& init, int max_len,
                     std::function<void(const std::string&)> on_ok);
    static void open(const std::string& title, const std::string& init, int max_len, const TextKbdOpts& opts,
                     std::function<void(const std::string&)> on_ok);
    static void close();
};

// ============================================================ 弹窗
// 半透明遮罩（顶栏以下），点空白处不关（停止类操作必须明确选择）
lv_obj_t* modal_dim(lv_opa_t opa = 97);
// L 型弹窗：彩色标题条 + 内容 + 右下两个 150×52 按钮
struct DialogSpec {
    int w = 560;
    uint32_t color = col::RED;               // 标题条和主按钮颜色
    const lv_image_dsc_t* icon = &ic::tri_white22;
    std::string title, subtitle;
    std::function<int(lv_obj_t* body, int w)> body;  // 往 body 里放内容，返回内容高度
    std::string secondary, primary;          // secondary 为空 = 只有一个按钮
    std::function<void()> on_primary, on_secondary;
};
class Dialog {
public:
    static lv_obj_t* open(const DialogSpec& s);   // 返回遮罩（删掉它就关了）
    static void close(lv_obj_t* dim);
};
// 每次停止都要确认：统一的「停止确认」
void confirm_stop(const std::string& title, const std::string& who, const std::string& detail,
                  const std::string& consequence, const std::string& keep_text, const std::string& stop_text,
                  std::function<void()> on_stop);

// ============================================================ 报警三级
// 三级：铃铛下面的气泡，5 s 后自己收起
class Bubble {
public:
    static void show(const model::AlarmItem& a, int bell_cx, std::function<void()> on_view);
    static void hide();
};
// 一级：盖住整个内容区的红色全屏（顶栏还在）
class AlarmFull {
public:
    static void show(const model::AlarmItem& a, int more, std::function<void()> on_ack, std::function<void()> on_page,
                     std::function<void()> on_curve);
    static void hide();
    static bool showing();
};
// 二级：L 型弹窗（红色标题条）
// 报警 / 事件标题：后台的程序类标题自己带「3 号 …」（来源是「系统」、channel 由标题推出），界面别再重复加号码。
// alarm_title_body：去掉开头和 ch 一致的「n 号 」后翻译；alarm_title：有通道时统一成「n 号 标题」。
std::string alarm_title_body(std::optional<int> ch, const std::string& title);
std::string alarm_title(std::optional<int> ch, const std::string& title);
lv_obj_t* alarm_dialog(const model::AlarmItem& a, int64_t now_ms, std::function<void()> on_ack, std::function<void()> on_page);

// ============================================================ 文件图标（CSV / JSON / PDF …）
lv_obj_t* file_icon(lv_obj_t* parent, int x, int y, const char* ext, bool large = false);

// ============================================================ 小釜图标（页签、列表里的 13~34 高）
lv_obj_t* mini_vessel(lv_obj_t* parent, int x, int y, int h, uint32_t liquid, bool disabled = false);

// ============================================================ 釜「乙」
// 序号标在盖上；液体颜色随温度；状态靠动画：升温 = 加热带呼吸，升压 / 反应 = 冒泡，
// 搅拌 = 桨叶转，保持 = 序号外倒计时环，降温 = 外壁虚线，超温 / 超压 = 釜身红边闪，离线 = 序号外黄圈闪，结束 = 对勾。
class Vessel {
public:
    enum Size { S, M, L };  // 22×25 / 120×134 / 190×212（画布的 viewBox 120×134）
    void create(lv_obj_t* parent, int x, int y, Size sz);
    void set(model::VState st, int number, std::optional<double> temp_c, double hold_fraction = 0);
    lv_obj_t* obj() const { return root_; }

private:
    void stop_anims();
    double s_ = 1;
    Size sz_ = M;
    lv_obj_t* root_ = nullptr;
    lv_obj_t* body_ = nullptr; lv_obj_t* liq_ = nullptr; lv_obj_t* over_ = nullptr;
    lv_obj_t* heat_ = nullptr; lv_obj_t* cool_ = nullptr; lv_obj_t* edge_ = nullptr;
    lv_obj_t* imp_ = nullptr; lv_obj_t* bub_[4]{};
    lv_obj_t* badge_ = nullptr; lv_obj_t* num_ = nullptr; lv_obj_t* check_ = nullptr;
    lv_obj_t* ring_ = nullptr; lv_obj_t* warn_ring_ = nullptr;
    int last_state_ = -1;
};

// 读数块（States-D3 values()）：大温度 + 压力 + 吸气量 + 提示行
class Readings {
public:
    // w：宽；scale：1 = 画布的 150×182 viewBox
    void create(lv_obj_t* parent, int x, int y, int w, double scale = 1.0);
    void set(const model::Model& m, int ch);

private:
    lv_obj_t* root_ = nullptr;
    lv_obj_t* tbg_ = nullptr; lv_obj_t* t_ = nullptr; lv_obj_t* tu_ = nullptr;
    lv_obj_t* pbg_ = nullptr; lv_obj_t* pi_ = nullptr; lv_obj_t* p_ = nullptr; lv_obj_t* pu_ = nullptr;
    lv_obj_t* mi_ = nullptr; lv_obj_t* mm_ = nullptr; lv_obj_t* mu_ = nullptr;
    lv_obj_t* hi_ = nullptr; lv_obj_t* ht_ = nullptr;
    int w_ = 150; double s_ = 1;
};

// 弹窗（点釜面板、语言）用的白色浮层：圆角 16，阴影
lv_obj_t* popover(lv_obj_t* parent, int x, int y, int w, int h, int radius = 16);

// 小图标名 → 图像（st_*13 / st_*16）
const lv_image_dsc_t* state_icon(const char* name, int px);

// ============================================================ 第二批加的
// 小药丸（「我的程序」「已完成」「在线」）：圆角底色 + 字；可在字前放图标或一个圆点
lv_obj_t* pill(lv_obj_t* parent, const char* text, uint32_t fg, uint32_t bg, double px = 13, const lv_image_dsc_t* icon = nullptr,
               bool dot = false, int weight = 600);
// 通道小块（小釜 + 号码）：选中 = 白底蓝描边，没选 = 浅底，停用 = 半透明不能点
lv_obj_t* chan_chip(lv_obj_t* parent, int x, int y, int ch, bool on, bool disabled, int w = 46, int h = 34);
// 进度条：底色 + 已完成段，可选一条阈值红竖线（marker 0..1）
lv_obj_t* bar(lv_obj_t* parent, int x, int y, int w, int h, double frac, uint32_t color, std::optional<double> marker = std::nullopt,
              uint32_t track = 0xe6ebf1);
// 步骤图标（px = 16 / 20 / 28）
const lv_image_dsc_t* step_icon(const std::string& type, int px);
// 圆形关闭钮（44，浅灰底）
lv_obj_t* close_btn(lv_obj_t* parent, int x, int y, int size = 44);
// 带图标的描边按钮（高 40，宽度按文字），返回按钮；color = 文字和图标颜色
lv_obj_t* tool_btn(lv_obj_t* parent, int x, int y, const char* text, const lv_image_dsc_t* icon, uint32_t color = 0x1f2a35,
                   int h = 40, double px = 15, uint32_t ring_color = 0xc9d6e5);
// 「11 月 / 18」小日历（Leak-Setup、Leak-Pass）
lv_obj_t* cal_badge(lv_obj_t* parent, int x, int y, int month, int day, uint32_t color, int w = 62, int h = 62);
// 选项方块（Rec-Export：灯 + 文件图标 + 标题 / 说明），点了切换
class LampTile {
public:
    void create(lv_obj_t* parent, int x, int y, int w, int h, const char* ext, const char* title, const char* sub, bool on);
    void set(bool on);
    bool get() const { return on_; }
    std::function<void(bool)> on_change;

private:
    lv_obj_t* root_ = nullptr; lv_obj_t* dot_ = nullptr;
    bool on_ = false;
};
// 简单的 toast（屏幕下方一条，3 s 后消失）：命令结果、导出完成
void toast(const std::string& text, bool ok = true);

}  // namespace reactor::hmi::kit
