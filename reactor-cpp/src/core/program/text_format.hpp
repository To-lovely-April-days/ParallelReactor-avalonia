// 程序模块用的文案格式化。
// 压力单位换算 / format_p / p_value 用 core/units.hpp（C#: Models/Units.cs），fmt_dec 用 core/num_format.hpp
// （模仿 C# 自定义格式串）。reactor::core::program 嵌在 reactor::core 里，本模块里不带前缀直接调用即可。
// 这里只补本模块额外需要的几个小件。
#pragma once
#include <string>

#include "core/num_format.hpp"
#include "core/units.hpp"

namespace reactor::core::program {

// StepSpecs.N / ProgramLimits.N：整数不带小数，其余最多 3 位（"0.###"）
std::string fmt_n(double v);

// "{i:00}"：至少两位，前补 0（步骤序号 01 / 02 …）
std::string fmt_00(int i);

// C# Math.Round(x)（默认 MidpointRounding.ToEven）
double round_even(double x);
// C# Math.Round(x, digits)（ToEven）
double round_even(double x, int digits);

}  // namespace reactor::core::program
