using System;

namespace ParallelReactor.Commands;

/// <summary>
/// 写入口登记：该方法（触屏 RelayCommand / 键盘回调所在方法 / 气路图回调）把写操作转给列出的 <c>OperatorCommands.Try*</c>。
/// 只供架构测试做源码扫描，运行时不读取。写法：<c>[WriteEntry(nameof(OperatorCommands.TryStopHeat))]</c>。
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class WriteEntryAttribute : Attribute
{
    public WriteEntryAttribute(params string[] commands) => Commands = commands;
    public string[] Commands { get; }
}

/// <summary>只改视图的入口（切页、开关对话框、键盘按键……），不经命令层。参数写明原因。只供架构测试扫描。</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class ViewEntryAttribute : Attribute
{
    public ViewEntryAttribute(string reason) => Reason = reason;
    public string Reason { get; }
}
