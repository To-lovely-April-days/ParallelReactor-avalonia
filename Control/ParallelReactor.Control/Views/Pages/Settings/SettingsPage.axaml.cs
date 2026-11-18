using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ParallelReactor.Control.Pages.Settings;

namespace ParallelReactor.Control.Views.Pages.Settings;

/// <summary>
/// 设置页（见 SettingsPage.axaml）。选文件夹（数据目录「浏览」）、选配置文件（「导入配置」）经 TopLevel 的 StorageProvider 交给视图模型；
/// 回车 = 让出焦点（输入框照常提交）。
/// </summary>
public partial class SettingsPage : UserControl
{
    private SettingsPageVm? _vm;

    public SettingsPage()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null) { _vm.PickFolder = null; _vm.OpenFile = null; }
        _vm = DataContext as SettingsPageVm;
        if (_vm != null) { _vm.PickFolder = PickFolderAsync; _vm.OpenFile = OpenAsync; }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source is TextBox && e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private async Task<string?> PickFolderAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || !sp.CanPickFolder) return null;
        var r = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择数据目录", AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> OpenAsync()
    {
        var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (sp == null || !sp.CanOpen) return null;
        var r = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入配置", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("配置文件") { Patterns = new[] { "*.json" } } },
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }
}
