using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// C25 的约束机制：触屏写入口必须经 <c>OperatorCommands.Try*</c>（设计稿 §6.2）。
/// 测试工程不引用主工程，所以用源码扫描（<see cref="SourceScan"/>）检查：
/// <list type="bullet">
/// <item>R1 每个 [RelayCommand] 恰好带 [WriteEntry] 或 [ViewEntry] 之一；</item>
/// <item>R2 [WriteEntry] 的方法体调用了它登记的每个 .TryX(；</item>
/// <item>R3 登记的 TryX 在 Commands/OperatorCommands*.cs 中有 public Task&lt;OpResult&gt; TryX( 声明；</item>
/// <item>R4 Try* 不是 async、首句是仲裁、命令层不调用 Toast(；</item>
/// <item>R5 Views / Controls 代码后置对 ViewModel 的调用（任何绑定到 XxxViewModel 的标识符，按「类型.方法」解析）只能落到登记过的入口或只读成员；</item>
/// <item>R6 ViewModels 里直接写设备的调用只出现在白名单方法里：token = 手写项 + 从服务源码自动生成的写 API + 白名单方法本身（传递闭包）；入口不得进白名单；</item>
/// <item>R7 每个 Try* 的仲裁类别（Control / Stop / Record / Reset）与 Exposure 与登记表一致（Remote 模式自检的静态保证，§8）；</item>
/// <item>R8 入口（[ViewEntry] / [WriteEntry]）体内不得直接改过程状态（通道状态 / 设定值、曲线、程序步骤），Ops.TryX( 的实参除外。</item>
/// </list>
/// 白名单只减不增，每项带理由；批 B / C 完成后不再有待迁移清单。
/// </summary>
public class ArchitectureTests
{
    // ======================================================================
    // 白名单（只减不增）。键为「文件名:方法名」（嵌套类型为「文件名:类型.方法名」），值为理由。
    // 批 B / C 已完成：待迁移清单（PendingEntries / PendingViewCalls）已清空并删除——
    // 每个 [RelayCommand] 都必须标 [WriteEntry] 或 [ViewEntry("原因")]，没有例外。
    // ======================================================================

    /// <summary>
    /// R5：View / Controls 可以直接调用的只读 / 纯视图成员（不写设备、不改过程状态）。
    /// 键为「类型.方法」，或落在 VM 集合 / 子对象上的「类型.成员.调用」。
    /// </summary>
    public static readonly Dictionary<string, string> ViewReadAllowlist = new(StringComparer.Ordinal)
    {
        ["GraphViewModel.Val"] = "绘图取样：只读",
        ["GraphViewModel.WallTime"] = "绘图：采样序号 → 时刻，只读",
        ["GraphViewModel.MetricName"] = "静态：指标名称",
        ["GraphViewModel.MetricUnit"] = "静态：指标单位",
        ["GraphViewModel.MetricDecimals"] = "静态：指标小数位",
        ["GraphViewModel.Channels.Where"] = "绘图：筛选显示中的曲线（LINQ 只读）",
    };

