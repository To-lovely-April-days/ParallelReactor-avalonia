using Avalonia.Controls;

namespace ParallelReactor.Control.Views.Pages;

/// <summary>
/// 第二阶段视图的占位页（新建程序 / 全部曲线 / 性能测试 / 记录 / 报警 / 设置），文字照抄样稿。
/// 说明句子同样稿不做「不乱断行」处理（样稿 pages/placeholder.js 直接输出 esc(p.text)，不用 PR.keep）：断行位置与样稿一致。
/// </summary>
public partial class PlaceholderPage : UserControl
{
    public PlaceholderPage() => InitializeComponent();
}
