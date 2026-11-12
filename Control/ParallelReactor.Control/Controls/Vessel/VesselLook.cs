using System;
using Avalonia.Media;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Vessel;

/// <summary>
/// 釜体的尺寸档（样稿 ui.reactorSVG 的 size）：card / compact 的线条不随缩放变粗变细（样稿 vector-effect: non-scaling-stroke，
/// 轮廓 1.5 px、细节 1.25 px、液面 1 px），两者画法完全相同；large（M5 反应釜页）按样稿不加 non-scaling-stroke，线宽随 k 缩放（1.5 × k），
/// 角标 14 px，报警时压力表画红圈。
/// </summary>
public enum VesselSize { Card, Compact, Large }

/// <summary>夹套的画法（样稿 jacketStyle）：加热中实线（轮廓色）、降温中长虚线 7 / 3（轮廓色）+ 雪花、其它短虚线 4 / 4（细节色）。</summary>
public enum JacketStyle { Idle, Heating, Cooling }

/// <summary>
/// 釜体此刻的样子（样稿 vesselLook）：状态键 → 颜色（StateStyles / PR.ST）、液面、夹套、搅拌、气泡。纯数据，不碰渲染（可测）。
/// 「在线」= 状态键不是离线 / 未安装（样稿 live = PR.sim.online(dev)：设备断线时状态键一律是 offline）。
/// </summary>
public readonly record struct VesselLook(
    StateKey Key,
    bool Uninstalled, bool Offline, bool Alarm,
    bool Heating, bool Cooling, bool Spin, bool Bubbles,
    double LevelY,
    Color Line, Color Detail, Color? Liquid, Color LiquidLine, bool Hatch)
{
    /// <summary>夹套画法：降温优先（同样稿 jacketStyle 的判断顺序）。</summary>
    public JacketStyle Jacket => Cooling ? JacketStyle.Cooling : Heating ? JacketStyle.Heating : JacketStyle.Idle;

    /// <summary>
    /// 样稿 vesselLook(dev, ch)：heating / cooling 只在在线时画；搅拌桨转 = 在线 + 运行类状态 + 转速 &gt; 0；气泡 = 在线 + 反应中。
    /// 颜色：轮廓 = 状态的 line（报警 v-line-alarm、离线 v-detail、未安装 line，其余 v-line）；细节 = 未安装 line，其余 v-detail；
    /// 液体 = 离线灰斜纹（bg-sunken + line-soft），其余按状态（运行 v-liq-run、报警 v-liq-alarm、其余 v-liq；未安装不画）；液面线 = 状态的 liqLine。
    /// </summary>
    /// <param name="key">界面上的状态键（ReactorVm.Key：未安装 / 离线 / 反应釜自己的状态）。</param>
    /// <param name="heating">加热中（ReactorVm.Heating）。</param>
    /// <param name="cooling">冷却水降温中（演示才有，ChannelExtras.Cooling）。</param>
    /// <param name="stirring">搅拌在转（有效转速 &gt; 0）。</param>
    /// <param name="level">液位 = 体积 / 釜容积（<see cref="VesselAnchors.Level(double?, double?, double?)"/>；NaN = 默认 10 / 50）。</param>
    public static VesselLook Of(StateKey key, bool heating, bool cooling, bool stirring, double level)
    {
        var st = StateStyles.Get(key);
        bool un = key == StateKey.Uninstalled, off = key == StateKey.Offline;
        bool live = !un && !off;
        return new VesselLook(
            key, un, off, key == StateKey.Alarm,
            live && heating, live && cooling, live && StateKeys.IsRunning(key) && stirring, live && key == StateKey.React,
            VesselAnchors.LevelY(level),
            st.VesselLine.Color, un ? Palette.LineColor : Palette.VDetailColor,
            off ? Palette.VLiqColor : st.Liquid?.Color,
            st.LiquidLine?.Color ?? Palette.VDetailColor,
            off);
    }
}

/// <summary>
/// 釜内角标（HT / OFF / TT / PT）与火焰的取舍（样稿 ui.fitReactors + base.css）：角标按实际渲染尺寸换算成固定字号（card / compact 13.5 px、large 14 px）；
/// k &lt; 0.85 不写「OFF」，k &lt; 0.72 TT / PT / HT 与火焰都不画（字号永远不会低于 13 px）；断线不写 HT / OFF（不知道现在加热没有）；
/// 简化釜体（simple）与未安装没有角标和火焰。反应釜页（M5）的标注框已经写了 TT / PT：<c>showTags = false</c>（同 reactor.css 的 .reactor-vbox .rv-tt / .rv-pt）。
/// </summary>
public readonly record struct VesselLabels(bool Ht, bool Off, bool Tt, bool Pt, bool Flame)
{
    /// <summary>k 低于它不写「OFF」（样稿 rv-lt85）。</summary>
    public const double OffMinK = 0.85;
    /// <summary>k 低于它 TT / PT / HT / 火焰都不画（样稿 rv-lt72）。</summary>
    public const double TagMinK = 0.72;
    /// <summary>角标字号（px）：card / compact 13.5，large 14（样稿 fitReactors 的 target）。</summary>
    public const double CardPx = 13.5, LargePx = 14;

    public static double Px(VesselSize size) => size == VesselSize.Large ? LargePx : CardPx;

    public static readonly VesselLabels None = new(false, false, false, false, false);

    /// <param name="look">釜体外观。</param>
    /// <param name="k">viewBox → 控件的缩放（<see cref="VesselFit.K"/>）。</param>
    /// <param name="simple">简化釜体。</param>
    /// <param name="showLabels">总开关（false = 不写任何角标；火焰照画）。</param>
    /// <param name="showTags">写 TT / PT（反应釜页的大号釜体由标注框代替）。</param>
    public static VesselLabels Of(VesselLook look, double k, bool simple, bool showLabels = true, bool showTags = true)
    {
        if (simple || look.Uninstalled || !(k > 0)) return None;
        bool big = k >= TagMinK;
        bool ht = showLabels && big && !look.Offline && (look.Heating || k >= OffMinK);
        return new VesselLabels(
            Ht: ht && look.Heating,
            Off: ht && !look.Heating,
            Tt: showLabels && showTags && big,
            Pt: showLabels && showTags && big,
            Flame: big && look.Heating);
    }

    /// <summary>有任何一个角标要写。</summary>
    public bool AnyText => Ht || Off || Tt || Pt;
}
