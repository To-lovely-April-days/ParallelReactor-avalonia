using System;
using System.Globalization;
using System.Numerics;

namespace ParallelReactor.Control.Core.Format;

/// <summary>
/// JavaScript 数字转文字的精确移植：Number.prototype.toFixed、String(number)、Math.round。
/// 样稿 PR.fmt 依赖它们；.NET 的 ToString("F2") / Math.Round 在一半进位、负零、≥ 1e21 等处与 JS 不同，所以按 ECMAScript 规范自己实现。
/// </summary>
public static class JsNumber
{
    /// <summary>JS Math.round：最近的整数，正好一半时向 +∞ 取（2.5 → 3，-2.5 → -2）。JS 的 -0 这里统一成 +0（String(-0) 本来就是 "0"）。</summary>
    public static double Round(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return x;
        double f = Math.Floor(x);
        double r = x - f >= 0.5 ? f + 1 : f;   // x - floor(x) 对 double 是精确的
        return r == 0 ? 0 : r;
    }

    /// <summary>
    /// JS (x).toFixed(digits)：按 double 的精确十进制值取整（两个候选一样近时取大的），符号单独处理
    /// （-0.0004 → "-0.00"，-0 → "0.00"）；|x| ≥ 1e21 时同 String(x)；NaN / ±∞ 给 "NaN" / "Infinity"。
    /// </summary>
    public static string ToFixed(double x, int digits)
    {
        if (digits < 0 || digits > 100) throw new ArgumentOutOfRangeException(nameof(digits));
        if (double.IsNaN(x)) return "NaN";
        if (double.IsInfinity(x)) return x > 0 ? "Infinity" : "-Infinity";
        string sign = "";
        if (x < 0) { sign = "-"; x = -x; }
        if (x >= 1e21) return sign + ToString(x);
        string m = ScaledRoundHalfUp(x, digits).ToString(CultureInfo.InvariantCulture);
        if (digits > 0)
        {
            if (m.Length <= digits) m = new string('0', digits + 1 - m.Length) + m;
            m = m[..^digits] + "." + m[^digits..];
        }
        return sign + m;
    }

    /// <summary>
    /// JS String(x)（Number::toString）：最短往返的十进制数字，按 JS 的规则决定写法
    /// （1e21 → "1e+21"，1e-7 → "1e-7"，0.000001 → "0.000001"，123456789012345680000 照写，-0 → "0"）。
    /// </summary>
    public static string ToString(double x)
    {
        if (double.IsNaN(x)) return "NaN";
        if (x == 0) return "0";
        if (double.IsInfinity(x)) return x > 0 ? "Infinity" : "-Infinity";
        string sign = x < 0 ? "-" : "";
        // .NET Core 3.0 起 "R" 给出最短往返的数字（与 JS 选的相同），只是写法不同：拆成数字串 + 小数点位置，再按 JS 规则拼
        string r = Math.Abs(x).ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOf('E');
        int exp10 = e >= 0 ? int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        string mant = e >= 0 ? r[..e] : r;
        int dot = mant.IndexOf('.');
        string digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
        int n = (dot >= 0 ? dot : mant.Length) + exp10;   // x = 0.digits × 10^n
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; n--; }
        digits = digits[lead..].TrimEnd('0');
        int k = digits.Length;
        if (k <= n && n <= 21) return sign + digits + new string('0', n - k);
        if (0 < n && n <= 21) return sign + digits[..n] + "." + digits[n..];
        if (-6 < n && n <= 0) return sign + "0." + new string('0', -n) + digits;
        int ee = n - 1;
        return sign + (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + (ee < 0 ? "-" : "+")
            + Math.Abs(ee).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>x ≥ 0、有限：精确计算 floor(x × 10^digits + 1/2)（ECMAScript toFixed 第 10.a 步）。</summary>
    private static BigInteger ScaledRoundHalfUp(double x, int digits)
    {
        long bits = BitConverter.DoubleToInt64Bits(x);
        int exp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0 && frac == 0) return BigInteger.Zero;
        BigInteger mant = exp == 0 ? frac : frac | (1L << 52);
        int e2 = exp == 0 ? -1074 : exp - 1075;   // x = mant × 2^e2
        BigInteger num = mant * BigInteger.Pow(10, digits);
        if (e2 >= 0) return num << e2;
        BigInteger den = BigInteger.One << -e2;
        return ((num << 1) + den) / (den << 1);
    }
}
