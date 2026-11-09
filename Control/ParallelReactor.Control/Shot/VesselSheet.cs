#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 釜体核对表（PR_CONTROL_DEMO=1 + PR_CONTROL_VESSEL_SHEET=1，仅 Debug）：陈列页只有 7 + 1 个釜体，这里把所有状态 × 尺寸 × 角标阈值 × 液位 × 小图标排开 ——
///  · A 行：card 104 × 150（k = 0.65，角标全不写）13 种状态；B 行：card 129 × 198（k = 0.86，HT / OFF / TT / PT 都写）；C 行：compact + simple 36 × 56；
///  · D 行：角标阈值（k = 0.713 / 0.727 / 0.84 / 0.86 / 1，各一个不加热、一个加热）；E 行：液位 0 … 1.2（card）；F 行：large 180 × 270；
///  · G 行：vesselGlyph 13 种状态 × 16 / 22 / 26 / 30 px。
/// 截图 vessel-sheet-&lt;w&gt;.png + 审计；同时写 vessel-sheet.js：在样稿的任一页面里执行它（snap 的 js 参数），用 ui.reactorSVG / ui.stateGlyph 在同样的位置画同一张表
/// （假设备：50 mL 釜、溶液上限 20 mL、共用搅拌 300 rpm），拍下来就是 WebKit 的参照图，两张图逐格对照。
/// </summary>
public static class VesselSheet
{
    /// <summary>一格：位置、框、画什么（釜体或小图标）、状态与标志、说明（13 px）。</summary>
    public sealed record Cell(double X, double Y, double W, double H, bool Glyph, StateKey Key, bool Heat, bool Cool, bool Stir, double Level,
        VesselSize Size, bool Simple, string Caption);

    public const double Width = 1920, Height = 1640;

    /// <summary>13 种状态（Core StateKeys 的顺序）与核对用的标志：加热类加热、搅拌类在转、降温在冷却；离线时标志照给（应当不画）。</summary>
    private static readonly (StateKey Key, bool Heat, bool Cool, bool Stir)[] States =
    {
        (StateKey.Idle, false, false, false), (StateKey.Ready, false, false, false), (StateKey.Heat, true, false, true), (StateKey.Press, true, false, true),
        (StateKey.React, true, false, true), (StateKey.Alarm, true, false, true), (StateKey.Wait, false, false, true), (StateKey.Stir, false, false, true),
        (StateKey.Cool, false, true, true), (StateKey.Done, false, false, false), (StateKey.Stopped, false, false, false), (StateKey.Offline, true, true, true),
        (StateKey.Uninstalled, false, false, false),
    };

    public static IReadOnlyList<Cell> Cells()
    {
        var c = new List<Cell>();
        string Code(StateKey k) => StateKeys.Code(k);
        for (int i = 0; i < States.Length; i++)
        {
            var (k, h, co, s) = States[i];
            double x = 20 + i * 140;
            c.Add(new Cell(x, 20, 104, 150, false, k, h, co, s, 0.4, VesselSize.Card, false, Code(k)));
            c.Add(new Cell(x, 210, 129, 198, false, k, h, co, s, 0.4, VesselSize.Card, false, Code(k) + " .86"));
            c.Add(new Cell(x, 440, 36, 56, false, k, h, co, s, 0.4, VesselSize.Compact, true, Code(k) + " simple"));
        }
        // D：角标阈值（宽决定 k；高比 230 / 150 多 1 px）
        int j = 0;
        foreach (var w in new double[] { 107, 109, 126, 129, 150 })
        {
            double hh = Math.Round(w * 230 / 150) + 1;
            string kk = (w / 150).ToString("0.###", CultureInfo.InvariantCulture);
            c.Add(new Cell(20 + j++ * 170, 530, w, hh, false, StateKey.Idle, false, false, false, 0.3, VesselSize.Card, false, "idle k " + kk));
            c.Add(new Cell(20 + j++ * 170, 530, w, hh, false, StateKey.Heat, true, false, false, 0.3, VesselSize.Card, false, "heat k " + kk));
        }
        // E：液位（ready = 无气泡；react 看气泡在极端液位的位置）
        j = 0;
        foreach (var lv in new[] { 0, 0.04, 0.1, 0.25, 0.5, 0.75, 1, 1.2 })
            c.Add(new Cell(20 + j++ * 140, 800, 104, 150, false, StateKey.Ready, false, false, false, lv, VesselSize.Card,
                false, "level " + lv.ToString("0.##", CultureInfo.InvariantCulture)));
        c.Add(new Cell(20 + j++ * 140, 800, 104, 150, false, StateKey.React, true, false, true, 0.04, VesselSize.Card, false, "react 0.04"));
        c.Add(new Cell(20 + j * 140, 800, 104, 150, false, StateKey.React, true, false, true, 1, VesselSize.Card, false, "react 1"));
        // F：large
        j = 0;
        foreach (var (k, h, co, s) in new[]
                 {
                     (StateKey.React, true, false, true), (StateKey.Alarm, true, false, true), (StateKey.Idle, false, false, false), (StateKey.Cool, false, true, true),
                     (StateKey.Offline, true, false, true), (StateKey.Uninstalled, false, false, false), (StateKey.Done, false, false, false),
                 })
            c.Add(new Cell(20 + j++ * 210, 1000, 180, 270, false, k, h, co, s, 0.45, VesselSize.Large, false, Code(k) + " large"));
        // G：小图标
        int r = 0;
        foreach (var gs in new double[] { 16, 22, 26, 30 })
        {
            for (int i = 0; i < States.Length; i++)
                c.Add(new Cell(20 + i * 44, 1320 + r * 70, gs, gs, true, States[i].Key, false, false, false, double.NaN, VesselSize.Card, false,
                    i == 0 ? "glyph " + gs.ToString(CultureInfo.InvariantCulture) : ""));
            r++;
        }
        return c;
    }

