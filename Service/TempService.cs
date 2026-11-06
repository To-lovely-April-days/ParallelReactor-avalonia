using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Hardware;

namespace ParallelReactor.Services;

/// <summary>
/// 温控业务服务：把两台宇电 AI-8（A 站 RV1-4、B 站 RV5-8）抽象成 8 个通道。
/// 负责轮询 PV/输出/自整定状态，并提供写 SP、写 PID、启动自整定等操作。
/// <para>
/// 支持「温度档位」：每个档位每通道独立存一组 PID。切换档位时界面 P/I/D 随之切换；
/// 同一温区整定一次即可复用，跑反应时按目标温度选对应档位的 PID 下发。
/// </para>
/// 当前总线由 <see cref="HardwareOptions.CreateTempBus"/> 决定（真串口或离线）。
/// </summary>
public sealed partial class TempService : ObservableObject
{
    private readonly TempController _ctrl;   // AI-8 一台 8 回路（RV1-8）
    private readonly TempController _urgentCtrl;   // 同一台 AI-8，走总线紧急视图（急停帧插队到轮询之前）
    private int _curBand;
    private int _fails = 3;   // 连续轮询失败次数（初值=离线，首次读成功才算在线）

    /// <summary>温控器是否在线：连续 3 次轮询失败视为离线（未配置串口时恒为离线）。离线时 PV 为旧值，界面显示「—」。</summary>
    public bool Online => _fails < 3;

    /// <summary>轮询统计（一轮 5 次块读算一次事务；被上一轮未完成而跳过的不计）。LastOkAt = 最近一轮读完的时刻。</summary>
    public CommStats Stats { get; } = new();

    /// <summary>当前 Channels 里这组值（PV / 输出 / 模式）所属那一轮轮询的响应时刻（UI 线程写，尚无为 null）。</summary>
    public DateTimeOffset? ValuesAt { get; private set; }
    // 每通道整定发起时所在的档位（-1=无进行中的整定）。整定期间用户可能切档，
    // 完成回读必须存回"发起时"的档，而不是"完成时"正显示的档。
    private readonly int[] _tuneBand = { -1, -1, -1, -1, -1, -1, -1, -1 };

    // 整定被取消 / 急停打断：仪表 At 从 1 变成 4 时轮询会看到「整定结束」，但仪表里并没有新整定结果，
    // 不能回读存档（否则会把旧 PID 当成整定结果存进档位）。
    private readonly bool[] _tuneAborted = new bool[8];

    private volatile bool _locked;

    /// <summary>急停锁定：为 true 时拒绝启动加热 / 自整定（写 SP、PID 不受影响，它们本身不会让仪表出力）。
    /// volatile：急停快速路径可能在任意线程置位，之后任何线程上的启动流程都能立即看到。</summary>
    public bool Locked { get => _locked; set => _locked = value; }

    /// <summary>档位 PID / 档位边界发生变化（整定完成、手改、切档落存）——上层据此持久化。</summary>
    public event Action? PidsChanged;

    public ObservableCollection<TempChannel> Channels { get; } = new();

    /// <summary>温度档位（边界可改；默认 3 档）。</summary>
    public List<TempBand> Bands { get; } = new()
    {
        new TempBand("低温档", 0, 100),
        new TempBand("中温档", 100, 200),
        new TempBand("高温档", 200, 400),
    };

    public int CurrentBand => _curBand;

    public TempService(IModbusMaster bus, byte slave, int dpt)
    {
        _ctrl = new TempController(bus, slave, 8, dpt);   // 1 拖 8
        _urgentCtrl = new TempController(bus.AsUrgent(), slave, 8, dpt);
        for (int i = 1; i <= 8; i++) Channels.Add(new TempChannel(i, Bands.Count));
    }

    /// <summary>通道号(1..8) → (温控器, 回路号 1..8)。一台 AI-8 直接对应。</summary>
    private (TempController c, int loop) Map(int ch) => (_ctrl, ch);