    /// <summary>
    /// R6：允许直接写设备的 VM 方法（设计稿 §5.11：不经触屏入口的设备写——Tick、程序引擎、泄漏测试流程、急停、开机、逼近，
    /// 以及命令层调用的 VM 原语）。每项必须写明理由；只减不增：新增出现会失败并提示「请经 OperatorCommands」。
    /// 白名单里的方法同时是「写原语」token：VM 里其他成员调用它们同样违规（只有命令层、白名单内彼此可以调用）。
    /// </summary>
    public static readonly Dictionary<string, string> DeviceWriteAllowlist = new(StringComparer.Ordinal)
    {
        // —— 开机（§5.11）——
        ["MainViewModel.cs:MainViewModel"] = "开机：按 8AI 内部满程回填「变送器满量程」设置显示（读回，不是操作员写入）",
        ["MainViewModel.cs:InitStirMicrostepAsync"] = "开机：从驱动器回填细分到设置显示（读回，不是操作员写入）",
        // —— 搅拌属性的 setter：命令层（TrySetStir / TrySetStirRpm / TryRun）与程序引擎（StepStirAsync / ReleaseStir）改属性，setter 负责下发帧 ——
        ["MainViewModel.cs:OnStirOnChanged"] = "StirOn setter 的下发：唯一调用方是命令层与程序引擎 / 急停",
        ["MainViewModel.cs:OnStirRpmChanged"] = "StirRpm setter 的下发：唯一调用方是命令层与程序引擎",
        // —— 命令层调用的加热 / 停加热 / 关阀原语（TryStartHeat、TryStopHeat、TryStopChannel、TryStartProfile……）——
        ["MainViewModel.cs:WriteTempSpSafeAsync"] = "VM 原语：写 SP（命令层定值加热、逼近每 Tick 写 SP 共用）",
        ["MainViewModel.cs:StartFixedHeatAsync"] = "VM 原语：定值加热启动链（命令层 TryStartHeat 与程序引擎共用）",
        ["MainViewModel.cs:StartBandHeatAsync"] = "VM 原语：按目标温度套档启动加热（命令层曲线启动与程序引擎共用）",
        ["MainViewModel.cs:StopHeatCoreAsync"] = "VM 原语：停加热输出 At=4（命令层停止类命令、程序引擎、Tick 共用）",
        ["MainViewModel.cs:CloseReactorValveCoreAsync"] = "VM 原语：关某通道进气阀（命令层停止通道、程序收尾共用）",
        ["MainViewModel.cs:StartFixedHeat"] = "VM 原语：定值加热启动（规划逼近首个 SP 后调 StartFixedHeatAsync；命令层 TryStartHeat 与程序引擎共用）",
        ["MainViewModel.cs:SetFixedTarget"] = "VM 原语：定值改目标（加热中交给逼近重规划，否则直接写 SP；命令层 TrySetChannelTemp 调用）",
        ["MainViewModel.cs:StopHeatAsync"] = "非命令路径的停加热 + 提示（程序引擎 / Tick 用；命令层用 StopHeatCoreAsync）",
        ["MainViewModel.cs:CloseReactorValveAsync"] = "非命令路径的关进气阀 + 提示（程序引擎收尾用；命令层用 CloseReactorValveCoreAsync）",
        ["MainViewModel.Gas.cs:CloseAllValvesAsync"] = "VM 原语：尽力全关阀（气路序列收尾、命令层全部停止 / 退出 / 手动阀补关、急停 UI 流程共用）",
        ["MainViewModel.Gas.cs:WriteValvesAsync"] = "VM 原语：整帧写阀（ApplyValvesAsync / HoldAsync 共用，只被气路序列调用）",
        ["MainViewModel.Gas.cs:ReleaseChannelFromGasAsync"] = "C9：停止 / 停用通道时中止占用该通道的程序 / 序列（命令层 TryStopChannel / TryEnableChannel(false) 调用）",
        // —— Tick：曲线引擎 / 逼近 / 超温超压联锁（§5.11）——
        ["MainViewModel.cs:WriteCurveSpAsync"] = "Tick 的曲线引擎：逐周期写 SP",
        ["MainViewModel.cs:WriteApproachSpAsync"] = "Tick 的防超调逼近：写中间 SP",
        ["MainViewModel.Logging.cs:SendOverTempStopAsync"] = "Tick 的超温联锁：停该通道加热",
        ["MainViewModel.Logging.cs:SendOverPressureHeatAsync"] = "Tick 的超压联锁：停该通道加热",
        ["MainViewModel.Logging.cs:FlushOverPressureValves"] = "Tick 的超压联锁：下发待关的阀",
        ["MainViewModel.Logging.cs:CheckOverTemp"] = "Tick 的超温联锁：判定并停加热",
        ["MainViewModel.Logging.cs:CheckOverPressure"] = "Tick 的超压联锁：判定并停加热 / 关阀",
        ["MainViewModel.cs:StepApproaches"] = "Tick 的防超调逼近：推进各通道中间 SP",
        ["MainViewModel.cs:Tick"] = "控制循环（ControlLoop 每 1.2 s 调用）：曲线 / 逼近 / 联锁",
        ["MainViewModel.Gas.cs:ApplyValvesAsync"] = "VM 原语：气路序列整帧写阀并同步界面（只被气路序列调用）",
        ["MainViewModel.Gas.cs:HoldAsync"] = "VM 原语：气路序列保持阀态（心跳重写当前帧）",
        ["MainViewModel.Gas.cs:RunPurgeAsync"] = "气路序列：吹扫（程序引擎吹扫步骤调用）",
        ["MainViewModel.Gas.cs:AbortGasAndWaitAsync"] = "中止气路序列并等收尾、补一次全关（命令层退出、泄漏 / 程序中止共用）",
        // —— 急停 / 超压联锁 / 泄漏测试流程 / 程序引擎（§5.11）——
        ["MainViewModel.Estop.cs:TriggerEstopAsync"] = "急停流程：全关阀、停加热、停搅拌（任意线程快速路径之后的 UI 流程）",
        ["MainViewModel.Estop.cs:StirStopOp"] = "急停流程：停搅拌帧",
        ["MainViewModel.Estop.cs:FastStirStopOp"] = "急停快速路径：停搅拌帧（不进 UI 线程）",
        ["MainViewModel.Estop.cs:FastHeatStopOp"] = "急停快速路径：停加热帧（不进 UI 线程）",
        ["MainViewModel.Estop.cs:FastValveCloseOp"] = "急停快速路径：全关阀帧（不进 UI 线程）",
        ["MainViewModel.Estop.cs:RequestEstop"] = "急停快速路径入口（命令层 TryEstop 调用，任意线程）",
        ["MainViewModel.Estop.cs:LockServices"] = "急停：置 / 解三个服务锁（快速路径与复位共用）",
        ["MainViewModel.Estop.cs:RestoreEstopLatch"] = "开机发现急停锁存（C23）：直接进入急停流程",
        ["MainViewModel.Estop.cs:RetryEstopAsync"] = "急停流程：未确认的停止帧自动补发",
        ["MainViewModel.Estop.cs:HeatStopTimed"] = "急停流程：停加热帧（计时）",
        ["MainViewModel.Estop.cs:ValveCloseOp"] = "急停流程：全关阀帧",
        ["MainViewModel.Estop.cs:OnEstoppedChanged"] = "Estopped setter 回调：置 / 解服务锁（急停流程、复位、开机锁存）",
        ["MainViewModel.Estop.cs:EstopRetryTick"] = "急停流程：补发定时器",
        ["MainViewModel.Estop.cs:ResetEstopCore"] = "急停复位（命令层 TryResetEstop 调用，只允许本地）",
        ["ProgramViewModel.Run.cs:ExecuteAsync"] = "程序引擎主循环（TryRunProgram 启动的后台流程）",
        ["ProgramViewModel.Run.cs:StepPurgeAsync"] = "程序引擎：吹扫步骤",
        ["ProgramViewModel.Run.cs:StepPressAsync"] = "程序引擎：加压步骤",
        ["ProgramViewModel.Run.cs:StepEndAsync"] = "程序引擎：收尾步骤",
        ["ProgramViewModel.Run.cs:RegulateAsync"] = "程序引擎：后台稳压回路",
        ["ProgramViewModel.Run.cs:StartRegulation"] = "程序引擎：启动后台稳压回路",
        ["ProgramViewModel.Run.cs:FillToAsync"] = "程序引擎：充压到目标",
        ["ProgramViewModel.cs:StartRun"] = "程序引擎启动（命令层 TryRunProgram 调用）",
        ["LeakViewModel.cs:FillAsync"] = "泄漏测试流程：充压",
        ["LeakViewModel.cs:SettleAsync"] = "泄漏测试流程：平衡（保持阀态）",
        ["LeakViewModel.cs:MeasureAsync"] = "泄漏测试流程：测量（保持阀态）",
        ["LeakViewModel.cs:TryVentAsync"] = "泄漏测试流程：排空",
        ["LeakViewModel.cs:RunAsync"] = "泄漏测试流程主体（TryStartLeakTest 启动的后台流程）",
        ["ProgramViewModel.Run.cs:RunStepAsync"] = "程序引擎：执行一步（TryRunProgram 启动的后台流程）",
        ["MainViewModel.Logging.cs:SendOverPressureValvesAsync"] = "Tick 的超压联锁：关该通道 SV 与全部气源",
        ["LeakViewModel.cs:Finish"] = "泄漏测试流程收尾（TryStartLeakTest 启动的后台流程）",
        ["ProgramViewModel.Run.cs:AbortProgram"] = "程序引擎中止（按通道）：停曲线 / 逼近、释放搅拌引用、停加热、中止自己的气路租约（TryAbortProgram、TryStopHeat、停止通道共用）",
        ["ProgramViewModel.Run.cs:AbortAllPrograms"] = "程序引擎：中止全部并行程序（全部停止、急停共用，逐通道调 AbortProgram）",
        ["ProgramViewModel.Run.cs:AcquireGasAsync"] = "程序引擎：排队取气路租约（吹扫 / 升压 / 收尾 / 稳压脉冲共用，多程序并行按 GasQueue 先到先得）",
        ["ProgramViewModel.Run.cs:ReleaseStir"] = "程序引擎：释放本程序的搅拌引用，最后一个释放者才停搅拌（StirShare 引用计数）",
        ["ProgramViewModel.Run.cs:StepHeatAsync"] = "程序引擎：升温步骤（TryRunProgram 启动的后台流程）",
        ["ProgramViewModel.Run.cs:StepStirAsync"] = "程序引擎：搅拌步骤（TryRunProgram 启动的后台流程）",
    };

