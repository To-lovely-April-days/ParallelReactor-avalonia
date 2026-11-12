using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ParallelReactor.Control.Controls.Pr;

/// <summary>
/// 一张表的列宽（样稿 ui.table：HTML 表格的自动布局）。表头和每一行（<see cref="TableRow"/>）共用一个实例：
/// 每列的宽 = 这一列各单元格内容宽的最大值（加左右内边距）；表格比内容宽时，多出来的宽度按各列内容宽的比例分给各列（同浏览器的自动表格布局）。
/// 最大值只增不减（对话框开着时别名改短了也不收窄，避免抖动）。
/// 表格比内容窄时（真机的长字，例如第 5 种控制权「其他客户端 PC-工位2」；样稿的字都放得下），<see cref="Shrinkable"/> 的列按各自宽度的比例让出不够的宽度
/// （最窄 <see cref="MinShrunk"/>），这些列里的内容按缩后的宽度重新量（徽标的字省略，完整文字在悬停提示里）；没有可缩的列时照旧按内容宽排（超出表格）。
/// </summary>
public sealed class TableColumns
{
    private readonly double[] _max;
    private readonly bool[] _shrink;

    /// <param name="shrinkable">放不下时可以缩窄的列（0 起）。</param>
    public TableColumns(int count, params int[] shrinkable)
    {
        _max = new double[count];
        _shrink = new bool[count];
        foreach (var c in shrinkable) if (c >= 0 && c < count) _shrink[c] = true;
    }

    /// <summary>可缩列最窄缩到多宽（含左右内边距）。</summary>
    public const double MinShrunk = 72;

    /// <summary>第 <paramref name="col"/> 列放不下时可以缩窄。</summary>
    public bool Shrinkable(int col) => col >= 0 && col < _shrink.Length && _shrink[col];

    public int Count => _max.Length;

    /// <summary>某一列的内容宽变大了（各行在测量时报告）：所有行重新排列。</summary>
    public event Action? Changed;

    internal void Report(int col, double width)
    {
        if (col < 0 || col >= _max.Length || width <= _max[col] + 0.01) return;
        _max[col] = width;
        Changed?.Invoke();
    }

    /// <summary>可用宽度 <paramref name="available"/> 下各列的宽（内容宽 + 按比例分到的余量；放不下时可缩列让出不够的部分）。</summary>
    public double[] Widths(double available)
    {
        var w = (double[])_max.Clone();
        double sum = 0;
        foreach (var x in w) sum += x;
        if (sum <= 0 || double.IsInfinity(available)) return w;
        if (available < sum - 0.01)
        {
            double need = sum - available, room = 0;
            for (int i = 0; i < w.Length; i++) if (_shrink[i]) room += Math.Max(0, w[i] - MinShrunk);
            if (room <= 0) return w;
            double take = Math.Min(need, room);
            for (int i = 0; i < w.Length; i++)
                if (_shrink[i]) w[i] -= take * Math.Max(0, w[i] - MinShrunk) / room;
            return w;
        }
        double extra = available - sum;
        for (int i = 0; i < w.Length; i++) w[i] += extra * _max[i] / sum;
        return w;
    }
}

/// <summary>
/// 表格的一行（表头也是一行）：子元素按附加属性 <see cref="ColumnProperty"/> 放进各列，列宽来自共用的 <see cref="Columns"/>；
/// 单元格左右各 <see cref="CellPadding"/>（样稿 td / th 的 padding 0 12px），子元素在单元格里按自己的 HorizontalAlignment 放，垂直居中。
/// 单元格内容不换行（样稿 white-space: nowrap）：按无限宽测量。
/// </summary>
public sealed class TableRow : Panel
{
    public static readonly StyledProperty<TableColumns?> ColumnsProperty = AvaloniaProperty.Register<TableRow, TableColumns?>(nameof(Columns));
    public static readonly StyledProperty<double> CellPaddingProperty = AvaloniaProperty.Register<TableRow, double>(nameof(CellPadding), 12);
    public static readonly AttachedProperty<int> ColumnProperty = AvaloniaProperty.RegisterAttached<TableRow, Avalonia.Controls.Control, int>("Column");

    static TableRow()
    {
        AffectsMeasure<TableRow>(ColumnsProperty, CellPaddingProperty);
        ColumnProperty.Changed.AddClassHandler<Avalonia.Controls.Control>((c, _) => (c.Parent as TableRow)?.InvalidateMeasure());
    }

    public TableColumns? Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double CellPadding { get => GetValue(CellPaddingProperty); set => SetValue(CellPaddingProperty, value); }

    public static int GetColumn(Avalonia.Controls.Control c) => c.GetValue(ColumnProperty);
    public static void SetColumn(Avalonia.Controls.Control c, int value) => c.SetValue(ColumnProperty, value);

    private TableColumns? _hooked;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ColumnsProperty) Hook(IsAttachedToVisualTree() ? Columns : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook(Columns);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Hook(null);
    }

    private bool IsAttachedToVisualTree() => this.GetVisualRoot() != null;

    private void Hook(TableColumns? c)
    {
        if (ReferenceEquals(c, _hooked)) return;
        if (_hooked != null) _hooked.Changed -= OnColumnsChanged;
        _hooked = c;
        if (c != null) c.Changed += OnColumnsChanged;
    }

    /// <summary>某一列变宽了：重新量（可缩列缩多少跟着变，里面的内容要按新宽度重新量）再排。</summary>
    private void OnColumnsChanged() => InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        double h = 0, pad = CellPadding * 2;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            h = Math.Max(h, child.DesiredSize.Height);
            Columns?.Report(GetColumn(child), child.DesiredSize.Width + pad);
        }
        // 放不下时缩窄的列：里面的内容按缩后的宽度重新量（徽标的字省略）
        if (Columns is { } cols && !double.IsInfinity(availableSize.Width))
        {
            var widths = cols.Widths(availableSize.Width);
            foreach (var child in Children)
            {
                int col = GetColumn(child);
                if (!cols.Shrinkable(col) || col >= widths.Length) continue;
                double room = Math.Max(0, widths[col] - pad);
                if (child.DesiredSize.Width > room + 0.01) child.Measure(new Size(room, availableSize.Height));
            }
        }
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var widths = Columns?.Widths(finalSize.Width);
        double pad = CellPadding;
        foreach (var child in Children)
        {
            int col = GetColumn(child);
            double x = 0, w = finalSize.Width;
            if (widths != null && col >= 0 && col < widths.Length)
            {
                for (int i = 0; i < col; i++) x += widths[i];
                w = widths[col];
            }
            var cell = new Rect(x + pad, 0, Math.Max(0, w - 2 * pad), finalSize.Height);
            var d = child.DesiredSize;
            double cw = Math.Min(d.Width, cell.Width);
            double cx = child.HorizontalAlignment switch
            {
                HorizontalAlignment.Right => cell.Right - cw,
                HorizontalAlignment.Center => cell.X + (cell.Width - cw) / 2,
                HorizontalAlignment.Stretch => cell.X,
                _ => cell.X,
            };
            if (child.HorizontalAlignment == HorizontalAlignment.Stretch) cw = cell.Width;
            double ch = Math.Min(d.Height, finalSize.Height);
            child.Arrange(new Rect(cx, (finalSize.Height - ch) / 2, cw, ch));
        }
        return finalSize;
    }
}
