using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>状态行的一项（DataTemplate 用）。</summary>
public sealed record StateSample(StateKey Key);

public partial class BadgesSection : UserControl
{
    /// <summary>状态点 + 状态字一行：顺序同样稿（Core StateKeys.GalleryOrder）。</summary>
    public static IReadOnlyList<StateSample> Samples { get; } = StateKeys.GalleryOrder.Select(k => new StateSample(k)).ToList();

    public BadgesSection() => InitializeComponent();
}
