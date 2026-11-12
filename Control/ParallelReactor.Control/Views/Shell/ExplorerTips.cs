using System.Linq;
using Avalonia.Data.Converters;

namespace ParallelReactor.Control.Views.Shell;

/// <summary>资源管理器的悬停提示（样稿 shell.js renderExplorer 写的 title）。</summary>
public static class ExplorerTips
{
    /// <summary>
    /// 断线细节条整条的提示（样稿 .pr-xoff 的 title：connInfo.text · dataText，例如「连接中断 · 重连中 12 s · 数据停在 10:12:00」）。
    /// 值：[IsConn, D.ConnText, D.DataText]。控制权宽限那一条（IsConn = false）整条没有提示：样稿只给它的文字加 title。
    /// </summary>
    public static readonly IMultiValueConverter XOff = new FuncMultiValueConverter<object?, string?>(values =>
    {
        var v = values.ToList();
        if (v.Count < 3 || v[0] is not true) return null;
        var conn = v[1] as string ?? "";
        var data = v[2] as string ?? "";
        return data.Length > 0 ? conn + " · " + data : conn;
    });
}
