using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Text;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Text.Keep 对照样稿 PR.nbsp / PR.keep（js/core.js）：Nbsp / Web 与样稿逐字节相同（手写例子 + 样稿自己产生的 400 多条报警 / 事件 / 原因 /
/// 步骤文字和正则边界）；Apply 只在 Web 的基础上插 U+2060（代替 CSS keep-all）。
/// 期望值里用「_」表示 U+00A0、「^」表示 U+2060（源文件里看得见）。
/// </summary>
public class KeepTests
{
    private static string V(string s) => s.Replace('_', Keep.NoBreakSpace).Replace("^", Keep.WordJoiner.ToString());

    [Theory]
    [InlineData("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", "RV11 超压 3.12_MPa ≥ 3.00_MPa · 已关闭 SV11")]
    [InlineData("RV3 升温较慢 · 已用 18 min", "RV3 升温较慢 · 已用 18_min")]
    [InlineData("120 ℃ · 0.350 MPa · 气体 A", "120_℃ · 0.350_MPa · 气体 A")]
    [InlineData("第 3/4 步 · 剩 42 min", "第 3/4 步 · 剩 42_min")]
    [InlineData("18 mins", "18 mins")]   // 单位后面紧跟字母：不是单位
    [InlineData("5 hPa", "5 hPa")]
    [InlineData("", "")]
    public void Nbsp_MatchesMock(string input, string expected) => Assert.Equal(V(expected), Keep.Nbsp(input));

    [Theory]
    [InlineData("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", "RV11 超压_3.12_MPa_≥_3.00_MPa_· 已关闭_SV11")]
    [InlineData("RV3 升温较慢 · 已用 18 min", "RV3 升温较慢_· 已用_18_min")]
    [InlineData("温度 ≥ 119.5 ℃", "温度_≥_119.5_℃")]
    [InlineData("… · ≤ 200 ℃", "…_· ≤_200_℃")]          // 点后面照样能断，运算符与后面的数不断开
    [InlineData("只急停1号机 · 复位在触屏", "只急停1号机_· 复位在触屏")]
    [InlineData("第 3/4 步 · 剩 42 min", "第_3/4 步_· 剩_42_min")]
    [InlineData("120 ℃ · 0.350 MPa · 气体 A", "120_℃_· 0.350_MPa_· 气体_A")]
    [InlineData("18 mins", "18 mins")]
    [InlineData("5 hPa", "5 hPa")]
    [InlineData("a  ≥ b", "a  ≥ b")]                      // 两个空格：样稿的正则不管
    public void Web_MatchesMock(string input, string expected) => Assert.Equal(V(expected), Keep.Web(input));

    [Theory]
    [InlineData("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", "RV11 超^压_3.12_MPa_≥_3.00_MPa_· 已^关^闭_SV11")]
    [InlineData("只急停1号机 · 复位在触屏", "只^急^停^1^号^机_· 复^位^在^触^屏")]
    [InlineData("RV3 程序「加氢-标准」开始", "RV3 程^序「加^氢-标^准」开^始")]   // 标点、连字符处的断行机会保留
    [InlineData("高压加氢测试一号 · RV1 · 反应中", "高^压^加^氢^测^试^一^号_· RV1_· 反^应^中")]
    [InlineData("通入 H₂ 气", "通^入_H₂ 气")]
    [InlineData("第3步", "第^3^步")]
    [InlineData("（RV3）升温", "（RV3）升^温")]
    [InlineData("ReactorLab-PR8-A · RV16", "ReactorLab-PR8-A_· RV16")]
    [InlineData("1号机", "1^号^机")]
    public void Apply_JoinsCjkWords(string input, string expected) => Assert.Equal(V(expected), Keep.Apply(input));

    [Fact]
    public void JoinCjk_SurrogatesAndLoneSurrogates()
    {
        string ext = char.ConvertFromUtf32(0x20000) + char.ConvertFromUtf32(0x20001);   // 扩展 B 区两个字
        Assert.Equal(char.ConvertFromUtf32(0x20000) + Keep.WordJoiner + char.ConvertFromUtf32(0x20001), Keep.JoinCjk(ext));
        string lone = "中" + (char)0xD800 + "文";                                        // 孤立代理项原样保留，不当成汉字
        Assert.Equal(lone, Keep.JoinCjk(lone));
        Assert.Equal("", Keep.JoinCjk(null));
        Assert.Equal("", Keep.JoinCjk(""));
        Assert.Equal("abc 123", Keep.JoinCjk("abc 123"));
    }

    [Fact]
    public void Null_GivesEmpty()
    {
        Assert.Equal("", Keep.Nbsp(null));
        Assert.Equal("", Keep.Web(null));
        Assert.Equal("", Keep.Apply(null));
        Assert.Equal("", Keep.Plain(null));
    }