    /// <summary>切换当前档位：把界面值存回旧档，再载入新档到界面。（手动更改PID，不会更新存储的对应档位的PID）</summary>
    public void SelectBand(int idx)
    {
        if (idx < 0 || idx >= Bands.Count || idx == _curBand) return;
        foreach (var ch in Channels) ch.StoreBand(_curBand);
        _curBand = idx;
        foreach (var ch in Channels) ch.LoadBand(_curBand);
        PidsChanged?.Invoke();
    }

    /// <summary>给定温度落在哪个档位（超出上限归最高档）。</summary>
    public int BandIndexForTemp(double t)
    {
        for (int i = 0; i < Bands.Count; i++)
            if (t >= Bands[i].Lo && t < Bands[i].Hi) return i;
        return Bands.Count - 1;
    }

    /// <summary>按目标温度为某通道选用对应档位的 PID，并连同 SP 一起下发到仪表（跑反应前调用）。</summary>
    public Task<bool> ApplyBandForAsync(int ch, double targetTemp) => StartChannelAsync(ch, targetTemp, targetTemp);


    // 每通道仪表里当前生效的 PID 属于哪个档位（-1=未知，下次必写）。8 通道各自独立，不能共用。
    private readonly int[] _lastBand = { -1, -1, -1, -1, -1, -1, -1, -1 };

    /// <summary>
    /// 按照设定温度应用对应档位的 PID 参数并下发到温度控制器
    /// </summary>
    /// <param name="ch">通道编号</param>
    /// <param name="sp">温度设定值</param>
    /// <returns></returns>
    public async Task ApplyPidForBandAsync(int ch, double sp)
    {
        int idx = BandIndexForTemp(sp);
        if (_lastBand[ch - 1] == idx) return;  // 仪表里已是该档 PID，避免每周期重复写
        var (p, i, d) = Channels[ch - 1].GetBand(idx);
        var (c, loop) = Map(ch);

        try
        {
            await c.SetPidAsync(loop, p, i, d);
            _lastBand[ch - 1] = idx;   // 写成功才记录，失败下次重试
        }
        catch (System.Exception)
        {
            _lastBand[ch - 1] = -1;   // 可能只写了一部分，标记未知
        }
    }

