using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ParallelReactor.Control.Pages.Programs;

namespace ParallelReactor.Control.Views.Pages.Programs;

/// <summary>
/// 程序页（见 ProgramPage.axaml）。列宽按模式和宽窄设（样稿 .board.pg-prog 的 grid-template-columns，列宽不能用样式改）；
/// 文件对话框（导入 / 导出程序、CSV）经 TopLevel 的 StorageProvider 交给视图模型。
/// 输入框是失焦提交（UpdateSourceTrigger=LostFocus）：回车也提交（让出焦点）。
/// </summary>
public partial class ProgramPage : UserControl
{
    private ProgramPageVm? _vm;

    public ProgramPage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.PropertyChanged -= OnVm; _vm.OpenFile = null; _vm.SaveFile = null; }
        _vm = DataContext as ProgramPageVm;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVm;
            _vm.OpenFile = OpenAsync;
            _vm.SaveFile = SaveAsync;
            Apply();
        }
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProgramPageVm.IsMatrix) or nameof(ProgramPageVm.IsNarrow)) Apply();
    }

    /// <summary>程序库这一列：单路 268（窄屏 236），矩阵 248（窄屏 200）。</summary>
    private void Apply()
    {
        if (_vm == null) return;
        var root = this.FindControl<Grid>("Root");
        if (root == null) return;
        double w = _vm.IsMatrix ? (_vm.IsNarrow ? 200 : 248) : (_vm.IsNarrow ? 236 : 268);
        if (root.ColumnDefinitions[0].Width.Value != w) root.ColumnDefinitions[0].Width = new GridLength(w);
    }

    /// <summary>回车 = 提交这一格（输入框都是失焦提交）：让出焦点；上下键在溶液体积框里 ±0.1 mL。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is not TextBox tb) return;
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
        else if (tb.Name == "Vol" && e.Key is Key.Up or Key.Down && _vm != null)
        {
            _vm.One.VolText = tb.Text ?? "";
            _vm.One.StepVol(e.Key == Key.Up ? 1 : -1);
            tb.Text = _vm.One.VolText;
            tb.CaretIndex = tb.Text.Length;
            e.Handled = true;
        }
    }

    private async Task<string?> OpenAsync(string title, string ext)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return null;
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> SaveAsync(string title, string name, string ext)
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp is null) return null;
        var f = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = name,
            DefaultExtension = ext,
            FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant()) { Patterns = new[] { "*." + ext } } },
        });
        return f?.TryGetLocalPath();
    }
}
