using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;

namespace ParallelReactor.Control.Shell;

/// <summary>通道小块（样稿 .a2chip）：记号（进行中 = 品牌蓝点，通过 / 不通过 / 未测 = 实心图标）+ RVn + 数值 + 单位，下面一行状态字。</summary>
public sealed record ActChipVm(string Kind, string? Icon, string Rv, string Value, string Unit, string Word)
{
    public bool IsOk => Kind == "ok";
    public bool IsNo => Kind == "no";
    public bool IsAmb => Kind == "amb";
    public bool IsN => Kind == "n";
    public bool HasDot => Icon == null;
    public bool HasIcon => Icon != null;
    public bool HasUnit => Unit.Length > 0;
}

/// <summary>阶段分段文字的一段（样稿 .a2seg span）：done 带 ✓、cur 白色凸起（等人操作琥珀字）、next 灰。</summary>
public sealed record ActSegVm(string Text, bool Done, bool Cur, bool Amb);

/// <summary>标定的两行键值（样稿 .a2kv）：键、值的前半、粗体、后半。</summary>
public sealed record ActKvVm(string Key, string A, string B, string C);

/// <summary>收起后的胶囊（样稿 .a2pill）：小圆环 +「1号机 · 泄漏测试」+ 说明 + ˄。点一下展开。</summary>
public sealed record ActPillVm(int K, string Tone, IReadOnlyList<RingSeg> Mini, string Title, string Meta, string Tip, ICommand Command)
{
    public bool IsAmb => Tone == "amb";
    public bool IsNo => Tone == "no";
}

/// <summary>
/// 展开的那张（样稿 act2.js ax2A，方向 A 浮动通知式）：头（图标块 · 标题 · 副行 · 收起）、主角区（分段圆环 + 当前阶段 / 结果 / 等待触屏操作）、
/// 阶段分段文字、各路小块 +「RV5–8 不测」、琥珀带压提示、标定的键值、底行（开始 · 判据 + 中止 / 记录 + 关闭 · 查看结果 / 标定只能在触屏操作）。
/// </summary>
public sealed record ActCardVm
{
    public int K { get; init; }
    public string Tone { get; init; } = "run";
    public string Icon { get; init; } = "timer";
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    /// <summary>主角区整块浅琥珀底（等人操作）。</summary>
    public bool Amb { get; init; }
    public IReadOnlyList<RingSeg> Ring { get; init; } = Array.Empty<RingSeg>();
    /// <summary>圆环中心：小字在上（剩余 / 已等 / 已中止）还是大字在上（不通过路数）。</summary>
    public bool SmallFirst { get; init; } = true;
    public string CenterSmall { get; init; } = "";
    public string CenterBig { get; init; } = "";
    /// <summary>中心大字的样子：空 = 正常 28 px；amb / no / ok（34 px）/ mut。</summary>
    public string CenterKind { get; init; } = "";
    public string CurSmall { get; init; } = "";
    public bool CurWarn { get; init; }
    public string CurBig { get; init; } = "";
    /// <summary>当前块的语气：空 = 品牌蓝；amb / no / ok / ab。</summary>
    public string CurKind { get; init; } = "";
    public string Line1A { get; init; } = "";
    public string Line1Em { get; init; } = "";
    public string Line1B { get; init; } = "";
    public string Line2 { get; init; } = "";
    public bool HasLine1 => Line1A.Length + Line1Em.Length + Line1B.Length > 0;
    public bool HasLine2 => Line2.Length > 0;
    public IReadOnlyList<ActSegVm> Segs { get; init; } = Array.Empty<ActSegVm>();
    public bool HasSegs => Segs.Count > 0;
    public IReadOnlyList<ActChipVm> Chips { get; init; } = Array.Empty<ActChipVm>();
    public bool HasChips => Chips.Count > 0;
    public string Off { get; init; } = "";
    public bool HasOff => Off.Length > 0;
    public string? WarnTitle { get; init; }
    public string? WarnBody { get; init; }
    public bool HasWarn => WarnTitle != null;
    public IReadOnlyList<ActKvVm> Kv { get; init; } = Array.Empty<ActKvVm>();
    public bool HasKv => Kv.Count > 0;
    public string Foot { get; init; } = "";
    /// <summary>底行是「标定只能在触屏操作」（锁图标）。</summary>
    public bool Lock { get; init; }
    public bool CanAbort { get; init; }
    public bool CanClose { get; init; }
    public bool CanResults { get; init; }
    /// <summary>底行的最小高度：有按钮时 32（按钮高），只有一行字（标定的锁）时不撑高。</summary>
    public double FootMinH => CanAbort || CanClose || CanResults ? 32 : 0;
    public bool ToneAmb => Tone == "amb";
    public bool ToneNo => Tone == "no";
    public bool ToneOk => Tone == "ok";
    public bool ToneAb => Tone == "ab";
    public bool CenterAmb => CenterKind == "amb";
    public bool CenterNo => CenterKind == "no";
    public bool CenterOk => CenterKind == "ok";
    public bool CenterMut => CenterKind == "mut";
    public bool CurAmb => CurKind == "amb";
    public bool CurNo => CurKind == "no";
    public bool CurOk => CurKind == "ok";
    public bool CurAb => CurKind == "ab";
    public bool BigFirst => !SmallFirst;
    public ICommand? FoldCommand { get; init; }
    public ICommand? AbortCommand { get; init; }
    public ICommand? CloseCommand { get; init; }
    public ICommand? ResultsCommand { get; init; }
}