    /// <summary>
    /// 显式启动单通道加热（全链：Srun=0 + At=0 + 按 bandTemp 套档 PID + 写 initialSp）。
    /// 定值恒温：bandTemp = initialSp = 目标温度；曲线：bandTemp 用最高段温度选 PID 档，
    /// initialSp 用起点 PV（避免启动瞬间先朝高温冲一下，随后曲线引擎逐周期接管 SP）。
    /// 返回是否全部写入成功（通讯失败由调用方提示）。
    /// </summary>
    public async Task<bool> StartChannelAsync(int ch, double bandTemp, double initialSp)
    {
        int band = BandIndexForTemp(bandTemp);
        var (p, i, d) = Channels[ch - 1].GetBand(band);
        var (c, loop) = Map(ch);
        try
        {
            // 每个出力步骤前都查一次急停锁：启动途中按下急停，最多只有一帧已排队的写入会落在急停之后，
            // 而 Srun=0 与 At=0 分属两步，不会同时漏过去（任一保持停止，仪表就不出力）
            if (Locked) return false;
            await c.RunAllAsync();     // 解除可能的 Srun 全局停止（9655/断电后 15→9655），否则输出被闸死
            if (Locked) return false;
            await c.StartAsync(loop);  // At=0 回到 APID 自动控制——开机/停止/取消整定都会把通道置于 At=4
            _lastBand[ch - 1] = -1;
            await c.SetPidAsync(loop, p, i, d);
            _lastBand[ch - 1] = band;
            await c.SetSetpointAsync(loop, initialSp);
            Channels[ch - 1].Sp = initialSp; Channels[ch - 1].SpKnown = true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>开机读一次各通道 PID 作为「当前档位」初值填充界面。串口读在后台，结果批量回 UI。
    /// 同时自愈启用回路数：Ctn&lt;8 时自动写 8（出厂可能按 4 路配置，会导致 RV5-8 完全不受控）。</summary>
    public async Task InitAsync(bool seedFromInstrument = true)
    {
        try
        {
            int ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            if (ctn >= 0 && ctn < 8)
            {
                await _ctrl.SetCtnAsync(8);
                ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            }
            int v = ctn;
            await Dispatcher.UIThread.InvokeAsync(() => Ctn = v);
        }
        catch { /* 通讯没通：徽章保持"未读到"，轮询恢复后会刷新 */ }

        // 安全：开机把 8 路全部置停止（At=4）——「设温度 ≠ 启动加热」，加热只能由
        // 「开始运行」/ 抽屉「启动加热」/ 曲线「启动」显式发起。仪表上电默认是运行态，
        // 不停的话改个 SP 数字就会开始发热。
        for (int ch = 1; ch <= 8; ch++)
        {
            var (c, loop) = Map(ch);
            try { await c.StopAsync(loop).ConfigureAwait(false); }
            catch { break; }   // 通讯没通：别再空试剩下的通道
        }
        // 仪表 PID 只有"一组当前值"、不知道属于哪个档：只有首次运行（无本地存档）才用它
        // 作当前档种子；有存档时用存档，绝不能把仪表里残留的上次整定值（可能属于高温档）
        // 盲写进开机默认的低温档。
        if (seedFromInstrument)
        {
            var pids = new PidParams?[8];
            for (int ch = 1; ch <= 8; ch++)
            {
                var (c, loop) = Map(ch);
                try { pids[ch - 1] = await c.ReadPidAsync(loop).ConfigureAwait(false); }
                catch { pids[ch - 1] = null; }
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (int i = 0; i < 8; i++)
                    if (pids[i] is { } p) Channels[i].SetBand(_curBand, p.P, p.I, p.D);
                foreach (var m in Channels) m.LoadBand(_curBand);
            });
        }
    }

    /// <summary>导出 8 通道 × 3 档 PID（持久化快照用）。先把界面当前值落回当前档。</summary>
    public double[][][] ExportBandPids()
    {
        var all = new double[8][][];
        for (int ch = 0; ch < 8; ch++)
        {
            Channels[ch].StoreBand(_curBand);
            all[ch] = new double[Bands.Count][];
            for (int b = 0; b < Bands.Count; b++)
            {
                var (p, i, d) = Channels[ch].GetBand(b);
                all[ch][b] = new[] { p, i, d };
            }
        }
        return all;
    }

    /// <summary>从持久化数据恢复 8 通道 × 3 档 PID（开机调用，早于/替代仪表种子）。</summary>
    public void ImportBandPids(double[][][]? data)
    {
        if (data == null) return;
        for (int ch = 0; ch < 8 && ch < data.Length; ch++)
            for (int b = 0; b < Bands.Count && b < (data[ch]?.Length ?? 0); b++)
                if (data[ch][b] is { Length: 3 } v)
                    Channels[ch].SetBand(b, v[0], v[1], v[2]);
        foreach (var m in Channels) m.LoadBand(_curBand);
    }

    private bool _polling;   // 防止上一轮没读完又发起新一轮，造成堆积刷爆 UI 线程

    /// <summary>周期轮询：用 3 次块读(PV/输出/模式各读 8 路)代替 24 次单读；后台读完一次性回 UI 批量刷新。</summary>
    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            double[] op;
            short[] raw;
            int[] modes;
            int srun, ctn;
            DateTimeOffset at;
            try
            {
                raw = await _ctrl.ReadAllPvRawAsync().ConfigureAwait(false);
                op = await _ctrl.ReadAllOutputsAsync().ConfigureAwait(false);
                modes = await _ctrl.ReadAllModesAsync().ConfigureAwait(false);
                srun = await _ctrl.ReadSrunAsync().ConfigureAwait(false);
                ctn = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
                _fails = 0;
                at = DateTimeOffset.Now;
                Stats.Ok(at);   // 记录响应到达时刻（不是 Tick 时刻）
            }
            catch
            {
                Stats.Fail();
                if (_fails < 3) _fails++;   // 通讯异常：保留上次值（界面按离线显示「—」），下个周期重试
                if (!Online)
                    await Dispatcher.UIThread.InvokeAsync(() => { foreach (var m in Channels) m.Online = false; });
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ValuesAt = at;   // 与下面的值在同一次 UI 更新里写，运行记录里「最近响应」与 PV 同属一轮
                Srun = srun;
                Ctn = ctn;
                for (int i = 0; i < 8 && i < raw.Length; i++)
                {
                    var m = Channels[i];
                    bool tuning = modes[i] == 1;
                    bool finished = m.Autotuning && !tuning;
                    m.PvRaw = raw[i]; m.Mode = modes[i];
                    m.Pv = _ctrl.PvFromRaw(raw[i]); m.OutputPct = op[i]; m.Autotuning = tuning; m.Online = true;
                    if (finished)
                    {
                        if (_tuneAborted[i]) _tuneAborted[i] = false;   // 取消 / 急停打断：没有新结果
                        else _ = ReloadPidAsync(i + 1);                 // 整定刚结束：回读新 PID
                    }
                }
            });
        }
        finally { _polling = false; }
    }

    public async Task SetSetpointAsync(int ch, double sp)
    {
        var (c, loop) = Map(ch);
        await c.SetSetpointAsync(loop, sp);
        Channels[ch - 1].Sp = sp; Channels[ch - 1].SpKnown = true;
    }

    public async Task SetPidAsync(int ch, double p, double i, double d)
    {
        var (c, loop) = Map(ch);
        _lastBand[ch - 1] = -1;
        await c.SetPidAsync(loop, p, i, d);
        _lastBand[ch - 1] = _curBand;   // 手改写入的是当前显示档的 PID
        var m = Channels[ch - 1];
        m.P = p; m.I = i; m.D = d;
        m.StoreBand(_curBand);
        PidsChanged?.Invoke();
    }

    /// <summary>AutotuneAsync 的返回值：需要先设定目标温度（SP 未设或过低）。</summary>
    public const string NoSetpoint = "SP_NOT_SET";

    /// <summary>
    /// 对该通道启动自整定（At=1）。整定结束后轮询会自动回读并存入当前档位。
    /// <para>
    /// AI-8 整定原理 = 在 SP 附近做 ON/OFF 振荡后写出 PID。若 SP 未设（≈0℃，低于室温），
    /// 加热输出永远不开、振荡不起来，仪表会停在 At=1 出不来——界面表现为「整定中」但温度不动。
    /// 因此启动前先回读 SP 把关；同时写 Srun=0 确保仪表处于全局运行态（Srun=15 的仪表断电重启后
    /// 会自动进入 9655 全局停止，输出被闸死，同样表现为不加热，见手册 §6.1）。
    /// </para>
    /// 返回 null=已启动；<see cref="NoSetpoint"/>=需先设 SP；其他=错误文本。
    /// </summary>
    public async Task<string?> AutotuneAsync(int ch)
    {
        var (c, loop) = Map(ch);
        if (Locked) return "急停状态下不可自整定";
        try
        {
            double sp = await c.ReadSetpointAsync(loop).ConfigureAwait(false);
            if (sp < 35) return NoSetpoint;   // 必须明显高于室温，加热振荡才可能发生
            if (Locked) return "急停状态下不可自整定";
            _tuneAborted[ch - 1] = false;
            await c.RunAllAsync();
            if (Locked) return "急停状态下不可自整定";
            await c.AutotuneAsync(loop);
            // 整定出的 PID 属于整定 SP 所在的温区，而不是界面当前选中的档
            _tuneBand[ch - 1] = BandIndexForTemp(sp);
            _lastBand[ch - 1] = -1;   // 整定会改写仪表 PID，完成回读后再确定
            await Dispatcher.UIThread.InvokeAsync(() => Channels[ch - 1].Autotuning = true);
            return null;
        }
        catch { return "通讯失败，未能启动整定"; }
    }

    /// <summary>取消自整定：At 写 4（停止控制、关闭输出），解除「整定中」。
    /// 注意不能写回 0（APID）——那是恢复自动控温，SP 还在，通道会继续朝目标温度输出
    /// （表现为取消后输出仍有百分之几十）。取消的语义应当是"停下来"。</summary>
    public async Task CancelAutotuneAsync(int ch)
    {
        var (c, loop) = Map(ch);
        AbortTune(ch - 1);
        try { await c.StopAsync(loop); } catch { /* 通讯失败也要把界面状态解开 */ }
        await Dispatcher.UIThread.InvokeAsync(() => Channels[ch - 1].Autotuning = false);
    }

    /// <summary>标记整定被打断：之后轮询看到「整定结束」时不回读、不存档。</summary>
    private void AbortTune(int i)
    {
        _tuneAborted[i] = true;
        _tuneBand[i] = -1;
    }

    /// <summary>停止该通道的温控输出（At=4，停止控制、关闭输出）。返回是否写入成功。
    /// 停止/停用反应釜时必须调用——否则仪表带着旧 SP 继续加热，界面上看着停了、加热器还在全力工作。</summary>
    public async Task<bool> StopChannelAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try { await c.StopAsync(loop); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 超温切断单通道：正在自整定的先标记整定被打断（否则轮询会把仪表里的旧 PID 当成整定结果存档），
    /// 再写 At=4（停止控制、关闭输出）。返回 At=4 是否写入确认（Modbus 0x06 回显）。
    /// <para>不用 Srun=9655：Srun 是 AI-8 的全局寄存器，会把 8 路一起闸死。急停锁定不影响停止。</para>
    /// </summary>
    public async Task<bool> OverTempStopAsync(int ch)
    {
        var (c, loop) = Map(ch);
        // 先标记整定被打断，写失败再撤销：写入失败时仪表上的整定仍在进行，完成后的新 PID 不能丢
        bool wasTuning = Channels[ch - 1].Autotuning && !_tuneAborted[ch - 1];
        int band = _tuneBand[ch - 1];
        if (wasTuning) AbortTune(ch - 1);
        try { await c.StopAsync(loop); }
        catch
        {
            if (wasTuning && Channels[ch - 1].Autotuning) { _tuneAborted[ch - 1] = false; _tuneBand[ch - 1] = band; }
            return false;
        }
        await Dispatcher.UIThread.InvokeAsync(() => Channels[ch - 1].Autotuning = false);
        return true;
    }

    /// <summary>停止全部 8 路温控输出（逐路 At=4，某路失败也继续下一路）。「全部停止」时调用。返回是否 8 路全部写入成功。
    /// 不用 Srun=9655 全局闸——那会把状态徽章打红，且语义上属于急停级别。</summary>
    public async Task<bool> StopAllChannelsAsync()
    {
        bool ok = true;
        for (int ch = 1; ch <= 8; ch++) ok &= await StopChannelAsync(ch);
        return ok;
    }

    /// <summary>急停：先 Srun=9655 一条指令闸死全部输出，再逐路 At=4 兜底（任一步通讯失败都继续下一步）。
    /// 返回加热是否确认已停（全局闸写入成功，或 8 路 At=4 全部成功）。
    /// 之后任一通道重新「启动加热」时 <see cref="StartChannelAsync"/> 会写回 Srun=0。</summary>
    public async Task<bool> EmergencyStopAsync() => (await EmergencyStopDetailedAsync()).Ok;

    /// <summary>同 <see cref="EmergencyStopAsync"/>，另外返回本次两步各自的结果：
    /// 全局闸 Srun=9655 是否写入（Gate）、8 路 At=4 是否全部写入（Each）。事件日志用。
    /// <para>线程安全，可在任意线程调用（急停快速路径）：只改本服务的普通数组，界面上的「整定中」投递回 UI 线程解除。</para>
    /// <para>urgent：走总线紧急视图（排在已排队的轮询 / 普通写入之前），只给快速路径的第一轮用；补发 / 自动重发用普通优先级。</para>
    /// <para>ct：每一帧下发前都查一次——复位（取消）之后不再排出新的停止帧，避免落在复位后新的 Srun=0 / At=0 之后（C18）。
    /// 取消时未下发的部分记为失败，调用方按令牌丢弃结果。</para></summary>
    public async Task<(bool Ok, bool Gate, bool Each)> EmergencyStopDetailedAsync(CancellationToken ct = default, bool urgent = false)
    {
        var ctrl = urgent ? _urgentCtrl : _ctrl;
        // 正在整定的通道会被 At=4 打断：先标记，避免轮询把仪表里的旧 PID 当成整定结果存档
        // （只读 Autotuning 的值；改界面对象投递到 UI 线程）
        var tuning = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            if (!Channels[i].Autotuning) continue;
            AbortTune(i);
            tuning.Add(i);
        }
        if (tuning.Count > 0)
            Dispatcher.UIThread.Post(() => { foreach (var i in tuning) Channels[i].Autotuning = false; });
        if (ct.IsCancellationRequested) return (false, false, false);
        bool gate;
        try { await ctrl.StopAllAsync().ConfigureAwait(false); gate = true; }
        catch { gate = false; }
        bool each = true;
        for (int ch = 1; ch <= 8; ch++)
        {
            if (ct.IsCancellationRequested) { each = false; break; }
            try { await ctrl.StopAsync(ch).ConfigureAwait(false); }
            catch { each = false; }
        }
        return (gate || each, gate, each);
    }

    // ===== 仪表全局运行状态（Srun 0x0845），随轮询刷新：现场排查「输出100%却不加热」用 =====
    /// <summary>-1=尚未读到；0=运行；15=运行(断电重启后自动全停)；9655=全局停止。</summary>
    [ObservableProperty] private int _srun = -1;

    public string SrunText => Srun switch
    {
        -1 => "Srun 状态：未读到（通讯未通）",
        0 => "Srun：运行（全局输出已放行）",
        15 => "Srun：运行 · 断电重启后会自动全停（建议改为 0）",
        9655 => "Srun：全局停止（所有输出被闸死）",
        _ => $"Srun：{Srun}（非常规值）"
    };
    public bool SrunOk => Srun == 0;
    public bool SrunBad => Srun == 9655;
    public bool SrunOther => !SrunOk && !SrunBad;

    partial void OnSrunChanged(int value)
    {
        OnPropertyChanged(nameof(SrunText));
        OnPropertyChanged(nameof(SrunOk));
        OnPropertyChanged(nameof(SrunBad));
        OnPropertyChanged(nameof(SrunOther));
    }

    // ===== 启用回路数 Ctn（0x0844）：<8 时后面的通道完全不受控（出厂可能按 4 路配） =====
    /// <summary>-1=尚未读到。</summary>
    [ObservableProperty] private int _ctn = -1;

    public string CtnText => Ctn < 0 ? "" : Ctn >= 8
        ? $"启用回路 Ctn={Ctn}"
        : $"启用回路 Ctn={Ctn}（不足 8：RV{Ctn + 1}~RV8 不受控！点此修复）";
    public bool CtnOk => Ctn >= 8;
    public bool CtnBad => Ctn >= 0 && Ctn < 8;

    partial void OnCtnChanged(int value)
    {
        OnPropertyChanged(nameof(CtnText));
        OnPropertyChanged(nameof(CtnOk));
        OnPropertyChanged(nameof(CtnBad));
    }

    /// <summary>把 Ctn 设为 8（修复"部分通道不受控"）。返回 null=成功，否则错误文本。</summary>
    public async Task<string?> FixCtnAsync()
    {
        try
        {
            await _ctrl.SetCtnAsync(8);
            int v = await _ctrl.ReadCtnAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Ctn = v);
            return v >= 8 ? null : $"写入后回读 Ctn={v}，未生效（可能需在仪表面板解锁参数）";
        }
        catch { return "通讯失败，未能写入 Ctn"; }
    }

    private async Task ReloadPidAsync(int ch)
    {
        var (c, loop) = Map(ch);
        try
        {
            var pid = await c.ReadPidAsync(loop).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var m = Channels[ch - 1];
                int band = _tuneBand[ch - 1] >= 0 ? _tuneBand[ch - 1] : _curBand;
                _tuneBand[ch - 1] = -1;
                m.SetBand(band, pid.P, pid.I, pid.D);
                _lastBand[ch - 1] = band;   // 仪表里现在就是该档刚整定出的 PID
                if (band == _curBand) m.LoadBand(band);   // 正显示该档才刷新界面 P/I/D
                PidsChanged?.Invoke();
            });
        }
        catch { }
    }
}

