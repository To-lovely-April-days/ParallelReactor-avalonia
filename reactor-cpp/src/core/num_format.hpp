// 数字格式化：模仿 C# 自定义格式串（InvariantCulture）在文案里的效果，保证中文文案里的数值逐字一致。
//   "0"      → fmt_dec(v, 0, 0)
//   "0.#"    → fmt_dec(v, 1, 0)      "0.##" → fmt_dec(v, 2, 0)      "0.###" → fmt_dec(v, 3, 0)
//   "0.0"    → fmt_dec(v, 1, 1)      "0.00" → fmt_dec(v, 2, 2)      "0.0000" → fmt_dec(v, 4, 4)
// 和 .NET 一样：先取 15 位有效数字，再按「四舍五入（远离零）」舍到 max_dec 位小数，去掉多余的尾零（保留 min_dec 位）。
// 结果为 0 时不带负号。NaN → "NaN"，±∞ → "∞" / "-∞"（与 .NET InvariantCulture 相同）。
#pragma once
#include <string>

namespace reactor::core {

std::string fmt_dec(double v, int max_dec, int min_dec = 0);

}  // namespace reactor::core
