#include "theme.hpp"

#include <climits>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <map>
#include <sys/stat.h>
#include <vector>
#include <cstdint>
#include <functional>
#include <string>

namespace reactor::hmi {

uint32_t chan_color(int ch) {
    static const uint32_t C[8] = {0x1d63c4, 0x2a9fd6, 0x0f8a8a, 0x6b8e23, 0xb8860b, 0xd9730d, 0x8b5a2b, 0x5b6672};
    return (ch >= 1 && ch <= 8) ? C[ch - 1] : 0x5b6672;
}

uint32_t liquid_color(double t) {
    // states4.py STOPS
    struct S { double t; int r, g, b; };
    static const S st[] = {{20, 140, 200, 244}, {45, 160, 214, 200}, {65, 246, 197, 107}, {110, 243, 150, 80}, {200, 229, 72, 77}};
    if (std::isnan(t)) return 0xd3d9e0;
    if (t <= st[0].t) return (140u << 16) | (200u << 8) | 244u;
    for (int i = 0; i < 4; ++i) {
        if (t <= st[i + 1].t) {
            double f = (t - st[i].t) / (st[i + 1].t - st[i].t);
            int r = (int)std::lround(st[i].r + (st[i + 1].r - st[i].r) * f);
            int g = (int)std::lround(st[i].g + (st[i + 1].g - st[i].g) * f);
            int b = (int)std::lround(st[i].b + (st[i + 1].b - st[i].b) * f);
            return (uint32_t)((r << 16) | (g << 8) | b);
        }
    }
    return 0xe5484d;
}

uint32_t mix(uint32_t fg, uint32_t bg, double a) {
    auto ch = [&](int sh) {
        double f = (fg >> sh) & 0xff, b = (bg >> sh) & 0xff;
        return (uint32_t)std::lround(f * a + b * (1 - a)) & 0xff;
    };
    return (ch(16) << 16) | (ch(8) << 8) | ch(0);
}

// ---------------------------------------------------------------- 字体
namespace {

bool exists(const std::string& p) {
    struct stat st{};
    return !p.empty() && ::stat(p.c_str(), &st) == 0;
}
std::string dirname_of(const std::string& p) {
    auto k = p.find_last_of('/');
    return k == std::string::npos ? std::string(".") : p.substr(0, k);
}

std::string g_dir;
std::string g_cjk_override;  // tiny_ttf 时的中文回退（系统字体）
std::map<int, lv_font_t*> g_cache;

const char* inter_file(int w) {
    return w >= 700 ? "Inter-Bold.ttf" : w >= 600 ? "Inter-SemiBold.ttf" : w >= 500 ? "Inter-Medium.ttf" : "Inter-Regular.ttf";
}
const char* noto_file(int w) {
    return w >= 700 ? "NotoSansSC-Bold.otf" : w >= 500 ? "NotoSansSC-Medium.otf" : "NotoSansSC-Regular.otf";
}

lv_font_t* load(const std::string& path, int px) {
    if (!exists(path)) return nullptr;
#if LV_USE_FREETYPE
    return lv_freetype_font_create(path.c_str(), LV_FREETYPE_FONT_RENDER_MODE_BITMAP, (uint32_t)px,
                                   LV_FREETYPE_FONT_STYLE_NORMAL);
#elif LV_USE_TINY_TTF
    return lv_tiny_ttf_create_file(("A:" + path).c_str(), px);
#else
    (void)px;
    return nullptr;
#endif
}

const lv_font_t* builtin(int px) {
    if (px >= 26) return &lv_font_montserrat_28;
    if (px >= 22) return &lv_font_montserrat_24;
    if (px >= 18) return &lv_font_montserrat_20;
    if (px >= 15) return &lv_font_montserrat_16;
    return &lv_font_montserrat_14;
}

}  // namespace

FontSet fonts_init(const std::string& explicit_dir, const std::string& exe_path) {
    FontSet fs;
    std::vector<std::string> cands;
    if (!explicit_dir.empty()) cands.push_back(explicit_dir);
    char buf[PATH_MAX];
    std::string exe = exe_path;
    if (!exe.empty() && ::realpath(exe.c_str(), buf)) exe = buf;
    std::string ed = dirname_of(exe);
    cands.push_back(ed + "/fonts");
    cands.push_back("/opt/reactor/fonts");
    // 工程里的 assets/fonts：build/<x>/reactor-hmi、tools/preview/out/reactor-hmi-shot 等位置都试一下
    for (const char* up : {"/../assets/fonts", "/../../assets/fonts", "/../../../assets/fonts", "/../../../../assets/fonts"})
        cands.push_back(ed + up);
    for (auto& c : cands) {
        if (exists(c + "/Inter-Regular.ttf")) { g_dir = c; break; }
    }
#if LV_USE_FREETYPE
    lv_freetype_init(LV_FREETYPE_CACHE_FT_GLYPH_CNT);
#endif
#if !LV_USE_FREETYPE && LV_USE_TINY_TTF
    // tiny_ttf 读不了 CFF 轮廓的 NotoSansSC .otf，电脑预览时中文回退到系统黑体
    for (const char* p : {"/System/Library/Fonts/STHeiti Medium.ttc", "/System/Library/Fonts/Hiragino Sans GB.ttc"})
        if (exists(p)) { g_cjk_override = p; break; }
#endif
    fs.dir = g_dir;
    fs.summary = g_dir.empty() ? std::string("没找到字体目录，用 LVGL 自带的 Montserrat（中文显示成方框）")
                               : "Inter + " + (g_cjk_override.empty() ? std::string("Noto Sans SC") : g_cjk_override) + " · " + g_dir;
    return fs;
}

const lv_font_t* font(double pxf, int weight) {
    int px = (int)std::lround(pxf);
    if (px < 6) px = 6;
    int w = weight >= 700 ? 700 : weight >= 600 ? 600 : weight >= 500 ? 500 : 400;
    int key = px * 1000 + w;
    auto it = g_cache.find(key);
    if (it != g_cache.end()) return it->second;
    lv_font_t* f = nullptr;
    if (!g_dir.empty()) {
        f = load(g_dir + "/" + inter_file(w), px);
        if (f) {
            lv_font_t* cjk = g_cjk_override.empty() ? load(g_dir + "/" + noto_file(w), px) : load(g_cjk_override, px);
            if (cjk) {
                // 让行高、基线跟主字体一致（回退字体只借字形）
                f->fallback = cjk;
            }
        }
    }
    if (!f) return builtin(px);
    g_cache[key] = f;
    return f;
}

// ---------------------------------------------------------------- 小工具
lv_obj_t* box(lv_obj_t* parent, int x, int y, int w, int h, uint32_t bg, int radius, bool transparent) {
    lv_obj_t* o = lv_obj_create(parent);
    lv_obj_remove_style_all(o);
    lv_obj_set_pos(o, x, y);
    lv_obj_set_size(o, w, h);
    if (!transparent) {
        lv_obj_set_style_bg_color(o, lv_color_hex(bg), 0);
        lv_obj_set_style_bg_opa(o, LV_OPA_COVER, 0);
    }
    if (radius) lv_obj_set_style_radius(o, radius, 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_SCROLLABLE);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    return o;
}

lv_obj_t* label(lv_obj_t* parent, const char* text, double px, int weight, uint32_t color) {
    lv_obj_t* l = lv_label_create(parent);
    lv_obj_remove_style_all(l);
    lv_label_set_text(l, text);
    lv_obj_set_style_text_font(l, font(px, weight), 0);
    lv_obj_set_style_text_color(l, lv_color_hex(color), 0);
    return l;
}

void dot_line(lv_obj_t* l) {
    lv_label_set_long_mode(l, LV_LABEL_LONG_DOT);
    const lv_font_t* f = lv_obj_get_style_text_font(l, LV_PART_MAIN);
    if (f) lv_obj_set_height(l, lv_font_get_line_height(f));
}

lv_obj_t* label_at(lv_obj_t* parent, int x, int y, const char* text, double px, int weight, uint32_t color) {
    lv_obj_t* l = label(parent, text, px, weight, color);
    lv_obj_set_pos(l, x, y);
    return l;
}

lv_obj_t* rich(lv_obj_t* parent, int x, int y, int w, const std::vector<SpanDef>& spans) {
    lv_obj_t* g = lv_spangroup_create(parent);
    lv_obj_remove_style_all(g);
    lv_obj_set_pos(g, x, y);
    lv_obj_set_width(g, w);
    lv_obj_set_height(g, LV_SIZE_CONTENT);
    lv_spangroup_set_mode(g, LV_SPAN_MODE_BREAK);
    if (!spans.empty()) lv_obj_set_style_text_font(g, font(spans[0].px, spans[0].weight), 0);
    for (auto& s : spans) {
        lv_span_t* sp = lv_spangroup_new_span(g);
        lv_span_set_text(sp, s.text.c_str());
        lv_style_t* st = lv_span_get_style(sp);
        lv_style_set_text_font(st, font(s.px, s.weight));
        lv_style_set_text_color(st, lv_color_hex(s.color));
    }
    lv_spangroup_refr_mode(g);
    return g;
}

void place_baseline(lv_obj_t* lbl, int x, int baseline_y) {
    const lv_font_t* f = lv_obj_get_style_text_font(lbl, 0);
    int asc = f->line_height - f->base_line;
    lv_obj_set_pos(lbl, x, baseline_y - asc);
}

int text_width(const char* text, const lv_font_t* f) {
    lv_point_t p;
    lv_text_get_size(&p, text, f, 0, 0, LV_COORD_MAX, LV_TEXT_FLAG_NONE);
    return p.x;
}

void card_shadow(lv_obj_t* o) {
    lv_obj_set_style_border_width(o, 1, 0);
    lv_obj_set_style_border_color(o, lv_color_hex(0x102a52), 0);
    lv_obj_set_style_border_opa(o, 16, 0);
    lv_obj_set_style_shadow_width(o, 4, 0);
    lv_obj_set_style_shadow_offset_y(o, 1, 0);
    lv_obj_set_style_shadow_color(o, lv_color_hex(0x102a52), 0);
    lv_obj_set_style_shadow_opa(o, 22, 0);
}

void ring(lv_obj_t* o, uint32_t color, int width, lv_opa_t opa) {
    lv_obj_set_style_border_width(o, width, 0);
    lv_obj_set_style_border_color(o, lv_color_hex(color), 0);
    lv_obj_set_style_border_opa(o, opa, 0);
}

void pop_shadow(lv_obj_t* o, int blur, int dy, lv_opa_t opa) {
    lv_obj_set_style_shadow_width(o, blur, 0);
    lv_obj_set_style_shadow_offset_y(o, dy, 0);
    lv_obj_set_style_shadow_color(o, lv_color_hex(0x0a192d), 0);
    lv_obj_set_style_shadow_opa(o, opa, 0);
}

lv_obj_t* icon(lv_obj_t* parent, const lv_image_dsc_t* img, uint32_t color, int x, int y) {
    lv_obj_t* o = lv_image_create(parent);
    lv_obj_remove_style_all(o);
    lv_image_set_src(o, img);
    lv_obj_set_pos(o, x, y);
    lv_obj_set_style_image_recolor(o, lv_color_hex(color), 0);
    lv_obj_remove_flag(o, LV_OBJ_FLAG_CLICKABLE);
    return o;
}

void on_tap(lv_obj_t* o, std::function<void()> fn) {
    auto* f = new std::function<void()>(std::move(fn));
    lv_obj_add_flag(o, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(
        o,
        [](lv_event_t* e) {
            // 推迟到这一轮事件处理完再执行：回调里常常会删掉被点的控件（换页、关弹窗）
            auto* copy = new std::function<void()>(*static_cast<std::function<void()>*>(lv_event_get_user_data(e)));
            lv_async_call(
                [](void* p) {
                    auto* g = static_cast<std::function<void()>*>(p);
                    (*g)();
                    delete g;
                },
                copy);
        },
        LV_EVENT_CLICKED, f);
    lv_obj_add_event_cb(
        o, [](lv_event_t* e) { delete static_cast<std::function<void()>*>(lv_event_get_user_data(e)); }, LV_EVENT_DELETE, f);
}

void icon_color(lv_obj_t* img, uint32_t color) { lv_obj_set_style_image_recolor(img, lv_color_hex(color), 0); }

void on_click(lv_obj_t* o, lv_event_cb_t cb, void* user) {
    lv_obj_add_flag(o, LV_OBJ_FLAG_CLICKABLE);
    lv_obj_add_event_cb(o, cb, LV_EVENT_CLICKED, user);
}

}  // namespace reactor::hmi