/// <summary>温度档位（一段温区，边界可改；变更后标签自动刷新）。</summary>
public sealed partial class TempBand : ObservableObject
{
    public string Name { get; }

    [ObservableProperty] private double _lo;
    [ObservableProperty] private double _hi;

    public TempBand(string name, double lo, double hi) { Name = name; _lo = lo; _hi = hi; }

    public string Range => Hi >= 400 ? $"≥{Lo:0}℃" : Lo <= 0 ? $"≤{Hi:0}℃" : $"{Lo:0}–{Hi:0}℃";
    public string Label => $"{Name} · {Range}";

    partial void OnLoChanged(double value) { OnPropertyChanged(nameof(Range)); OnPropertyChanged(nameof(Label)); }
    partial void OnHiChanged(double value) { OnPropertyChanged(nameof(Range)); OnPropertyChanged(nameof(Label)); }
}

/// <summary>单个温控通道的可观察状态（绑定到界面）。P/I/D 为「当前档位」的值。</summary>
public partial class TempChannel : ObservableObject
{
    public int Id { get; }
    public string Name => $"RV{Id}";

    private readonly double[] _bp, _bi, _bd;   // 各档位的 P / I / D

    public TempChannel(int id, int bandCount)
    {
        Id = id;
        _bp = new double[bandCount];
        _bi = new double[bandCount];
        _bd = new double[bandCount];
        for (int b = 0; b < bandCount; b++)   // 每档预置一组合理默认（高温档 P/I/D 更大）
        {
            _bp[b] = 40 + 20 * b;
            _bi[b] = 100 + 25 * b;
            _bd[b] = 15 + 5 * b;
        }
        LoadBand(0);
    }

