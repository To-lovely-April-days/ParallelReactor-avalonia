using System;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Format;

/// <summary>
/// 界面上压力的显示单位与格式化 —— <b>全界面只有这一处</b>：读数、表格、曲线轴、步骤文字、条件框、提示、输入框都经这里换算和格式化。
/// 单位由设置 → 常规「压力单位」决定（MPa / bar / psi，存在本机设置里；App 的 PrefsStore 改了就 <see cref="Set"/>，界面跟着刷新）。
/// 输入一律是电脑端的内部单位 MPa（换算见 <see cref="PressureUnits"/>）。
/// 小数位：MPa 按型号（量程 ≥ 5 MPa 用 2 位，否则 3 位，同样稿 PR.sim.fmtP）或调用处给的位数（表格 2 位）—— 与改单位之前逐字节相同；
/// bar 一律 2 位；psi 一律 1 位。演示数据里的原文（报警 / 事件句子、程序说明）是数据，不经这里改写。
/// 取整后是 0 的负读数（真机静置时传感器的零点漂移，例如 −0.0004 MPa）写成「0.000」，不写「-0.000」；「-0.001」这类不是 0 的照写（触屏也这样写）。
/// </summary>
public static class PressureDisplay
{
    /// <summary>当前的显示单位（默认 MPa）。</summary>
    public static PressureUnit Primary { get; private set; } = PressureUnit.MPa;

    /// <summary>单位每改一次加 1（界面缓存的键里带上它，改单位后缓存失效）。</summary>
    public static int Version { get; private set; }

    /// <summary>单位变了（界面订阅后刷新压力文字）。</summary>
    public static event Action? Changed;

