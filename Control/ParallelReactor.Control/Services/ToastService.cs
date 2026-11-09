using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using ParallelReactor.Control.Theme;
using PrBehaviors = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Services;

/// <summary>Toast 的语气（样稿 PR.toast 的 kind）：ok = 白色 ✓（成功不用绿色）· info = 强调色点 · warn = 灰点 · err = 红点。</summary>
public enum ToastKind { Ok, Info, Warn, Err }

/// <summary>一条 toast（不可变）。</summary>
public sealed class ToastItem
{
    public ToastItem(string text, ToastKind kind) { Text = text; Kind = kind; }

    public string Text { get; }
    public ToastKind Kind { get; }
    public bool IsOk => Kind == ToastKind.Ok;
    public bool IsInfo => Kind == ToastKind.Info;
    public bool IsWarn => Kind == ToastKind.Warn;
    public bool IsErr => Kind == ToastKind.Err;
}

/// <summary>
/// Toast（PORT-PLAN §2.1「Toast」；样稿 shell.js PR.toast / css .pr-toasts）：距外框底 48、居中、深色、15 px、语气点、可堆叠（最多 3 条，最早的先走），
/// ok / info / warn 显示 2.6 s，err 4.2 s。界面在 Views/Shell/ToastHost。
/// 被 b:Pr.Reason 拦下的操作（Behaviors/Reason.cs 的 ReasonRequested）也在这里说明原因（warn 语气，同样稿的禁用按钮点击）。
/// 只在 UI 线程上调用。截图时（冻结）不自动消失，方便拍到。
/// </summary>
public sealed class ToastService : IDisposable
{
    public const int MaxShown = 3;
    public static readonly TimeSpan ShortFor = TimeSpan.FromMilliseconds(2600), LongFor = TimeSpan.FromMilliseconds(4200);

    public ObservableCollection<ToastItem> Items { get; } = new();

    /// <summary>冻结（演示截图）：toast 不自动消失。</summary>
    public bool Frozen { get; set; }

    public ToastService()
    {
        PrBehaviors.ReasonRequested += OnReason;
    }

    private void OnReason(Avalonia.Controls.Control c, string reason) => Show(UiText.Plain(reason), ToastKind.Warn);

    public void Show(string text, ToastKind kind = ToastKind.Ok)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var item = new ToastItem(text, kind);
        Items.Add(item);
        while (Items.Count > MaxShown) Items.RemoveAt(0);
        if (Frozen) return;
        DispatcherTimer.RunOnce(() => Items.Remove(item), kind == ToastKind.Err ? LongFor : ShortFor);
    }

    public void Clear() => Items.Clear();

    public void Dispose() => PrBehaviors.ReasonRequested -= OnReason;
}
