// CSV 基础格式化（C#: Service/TelemetryCsv.cs 的 Csv 类）。纯函数，任意线程可调。
// - 字段含 逗号 / 双引号 / 回车 / 换行 时整体加引号、内部双引号加倍（RFC 4180）；
// - 以 = + - @ 开头的非数字文本前加 '（防 Excel 把标记 / 详情当公式）；
// - 数值一律用「.」作小数点；null / NaN / ∞ 输出空单元格（不臆造数据）。
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "store/store_util.hpp"

namespace reactor::store::csv {

std::string escape(const std::string& s);

// .NET 自定义数值格式的子集："0.###"、"0.0##"、"0.00000"…（整数部分一个 0，小数部分 0 = 必有、# = 可选）。
// 和 .NET Core 3.0+ 一致：先取 15 位有效数字，再按十进制四舍五入（远离零）；结果为 0 时不带负号。
std::string net_format(double v, const char* fmt);

std::string num(std::optional<double> v, const char* fmt = "0.###");
std::string integer(std::optional<int64_t> v);
std::string boolean(std::optional<bool> v);
std::string time(const std::optional<WallTime>& t);

// 把已格式化（尚未转义）的字段拼成一行（不含换行符）
std::string line(const std::vector<std::string>& fields);

// RFC 4180 风格拆分一行（C#: HistoryFiles.SplitCsv）
std::vector<std::string> split(const std::string& line);

// double.TryParse(s, NumberStyles.Float, InvariantCulture) 能否成功（防注入判断用）
bool looks_like_number(const std::string& s);

}  // namespace reactor::store::csv
