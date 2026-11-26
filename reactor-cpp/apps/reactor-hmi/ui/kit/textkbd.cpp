// 文字键盘（Kbd-Text）：输入框 + 拼音候选行 + 4 排键（大写 / 退格 / 123 / 中 / 空格 / - / . / 确定）。
// 拼音只带了一张小词表（程序名里常用的字），完整输入法要么接 LVGL 的 lv_ime_pinyin（需要在
// lv_conf.h 打开 LV_USE_IME_PINYIN），要么接一份完整词库 —— 见模块报告。
#include "i18n.hpp"
#include "widgets.hpp"

#include <map>

namespace reactor::hmi::kit {

namespace {

const std::map<std::string, const char*>& pinyin() {
    static const std::map<std::string, const char*> m = {
        {"qing", "氢清轻青请情庆晴"}, {"jia", "加家甲价假架"}, {"shai", "筛晒"}, {"xuan", "选旋宣"}, {"cheng", "程成称承"},
        {"xu", "序需续"}, {"ce", "测侧策"}, {"shi", "试实时室式"}, {"fan", "反范返"}, {"ying", "应硬"},
        {"wen", "温文稳"}, {"du", "度"}, {"ya", "压"}, {"li", "力理"}, {"qi", "气器起"}, {"ti", "体题"},
        {"zhi", "值置制"}, {"huan", "换环"}, {"ban", "半板版"}, {"bao", "保"}, {"chi", "持"}, {"sheng", "升"},
        {"jiang", "降"}, {"cui", "催"}, {"hua", "化"}, {"ji", "剂级"}, {"yang", "样氧"}, {"pin", "品"},
        {"zu", "组"}, {"di", "第低"}, {"gao", "高"}, {"xin", "新"}, {"jiu", "旧"},
        {"a", "啊"}, {"b", "不"}, {"c", "从"}, {"d", "的"}, {"l", "了"}, {"s", "是"}, {"z", "在"},
    };
    return m;
}

struct Kb {
    lv_obj_t* dim = nullptr;
    lv_obj_t* text = nullptr; lv_obj_t* cursor = nullptr; lv_obj_t* count = nullptr;
    lv_obj_t* py = nullptr; lv_obj_t* cands = nullptr;
    std::vector<lv_obj_t*> letter_keys;
    std::vector<std::string> letters;
    lv_obj_t* mode_lbl = nullptr; lv_obj_t* shift_lbl = nullptr;
    std::string value, pinyin;
    int max_len = 30;
    bool zh = true, upper = false, digits = false;
    bool password = false, reveal = false;
    lv_obj_t* eye = nullptr;
    std::function<void(const std::string&)> on_ok;
};
Kb* g_kb = nullptr;

int u8len(const std::string& s) {
    int n = 0;
    for (unsigned char c : s) n += (c & 0xC0) != 0x80;
    return n;
}
void u8pop(std::string& s) {
    while (!s.empty()) {
        unsigned char c = (unsigned char)s.back();
        s.pop_back();
        if ((c & 0xC0) != 0x80) break;
    }
}

void render();

void put(const std::string& s) {
    if (u8len(g_kb->value) + u8len(s) > g_kb->max_len) return;
    g_kb->value += s;
}

void render_cands() {
    Kb& k = *g_kb;
    lv_obj_clean(k.cands);
    lv_label_set_text(k.py, k.pinyin.c_str());
    auto it = pinyin().find(k.pinyin);
    if (k.pinyin.empty() || it == pinyin().end()) return;
    std::string all = it->second;
    int x = 0;
    for (size_t i = 0; i < all.size() && x < 8 * 68;) {
        size_t n = 1;
        unsigned char c = (unsigned char)all[i];
        if (c >= 0xF0) n = 4; else if (c >= 0xE0) n = 3; else if (c >= 0xC0) n = 2;
        std::string ch = all.substr(i, n);
        i += n;
        lv_obj_t* b = box(k.cands, x, 0, 58, 46, 0xffffff, 8);
        ring(b, 0xd5dde6, 2);
        lv_obj_t* l = label(b, ch.c_str(), 20, 400, col::INK);
        lv_obj_center(l);
        on_tap(b, [ch] {
            put(ch);
            g_kb->pinyin.clear();
            render();
        });
        x += 68;
    }
}

void render() {
    Kb& k = *g_kb;
    if (k.password && !k.reveal) {
        std::string dots;
        for (int i = 0; i < u8len(k.value); ++i) dots += "●";
        lv_label_set_text(k.text, dots.c_str());
    } else {
        lv_label_set_text(k.text, k.value.c_str());
    }
    lv_obj_update_layout(k.text);
    lv_obj_set_x(k.cursor, lv_obj_get_x(k.text) + lv_obj_get_width(k.text) + 2);
    if (!k.password) lv_label_set_text_fmt(k.count, "%d / %d", u8len(k.value), k.max_len);
    lv_label_set_text(k.mode_lbl, k.zh ? "中" : "英");
    for (size_t i = 0; i < k.letter_keys.size(); ++i) {
        std::string s = k.letters[i];
        if (k.upper && s.size() == 1 && s[0] >= 'a' && s[0] <= 'z') s[0] = (char)(s[0] - 'a' + 'A');
        lv_label_set_text(lv_obj_get_child(k.letter_keys[i], 0), s.c_str());
    }
    render_cands();
}

void letter(size_t i) {
    Kb& k = *g_kb;
    std::string s = k.letters[i];
    if (k.zh && !k.digits && s.size() == 1 && s[0] >= 'a' && s[0] <= 'z') {
        if (k.pinyin.size() < 8) k.pinyin += s;
    } else {
        if (k.upper && s.size() == 1 && s[0] >= 'a' && s[0] <= 'z') s[0] = (char)(s[0] - 'a' + 'A');
        put(s);
    }
    render();
}

void set_layout() {
    Kb& k = *g_kb;
    static const char* L[] = {"q", "w", "e", "r", "t", "y", "u", "i", "o", "p", "a", "s", "d", "f", "g", "h", "j", "k", "l",
                              "z", "x", "c", "v", "b", "n", "m"};
    static const char* D[] = {"1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "(", ")", "/", "_", "%", "+", "#", ":", "~",
                              "°", "×", "@", "&", "!", "?", ","};
    k.letters.clear();
    for (int i = 0; i < 26; ++i) k.letters.push_back(k.digits ? D[i] : L[i]);
}

lv_obj_t* key(lv_obj_t* pan, int x, int y, int w, const char* t, double px, int wt = 500) {
    lv_obj_t* b = box(pan, x, y, w, 60, 0xeaf1f9, 10);
    lv_obj_set_style_bg_color(b, lv_color_hex(0xd5e3f2), LV_STATE_PRESSED);
    lv_obj_t* l = label(b, t, px, wt, col::INK);
    lv_obj_center(l);
    return b;
}

}  // namespace

void TextKbd::close() {
    if (!g_kb) return;
    lv_obj_delete(g_kb->dim);
    delete g_kb;
    g_kb = nullptr;
}

void TextKbd::open(const std::string& title, const std::string& init, int max_len, std::function<void(const std::string&)> on_ok) {
    open(title, init, max_len, TextKbdOpts{}, std::move(on_ok));
}

void TextKbd::open(const std::string& title, const std::string& init, int max_len, const TextKbdOpts& opts,
                   std::function<void(const std::string&)> on_ok) {
    close();
    g_kb = new Kb;
    Kb& k = *g_kb;
    k.value = init; k.max_len = max_len; k.on_ok = std::move(on_ok);
    k.password = opts.password;
    if (k.password) k.zh = false;
    const int dy = k.password ? -32 : 0;  // 密码：没有拼音候选行，键往上挪
    set_layout();
    k.dim = modal_dim();
    int W = lv_obj_get_width(k.dim), H = lv_obj_get_height(k.dim);
    int PH = 479 + dy;
    lv_obj_t* pan = box(k.dim, (W - 1000) / 2, (H - PH) / 2, 1000, PH, 0xffffff, 20);
    pop_shadow(pan, 60, 24, 90);
    lv_obj_add_flag(pan, LV_OBJ_FLAG_CLICKABLE);
    label_at(pan, 30, 26, title.c_str(), 15, 400, col::SEC);
    lv_obj_t* in = box(pan, 30, 57, 940, 60, 0xffffff, 12);
    ring(in, col::BLUE, 2);
    k.text = label_at(in, 16, 10, "", 26, 700, col::DEEP);
    k.cursor = box(in, 18, 13, 2, 30, col::BLUE);
    k.count = label(in, "", 13, 400, col::MUT);
    lv_obj_align(k.count, LV_ALIGN_RIGHT_MID, -16, 0);
    if (k.password) {
        lv_obj_add_flag(k.count, LV_OBJ_FLAG_HIDDEN);
        k.eye = box(in, 940 - 90, 10, 80, 40, 0, 0, true);
        icon(k.eye, &ic::eye18, col::BLUE, 0, 11);
        label_at(k.eye, 24, 9, tr("显示"), 15, 600, col::BLUE);
        on_tap(k.eye, [] { g_kb->reveal = !g_kb->reveal; render(); });
    }
    if (!opts.hint.empty()) label_at(pan, 30, 126, opts.hint.c_str(), 13.5, 400, col::MUT);
    k.py = label_at(pan, 30, 141, "", 18, 700, col::BLUE);
    k.cands = box(pan, 110, 131, 8 * 68, 46, 0, 0, true);
    if (!k.password) {
        lv_obj_t* prev = box(pan, 874, 132, 44, 44, 0xeaf1f9, 8);
        lv_obj_center(icon(prev, &ic::chev_left18, col::SEC));
        lv_obj_t* next = box(pan, 926, 132, 44, 44, 0xeaf1f9, 8);
        lv_obj_center(icon(next, &ic::chev_right18, col::SEC));
    } else {
        lv_obj_add_flag(k.py, LV_OBJ_FLAG_HIDDEN);
        lv_obj_add_flag(k.cands, LV_OBJ_FLAG_HIDDEN);
    }
    // 第 1 排 10 个，第 2 排 9 个，第 3 排 大写 + 7 + 退格，第 4 排 123 / 中 / 空格 / - / . / 确定
    size_t li = 0;
    for (int i = 0; i < 10; ++i, ++li) {
        lv_obj_t* b = key(pan, 30 + (int)std::lround(i * 94.8), 189 + dy, 87, "", 19);
        k.letter_keys.push_back(b);
        size_t idx = li;
        on_tap(b, [idx] { letter(idx); });
    }
    for (int i = 0; i < 9; ++i, ++li) {
        lv_obj_t* b = key(pan, 68 + i * 97, 257 + dy, 89, "", 19);
        k.letter_keys.push_back(b);
        size_t idx = li;
        on_tap(b, [idx] { letter(idx); });
    }
    lv_obj_t* sh = key(pan, 30, 325 + dy, 137, tr("大写"), 16);
    k.shift_lbl = lv_obj_get_child(sh, 0);
    on_tap(sh, [] { g_kb->upper = !g_kb->upper; render(); });
    for (int i = 0; i < 7; ++i, ++li) {
        lv_obj_t* b = key(pan, 175 + i * 94, 325 + dy, 86, "", 19);
        k.letter_keys.push_back(b);
        size_t idx = li;
        on_tap(b, [idx] { letter(idx); });
    }
    on_tap(key(pan, 833, 325 + dy, 137, tr("退格"), 16), [] {
        Kb& q = *g_kb;
        if (!q.pinyin.empty()) q.pinyin.pop_back();
        else u8pop(q.value);
        render();
    });
    on_tap(key(pan, 30, 393 + dy, 126, "123", 16), [] {
        g_kb->digits = !g_kb->digits;
        g_kb->pinyin.clear();
        set_layout();
        render();
    });
    lv_obj_t* zh = key(pan, 164, 393 + dy, 126, "中", 16);
    k.mode_lbl = lv_obj_get_child(zh, 0);
    on_tap(zh, [] { g_kb->zh = !g_kb->zh; g_kb->pinyin.clear(); render(); });
    on_tap(key(pan, 298, 393 + dy, 270, tr("空格"), 16), [] {
        Kb& q = *g_kb;
        if (!q.pinyin.empty()) {  // 空格 = 选第一个候选
            auto it = pinyin().find(q.pinyin);
            if (it != pinyin().end()) {
                std::string a = it->second;
                put(a.substr(0, 3));
            }
            q.pinyin.clear();
        } else {
            put(" ");
        }
        render();
    });
    on_tap(key(pan, 576, 393 + dy, 90, "-", 19), [] { put("-"); render(); });
    on_tap(key(pan, 674, 393 + dy, 90, ".", 19), [] { put("."); render(); });
    lv_obj_t* ok = box(pan, 772, 393 + dy, 198, 60, col::BLUE, 10);
    lv_obj_center(label(ok, opts.ok_text.empty() ? tr("确定") : opts.ok_text.c_str(), 18, 700, 0xffffff));
    on_tap(ok, [] {
        auto cb = g_kb->on_ok;
        std::string v = g_kb->value;
        TextKbd::close();
        if (cb) cb(v);
    });
    lv_obj_t* cl = box(pan, 938, 18, 44, 44, 0xeef1f5, 22);
    lv_obj_center(icon(cl, &ic::close20, col::SEC));
    on_tap(cl, [] { TextKbd::close(); });
    if (!init.empty() && init.back() == '-') {
        // 预览：画布上正在打「qing」
        k.pinyin = "qing";
    }
    render();
}

}  // namespace reactor::hmi::kit
