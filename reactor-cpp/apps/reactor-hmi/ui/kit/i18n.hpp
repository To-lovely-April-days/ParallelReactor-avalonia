// 文案：界面里只写中文原文，经 tr() 查表换成当前语言。第一版中文、英文完整；
// 其余 6 种（日、韩、德、法、西、俄）还没有译文，查不到的条目显示英文（语言弹窗里标「未齐」）。
#pragma once
#include <functional>
#include <string>

namespace reactor::hmi {

enum class Lang { Zh, En, Ja, Ko, De, Fr, Es, Ru };

void set_lang(Lang l);
Lang lang();
const char* lang_code(Lang l);   // "zh" "en" …（PUT /system/language 用）
bool lang_complete(Lang l);      // 译文是否齐全

// 中文原文 → 当前语言。查不到就原样返回（中文界面）或英文（其余语言）。
const char* tr(const char* zh);
// 同一个中文在不同地方要不同译法时，原文后面加「 (说明)」：中文界面只显示说明前的部分，
// 英文按整串查表。例：trc("曲线 (升温方式)") → 中文「曲线」/ 英文「Curve」。
const char* trc(const char* zh_with_ctx);
// printf 风格：格式串本身也经过 tr()
std::string trf(const char* zh_fmt, ...);

}  // namespace reactor::hmi
