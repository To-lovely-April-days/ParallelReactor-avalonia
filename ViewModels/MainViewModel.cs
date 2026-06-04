using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ParallelReactor.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public ObservableCollection<Reactor> Reactors { get; } = new();
    public ObservableCollection<GasInlet> GasInlets { get; } = new();
    public List<Recipe> Recipes { get; } = new();

    [ObservableProperty] private bool _ventOn;
    [ObservableProperty] private bool _manualValve;
    [ObservableProperty] private bool _isAdmin = true;
    [ObservableProperty] private bool _estopped;

    [ObservableProperty] private string _runCountText = "6 / 8";
    [ObservableProperty] private string _clockTime = "14:33:00";
    [ObservableProperty] private string _clockDate = "2026-06-02 周二";

    [ObservableProperty] private string _activeTab = "home";

    public DrawerViewModel Drawer { get; }
    public KeyboardViewModel Keyboard { get; } = new();

    // 气路图重绘信号（View 订阅后调用 InvalidateVisual）
    public event Action? SchematicInvalidated;
    // Toast 信号
    public event Action<string, string>? ToastRequested;
    // Tab 切换信号
    public event Action<string>? TabSwitched;

    private DateTime _clock = new(2026, 6, 2, 14, 33, 0);
    private static readonly string[] Wk = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    public MainViewModel()
    {
        Drawer = new DrawerViewModel(this);
        SeedData();
        UpdateRunCount();
    }

    private void SeedData()
    {
        Reactors.Add(new Reactor { Id = 1, State = ReactorState.React, T = 150.4, TSp = 150, P = 325.6, PSp = 320, Gas = 13.00, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 2, State = ReactorState.React, T = 150.0, TSp = 150, P = 317.3, PSp = 320, Gas = 12.45, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 3, State = ReactorState.Pressing, T = 148.0, TSp = 150, P = 269.8, PSp = 320, Gas = 0, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "吹扫" });
        Reactors.Add(new Reactor { Id = 4, State = ReactorState.Heating, T = 117.8, TSp = 150, P = 199.2, PSp = 320, Gas = 0, Rpm = 600, RpmSp = 600, Valve = false, Vol = 3, Blade = "锚式", End = "密封" });
        Reactors.Add(new Reactor { Id = 5, State = ReactorState.Alarm, T = 151.8, TSp = 150, P = 511.8, PSp = 320, Gas = 14.90, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 6, State = ReactorState.React, T = 149.4, TSp = 150, P = 317.1, PSp = 320, Gas = 12.71, Rpm = 600, RpmSp = 600, Valve = true, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 7, State = ReactorState.Done, T = 42.0, TSp = 0, P = 14.7, PSp = 0, Gas = 13.50, Rpm = 0, RpmSp = 600, Valve = false, Vol = 3, Blade = "桨式", End = "密封" });
        Reactors.Add(new Reactor { Id = 8, State = ReactorState.Idle, T = 0, TSp = 0, P = 0, PSp = 0, Gas = 0, Rpm = 0, RpmSp = 600, Valve = false, Vol = 0, Blade = "桨式", End = "密封" });

        GasInlets.Add(new GasInlet { Label = "惰性气体", On = true, P = 518 });
        GasInlets.Add(new GasInlet { Label = "气体 A", On = true, P = 520 });
        GasInlets.Add(new GasInlet { Label = "气体 B", On = false, P = 515 });

        Recipes.Add(new Recipe { Id = "hydro-pdc", Name = "加氢 · Pd/C", Sub = "苯甲酸酯加氢 · 标准条件", Tag = "催化筛选", TSp = 40, PSp = 50, RpmSp = 1000, Vol = 4, Blade = "桨式", End = "密封", Run = "02:00:00" });
        Recipes.Add(new Recipe { Id = "hydro-rh", Name = "加氢 · Rh 均相", Sub = "温和条件 · 均相催化", Tag = "催化筛选", TSp = 60, PSp = 200, RpmSp = 800, Vol = 3, Blade = "桨式", End = "密封", Run = "04:00:00" });
        Recipes.Add(new Recipe { Id = "olefin-poly", Name = "烯烃聚合", Sub = "Ziegler-Natta · 高温高压", Tag = "聚合", TSp = 80, PSp = 350, RpmSp = 1000, Vol = 4, Blade = "锚式", End = "吹扫", Run = "01:30:00" });
        Recipes.Add(new Recipe { Id = "co-carbo", Name = "CO 羰基化", Sub = "低压 CO · 长反应时间", Tag = "有机", TSp = 120, PSp = 150, RpmSp = 600, Vol = 3, Blade = "桨式", End = "淬灭", Run = "06:00:00" });
        Recipes.Add(new Recipe { Id = "leak-test", Name = "空白泄漏测试", Sub = "仅充氮 · 不加热", Tag = "诊断", TSp = 25, PSp = 500, RpmSp = 0, Vol = 0, Blade = "桨式", End = "排空", Run = "00:40:00" });
    }

    public Reactor? FindReactor(int id) => Reactors.FirstOrDefault(r => r.Id == id);

    public void RefreshSchematic() => SchematicInvalidated?.Invoke();
    public void Toast(string kind, string msg) => ToastRequested?.Invoke(kind, msg);

    // ============ 阀门手动控制（带权限/状态守卫）============
    public void TryValve(int id)
    {
        var c = FindReactor(id);
        if (c == null) return;
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先在工具栏开启「手动阀控」"); return; }
        if (!c.Valve && c.State is ReactorState.Pressing or ReactorState.React or ReactorState.Alarm)
        {
            Toast("err", $"加压/反应中禁止手动开阀（RV{id}）");
            return;
        }
        c.Valve = !c.Valve;
        RefreshSchematic();
        Drawer.RaiseAll();
        Toast(c.Valve ? "ok" : "warn", $"SV{id} {(c.Valve ? "已开" : "已关")}");
    }

    public void ToggleGas(int index)
    {
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        var g = GasInlets[index];
        g.On = !g.On;
        RefreshSchematic();
        Toast(g.On ? "ok" : "warn", $"{g.Label} 进气阀 {(g.On ? "已开" : "已关")}");
    }

    public void ToggleVent()
    {
        if (!IsAdmin) { Toast("err", "需要管理员权限"); return; }
        if (!ManualValve) { Toast("warn", "请先开启「手动阀控」"); return; }
        VentOn = !VentOn;
        RefreshSchematic();
        Toast(VentOn ? "warn" : "ok", $"排空阀 {(VentOn ? "已开" : "已关")}");
    }

    public void OpenDrawer(int id) => Drawer.Open(id);

    public void CopyConfigToAll(Reactor src)
    {
        foreach (var x in Reactors.Where(r => r.State != ReactorState.Idle))
        {
            x.TSp = src.TSp; x.PSp = src.PSp; x.RpmSp = src.RpmSp;
            x.Vol = src.Vol; x.Blade = src.Blade; x.End = src.End; x.Run = src.Run;
        }
        RefreshSchematic();
    }

    // ============ 工具栏命令 ============
    [RelayCommand]
    private void ToggleManual()
    {
        if (!IsAdmin) { Toast("err", "手动阀控需要管理员权限"); return; }
        ManualValve = !ManualValve;
        Toast(ManualValve ? "warn" : "ok",
            ManualValve ? "手动阀控已开启 · 点击阀门可手动操作" : "手动阀控已关闭");
    }

    [RelayCommand]
    private void StopAll()
    {
        foreach (var c in Reactors.Where(r => r.State != ReactorState.Idle))
        {
            c.State = ReactorState.Done;
            c.Valve = false;
            c.Rpm = 0;
        }
        RefreshSchematic();
        Drawer.RaiseAll();
        UpdateRunCount();
        Toast("warn", "已停止全部通道");
    }

    [RelayCommand]
    private void RunAll()
    {
        if (Estopped) { Toast("err", "急停状态下不可启动 · 请先复位"); return; }
        Toast("ok", "（演示）预运行检查 — 后续页面接入");
    }

    [RelayCommand]
    private void Estop() => Toast("err", "（演示）急停确认弹窗 — 后续接入");

    [RelayCommand]
    private void Logout() => Toast("ok", "（演示）退出登录");

    [RelayCommand]
    private void SwitchTabCmd(string id) => SwitchTab(id);

    public void SwitchTab(string id)
    {
        ActiveTab = id;
        TabSwitched?.Invoke(id);
    }

    public void UpdateRunCount()
        => RunCountText = $"{Reactors.Count(r => r.State != ReactorState.Idle)} / 8";

    // ============ 实时数据 tick（对应 HTML setInterval）============
    public void Tick()
    {
        foreach (var c in Reactors)
        {
            if (c.State is ReactorState.Idle or ReactorState.Done) continue;
            c.T += (Random.Shared.NextDouble() - 0.5) * 0.3;
            if (c.State == ReactorState.React)
            {
                c.P += (Random.Shared.NextDouble() - 0.5) * 1.2;
                c.Gas += Random.Shared.NextDouble() * 0.02;
            }
            else if (c.State == ReactorState.Pressing)
            {
                c.P = Math.Min(c.PSp, c.P + Random.Shared.NextDouble() * 2);
            }
            else if (c.State == ReactorState.Heating)
            {
                c.T = Math.Min(c.TSp, c.T + Random.Shared.NextDouble() * 0.4);
            }
        }
        RefreshSchematic();
        if (Drawer.IsOpen) Drawer.RaiseAll();
    }

    public void ClockTick()
    {
        _clock = _clock.AddSeconds(1);
        ClockTime = _clock.ToString("HH:mm:ss");
        ClockDate = _clock.ToString("yyyy-MM-dd") + " " + Wk[(int)_clock.DayOfWeek];
    }
}
