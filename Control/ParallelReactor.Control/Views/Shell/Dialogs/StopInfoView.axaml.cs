using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Shell.Dialogs;

namespace ParallelReactor.Control.Views.Shell.Dialogs;

/// <summary>停止类对话框的说明 + 表 + 琥珀说明（见 StopInfoView.axaml）：从 DataContext（急停 / 全部停止 / 停止一路的视图模型）取内容。</summary>
public partial class StopInfoView : UserControl
{
    public StopInfoView() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        (IReadOnlyList<TextSeg> lead, IReadOnlyList<StopRow> rows, IReadOnlyList<TextSeg> note) = DataContext switch
        {
            PickDialogVm p => (p.Lead, p.StopRows, p.StopNote),
            StopChannelDialogVm c => (c.Lead, c.Rows, c.StopNote),
            _ => (System.Array.Empty<TextSeg>(), System.Array.Empty<StopRow>(), System.Array.Empty<TextSeg>()),
        };
        LeadText.Segments = lead;
        Table.ItemsSource = rows;
        NoteText.Segments = note;
    }
}