    /// <summary>R4：首句不是 Admit 的 Try*（急停快速路径：任意线程可调，只做仲裁不断言 UI 线程）。</summary>
    public static readonly HashSet<string> AdmitExempt = new(StringComparer.Ordinal) { "TryEstop" };

    /// <summary>
    /// R6 手写的直接写设备 / 写设置 token（ViewModels/*.cs）。服务类的写 API 不在这里手列，
    /// 由 <see cref="ServiceWriteTokens"/> 从服务源码自动生成（新增的服务写方法自动纳入检查）。
    /// </summary>
    public static readonly (string Label, Regex Pattern)[] ManualWriteTokens =
    {
        // 只认 VM 自己的属性（裸写或 _main. / this. 前缀），不认 row.StirOn 之类别的对象的同名属性
        ("StirOn =", new Regex(@"(?:(?<![\.\w])|(?<=\b(?:_main|_m|this)\.))StirOn\s*=(?!=)")),
        ("StirRpm =", new Regex(@"(?:(?<![\.\w])|(?<=\b(?:_main|_m|this)\.))StirRpm\s*=(?!=)")),
        ("Settings.<Prop> =", new Regex(@"\bSettings\.\w+\s*=(?!=)")),
        // 不写设备、但改变过程 / 进程状态的 VM 原语：占用 / 中止气路、退出进程
        ("BeginGas(", new Regex(@"(?<![\w])BeginGas\s*\(")),
        ("AbortGas / AbortAllGas(", new Regex(@"(?<![\w])Abort(?:All)?Gas\s*\(")),
        ("ShutdownApp(", new Regex(@"(?<![\w])ShutdownApp\s*\(")),
    };

    /// <summary>R6 自动生成 token 的服务类：源文件 → VM 里引用它的接收者名（MainViewModel 的字段 / 属性）。</summary>
    public static readonly (string File, string Type, string Receiver)[] Services =
    {
        ("Service/TempService.cs", "TempService", "Temp"),
        ("Service/ValveService.cs", "ValveService", "_valves"),
        ("Service/StirController.cs", "StirController", "_stir"),
        ("Service/PressureService.cs", "PressureService", "_press"),
    };

    /// <summary>
    /// 服务类里名字不以 Read / Poll / Get 开头、却不写设备的公开方法（只读 / 纯计算 / 只换界面显示档）。
    /// 键「类型.方法」，值为理由；其余公开方法与公开 setter 一律视为写设备 token。
    /// </summary>
    public static readonly Dictionary<string, string> ServiceReadOnly = new(StringComparer.Ordinal)
    {
        ["TempService.BandIndexForTemp"] = "纯计算：温度 → 档位号",
        ["TempService.LiveBand"] = "只读：仪表当前生效的 PID 档",
        ["TempService.ExportBandPids"] = "只读：导出档位 PID 供持久化",
        ["TempService.SelectBand"] = "只换界面显示档（存回 / 载入工作副本），不写仪表",
    };

