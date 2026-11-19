using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelReactor.Control.Core.Activity;

/// <summary>一次刷新里看到的变化（自动展开用）：开始 / 开始等人操作 / 结束。</summary>
public enum ActTransition { None, Started, Waiting, Finished }

/// <summary>浮动面板的一行（展开的那张或胶囊）：设备号、活动、是不是收起的。</summary>
public sealed record DeckItem(int K, DeviceActivity Activity, bool Folded);

/// <summary>
/// 浮动活动面板的规则（ACT-SPEC §3.1，样稿 act2.js ax2Render）：纯逻辑，单元测试直接调。
/// <list type="bullet">
/// <item><b>自动展开</b>：一台设备开始泄漏测试 / 标定（不管谁发起；PC 第一次看到它在进行也算）、开始等人操作、结束时 —— 展开它，并且优先显示它
///   （另一台正在等人操作时不抢：等人操作的优先）。PC 第一次看到的就是已经结束的结果时不弹，收在胶囊里；结束超过 <see cref="ActivityText.StaleAfter"/> 又没看着它进行的不显示。</item>
/// <item><b>只展开一张</b>：没收起的里面，用户点过的 &gt; 等人操作的 &gt; 设备号小的；其余（含收起的）是胶囊，按设备号堆在展开的那张上面。</item>
/// <item><b>收起</b>：变成胶囊；点胶囊重新展开（并且优先显示它）。<b>关闭</b>（只有结束的有）：这一次活动不再显示，下一次开始照常弹。</item>
/// </list>
/// </summary>
public sealed class ActivityDeck
{
    private sealed class Slot
    {
        public DeviceActivity? A;
        public bool SeenLive;
        public bool Folded;
        public string? Closed;
    }

    private readonly Dictionary<int, Slot> _slots = new();
    private int _pick;

    /// <summary>用户点过、要优先显示的设备（0 = 自动）。</summary>
    public int Pick => _pick;

    /// <summary>
    /// 按最新的活动更新（每次刷新调，<paramref name="now"/> = 判断「结束很久了」用的现在）。返回这次看到的变化（设备号 → 变化，没变化的不列）。
    /// </summary>
    public IReadOnlyDictionary<int, ActTransition> Update(IEnumerable<(int K, DeviceActivity? A)> all, DateTime now)
    {
        var seen = new HashSet<int>();
        var changes = new Dictionary<int, ActTransition>();
        foreach (var (k, a) in all)
        {
            seen.Add(k);
            bool fresh = !_slots.TryGetValue(k, out var s);
            if (fresh) _slots[k] = s = new Slot();
            var prev = s!.A;
            var t = ActTransition.None;
            if (a != null)
            {
                bool same = prev != null && prev.Key == a.Key;
                if (!same)
                {
                    s.SeenLive = a.Live;
                    if (a.Live) t = a.Waiting ? ActTransition.Waiting : ActTransition.Started;
                    // 第一次看到就已经结束的：不弹，收成胶囊（打开软件时不把旧结果甩在脸上）；这一刷新之前就在显示的（同一台换了一次）照常弹
                    else if (prev != null && prev.Live) t = ActTransition.Finished;
                    else s.Folded = true;
                }
                else
                {
                    if (a.Live) s.SeenLive = true;
                    if (a.Waiting && !prev!.Waiting) t = ActTransition.Waiting;
                    else if (a.Finished && prev!.Live) t = ActTransition.Finished;
                }
            }
            s.A = a;
            if (t != ActTransition.None)
            {
                changes[k] = t;
                s.Folded = false;
                s.Closed = null;
                // 正在显示的是另一台等人操作的：不抢（等人操作的优先）
                if (t == ActTransition.Waiting || !OpenIsWaitingOther(k)) _pick = k;
            }
        }
        foreach (var k in _slots.Keys.Where(k => !seen.Contains(k)).ToList()) _slots.Remove(k);
        if (_pick != 0 && !Visible(now).Any(x => x.K == _pick)) _pick = 0;
        return changes;
    }

    private bool OpenIsWaitingOther(int k) => _slots.Any(kv => kv.Key != k && kv.Value.A is { Waiting: true } && !kv.Value.Folded && kv.Value.Closed != kv.Value.A.Key);

    /// <summary>这台设备此刻的活动（看不到的 / 关掉的也给）。</summary>
    public DeviceActivity? Of(int k) => _slots.TryGetValue(k, out var s) ? s.A : null;

    /// <summary>显示着的活动（没关掉、不是很久以前结束而又没看着它进行的），按设备号。</summary>
    public IReadOnlyList<DeckItem> Visible(DateTime now) => _slots.OrderBy(kv => kv.Key)
        .Where(kv => kv.Value.A is { } a && kv.Value.Closed != a.Key && !(a.Finished && !kv.Value.SeenLive && Stale(a, now)))
        .Select(kv => new DeckItem(kv.Key, kv.Value.A!, kv.Value.Folded)).ToList();

    private static bool Stale(DeviceActivity a, DateTime now) =>
        (a.FinishedAt ?? a.StartedAt) is { } t && now - t > ActivityText.StaleAfter;

    /// <summary>
    /// 摆法：展开的那张（没有为 null）+ 胶囊（其余显示着的，按设备号；从上往下堆在展开的那张上面）。
    /// 展开的 = 没收起的里面：用户点过的 &gt; 等人操作的 &gt; 第一个。
    /// </summary>
    public (DeckItem? Open, IReadOnlyList<DeckItem> Pills) Layout(DateTime now)
    {
        var vis = Visible(now);
        var live = vis.Where(x => !x.Folded).ToList();
        var open = live.FirstOrDefault(x => x.K == _pick) ?? live.FirstOrDefault(x => x.Activity.Waiting) ?? live.FirstOrDefault();
        return (open, vis.Where(x => open == null || x.K != open.K).ToList());
    }

    /// <summary>收起成胶囊。</summary>
    public void Fold(int k)
    {
        if (_slots.TryGetValue(k, out var s)) s.Folded = true;
        if (_pick == k) _pick = 0;
    }

    /// <summary>展开并优先显示（点胶囊、设备栏的活动行、状态栏一项）。</summary>
    public void Expand(int k)
    {
        if (!_slots.TryGetValue(k, out var s) || s.A is null) return;
        s.Folded = false;
        s.Closed = null;
        _pick = k;
    }

    /// <summary>关掉这一次的结果（「关闭」「查看结果」之后）：下一次开始照常弹。</summary>
    public void Close(int k)
    {
        if (_slots.TryGetValue(k, out var s) && s.A is { } a) s.Closed = a.Key;
        if (_pick == k) _pick = 0;
    }

    /// <summary>全部收起 / 全部展开（演示参数 actfold=1；截图）。</summary>
    public void SetAllFolded(bool folded)
    {
        foreach (var s in _slots.Values) { s.Folded = folded; if (!folded) s.Closed = null; }
        if (folded) _pick = 0;
    }

    /// <summary>演示 / 截图：当作 PC 一直看着它（结束的结果照常显示、不算过期）。</summary>
    public void MarkSeen()
    {
        foreach (var s in _slots.Values) s.SeenLive = true;
    }

    /// <summary>忘掉一切（演示数据重建）。</summary>
    public void Reset()
    {
        _slots.Clear();
        _pick = 0;
    }
}