    public static Avalonia.Controls.Control Build()
    {
        var canvas = new Canvas { Width = Width, Height = Height, Background = Palette.BgCardBrush };
        foreach (var cell in Cells())
        {
            Avalonia.Controls.Control v = cell.Glyph
                ? new VesselGlyph { Key = cell.Key, Size = cell.W }
                : new ReactorVessel
                {
                    Width = cell.W, Height = cell.H, Key = cell.Key, Heating = cell.Heat, Cooling = cell.Cool, Stirring = cell.Stir,
                    Level = cell.Level, Size = cell.Size, Simple = cell.Simple,
                };
            Canvas.SetLeft(v, cell.X);
            Canvas.SetTop(v, cell.Y);
            canvas.Children.Add(v);
            if (cell.Caption.Length == 0) continue;
            var t = new TextBlock { Text = cell.Caption, FontSize = 13, LineHeight = 18, Foreground = Palette.T3Brush };
            Canvas.SetLeft(t, cell.X);
            Canvas.SetTop(t, cell.Y + cell.H + 4);
            canvas.Children.Add(t);
        }
        return canvas;
    }

    private static string N(double v) => double.IsNaN(v) ? "null" : v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// 同一张表的样稿版（在样稿页面里执行）：每格一个绝对定位的框，釜体 = ui.reactorSVG(假设备, 假通道, { size, simple })，svg 铺满框；
    /// 小图标 = ui.stateGlyph(key, { size })（display: block）；最后 ui.fitReactors。颜色一律用 PR.tok 取令牌。
    /// svg 的定位只改单个样式属性（style 里有 reactorSVG 写的 --vessel-line / --liquid 等变量，不能整个覆盖）。
    /// </summary>
    public static string Js()
    {
        var sb = new StringBuilder();
        sb.Append("(function(){var PR=window.PR,ui=PR.ui,tok=PR.tok;var cells=[");
        bool first = true;
        foreach (var c in Cells())
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{')
              .Append("x:").Append(N(c.X)).Append(",y:").Append(N(c.Y)).Append(",w:").Append(N(c.W)).Append(",h:").Append(N(c.H))
              .Append(",glyph:").Append(c.Glyph ? "true" : "false").Append(",key:'").Append(StateKeys.Code(c.Key)).Append('\'')
              .Append(",heat:").Append(c.Heat ? "true" : "false").Append(",cool:").Append(c.Cool ? "true" : "false").Append(",stir:").Append(c.Stir ? "true" : "false")
              .Append(",vol:").Append(double.IsNaN(c.Level) ? "undefined" : N(c.Level * 50))
              .Append(",size:'").Append(c.Size.ToString().ToLowerInvariant()).Append("',simple:").Append(c.Simple ? "true" : "false")
              .Append(",cap:'").Append(c.Caption).Append("'}");
        }
        sb.Append("];");
        sb.Append(@"
var root=document.createElement('div');
root.style.cssText='position:fixed;left:0;top:0;width:" + N(Width) + "px;height:" + N(Height) + @"px;z-index:2147483647;overflow:hidden';
root.style.background=tok('--bg-card');
cells.forEach(function(c){
  var box=document.createElement('div');
  box.style.cssText='position:absolute;left:'+c.x+'px;top:'+c.y+'px;width:'+c.w+'px;height:'+c.h+'px';
  if(c.glyph){box.innerHTML=ui.stateGlyph(c.key,{size:c.w});box.firstChild.style.display='block';}
  else{
    var dev={k:9,alias:'S',model:{vessel:{volumeMl:50,maxFillMl:20},stirMode:'shared'},conn:{state:c.key==='offline'?'offline':'online',everOnline:true},stir:{on:c.stir,rpm:c.stir?300:0}};
    var ch={n:1,installed:c.key!=='uninstalled',state:c.key==='offline'?'react':c.key,heating:c.heat,cooling:c.cool,volumeMl:c.vol,stirOn:c.stir,rpm:c.stir?300:0};
    box.innerHTML=ui.reactorSVG(dev,ch,{size:c.size,simple:c.simple});
    var sv=box.firstChild.style;sv.position='absolute';sv.left='0';sv.top='0';sv.width='100%';sv.height='100%';
  }
  root.appendChild(box);
  if(c.cap){var t=document.createElement('div');t.textContent=c.cap;
    t.style.cssText='position:absolute;left:'+c.x+'px;top:'+(c.y+c.h+4)+'px;font-size:13px;line-height:18px;white-space:nowrap';
    t.style.color=tok('--t-3');root.appendChild(t);}
});
document.body.appendChild(root);
ui.fitReactors(root);
})();");
        return sb.ToString();
    }
}
#endif