    /// <summary>改显示单位；变了返回 true 并发 <see cref="Changed"/>。</summary>
    public static bool Set(PressureUnit unit)
    {
        if (unit == Primary) return false;
        Primary = unit;
        Version++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>按设置里的键改（'MPa' / 'bar' / 'psi'，认不出的按 MPa）。</summary>
    public static bool Set(string? key) => Set(PressureUnits.Parse(key));

    /// <summary>次单位（属性卡片 / PT 标注的第二行）：MPa、bar 为主时是 psi；psi 为主时是 MPa。</summary>
    public static PressureUnit Secondary => Primary == PressureUnit.Psi ? PressureUnit.MPa : PressureUnit.Psi;

    /// <summary>当前单位的写法：'MPa' / 'bar' / 'psi'。</summary>
    public static string Unit => UnitText(Primary);

    /// <summary>次单位的写法。</summary>
    public static string SecondaryUnit => UnitText(Secondary);

    public static string UnitText(PressureUnit unit) => PressureUnits.Text(unit);

    /// <summary>「名字 + 空格 + 单位」：'压力 MPa' / '压力 psi'（表头、轴名）。</summary>
    public static string Label(string name) => name + " " + Unit;

    /// <summary>「名字 (单位)」：'压力 (MPa)'（曲线左上角的说明）。</summary>
    public static string LabelParen(string name) => name + " (" + Unit + ")";

    // ———————————————————— 换算 ————————————————————

    /// <summary>MPa → 当前单位。</summary>
    public static double ToUnit(double mpa) => PressureUnits.FromMpa(mpa, Primary);

    /// <summary>MPa → 当前单位（null 照样 null）。</summary>
    public static double? ToUnit(double? mpa) => PressureUnits.FromMpa(mpa, Primary);

    /// <summary>当前单位 → MPa（输入框的值换回内部单位）。</summary>
    public static double FromUnit(double v) => PressureUnits.ToMpa(v, Primary);

    /// <summary>当前单位的读数小数位（MPa 用 <paramref name="mpaDecimals"/>，bar 2、psi 1）。</summary>
    public static int Decimals(int mpaDecimals) => PressureUnits.ValueDecimals(Primary, mpaDecimals);

    /// <summary>按型号的读数小数位（型号未知时 MPa 3 位）。</summary>
    public static int Decimals(ModelProfile? model) => Decimals(model?.PressureDecimals ?? 3);

    /// <summary>当前单位输入框的小数位（MPa 2、bar 1、psi 0）。</summary>
    public static int InputDecimals => PressureUnits.InputDecimals(Primary);

    // ———————————————————— 格式化 ————————————————————

    /// <summary>按型号格式化（不带单位）：'0.350' / '5.20'（MPa）、'3.50'（bar）、'50.8'（psi）。null → '—'。</summary>
    public static string Value(ModelProfile? model, double? mpa) => Format(Primary, model, mpa);

    /// <summary>按型号格式化 + 单位：'0.350 MPa'。null → '—'。</summary>
    public static string WithUnit(ModelProfile? model, double? mpa) => FormatU(Primary, model, mpa);

    /// <summary>次单位的数值（不带单位）。</summary>
    public static string SecondaryValue(ModelProfile? model, double? mpa) => Format(Secondary, model, mpa);

    /// <summary>次单位的数值 + 单位：'50.8 psi'。</summary>
    public static string SecondaryWithUnit(ModelProfile? model, double? mpa) => FormatU(Secondary, model, mpa);

    /// <summary>指定单位格式化（不带单位）：MPa 按型号小数位，bar 2 位，psi 1 位。</summary>
    public static string Format(PressureUnit unit, ModelProfile? model, double? mpa) => unit switch
    {
        PressureUnit.MPa => NoNegativeZero(Units.FmtP(model, mpa)),
        _ => Fixed(unit, mpa, model?.PressureDecimals ?? 3),
    };

    /// <summary>指定单位格式化 + 单位；null → '—'。</summary>
    public static string FormatU(PressureUnit unit, ModelProfile? model, double? mpa)
    {
        string v = Format(unit, model, mpa);
        return v == Fmt.Dash ? v : v + " " + UnitText(unit);
    }

    /// <summary>
    /// 固定小数位的读数（不带单位）：MPa 写 <paramref name="mpaDecimals"/> 位（运行表 / 报警弹窗的「0.00」），bar 2 位、psi 1 位。null / NaN → '—'。
    /// </summary>
    public static string Fixed(double? mpa, int mpaDecimals) => Fixed(Primary, mpa, mpaDecimals);

    /// <summary>固定小数位的读数 + 单位：'3.12 MPa' / '31.20 bar' / '452.5 psi'。null → '—'。</summary>
    public static string FixedU(double? mpa, int mpaDecimals)
    {
        string v = Fixed(mpa, mpaDecimals);
        return v == Fmt.Dash ? v : v + " " + Unit;
    }

    /// <summary>指定单位、固定小数位（不带单位）。</summary>
    public static string Fixed(PressureUnit unit, double? mpa, int mpaDecimals) =>
        mpa is double v && double.IsFinite(v)
            ? NoNegativeZero(JsNumber.ToFixed(PressureUnits.FromMpa(v, unit), PressureUnits.ValueDecimals(unit, mpaDecimals)))
            : Fmt.Dash;

    /// <summary>当前单位、给定小数位（不按单位改位数；曲线刻度、统计这类调用处自己定位数的）。</summary>
    public static string Raw(double? mpa, int decimals) =>
        mpa is double v && double.IsFinite(v) ? NoNegativeZero(JsNumber.ToFixed(ToUnit(v), decimals)) : Fmt.Dash;

    /// <summary>程序参数（输入框）的写法，按输入小数位：'0.35'（MPa）/ '3.5'（bar）/ '51'（psi）。NaN → '—'。</summary>
    public static string Input(double mpa) => double.IsFinite(mpa) ? JsNumber.ToFixed(ToUnit(mpa), InputDecimals) : Fmt.Dash;

    /// <summary>程序参数 + 单位：'0.35 MPa' / '3.5 bar' / '51 psi'。</summary>
    public static string InputU(double mpa) => double.IsFinite(mpa) ? Input(mpa) + " " + Unit : Fmt.Dash;

    /// <summary>量程 / 上限的写法（不补尾零）：'4.14'（MPa，同 JS String(number)）/ '41.4'（bar）/ '600'（psi）。</summary>
    public static string Limit(double mpa) => PressureUnits.LimitText(mpa, Primary);

    /// <summary>量程 / 上限 + 单位：'4.14 MPa' / '41.4 bar' / '600 psi'。</summary>
    public static string LimitU(double mpa) => Limit(mpa) + " " + Unit;

    /// <summary>「-0.000」「-0.00」「-0.0」→ 去掉负号（JS toFixed 对很小的负数保留负号，Fmt 照样稿逐字节保留；压力读数在这里去掉）。</summary>
    private static string NoNegativeZero(string s) =>
        s.Length > 1 && s[0] == '-' && s.AsSpan(1).IndexOfAnyExcept('0', '.') < 0 ? s[1..] : s;
}