    /// <summary>服务类的写 API（公开方法 + 公开 setter，排除 Read* / Poll* / Get* 与 <see cref="ServiceReadOnly"/>）。</summary>
    public static readonly Lazy<(string Label, Regex Pattern)[]> ServiceWriteTokens = new(() =>
    {
        var list = new List<(string, Regex)>();
        foreach (var (file, type, recv) in Services)
        {
            var members = SourceScan.Members(Path.Combine(SourceScan.RepoRoot, file)).Where(m => m.Type == type).ToList();
            var r = Regex.Escape(recv);
            foreach (var m in members)
            {
                var head = SourceScan.StripAttributesPublic(m.Header);
                bool observable = SourceScan.HasAttribute(m, "ObservableProperty");
                if (observable && m.Name.StartsWith("_"))
                {
                    // [ObservableProperty] private T _x; → 公开属性 X 带公开 setter
                    var prop = char.ToUpperInvariant(m.Name[1]) + m.Name[2..];
                    list.Add(($"{recv}.{prop} =", new Regex($@"(?<![\w]){r}\??\.{prop}\s*=(?!=)")));
                    continue;
                }
                if (!Regex.IsMatch(head, @"\bpublic\b") || Regex.IsMatch(head, @"\b(event|const|class|record|struct)\b")) continue;
                if (m.Name == type) continue;   // 构造函数
                bool method = SourceScan.IsMethod(m);
                if (method)
                {
                    if (Regex.IsMatch(m.Name, "^(Read|Poll|Get)") || ServiceReadOnly.ContainsKey($"{type}.{m.Name}")) continue;
                    list.Add(($"{recv}.{m.Name}", new Regex($@"(?<![\w]){r}\??\.{m.Name}\b")));
                }
                else if (Regex.IsMatch(m.Body, @"(?<!(?:private|protected|internal)\s+)\bset\b"))
                    list.Add(($"{recv}.{m.Name} =", new Regex($@"(?<![\w]){r}\??\.{m.Name}\s*=(?!=)")));
            }
        }
        return list.ToArray();
    });

    /// <summary>
    /// 白名单方法本身也是「写原语」：VM 里任何不在白名单中的成员调用它们，同样算直接写设备（传递闭包）。
    /// 构造函数与 OnXxxChanged（源生成器回调，不会被直接调用）除外。
    /// </summary>
    private static (string Label, Regex Pattern)[] PrimitiveTokens()
        => DeviceWriteAllowlist.Keys.Select(k => k[(k.LastIndexOf(':') + 1)..]).Select(n => n.Contains('.') ? n[(n.LastIndexOf('.') + 1)..] : n)
            .Where(n => !Regex.IsMatch(n, @"^On\w+Changed$") && !VmTypeNames.Value.Contains(n))
            .Distinct()
            .Select(n => ($"{n}(（白名单原语）", new Regex($@"(?<![\w]){n}\s*\(")))
            .ToArray();

    /// <summary>R6 的全部 token：手写 + 服务写 API + 白名单原语。</summary>
    public static IEnumerable<(string Label, Regex Pattern)> DeviceWriteTokens()
        => ManualWriteTokens.Concat(ServiceWriteTokens.Value).Concat(PrimitiveTokens());

    /// <summary>
    /// R8：[ViewEntry] / [WriteEntry] 入口体内不得直接改过程状态（通道状态、设定值、曲线、程序步骤）——
    /// 这些都必须由命令层改。键为标签，值为模式（掩码后的方法体）。
    /// </summary>
    public static readonly (string Label, Regex Pattern)[] ProcessStateTokens =
    {
        ("<通道>.State / TSp / PSp / Vol / SpMode / RpmSp =", new Regex(@"\.(?:State|TSp|PSp|Vol|SpMode|RpmSp)\s*=(?!=)")),
        ("Profile.<写>(", new Regex(@"\bProfile\.(?:Start|Stop|Load|Replace|Clear)\w*\s*\(")),
        ("Profile.<属性> =", new Regex(@"\bProfile\.\w+\s*=(?!=)")),
        ("Steps.<改>(", new Regex(@"\bSteps\.(?:Add|Insert|Remove|RemoveAt|Move|Clear)\s*\(")),
        ("Steps[…] =", new Regex(@"\bSteps\s*\[[^\]]*\]\s*=(?!=)")),
    };

    // ======================================================================

    private static readonly Lazy<List<SourceMember>> VmMembers = new(() =>
        SourceScan.Files("ViewModels", "*.cs", recursive: true).SelectMany(SourceScan.Members).ToList());

