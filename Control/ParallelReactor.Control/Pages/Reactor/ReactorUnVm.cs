using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 反应釜页 · 未安装的位置（样稿 reactor.js unPanel / slotsHTML，css .reactor-un*）：一块安静的说明页 —— 虚线空位（大号釜体的未安装画法）+ 「1号机 · RV5 未安装」+
/// 说明 + 键值表（型号 / 通道 / 釜 / 说明）+ 这台设备的全部位置（当前的加框；已安装的点一下打开）+ 去已安装的反应釜（状态点 + RVn + 状态字）+ 「打开1号机设备页」。
/// 文字逐字照样稿（Core 的 ReactorPageText）；就地更新（位置与芯片按反应釜号保留实例）。
/// </summary>
public sealed partial class ReactorUnVm : ObservableObject
{
    private readonly ShellVm? _shell;

    public ReactorUnVm(ShellVm? shell, int k, int n)
    {
        _shell = shell;
        K = k;
        N = n;
        OpenCommand = new RelayCommand(() => _shell?.Navigate(Route.Device(K)));
        Caption = ReactorPageText.EmptySlot(n);
    }

    public int K { get; }
    public int N { get; }

    [ObservableProperty] private string _crumb = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
    /// <summary>键值表的值（键：型号 / 通道 / 釜 / 说明）。</summary>
    [ObservableProperty] private string _kvModel = "";
    [ObservableProperty] private string _kvChannels = "";
    [ObservableProperty] private string _kvVessel = "";
    [ObservableProperty] private string _kvNote = "";
    /// <summary>'RV5 · 空位'。</summary>
    public string Caption { get; }
    [ObservableProperty] private string _slotsTitle = "";
    public string ChipsTitle => ReactorPageText.ChipsTitle;
    [ObservableProperty] private string _openText = "";
    public ICommand OpenCommand { get; }

    /// <summary>这台设备的全部位置（按反应釜号）。</summary>
    public ObservableCollection<UnSlotVm> Slots { get; } = new();
    /// <summary>已安装的反应釜（去那里）。</summary>
    public ObservableCollection<UnChipVm> Chips { get; } = new();

    public void Refresh(DeviceVm? d)
    {
        if (d is null) return;
        var m = d.Model;
        Crumb = d.Alias + " › RV" + N.ToString(CultureInfo.InvariantCulture);
        Title = ReactorPageText.UninstalledTitle(d.Alias, N);
        Text = ReactorPageText.UninstalledText(d.Alias, m);
        var kv = ReactorPageText.UninstalledKv(m);
        KvModel = kv[0].Value;
        KvChannels = kv[1].Value;
        KvVessel = kv[2].Value;
        KvNote = kv[3].Value;
        SlotsTitle = ReactorPageText.SlotsTitle(d.Alias, m);
        OpenText = ReactorPageText.OpenDevice(d.Alias);

        // 位置：按反应釜号保留实例
        while (Slots.Count > d.Reactors.Count) Slots.RemoveAt(Slots.Count - 1);
        for (int i = Slots.Count; i < d.Reactors.Count; i++) Slots.Add(new UnSlotVm(this, d.Reactors[i].N));
        int chips = 0;
        foreach (var r in d.Reactors)
        {
            var s = Slots[r.N - 1];
            s.Installed = r.Installed;
            s.IsCur = r.N == N;
            s.Key = r.Key;
            s.Tip = ReactorPageText.SlotTip(d.Alias, r.N, r.Installed, r.Word);
            if (!r.Installed) continue;
            if (chips >= Chips.Count || Chips[chips].N != r.N)
            {
                int at = -1;
                for (int j = chips; j < Chips.Count; j++) if (Chips[j].N == r.N) { at = j; break; }
                if (at >= 0) Chips.Move(at, chips);
                else Chips.Insert(chips, new UnChipVm(this, r.N));
            }
            var c = Chips[chips++];
            c.Key = r.Key;
            c.Word = r.Word;
            c.Tip = ReactorPageText.ChipTip(d.Alias, r.N);
        }
        while (Chips.Count > chips) Chips.RemoveAt(Chips.Count - 1);
    }

    internal void Open(int n) => _shell?.Navigate(Route.Reactor(K, n));
}

/// <summary>位置按钮（样稿 .reactor-slot：44 × 58，小釜体 22 + 号）：未安装的虚线、不能点；当前的加 2 px a-line 框。</summary>
public sealed partial class UnSlotVm : ObservableObject
{
    public UnSlotVm(ReactorUnVm owner, int n)
    {
        N = n;
        Number = n.ToString(CultureInfo.InvariantCulture);
        OpenCommand = new RelayCommand(() => { if (Installed && !IsCur) owner.Open(N); });
    }

    public int N { get; }
    public string Number { get; }
    public ICommand OpenCommand { get; }
    [ObservableProperty] private bool _installed;
    [ObservableProperty] private bool _isCur;
    [ObservableProperty] private StateKey _key = StateKey.Uninstalled;
    [ObservableProperty] private string _tip = "";
}

/// <summary>「去已安装的反应釜」芯片（样稿 .reactor-unchip：状态点 + RVn + 状态字）。</summary>
public sealed partial class UnChipVm : ObservableObject
{
    public UnChipVm(ReactorUnVm owner, int n)
    {
        N = n;
        Rv = "RV" + n.ToString(CultureInfo.InvariantCulture);
        OpenCommand = new RelayCommand(() => owner.Open(N));
    }

    public int N { get; }
    public string Rv { get; }
    public ICommand OpenCommand { get; }
    [ObservableProperty] private StateKey _key = StateKey.Idle;
    [ObservableProperty] private string _word = "";
    [ObservableProperty] private string _tip = "";
}
