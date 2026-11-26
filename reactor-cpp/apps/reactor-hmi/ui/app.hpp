// 屏幕管理：顶栏常驻，下面的内容区按页面切换；弹窗、报警放 lv_layer_top()。
// 只在 LVGL 线程用。数据经 apply() 进来（来自 ModelMailbox），页面在 update() 里刷新。
#pragma once
#include "../model/actions.hpp"
#include "../model/model.hpp"
#include "kit/widgets.hpp"

#include <memory>
#include <set>
#include <string>
#include <cstdint>
#include <functional>

namespace reactor::hmi {

class App;

class Page {
public:
    virtual ~Page() = default;
    virtual void build(lv_obj_t* root, App& app) = 0;  // root：顶栏以下的内容区（0,60 起）
    virtual void update(const model::Model& m) = 0;
};

enum class PageId { Home, Program, Curve, Records, Settings, ChanFull, Alarm };

class App {
public:
    App(model::Actions& act, int w, int h);
    void build(lv_obj_t* screen);
    void apply(std::shared_ptr<const model::Model> m);
    void go(PageId p, int arg = 0);   // Home：arg = 页签（0 装置总览 / 1 气路图 / 2 方案总览 / 10+ch 方案总览点开一路）；ChanFull：arg = 通道
    void rebuild();                   // 换语言后整页重建

    // 弹层
    void open_quick(int ch);          // 点釜快捷面板
    void open_lang();                 // 语言弹窗
    void close_overlays();
    void show_alarm(const model::AlarmItem& a);  // 按级别弹出（一级全屏 / 二级弹窗 / 三级气泡）
    void stop_all_confirm();
    void abort_confirm(int ch);

    const model::Model& m() const { return *model_; }
    model::Actions& act() { return act_; }
    int w() const { return w_; }
    int h() const { return h_; }
    PageId page() const { return page_; }
    int page_arg() const { return arg_; }
    kit::TopBar& topbar() { return top_; }
    Page* current() { return cur_.get(); }

    // Home 页签之间切换时用（不整页重建）
    int home_tab = 0;
    // 打开着的弹层要跟着快照刷新时，挂在这里（关弹层时清掉）
    std::function<void(const model::Model&)> overlay_update;
    void set_overlay(lv_obj_t* dim) { overlay_ = dim; }

private:
    void update_topbar();
    void update_link_overlay();       // 断线：全屏盖一层「正在连接后台服务…」，挡住全部点击（设计文档 §8.1）
    void update_control_request();    // PC 申请远程控制：弹「允许 / 拒绝」（决定记录 2026-11-26）
    model::Actions& act_;
    int w_, h_;
    lv_obj_t* screen_ = nullptr;
    lv_obj_t* content_ = nullptr;
    kit::TopBar top_;
    std::unique_ptr<Page> cur_;
    PageId page_ = PageId::Home;
    int arg_ = 0;
    std::shared_ptr<const model::Model> model_;
    std::set<std::string> seen_alarms_;
    bool first_ = true;
    int64_t started_ms_ = 0;          // 界面启动时刻（墙上时间）：启动前就有的报警不弹
    lv_obj_t* overlay_ = nullptr;
    lv_obj_t* link_ = nullptr;        // 断线盖层（lv_layer_sys，在所有弹窗之上）
    lv_obj_t* link_sub_ = nullptr;
    lv_obj_t* ctl_dim_ = nullptr;     // 控制申请弹窗（遮罩）；关掉时由 DELETE 事件清空
    lv_obj_t* ctl_left_ = nullptr;    // 弹窗里「xx s 后自动拒绝」那一行
    std::string ctl_id_;              // 弹着的是哪一个申请
};

// 页面工厂（ui/pages/*.cpp）
std::unique_ptr<Page> make_home_page(int tab);
std::unique_ptr<Page> make_chan_full_page(int ch);
std::unique_ptr<Page> make_alarm_page();
std::unique_ptr<Page> make_placeholder_page(const char* title, const char* what);
// 弹层（ui/pages/*.cpp）
lv_obj_t* open_chan_quick(App& app, int ch);
lv_obj_t* open_lang_popup(App& app);

}  // namespace reactor::hmi