    [ObservableProperty] private double _pv;
    [ObservableProperty] private double _sp;
    [ObservableProperty] private double _outputPct;

    /// <summary>软件开机后是否成功写过 SP（仪表 SV 不回读；未写过时 Sp=0 不代表仪表 SV=0）。</summary>
    public bool SpKnown { get; set; }

    /// <summary>最近一次轮询的 PV 寄存器原码（尚未读到为 null）。</summary>
    public short? PvRaw { get; set; }

    /// <summary>最近一次轮询的工作模式 At 原值（0=APID 1=自整定 2=ON/OFF 3=手动 4=停止；-1=尚未读到）。</summary>
    public int Mode { get; set; } = -1;

    /// <summary>读数是否有效（温控在线）。由 TempService 轮询刷新；离线时 PV / 输出显示「—」，不显示旧值或 0。</summary>
    [ObservableProperty] private bool _online;

    public string PvText => Online ? $"{Pv:0.0}" : "—";
    public string OutputText => Online ? $"{OutputPct:0}%" : "—";

    partial void OnPvChanged(double value) => OnPropertyChanged(nameof(PvText));
    partial void OnOutputPctChanged(double value) => OnPropertyChanged(nameof(OutputText));
    partial void OnOnlineChanged(bool value)
    {
        OnPropertyChanged(nameof(PvText));
        OnPropertyChanged(nameof(OutputText));
    }
    [ObservableProperty] private double _p;
    [ObservableProperty] private double _i;
    [ObservableProperty] private double _d;
    [ObservableProperty] private bool _autotuning;

    /// <summary>把某档位的 PID 载入到界面 P/I/D。</summary>
    public void LoadBand(int idx) { P = _bp[idx]; I = _bi[idx]; D = _bd[idx]; }

    /// <summary>把界面当前 P/I/D 存回某档位。</summary>
    public void StoreBand(int idx) { _bp[idx] = P; _bi[idx] = I; _bd[idx] = D; }

    /// <summary>直接写某档位 PID（不影响当前界面值，除非该档位正显示）。</summary>
    public void SetBand(int idx, double p, double i, double d) { _bp[idx] = p; _bi[idx] = i; _bd[idx] = d; }

    /// <summary>读某档位存储的 PID。</summary>
    public (double p, double i, double d) GetBand(int idx) => (_bp[idx], _bi[idx], _bd[idx]);
}
