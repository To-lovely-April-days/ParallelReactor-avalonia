// 压力单位换算。C#: Models/Units.cs
// 压力内部一律 psi（表压）存储与判定，只在显示时换算。C# 里显示单位是全局静态状态；
// core 里不放全局状态，显示单位由调用方（设置的运行副本）作为参数传入。
#pragma once
#include <string>

namespace reactor::core {

enum class PressureUnit { MPa, Bar, Psi };

namespace units {
constexpr double kPsiPerBar = 14.5037738;   // C#: Units.PsiPerBar
constexpr double kPsiPerMpa = 145.037738;   // C#: Units.PsiPerMpa（1 MPa = 145.0377 psi）
}  // namespace units

// "psi" / "bar" → 对应单位，其他（含空）→ MPa（C#: Units.SetPressure 的归一化）
PressureUnit parse_pressure_unit(const std::string& s);
// "MPa" / "bar" / "psi"
const char* pressure_label(PressureUnit u);
// 小数位：MPa 3、bar 2、psi 1
int pressure_decimals(PressureUnit u);
// psi → 显示单位数值
double psi_to_unit(double psi, PressureUnit u);
// 显示单位数值 → psi
double unit_to_psi(double v, PressureUnit u);
// psi → 显示单位并按小数位格式化（不带单位），如 "0.345" / "3.45" / "50.0"
std::string p_value(double psi, PressureUnit u);
// 带单位，如 "0.345 MPa"
std::string format_p(double psi, PressureUnit u);
// 压降速率「0.0014 MPa/h」「0.10 bar/h」「2.00 psi/h」（比压力多一位小数）
std::string format_rate(double psi_per_h, PressureUnit u);
double mpa_to_psi(double mpa);
double psi_to_mpa(double psi);

}  // namespace reactor::core