    private static readonly Lazy<HashSet<string>> VmTypeNames = new(() =>
        VmMembers.Value.Select(m => m.Type).Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal));

    private static readonly Lazy<List<SourceMember>> CommandMembers = new(() =>
        SourceScan.Files("Commands", "OperatorCommands*.cs").SelectMany(SourceScan.Members).ToList());

    /// <summary>扫描入口所在的源码：ViewModels、Views 代码后置。</summary>
    private static IEnumerable<SourceMember> EntryMembers()
        => VmMembers.Value.Concat(SourceScan.Files("Views", "*.axaml.cs").SelectMany(SourceScan.Members));

    private static string Where(SourceMember m) => $"{SourceScan.Rel(m.File)}:{m.Line} {m.Name}";

    private static IEnumerable<SourceMember> RelayCommands()
        => EntryMembers().Where(m => SourceScan.HasAttribute(m, "RelayCommand"));

    private static HashSet<string> DeclaredTry()
        => CommandMembers.Value
            .Where(m => m.Name.StartsWith("Try", StringComparison.Ordinal)
                        && Regex.IsMatch(m.Header, @"\bpublic\s+Task<OpResult>\s+" + m.Name + @"\s*\("))
            .Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Scanner_FindsKnownMembers()
    {
        // 自检：扫描器能认出已知的入口与命令，避免扫描失效后规则「全部通过」
        var relay = RelayCommands().Select(m => m.Key).ToHashSet();
        Assert.Contains("MainViewModel.cs:ToggleStir", relay);
        Assert.Contains("DrawerViewModel.cs:StopHeat", relay);
        Assert.Contains("SettingViewModel.cs:EditTempP", relay);
        Assert.True(relay.Count > 60, $"只扫到 {relay.Count} 个 RelayCommand");
        var tries = DeclaredTry();
        Assert.Contains("TrySetStir", tries);
        Assert.Contains("TryStopHeat", tries);
        Assert.Contains("TrySetPid", tries);
    }

    [Fact]
    public void R1_EveryRelayCommand_IsRegisteredExactlyOnce()
    {
        var bad = new List<string>();
        foreach (var m in RelayCommands())
        {
            bool w = SourceScan.HasAttribute(m, "WriteEntry"), v = SourceScan.HasAttribute(m, "ViewEntry");
            if (w && v) bad.Add($"{Where(m)}：同时标了 [WriteEntry] 和 [ViewEntry]");
            else if (!w && !v)
                bad.Add($"{Where(m)}：缺少 [WriteEntry(nameof(OperatorCommands.TryX))] 或 [ViewEntry(\"原因\")]（键 \"{m.Key}\"）");
        }
        Assert.True(bad.Count == 0, "R1 违规：\n" + string.Join("\n", bad));
    }

    private static readonly Regex ViewEntryReason = new(@"[\[,]\s*(?:[\w\.]+\.)?ViewEntry(?:Attribute)?\s*\(\s*""( *)""\s*\)");

    [Fact]
    public void R1_ViewEntry_HasReason()
    {
        // [ViewEntry("原因")] 的原因不得为空（掩码后字符串内容是空格，只能按长度判断）
        var bad = EntryMembers().Where(m => SourceScan.HasAttribute(m, "ViewEntry"))
            .Where(m => ViewEntryReason.Match(m.Header) is not { Success: true } x || x.Groups[1].Length < 2)
            .Select(m => $"{Where(m)}：[ViewEntry] 必须写明原因（只改视图的理由）").ToList();
        Assert.True(bad.Count == 0, "R1 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Allowlists_EachEntryHasJustification()
    {
        var bad = DeviceWriteAllowlist.Concat(ViewReadAllowlist)
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, "白名单条目缺少理由：" + string.Join("、", bad));
    }

    [Fact]
    public void R2_WriteEntry_CallsEachRegisteredCommand()
    {
        var bad = new List<string>();
        foreach (var m in EntryMembers().Where(m => SourceScan.HasAttribute(m, "WriteEntry")))
        {
            var cmds = SourceScan.WriteEntryCommands(m);
            if (cmds.Count == 0) bad.Add($"{Where(m)}：[WriteEntry] 没有登记任何 nameof(OperatorCommands.TryX)");
            foreach (var c in cmds)
                if (!Regex.IsMatch(m.Body, $@"\.{c}\s*\("))
                    bad.Add($"{Where(m)}：登记了 {c}，方法体里却没有 .{c}( 调用");
        }
        Assert.True(bad.Count == 0, "R2 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void R3_RegisteredCommands_AreDeclared()
    {
        var declared = DeclaredTry();
        var bad = EntryMembers().Where(m => SourceScan.HasAttribute(m, "WriteEntry"))
            .SelectMany(m => SourceScan.WriteEntryCommands(m).Where(c => !declared.Contains(c))
                .Select(c => $"{Where(m)}：{c} 在 Commands/OperatorCommands*.cs 中没有 public Task<OpResult> {c}( 声明"))
            .ToList();
        Assert.True(bad.Count == 0, "R3 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void R4_TryMethods_SyncHeadAdmitFirst_NoToast()
    {
        var bad = new List<string>();
        foreach (var f in SourceScan.Files("Commands", "OperatorCommands*.cs"))
        {
            var code = SourceScan.Mask(File.ReadAllText(f));
            if (Regex.IsMatch(code, @"\basync\s+Task<OpResult>\s+Try\w*\s*\(") ||
                Regex.IsMatch(code, @"\bpublic\s+async\s+[\w<>\?]+\s+Try\w*\s*\("))
                bad.Add($"{SourceScan.Rel(f)}：Try* 不得是 async（异步部分放进 private async Task<OpResult> *TailAsync）");
            if (Regex.IsMatch(code, @"\bToast\s*\("))
                bad.Add($"{SourceScan.Rel(f)}：命令层不得调用 Toast(，结果通过 OpResult 返回");
        }
        foreach (var m in CommandMembers.Value.Where(m => m.Name.StartsWith("Try", StringComparison.Ordinal)
                                                          && Regex.IsMatch(m.Header, @"\bpublic\b")))
        {
            if (!Regex.IsMatch(m.Header, @"\bTask<OpResult>\s+" + m.Name + @"\s*\("))
            {
                bad.Add($"{Where(m)}：public Try* 必须返回 Task<OpResult>");
                continue;
            }
            var first = SourceScan.FirstStatement(m);
            if (!m.Body.TrimStart().StartsWith("{"))
                bad.Add($"{Where(m)}：Try* 必须是块体（首句为仲裁）");
            else if (AdmitExempt.Contains(m.Name))
            {
                if (!Regex.IsMatch(first, @"^if\s*\(\s*Arbiter\.Check\s*\("))
                    bad.Add($"{Where(m)}：{m.Name} 首句必须是 if (Arbiter.Check(");
            }
            else if (!Regex.IsMatch(first, @"^if\s*\(\s*Admit\s*\(\s*src\s*,\s*(?:\w+\s*\?\s*)?ActionClass\."))
                bad.Add($"{Where(m)}：首句必须是 if (Admit(src, ActionClass.…) 仲裁");
        }
        Assert.True(bad.Count == 0, "R4 违规：\n" + string.Join("\n", bad));
    }

    // ============ R5：View / Controls 代码后置对 ViewModel 的调用 ============

    /// <summary>R5 扫描的代码后置：Views 与 Controls 下的全部 .cs（含 GraphControl / SchematicControl 这类纯代码控件）。</summary>
    private static IEnumerable<string> ViewFiles()
        => SourceScan.Files("Views", "*.cs").Concat(SourceScan.Files("Controls", "*.cs"));

    /// <summary>ViewModels 里每个类型的成员：类型名 → 成员名 → 成员类型名（属性 / 字段；方法为 null）。含 [ObservableProperty] 生成的属性与 [RelayCommand] 生成的 XxxCommand。</summary>
    private static readonly Lazy<Dictionary<string, Dictionary<string, string?>>> VmShape = new(() =>
    {
        var map = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        foreach (var m in VmMembers.Value)
        {
            if (m.Type.Length == 0) continue;
            if (!map.TryGetValue(m.Type, out var t)) map[m.Type] = t = new(StringComparer.Ordinal);
            var head = SourceScan.StripAttributesPublic(m.Header);
            if (SourceScan.IsMethod(m))
            {
                t.TryAdd(m.Name, null);
                if (SourceScan.HasAttribute(m, "RelayCommand"))
                    t.TryAdd(Regex.Replace(m.Name, "Async$", "") + "Command", "<command>");
                continue;
            }
            // 成员类型 = 名字前的最后一个类型标识（去掉 ? 与泛型参数）
            var typeM = Regex.Match(Regex.Replace(head, @"<[^<>]*>", ""), @"([A-Za-z_][\w\.]*)\??\s+" + Regex.Escape(m.Name) + @"\s*$");
            string? type = typeM.Success ? typeM.Groups[1].Value.Split('.')[^1] : "?";
            if (SourceScan.HasAttribute(m, "ObservableProperty") && m.Name.StartsWith("_"))
                t.TryAdd(char.ToUpperInvariant(m.Name[1]) + m.Name[2..], type);
            t.TryAdd(m.Name, type);
        }
        return map;
    });

    /// <summary>代码后置里绑定到 ViewModel 的标识符：字段 / 属性 / 参数 / 模式变量（XxxViewModel name、DataContext as XxxViewModel），以及类型名本身（静态调用）。</summary>
    private static Dictionary<string, string> VmIdentifiers(string code)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in VmShape.Value.Keys.Where(t => t.EndsWith("ViewModel", StringComparison.Ordinal)))
        {
            foreach (Match x in Regex.Matches(code, $@"\b{type}\??\s+(\w+)\b")) ids[x.Groups[1].Value] = type;
            foreach (Match x in Regex.Matches(code, $@"\b(\w+)\s*=\s*DataContext\s+as\s+{type}\b")) ids[x.Groups[1].Value] = type;
            ids[type] = type;   // 静态成员
        }
        return ids;
    }

    /// <summary>View 代码后置里对 VM 的调用：（文件:行, 调用链, 解析出的「类型.方法」或 null = 解析不了）。XxxCommand.Execute( 解析为 Xxx。</summary>
    private static List<(string Where, string Chain, string? Target)> ViewCalls()
    {
        var list = new List<(string, string, string?)>();
        var shape = VmShape.Value;
        foreach (var f in ViewFiles())
        {
            var code = SourceScan.Mask(File.ReadAllText(f));
            var ids = VmIdentifiers(code);
            if (ids.Count == 0) continue;
            var call = new Regex(@"(?<![\.\w])(" + string.Join("|", ids.Keys.Select(Regex.Escape)) + @")\??((?:\??\.\w+)+)\s*\(");
            foreach (Match x in call.Matches(code))
            {
                var chain = x.Groups[2].Value.Replace("?", "").Split('.', StringSplitOptions.RemoveEmptyEntries);
                string? type = ids[x.Groups[1].Value];
                string? target = null;
                for (int k = 0; k < chain.Length && type != null; k++)
                {
                    var seg = chain[k];
                    bool last = k == chain.Length - 1;
                    if (!shape.TryGetValue(type, out var members) || !members.TryGetValue(seg, out var memberType))
                    {
                        // 落到非 VM 对象（集合、模型）上的调用：键为「最后一个 VM 类型.剩余调用链」，只能经 ViewReadAllowlist 放行
                        target = "?" + type + "." + string.Join(".", chain[k..]);
                        break;
                    }
                    if (memberType == "<command>")
                    {
                        // XxxCommand.Execute( / XxxCommand.ExecuteAsync( → 入口 Xxx（或 XxxAsync）
                        if (k == chain.Length - 2 && chain[^1] is "Execute" or "ExecuteAsync" or "CanExecute")
                        {
                            var name = seg[..^"Command".Length];
                            target = chain[^1] == "CanExecute" ? $"{type}.<read>" : $"{type}.{(members.ContainsKey(name) ? name : name + "Async")}";
                        }
                        type = null; break;
                    }
                    if (last) { target = memberType == null ? $"{type}.{seg}" : $"?{type}.{seg}"; break; }
                    if (!shape.ContainsKey(memberType!)) { target = "?" + type + "." + string.Join(".", chain[k..]); break; }
                    type = memberType;
                }
                int line = code[..x.Index].Count(c => c == '\n') + 1;
                list.Add(($"{SourceScan.Rel(f)}:{line}", x.Groups[1].Value + "." + string.Join(".", chain), target));
            }
        }
        return list;
    }

    [Fact]
    public void R5_ViewCalls_GoThroughRegisteredEntries()
    {
        // 登记集合按「类型.方法」：同名但未登记的别的方法（Close / Start / Apply …）不能蹭登记
        var registered = VmMembers.Value
            .Where(m => SourceScan.HasAttribute(m, "WriteEntry") || SourceScan.HasAttribute(m, "ViewEntry"))
            .Select(m => $"{m.Type}.{m.Name}").ToHashSet(StringComparer.Ordinal);
        var calls = ViewCalls();
        Assert.True(calls.Count > 5, $"只扫到 {calls.Count} 个 View → VM 调用（扫描失效？）");
        var bad = calls
            .Where(c => c.Target == null
                        || !registered.Contains(c.Target) && !c.Target.EndsWith(".<read>", StringComparison.Ordinal)
                           && !ViewReadAllowlist.ContainsKey(c.Target.TrimStart('?')))
            .Select(c => c.Target is null or ['?', ..]
                ? $"{c.Where} {c.Chain}(：调用落在 VM 的集合 / 子对象上（{c.Target?.TrimStart('?') ?? "无法解析"}），请改成登记过的入口，或把只读调用写进 ViewReadAllowlist"
                : $"{c.Where} {c.Chain}(：{c.Target} 未登记为入口（[WriteEntry] / [ViewEntry]），只读成员写进 ViewReadAllowlist")
            .Distinct().ToList();
        Assert.True(bad.Count == 0, "R5 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void R5_Scanner_SeesPatternVariablesAndControls()
    {
        // 自检：模式变量（DataContext is ProgramViewModel vm）与 Controls/ 下的调用都被扫到
        var calls = ViewCalls();
        Assert.Contains(calls, c => c.Target == "ProgramViewModel.ReorderTo");
        Assert.Contains(calls, c => c.Target == "ProgramViewModel.DropPaletteAt");
        Assert.Contains(calls, c => c.Where.StartsWith("Controls/", StringComparison.Ordinal));
    }

    // ============ R6：直接写设备 ============

    private static List<(SourceMember Member, string Token)> DeviceWrites()
    {
        var tokens = DeviceWriteTokens().ToList();
        return VmMembers.Value
            .SelectMany(m => tokens.Where(t => t.Pattern.IsMatch(m.Body)).Select(t => (m, t.Label)))
            .ToList();
    }

    [Fact]
    public void R6_TokenGeneration_CoversServiceWriteApis()
    {
        // 自检：自动生成的 token 覆盖评审列出的漏网写 API，且不含只读成员
        var labels = ServiceWriteTokens.Value.Select(t => t.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var want in new[]
                 {
                     "_valves.WriteAllAsync", "_valves.CloseRangeAsync", "_valves.CloseGasSourcesAsync", "_valves.Locked =",
                     "Temp.ApplyPidForBandAsync", "Temp.OverTempStopAsync", "Temp.StopAllChannelsAsync", "Temp.EmergencyStopDetailedAsync",
                     "Temp.InitAsync", "Temp.SetPidAsync", "Temp.Ctn =", "_stir.Stop", "_stir.StartAsync", "_press.FullScalePsi =",
                 })
            Assert.Contains(want, labels);
        Assert.DoesNotContain("Temp.Online", labels);
        Assert.DoesNotContain("Temp.ReadSetpointAsync", labels);
        Assert.DoesNotContain("_press.PollAsync", labels);
        Assert.DoesNotContain("Temp.BandIndexForTemp", labels);
        // 白名单原语也是 token：例如 VM 里新写一个调用 StopHeatCoreAsync( 的方法会被认出
        Assert.Contains(PrimitiveTokens(), t => t.Label.StartsWith("StopHeatCoreAsync(", StringComparison.Ordinal));
    }

    [Fact]
    public void R6_DeviceWrites_OnlyInAllowlistedMethods()
    {
        var bad = DeviceWrites().Where(w => !DeviceWriteAllowlist.ContainsKey(w.Member.Key))
            .Select(w => $"{Where(w.Member)}：直接写设备（{w.Token}）· 请经 OperatorCommands（键 \"{w.Member.Key}\"）")
            .Distinct().ToList();
        Assert.True(bad.Count == 0, "R6 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void R6_Allowlist_HasNoStaleEntries()
    {
        var used = DeviceWrites().Select(w => w.Member.Key).ToHashSet(StringComparer.Ordinal);
        var stale = DeviceWriteAllowlist.Keys.Where(k => !used.Contains(k)).OrderBy(k => k).ToList();
        Assert.True(stale.Count == 0, "DeviceWriteAllowlist 只减不增：以下项已不再直接写设备，请删除：\n" + string.Join("\n", stale));
    }

    [Fact]
    public void R6_EntriesAreNeverAllowlisted()
    {
        // 入口（[RelayCommand] / [WriteEntry] / [ViewEntry]）不得进白名单：入口的写操作只能经 Ops.Try*
        var bad = EntryMembers()
            .Where(m => DeviceWriteAllowlist.ContainsKey(m.Key)
                        && (SourceScan.HasAttribute(m, "RelayCommand") || SourceScan.HasAttribute(m, "WriteEntry") || SourceScan.HasAttribute(m, "ViewEntry")))
            .Select(m => $"{Where(m)}：入口不能进 DeviceWriteAllowlist").ToList();
        Assert.True(bad.Count == 0, "R6 违规：\n" + string.Join("\n", bad));
    }

    // ============ R8：入口体内不得直接改过程状态 ============

    [Fact]
    public void R8_Entries_DoNotMutateProcessState()
    {
        // Ops.TryX( 的实参（例如 TryEditProgram 的修改闭包）在命令层守卫之后执行，不算入口体
        var bad = new List<string>();
        foreach (var m in EntryMembers().Where(m => SourceScan.HasAttribute(m, "ViewEntry") || SourceScan.HasAttribute(m, "WriteEntry")))
        {
            var body = SourceScan.BlankCallArgs(m.Body, @"\.Try\w+\s*\(");
            foreach (var t in ProcessStateTokens.Where(t => t.Pattern.IsMatch(body)))
                bad.Add($"{Where(m)}：入口体内直接改过程状态（{t.Label}）· 请经 OperatorCommands");
        }
        Assert.True(bad.Count == 0, "R8 违规：\n" + string.Join("\n", bad));
    }

    // ======================================================================
    // R7：动作类别登记表（doc/命令层设计.md §12 映射表的「类」列）。
    // 值 = 首句仲裁里出现的 ActionClass（按出现顺序，| 分隔）+ 「·本地」（Exposure.LocalOnly）/「·按表」（取自 SettingSpecs）。
    // 这是 §8「Remote 模式自检」的静态保证：Remote 模式下凡含 Control 的命令对本地来源都返回 MODE_REMOTE，
    // Stop / Record / Reset 不被仲裁拒绝（Decide 的判定表另有 48 组单元测试）。
    // ======================================================================
    public static readonly Dictionary<string, string> ExpectedClass = new(StringComparer.Ordinal)
    {
        // 系统
        ["TryEstop"] = "Stop", ["TryResetEstop"] = "Reset·本地", ["TryStopAll"] = "Stop",
        ["TryRun"] = "Record|Control", ["TryExitApp"] = "Stop·本地",
        // 搅拌
        ["TrySetStir"] = "Control|Stop", ["TrySetStirRpm"] = "Control", ["TryClearStirFault"] = "Control·本地",
        // 手动阀
        ["TrySetManualValveMode"] = "Control|Stop·本地", ["TrySetReactorValve"] = "Control|Stop·本地",
        ["TrySetGasInlet"] = "Control|Stop·本地", ["TrySetVent"] = "Control|Stop·本地",
        // 通道 / 抽屉
        ["TrySetChannelTemp"] = "Control", ["TrySetChannelVolume"] = "Control", ["TrySetSpMode"] = "Control",
        ["TrySetProfileSegCount"] = "Control", ["TrySetProfileSegment"] = "Control", ["TryReplaceProfile"] = "Control",
        ["TryStartProfile"] = "Control", ["TryStopProfile"] = "Stop", ["TryStartHeat"] = "Control", ["TryStopHeat"] = "Stop",
        ["TryEnableChannel"] = "Control", ["TryStopChannel"] = "Stop", ["TryResetGasCounter"] = "Control",
        ["TryClearRecipeTag"] = "Control·本地", ["TryCopyChannelConfig"] = "Control",
        // 温控 / PID
        ["TrySetPid"] = "Control", ["TryStartAutotune"] = "Control", ["TryCancelAutotune"] = "Stop",
        ["TryFixCtn"] = "Control·本地", ["TrySetBandBound"] = "Control·本地",
        // 设置
        ["TrySetSetting"] = "Control·按表", ["TrySetStopHeatOnOverPressure"] = "Stop|Control·本地", ["TrySetPressureUnit"] = "Control·本地",
        // 程序
        ["TryEditProgram"] = "Control", ["TryReplaceProgram"] = "Control", ["TryApplyCurve"] = "Control",
        ["TryCopyProgram"] = "Control", ["TryRunProgram"] = "Control", ["TryAbortProgram"] = "Stop",
        // 泄漏测试
        ["TryStartLeakTest"] = "Control", ["TryAbortLeakTest"] = "Stop",
        // 记录类与配方
        ["TryApplyRecipe"] = "Control·本地", ["TryAckAlarm"] = "Record", ["TryBeginSession"] = "Record",
        ["TryEndSession"] = "Record", ["TryAddMarker"] = "Record",
        // 控制权（P3，§5.1「只有本地可以允许 / 切回」）：只改租约状态机，不碰设备 → Record · 本地
        ["TryGrantControl"] = "Record·本地", ["TryDenyControl"] = "Record·本地", ["TryTakeBackControl"] = "Record·本地",
    };

    /// <summary>从首句仲裁解析出的类别串（格式同 <see cref="ExpectedClass"/>）。</summary>
    private static string ActualClass(SourceMember m)
    {
        var first = SourceScan.FirstStatement(m);
        int end = first.IndexOf("is {", StringComparison.Ordinal);
        var head = end > 0 ? first[..end] : first;
        var cls = string.Join("|", Regex.Matches(head, @"\bActionClass\.(\w+)").Select(x => x.Groups[1].Value));
        string exp = Regex.IsMatch(head, @"\bExposure\.LocalOnly\b") ? "·本地"
                   : Regex.IsMatch(head, @"SettingSpecs\.Of\(\s*key\s*\)\.Exposure") ? "·按表" : "";
        return cls + exp;
    }

    [Fact]
    public void R7_ActionClass_MatchesRegistry()
    {
        var tries = CommandMembers.Value.Where(m => m.Name.StartsWith("Try", StringComparison.Ordinal)
                                                    && Regex.IsMatch(m.Header, @"\bpublic\s+Task<OpResult>\s+" + m.Name + @"\s*\(")).ToList();
        var bad = new List<string>();
        foreach (var m in tries)
        {
            if (!ExpectedClass.TryGetValue(m.Name, out var want)) { bad.Add($"{Where(m)}：新命令 {m.Name} 未登记到 ExpectedClass（并补 doc/命令层设计.md §12）"); continue; }
            var got = ActualClass(m);
            if (got != want) bad.Add($"{Where(m)}：仲裁类别为「{got}」，登记为「{want}」");
        }
        var declared = tries.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        bad.AddRange(ExpectedClass.Keys.Where(k => !declared.Contains(k)).Select(k => $"ExpectedClass 中的 {k} 已不存在，请删除"));
        Assert.True(bad.Count == 0, "R7 违规：\n" + string.Join("\n", bad));
    }

    [Fact]
    public void SettingSpecs_DeviceKeysAreLocalOnly()
    {
        // 「按表」的命令（TrySetSetting）：写设备 / EEPROM 的项必须是 LocalOnly（附录 B），由 SettingSpecsTests 逐项核对；
        // 这里只确认命令层确实取表里的 Exposure，而不是写死 Remote。
        var m = CommandMembers.Value.Single(x => x.Name == "TrySetSetting");
        Assert.Equal("Control·按表", ActualClass(m));
    }
}
