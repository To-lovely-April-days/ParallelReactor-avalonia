using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ParallelReactor.Control.Behaviors;

/// <summary>
/// 尺寸断点类（PORT-PLAN §4.8 第 5 层：容器查询的替代，只用于少数结构切换）：
/// <c>b:SizeClasses.Rules="fw-s: w &lt; 1600; fw-l: w &gt;= 1920; fh-s: h &lt; 860"</c> —— 控件按<b>自己的</b>尺寸（父元素排给它的大小）
/// 增减样式类。规则用分号分隔，每条「类名: w|h 运算符 数值」，运算符 &lt; &lt;= &gt; &gt;=。
/// 防振荡（R4）：刚切换的类如果 250 ms 内又要切回，先保持当前状态，等这一轮布局排完后按那时的尺寸再判一次（M1 复核：以前就此停住，
/// 尺寸只是短暂来回时会卡在旧类上）；延后复核本身又引起来回切换（类改变了控件自己的尺寸 = 真振荡）两次以后不再复核，保持现状并在 Debug 输出说明。
/// 判断用控件自己的尺寸（父元素排给它的大小）；读数字号这类连续变化不要用它，用 FitText。
/// </summary>
public static class SizeClasses
{
    public static readonly AttachedProperty<string?> RulesProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.Control, string?>("Rules", typeof(SizeClasses));

    public static string? GetRules(Avalonia.Controls.Control c) => c.GetValue(RulesProperty);
    public static void SetRules(Avalonia.Controls.Control c, string? value) => c.SetValue(RulesProperty, value);

    /// <summary>一条规则：尺寸满足条件时加上 Class。</summary>
    public readonly record struct Rule(string Class, bool Width, string Op, double Value)
    {
        public bool Matches(Size s)
        {
            var v = Width ? s.Width : s.Height;
            return Op switch { "<" => v < Value, "<=" => v <= Value, ">" => v > Value, ">=" => v >= Value, _ => false };
        }
    }

    /// <summary>切换后多久之内又要切回算振荡候选（ms）。</summary>
    public const double SettleMs = 250;

    private sealed class State
    {
        public Rule[] Rules = Array.Empty<Rule>();
        public HashSet<string> Applied = new(StringComparer.Ordinal);
        public HashSet<string>? Previous;
        public long LastChange;
        /// <summary>已安排了一次延后复核。</summary>
        public bool Pending;
        /// <summary>延后复核引起的连续切换次数（≥ 2 = 真振荡，不再复核）；安静 1 s 后的正常切换清零。</summary>
        public int Bounces;
    }

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<Avalonia.Controls.Control, State?>("SizeClassesState", typeof(SizeClasses));

    static SizeClasses()
    {
        RulesProperty.Changed.AddClassHandler<Avalonia.Controls.Control>((c, _) => Attach(c));
    }

    /// <summary>解析规则；格式不对的条目抛 FormatException（写错了要立刻看到，而不是悄悄不生效）。</summary>
    public static Rule[] Parse(string? spec)
    {
        var list = new List<Rule>();
        if (string.IsNullOrWhiteSpace(spec)) return list.ToArray();
        foreach (var part in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf(':');
            if (i <= 0) throw new FormatException($"SizeClasses 规则「{part}」缺少「类名:」");
            var cls = part[..i].Trim();
            var cond = part[(i + 1)..].Replace(" ", "");
            if (cond.Length < 3 || (cond[0] != 'w' && cond[0] != 'h')) throw new FormatException($"SizeClasses 规则「{part}」要以 w 或 h 开头");
            var op = cond.Length > 2 && cond[2] == '=' ? cond.Substring(1, 2) : cond.Substring(1, 1);
            if (op is not ("<" or "<=" or ">" or ">=")) throw new FormatException($"SizeClasses 规则「{part}」的运算符只能是 < <= > >=");
            if (!double.TryParse(cond[(1 + op.Length)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"SizeClasses 规则「{part}」的数值不对");
            list.Add(new Rule(cls, cond[0] == 'w', op, v));
        }
        return list.ToArray();
    }

    private static void Attach(Avalonia.Controls.Control c)
    {
        var st = c.GetValue(StateProperty);
        if (st == null)
        {
            st = new State();
            c.SetValue(StateProperty, st);
            c.SizeChanged += (_, e) => Evaluate(c, e.NewSize);
        }
        foreach (var cls in st.Applied) c.Classes.Remove(cls);
        st.Applied.Clear();
        st.Previous = null;
        st.Rules = Parse(c.GetValue(RulesProperty));
        Evaluate(c, c.Bounds.Size);
    }

    private static void Evaluate(Avalonia.Controls.Control c, Size size, bool deferred = false)
    {
        var st = c.GetValue(StateProperty);
        if (st == null || size.Width <= 0 || size.Height <= 0) return;
        var want = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in st.Rules) if (r.Matches(size)) want.Add(r.Class);
        if (want.SetEquals(st.Applied)) return;
        var now = Stopwatch.GetTimestamp();
        var since = Stopwatch.GetElapsedTime(st.LastChange, now).TotalMilliseconds;
        // 振荡候选：刚从 Previous 切到 Applied（< 250 ms），现在又要切回 Previous → 先保持 Applied，窗口过后再按当时的尺寸判一次
        if (!deferred && st.Previous != null && want.SetEquals(st.Previous) && since < SettleMs)
        {
            if (st.Bounces >= 2)
                Debug.WriteLine($"[SizeClasses] 振荡已抑制：{c.GetType().Name} {size} 保持「{string.Join(" ", st.Applied)}」");
            else if (!st.Pending)
            {
                // 等这一轮布局排完（Background 优先级低于布局 / 渲染）再按那时的尺寸判一次：尺寸只是短暂来回时类会跟上最终尺寸
                st.Pending = true;
                Dispatcher.UIThread.Post(() =>
                {
                    st.Pending = false;
                    Evaluate(c, c.Bounds.Size, deferred: true);
                }, DispatcherPriority.Background);
            }
            return;
        }
        if (deferred) st.Bounces++;
        else if (since > 1000) st.Bounces = 0;
        foreach (var cls in st.Applied) if (!want.Contains(cls)) c.Classes.Remove(cls);
        foreach (var cls in want) if (!st.Applied.Contains(cls)) c.Classes.Add(cls);
        st.Previous = st.Applied;
        st.Applied = want;
        st.LastChange = now;
    }
}
