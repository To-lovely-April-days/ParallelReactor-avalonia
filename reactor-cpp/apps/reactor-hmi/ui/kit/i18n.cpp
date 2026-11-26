#include "i18n.hpp"

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <string>
#include <unordered_map>

namespace reactor::hmi {

// i18n_en.cpp：中文原文 → 英文（第一批）；i18n_en2.cpp：第二批页面（程序 / 程序库 / 泄漏测试 / 曲线 / 记录 / 设置）
const std::unordered_map<std::string, const char*>& en_table();
const std::unordered_map<std::string, const char*>& en_table2();

namespace {
const char* en_find(const std::string& zh) {
    const auto& t = en_table();
    auto it = t.find(zh);
    if (it != t.end()) return it->second;
    const auto& t2 = en_table2();
    auto it2 = t2.find(zh);
    return it2 == t2.end() ? nullptr : it2->second;
}
}  // namespace

namespace {
Lang g_lang = Lang::Zh;
}

void set_lang(Lang l) { g_lang = l; }
Lang lang() { return g_lang; }

const char* lang_code(Lang l) {
    switch (l) {
        case Lang::Zh: return "zh";
        case Lang::En: return "en";
        case Lang::Ja: return "ja";
        case Lang::Ko: return "ko";
        case Lang::De: return "de";
        case Lang::Fr: return "fr";
        case Lang::Es: return "es";
        case Lang::Ru: return "ru";
    }
    return "zh";
}

bool lang_complete(Lang l) { return l == Lang::Zh || l == Lang::En; }

const char* tr(const char* zh) {
    if (g_lang == Lang::Zh || !zh) return zh;
    // 其余语言还没有译文：一律退回英文
    const char* en = en_find(zh);
    return en ? en : zh;
}

const char* trc(const char* s) {
    static std::unordered_map<std::string, std::string> stripped;
    const char* k = std::strstr(s, " (");
    if (!k) return tr(s);
    if (g_lang != Lang::Zh)
        if (const char* en = en_find(s)) return en;
    auto& v = stripped[s];
    if (v.empty()) v.assign(s, static_cast<size_t>(k - s));
    return g_lang == Lang::Zh ? v.c_str() : tr(v.c_str());
}

std::string trf(const char* zh_fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, zh_fmt);
    std::vsnprintf(buf, sizeof buf, tr(zh_fmt), ap);
    va_end(ap);
    return buf;
}

}  // namespace reactor::hmi