/// <summary>
/// 浮动活动面板（v0.8.1 设备活动同步，ACT-SPEC §3.1 方向 A；一个外框一个，列出每台设备的活动）：外框右下、状态栏上方，浮在内容上，<b>不是模态</b>。
/// 只展开一张（<see cref="Card"/>），其余是胶囊（<see cref="Pills"/>，堆在上面）；规则在 Core 的 <see cref="ActivityDeck"/>（自动展开、等人操作优先、收起、关闭）。
/// 设备栏的活动行、状态栏的一项都调 <see cref="Expand"/>。
/// </summary>
public sealed partial class ActivityPanelVm : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ShellVm _shell;

    public ActivityPanelVm(ShellVm shell) => _shell = shell;

    public ActivityDeck Deck { get; } = new();

    public ObservableCollection<ActPillVm> Pills { get; } = new();
    [ObservableProperty] private ActCardVm? _card;
    [ObservableProperty] private bool _hasCard;
    [ObservableProperty] private bool _isVisible;

    /// <summary>这台设备的活动此刻显示着（没关掉、没过期；设备栏的活动行、状态栏一项按它）。</summary>
    public DeckItem? Shown(int k) => Deck.Visible(_shell.Fleet.Now).FirstOrDefault(x => x.K == k);

    /// <summary>每次刷新（外框）：喂给 Deck、按摆法重建胶囊和展开的那张。</summary>
    public void Refresh()
    {
        var fleet = _shell.Fleet;
        var now = fleet.Now;
        var changes = Deck.Update(fleet.Devices.Select(d => (d.K, d.Act)), now);
        // 结束时提示一次（ACT-SPEC §3.3 第 4 条：「通过」也给提示）；结果在面板里显示
        foreach (var (k, t) in changes)
            if (t == ActTransition.Finished && fleet.Device(k) is { Act: { } fa } fd)
                _shell.Toast(FinishToast(fd.Alias, fa), fa.Phase switch { ActPhase.Passed => Services.ToastKind.Ok, ActPhase.Aborted => Services.ToastKind.Info, _ => Services.ToastKind.Warn });
        var (open, pills) = Deck.Layout(now);
        var pv = pills.Select(p => fleet.Device(p.K) is { } d ? Pill(d, p.Activity) : null).Where(p => p != null).Select(p => p!).ToList();
        if (Pills.Count != pv.Count || !Pills.Zip(pv).All(x => SamePill(x.First, x.Second)))
        {
            Pills.Clear();
            foreach (var p in pv) Pills.Add(p);
        }
        var card = open != null && fleet.Device(open.K) is { } od ? CardOf(od, open.Activity) : null;
        // 内容没变时不换对象（不然每秒整张重建：悬停、按下的状态都丢）
        if (card is null || Card is null || Sig(card) != Sig(Card)) Card = card;
        HasCard = Card != null;
        IsVisible = HasCard || Pills.Count > 0;
    }

    /// <summary>卡片显示的全部内容（比较用）。</summary>
    public static string Sig(ActCardVm c) => string.Join("|", c.K, c.Tone, c.Icon, c.Title, c.Sub, c.Amb, c.SmallFirst, c.CenterSmall, c.CenterBig, c.CenterKind,
        c.CurSmall, c.CurWarn, c.CurBig, c.CurKind, c.Line1A, c.Line1Em, c.Line1B, c.Line2, c.Off, c.WarnTitle, c.WarnBody, c.Foot, c.Lock, c.CanAbort, c.CanClose, c.CanResults,
        string.Join(",", c.Ring), string.Join(",", c.Segs), string.Join(",", c.Chips), string.Join(",", c.Kv));

    private static bool SamePill(ActPillVm a, ActPillVm b) => a.K == b.K && a.Tone == b.Tone && a.Title == b.Title && a.Meta == b.Meta && a.Mini.SequenceEqual(b.Mini);

    /// <summary>结束时的提示：「1号机 · 泄漏测试通过 · 4 路」「1号机 · 泄漏测试 2 路不通过」「1号机 · 容积标定完成 · RV3 10.82 mL」。</summary>
    public static string FinishToast(string alias, DeviceActivity a)
    {
        if (a.Kind == ActKind.Calib)
            return alias + " · " + ActivityText.Name(a) + (a.Phase == ActPhase.Passed ? " 完成" + (a.ResultMl is double r ? " · " + r.ToString("0.00", Inv) + " mL" : "") : " 没有完成");
        var (ok, no, _) = a.Counts;
        return alias + " · 泄漏测试" + a.Phase switch
        {
            ActPhase.Passed => "通过 · " + ok.ToString(Inv) + " 路",
            ActPhase.Failed => " " + no.ToString(Inv) + " 路不通过",
            ActPhase.Aborted => "已中止 · 本次不出结果",
            _ => "没有完成" + (a.FailReason is { Length: > 0 } f ? " · " + f : ""),
        };
    }

    /// <summary>展开并优先显示（胶囊、设备栏活动行、状态栏一项）。</summary>
    public void Expand(int k) { Deck.Expand(k); Refresh(); }

    public void Fold(int k) { Deck.Fold(k); Refresh(); _shell.RefreshChrome(); }

    public void Close(int k) { Deck.Close(k); Refresh(); _shell.RefreshChrome(); }

    /// <summary>「查看结果」：去性能测试 → 泄漏测试（这台设备），面板收起。</summary>
    public void Results(int k)
    {
        Deck.Fold(k);
        _shell.Nav.Set(AppPage.Perf, k, null);
        if (_shell.Page is Pages.Perf.PerfPageVm pp) { pp.ModeKey = "leak"; pp.Refresh(_shell.Fleet); }
        Refresh();
        _shell.RefreshChrome();
    }

    /// <summary>截图 / 演示：全部收起或全部展开（actfold=1），并当作一直看着（结束的结果不算过期）；axopen=k 指定展开哪一台。</summary>
    public void ApplyParams(bool fold, int open)
    {
        Deck.Update(_shell.Fleet.Devices.Select(d => (d.K, d.Act)), _shell.Fleet.Now);
        Deck.MarkSeen();
        Deck.SetAllFolded(fold);
        if (open > 0 && !fold) Deck.Expand(open);
        Refresh();
    }

    public void Reset() { Deck.Reset(); Refresh(); }

    // ———————————————————— 胶囊 / 卡片 ————————————————————

    private static string ToneKey(ActTone t) => t switch { ActTone.Amb => "amb", ActTone.Ok => "ok", ActTone.No => "no", ActTone.Ab => "ab", _ => "run" };

    /// <summary>胶囊的小圆环（样稿 ax2Mini：总进度一段 + 轨道）。</summary>
    public static IReadOnlyList<RingSeg> MiniRing(DeviceActivity a)
    {
        var (f, tone) = ActivityText.Mini(a);
        var kind = tone switch { ActTone.Amb => RingSegKind.Amb, ActTone.Ok => RingSegKind.Ok, ActTone.No => RingSegKind.No, ActTone.Ab => RingSegKind.Ab, _ => RingSegKind.Done };
        return f >= 0.999 ? new[] { new RingSeg(1, kind, 1) } : new[] { new RingSeg(f, kind, 1), new RingSeg(1 - f, RingSegKind.Next, 0) };
    }

    private ActPillVm Pill(DeviceVm d, DeviceActivity a) => new(d.K, ToneKey(ActivityText.Tone(a)), MiniRing(a), d.Alias + " · " + ActivityText.Name(a),
        ActivityText.Meta(a, NowOf(d)), "展开", new RelayCommand(() => Expand(d.K)));

    /// <summary>设备的「现在」：演示 = 演示时钟；真机 = 设备时钟（本机 UTC + 偏差，同曲线）。</summary>
    private DateTime NowOf(DeviceVm d) => d.Source.IsDemo ? _shell.Fleet.Now : d.Source.History.Now;

    /// <summary>展开的那张（见 <see cref="ActCardVm"/>）。</summary>
    public ActCardVm CardOf(DeviceVm d, DeviceActivity a)
    {
        var now = NowOf(d);
        var leak = d.Source.Plant.Leak;
        double settle = leak?.SettleMin ?? 10, measure = leak?.MeasureMin ?? 30;
        var client = _shell.Fleet.ClientName;
        int k = d.K;
        var card = new ActCardVm
        {
            K = k,
            Tone = ToneKey(ActivityText.Tone(a)),
            Icon = ActivityText.Icon(a),
            Title = ActivityText.Title(d.Alias, a),
            Sub = ActivityText.HeadSub(a, client),
            Ring = ActivityText.Ring(a, settle, measure),
            FoldCommand = new RelayCommand(() => Fold(k)),
            AbortCommand = new RelayCommand(() => ActivityActions.ConfirmAbort(_shell, d)),
            CloseCommand = new RelayCommand(() => Close(k)),
            ResultsCommand = new RelayCommand(() => Results(k)),
        };
        return a.Kind == ActKind.Calib ? CalibCard(card, d, a, now) : a.Live ? LeakRunCard(card, a, now) : LeakDoneCard(card, a);
    }

    private static IReadOnlyList<ActSegVm> SegsOf(DeviceActivity a) => ActivityText.Stages(a)
        .Select((n, i) => new ActSegVm(n, a.Finished || i < a.Stage, a.Live && i == a.Stage, a.Live && i == a.Stage && a.Waiting)).ToList();

    private static IReadOnlyList<ActChipVm> ChipsOf(DeviceActivity a) => a.Channels.Select(c =>
    {
        var ch = ActivityText.Chip(a, c);
        var (kind, icon) = ch.Kind switch
        {
            ChipKind.Ok => ("ok", (string?)"ok-f"),
            ChipKind.No => ("no", "no-f"),
            ChipKind.Amb => ("amb", "warn-f"),
            ChipKind.Neutral => ("n", null),
            _ => ("run", null),
        };
        return new ActChipVm(kind, icon, "RV" + c.N.ToString(Inv), ch.Value, ch.Unit, ch.Word);
    }).ToList();

    private static ActCardVm LeakRunCard(ActCardVm c, DeviceActivity a, DateTime now)
    {
        var end = ActivityText.EndAt(a, now);
        return c with
        {
            CenterSmall = a.TotalLeftSec is null ? "已完成" : "剩余",
            CenterBig = a.TotalLeftSec is double t ? ActivityText.Mmss(t) : ActivityText.Pct(a).ToString(Inv) + " %",
            CurSmall = "当前阶段 · " + (a.Stage + 1).ToString(Inv) + " / 4",
            CurBig = ActivityText.StageName(a),
            Line1A = a.StageLeftSec is null ? a.PhaseText ?? "" : "本阶段剩 ",
            Line1Em = a.StageLeftSec is double l ? ActivityText.Mmss(l) : "",
            Line2 = (end is { } e ? "约 " + Fmt.Hm(e) + " 结束 · " : "") + "已完成 " + ActivityText.Pct(a).ToString(Inv) + " %",
            Segs = SegsOf(a),
            Chips = ChipsOf(a),
            Off = ActivityText.OffLine(a),
            Foot = (a.StartedAt is { } s ? Fmt.Time(s) + " 开始 · " : "") + ActivityText.Threshold(a),
            CanAbort = true,
        };
    }

    private static ActCardVm LeakDoneCard(ActCardVm c, DeviceActivity a)
    {
        var (ok, no, _) = a.Counts;
        bool cut = a.Phase is ActPhase.Aborted or ActPhase.Error;
        var warn = ActivityText.HoldWarn(a);
        return c with
        {
            SmallFirst = cut,
            CenterSmall = a.Phase == ActPhase.Aborted ? "已中止" : a.Phase == ActPhase.Error ? "没有完成" : no > 0 ? "路不通过" : "路通过",
            CenterBig = cut ? "—" : (no > 0 ? no : ok).ToString(Inv),
            CenterKind = cut ? "mut" : no > 0 ? "no" : "ok",
            CurSmall = a.Phase == ActPhase.Aborted ? "本次不出结果" : a.Phase == ActPhase.Error ? "测试没有完成" : "测试结果",
            CurBig = ActivityText.Verdict(a),
            CurKind = cut ? "ab" : no > 0 ? "no" : "ok",
            Line1A = ActivityText.VerdictSub(a),
            Line2 = ActivityText.Threshold(a),
            Chips = ChipsOf(a),
            Off = ActivityText.OffLine(a),
            WarnTitle = warn?.Title,
            WarnBody = warn?.Body,
            Foot = a.Phase switch
            {
                ActPhase.Aborted => "中止已记入事件",
                ActPhase.Error => "失败已记入事件",
                _ => "已存入记录" + (a.RunId is { Length: > 0 } id ? " · " + id : ""),
            },
            CanClose = true,
            CanResults = a.Phase != ActPhase.Aborted,
        };
    }

    private static ActCardVm CalibCard(ActCardVm c, DeviceVm d, DeviceActivity a, DateTime now)
    {
        var rv = a.Channel is int n ? "RV" + n.ToString(Inv) : "";
        var water = a.WaterMl is double w ? w.ToString("0.00", Inv) + " mL" : null;
        var kv = new List<ActKvVm>();
        if (a.Stage >= 1 || a.Finished)
        {
            if (a.FirstPassMl is double v1) kv.Add(new ActKvVm("第一遍 · 空釜", "有效容积 ", v1.ToString("0.00", Inv), " mL" + (a.WaitingSince is { } ws ? " · " + Fmt.Hm(ws) + " 完成" : "")));
            else if (a.WaitingSince is { } ws2) kv.Add(new ActKvVm("第一遍 · 空釜", Fmt.Hm(ws2) + " 完成", "", ""));
        }
        if (a.Channel is int ch && d.Source.Plant.Volume is { } vol && vol.Calibrated(ch))
            kv.Add(new ActKvVm("上次标定", (vol.CalibratedAt.Count >= ch && vol.CalibratedAt[ch - 1] is { } at ? Fmt.Date(at) + " · " : "") + vol.EffectiveMl[ch - 1]!.Value.ToString("0.00", Inv) + " mL", "", ""));
        else kv.Add(new ActKvVm("上次标定", "没有标定过", "", ""));
        var baseCard = c with { Amb = a.Waiting, Segs = SegsOf(a), Kv = kv, Lock = true, Foot = "标定只能在触屏操作 · 本机不能开始或中止", CanClose = a.Finished };
        return a.Phase switch
        {
            ActPhase.Waiting => baseCard with
            {
                CenterSmall = "已等",
                CenterBig = ActivityText.Mmss(ActivityText.WaitedSec(a, now)),
                CenterKind = "amb",
                CurSmall = "等待触屏操作",
                CurWarn = true,
                CurBig = water != null ? "加水 " + water : "加水",
                CurKind = "amb",
                Line1A = "往 " + rv + " 加入 ",
                Line1Em = water ?? "",
                Line1B = water != null ? " 水后，在触屏上点「继续」" : "水后，在触屏上点「继续」",
            },
            ActPhase.Running => baseCard with
            {
                CenterSmall = "已完成",
                CenterBig = ActivityText.Pct(a).ToString(Inv) + " %",
                CurSmall = "当前阶段 · " + (a.Stage + 1).ToString(Inv) + " / 4",
                CurBig = ActivityText.StageName(a),
                Line1A = a.PhaseText ?? "",
            },
            ActPhase.Passed => baseCard with
            {
                CenterSmall = "mL",
                SmallFirst = false,
                CenterBig = a.ResultMl is double r ? r.ToString("0.00", Inv) : "—",
                CenterKind = "",
                CurSmall = "标定结果",
                CurBig = a.ResultMl is double r2 ? "有效容积 " + r2.ToString("0.00", Inv) + " mL" : "已完成",
                CurKind = "ok",
                Line1A = a.ResultText ?? "",
            },
            _ => baseCard with
            {
                CenterSmall = "没有完成",
                CenterBig = "—",
                CenterKind = "mut",
                CurSmall = "标定结果",
                CurBig = "标定失败",
                CurKind = "no",
                Line1A = a.ResultText ?? "",
            },
        };
    }
}
