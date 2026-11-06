namespace ParallelReactor.Commands;

/// <summary>
/// 严格守卫开关（设计稿 §2.4）：§7 标为「补」的检查（通道须 Ready / Done 才能启动加热、报警通道不能启用、
/// 曲线运行中不能改段、SP 须小于 OverTemp……）一旦对本地生效就会改变本地操作员的体验，
/// 所以本期只对远程生效。命令层统一写成 <c>if (CommandPolicy.Strict(src) &amp;&amp; …)</c>。
/// </summary>
public static class CommandPolicy
{
    /// <summary>本期固定 false；测试可改（改完须恢复）。</summary>
    public static bool StrictLocal { get; set; } = false;
    public static bool Strict(CommandSource s) => s.IsRemote || StrictLocal;
}