    [Fact]
    public void Plain_UndoesKeep()
    {
        const string s = "RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11";
        Assert.Equal(s, Keep.Plain(Keep.Apply(s)));
        Assert.Equal(s, Keep.Plain(Keep.Web(s)));
    }

    /// <summary>样稿在 JavaScriptCore 里对 400 多条文字的 PR.nbsp / PR.keep 结果。</summary>
    [Fact]
    public void MatchesMock_Corpus()
    {
        var bad = new List<string>();
        int n = 0;
        string wj = Keep.WordJoiner.ToString();
        foreach (var row in MockVectorData.Get("keep").EnumerateArray())
        {
            string? input = row[0].GetString();
            string nbsp = row[1].GetString()!, keep = row[2].GetString()!;
            string gotNbsp = Keep.Nbsp(input), gotWeb = Keep.Web(input), gotApply = Keep.Apply(input);
            if (gotNbsp != nbsp) bad.Add("Nbsp(" + Show(input) + ") = " + Show(gotNbsp) + "，样稿 " + Show(nbsp));
            if (gotWeb != keep) bad.Add("Web(" + Show(input) + ") = " + Show(gotWeb) + "，样稿 " + Show(keep));
            if (gotApply.Replace(wj, "") != keep) bad.Add("Apply 除 U+2060 外与样稿不同：" + Show(input));
            if (Keep.Apply(gotApply) != gotApply) bad.Add("Apply 不幂等：" + Show(input));
            // WJ 只插在两个字母单位之间：不会挨着空格 / NBSP，也不在首尾（StartsWith(string) 按区域比较、会忽略 U+2060，所以全用序数比较）
            if (gotApply.StartsWith(wj, StringComparison.Ordinal) || gotApply.EndsWith(wj, StringComparison.Ordinal)
                || gotApply.Contains(" " + wj, StringComparison.Ordinal) || gotApply.Contains(wj + " ", StringComparison.Ordinal)
                || gotApply.Contains(Keep.NoBreakSpace + wj, StringComparison.Ordinal) || gotApply.Contains(wj + Keep.NoBreakSpace, StringComparison.Ordinal))
                bad.Add("U+2060 位置不对：" + Show(gotApply));
            n++;
        }
        Assert.True(n > 300, "语料太少：" + n);
        Assert.True(bad.Count == 0, bad.Count + " 条问题：\n" + string.Join("\n", bad.Take(30)));
    }

    /// <summary>中文词内部（两个汉字之间）在 Apply 之后一定没有断行机会：每对相邻汉字之间都有 WJ。</summary>
    [Fact]
    public void Apply_EveryCjkPairIsJoined()
    {
        foreach (var row in MockVectorData.Get("keep").EnumerateArray())
        {
            string got = Keep.Apply(row[0].GetString());
            for (int i = 0; i + 1 < got.Length; i++)
                Assert.False(IsHan(got[i]) && IsHan(got[i + 1]), "汉字之间缺 U+2060：" + Show(got));
        }
    }

    /// <summary>
    /// 整段不断开（设备页进气阀列表的每一项「SV1 开」「开 SV1–SV6」「全部关闭」）：每两个字之间 U+2060、空格换成 U+00A0，代理对中间不插；
    /// Plain 还原原文；null / 空 → ""。界面上由 App 的 UiText.Joiners 换成当前字体能显示的连接符（UiText.NoBreak = Joiners(Keep.NoBreak)）。
    /// </summary>
    [Fact]
    public void NoBreak_JoinsEveryPair_NbspForSpaces()
    {
        Assert.Equal(V("S^V^1^_^开"), Keep.NoBreak("SV1 开"));
        Assert.Equal(V("开^_^S^V^1^–^S^V^6"), Keep.NoBreak("开 SV1–SV6"));
        Assert.Equal(V("全^部^关^闭"), Keep.NoBreak("全部关闭"));
        Assert.Equal("A" + Keep.WordJoiner + "\U0001F600", Keep.NoBreak("A\U0001F600"));
        Assert.Equal("", Keep.NoBreak(null));
        Assert.Equal("", Keep.NoBreak(""));
        Assert.Equal("x", Keep.NoBreak("x"));
        foreach (var s in new[] { "SV1 开", "开 SV1–SV6、SV9–SV10", "1号机触屏", "ReactorLab-PR8-A" }) Assert.Equal(s, Keep.Plain(Keep.NoBreak(s)));
    }

    private static bool IsHan(char c) => c >= (char)0x4E00 && c <= (char)0x9FFF;

    private static string Show(string? s) => s is null ? "null" : "\"" + s.Replace(Keep.NoBreakSpace, '_').Replace(Keep.WordJoiner, '^') + "\"";
}
