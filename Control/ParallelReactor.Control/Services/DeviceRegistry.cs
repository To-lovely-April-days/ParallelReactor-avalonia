using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Control.Core.Guard;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 登记在本机的一台真机：会话里的序号 <see cref="K"/>（页签、路由、选择、对话框参数都用它；同样稿的 d.k —— 启动时按 devices.json 的顺序 1、2、3…，
/// 之后添加的取最大值 + 1，移除 / 排序都不改别的设备的 K）、配置项（<see cref="Entry"/>，与 ControlConfig.Devices 里是同一个实例）、数据源。
/// </summary>
public sealed class RegisteredDevice
{
    internal RegisteredDevice(int k, DeviceEntry entry, LiveDeviceSource source)
    {
        K = k;
        Entry = entry;
        Source = source;
    }

    public int K { get; }
    public DeviceEntry Entry { get; }
    public LiveDeviceSource Source { get; }
    public Guid Id => Entry.Id;
}

/// <summary>编辑设置（样稿 editDevice 的字段 + D7 的「记住令牌」）。<see cref="Token"/> 为空 = 不改令牌（同样稿 if (o.token)）。</summary>
public sealed record DeviceEdit(string Alias, string Host, int Port, string? Token, bool RememberToken, bool AutoConnect);

/// <summary>
/// 多台设备的登记表（PORT-PLAN §5 M3「Services/DeviceRegistry.cs」）：增、删、改、排序设备，持有每台的 <see cref="LiveDeviceSource"/>，
/// 改动立即写回 devices.json（<see cref="ControlConfig.Save"/>；截图 / 演示 / 测试时不写）。
/// <list type="bullet">
/// <item><b>启动</b>：<see cref="StartAsync"/> 并行连接全部「启动时自动连接」的设备 —— 只有已经记住了令牌的（D7：没存令牌就不自动连接，
///   <see cref="DeviceEntry.CanAutoConnect"/>）；连不上不抛，设备留在列表里，各自倒计时重试（LiveDeviceSource 的重连监督：StartAsync 等第一次尝试的结果）。</item>
/// <item><b>退出</b>：<see cref="ShutdownAsync"/> 并行释放全部本机持有的控制权（每台 ≤ 3 s，总共 ≤ 5 s：App.axaml.cs 的关闭预算），再并行断开。</item>
/// <item><b>别名</b>：唯一、最长 8 个汉字或 16 个字母（样稿 aliasError，Core.Guard.ActionGuard.AliasError）；默认别名 = 第一个没用过的「k号机」。</item>
/// <item><b>K</b>：见 <see cref="RegisteredDevice"/>。FleetVm 按这里的顺序与 K 建 DeviceVm 列表（<see cref="Changed"/> 时就地同步）。</item>
/// </list>
/// 只在 UI 线程上用（数据源的事件经 UiContext 回到 UI 线程）。
/// </summary>
public sealed class DeviceRegistry : IAsyncDisposable
{
    /// <summary>退出时每台设备释放控制权的上限、全部设备合计的上限（App.axaml.cs 的关闭预算 5 s）。</summary>
    public static readonly TimeSpan ReleasePerDevice = TimeSpan.FromSeconds(3), ShutdownBudget = TimeSpan.FromSeconds(5);

    private readonly List<RegisteredDevice> _list = new();
    private readonly Func<DeviceEntry, LiveDeviceSource> _factory;
    private bool _disposed;

    /// <param name="config">devices.json（顺序、令牌、自动连接）。</param>
    /// <param name="sourceFactory">为一项配置新建数据源（FleetVm 给：带 UI 线程的 post、连接工厂、客户端名、「全部停止停不停搅拌」的设置）。</param>
    public DeviceRegistry(ControlConfig config, Func<DeviceEntry, LiveDeviceSource> sourceFactory)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _factory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        int k = 1;
        foreach (var e in config.Devices) _list.Add(Create(k++, e));
    }

    public ControlConfig Config { get; }

    /// <summary>全部设备，按顺序（设备管理、资源管理器、总览都按它排）。</summary>
    public IReadOnlyList<RegisteredDevice> Devices => _list;

    /// <summary>设备增、删、换顺序、改了设置（列表本身变了；设备的数据变化走各自数据源的 Changed）。</summary>
    public event Action? Changed;

    public RegisteredDevice? ByK(int k) => _list.FirstOrDefault(d => d.K == k);
    public RegisteredDevice? ById(Guid id) => _list.FirstOrDefault(d => d.Id == id);

    private RegisteredDevice Create(int k, DeviceEntry e)
    {
        var src = _factory(e);
        var rd = new RegisteredDevice(k, e, src);
        src.Changed += c => OnSourceChanged(rd, c);
        return rd;
    }

    /// <summary>连上之后记下序列号与型号（devices.json 的 lastSerial / lastModel：设备管理表在设备离线时也能显示），有变化才保存。</summary>
    private void OnSourceChanged(RegisteredDevice rd, SourceChange c)
    {
        if ((c & (SourceChange.Info | SourceChange.Conn)) == 0 || rd.Source.Info is not { } info) return;
        var serial = info.Serial;
        var model = rd.Source.Model.Key;
        if (serial == rd.Entry.LastSerial && model == rd.Entry.LastModel) return;
        rd.Entry.LastSerial = serial;
        rd.Entry.LastModel = model;
        Config.Save();
    }

    // ———————————————————— 别名 ————————————————————

    /// <summary>别名检查（样稿 aliasError）：'' = 可用；<paramref name="except"/> = 正在改的那台（不跟自己比）。</summary>
    public string AliasError(string? alias, Guid? except) =>
        ActionGuard.AliasError(alias, _list.Where(d => d.Id != except).Select(d => new PeerDevice(d.Entry.Alias, d.Entry.Host)));

    /// <summary>第一个没被占用的「k号机」（样稿 freeAlias）。</summary>
    public string FreeAlias() => ActionGuard.FreeAlias(_list.Select(d => d.Entry.Alias));

    // ———————————————————— 增、删、改、排序 ————————————————————

    /// <summary>
    /// 添加一台（样稿 addDevice）：别名不合规时抛 ArgumentException（对话框先用 <see cref="AliasError"/> 拦住）；K = 现有最大 + 1，排在最后；
    /// 立即保存（令牌按 RememberToken 决定是否落盘）。不连接 —— 调用方接着 <see cref="ConnectAsync"/>。
    /// </summary>
    public RegisteredDevice Add(DeviceEntry entry)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeviceRegistry));
        entry.Alias = ActionGuard.JsTrim(entry.Alias ?? "");
        entry.Host = (entry.Host ?? "").Trim();
        var err = AliasError(entry.Alias, null);
        if (err.Length > 0) throw new ArgumentException(err, nameof(entry));
        if (entry.Host.Length == 0) throw new ArgumentException("请填写地址", nameof(entry));
        if (_list.Any(d => d.Id == entry.Id)) throw new ArgumentException("设备已经在列表里", nameof(entry));
        if (!entry.RememberToken) entry.AutoConnect = false;   // D7：自动连接要求存了令牌
        int k = _list.Count == 0 ? 1 : _list.Max(d => d.K) + 1;
        Config.Devices.Add(entry);
        var rd = Create(k, entry);
        _list.Add(rd);
        Config.Save();
        Changed?.Invoke();
        return rd;
    }

    /// <summary>
    /// 编辑设置（样稿 editDevice）：返回 '' 或别名错误（不改任何东西）。地址、端口或令牌变了且正连着时，数据源先释放控制权、断开，再按新地址连接。
    /// 换了令牌时学到的令牌权限作废。「启动时自动连接」只在记住了令牌时生效（D7）。
    /// </summary>
    /// <param name="background">true = 存好就返回、重新连接在后台做（编辑设置对话框：不让对话框等释放控制权 + 第一次连接尝试，最长约 13 s，M3 复核）。</param>
    public async Task<string> EditAsync(Guid id, DeviceEdit e, bool background = false)
    {
        var rd = ById(id) ?? throw new ArgumentException("设备不存在", nameof(id));
        var err = AliasError(e.Alias, id);
        if (err.Length > 0) return err;
        var host = (e.Host ?? "").Trim();
        if (host.Length == 0) return "请填写地址";
        var en = rd.Entry;
        // 勾了「记住令牌」却没有令牌可记（这次没输入、本机也没有）：存下去下次启动只会得到「记住的令牌读不出来」—— 先要令牌（M3 复核）
        if (e.RememberToken && string.IsNullOrWhiteSpace(e.Token) && !en.HasToken) return "请输入访问令牌";
        en.Alias = ActionGuard.JsTrim(e.Alias);
        en.Host = host;
        if (e.Port is > 0 and <= 65535) en.Port = e.Port;
        if (!string.IsNullOrWhiteSpace(e.Token)) en.Token = e.Token.Trim();
        en.RememberToken = e.RememberToken;
        en.AutoConnect = e.AutoConnect && e.RememberToken;
        Config.Save();
        Changed?.Invoke();
        var update = rd.Source.UpdateConfigAsync(en.ToDeviceConfig());
        if (!background) await update.ConfigureAwait(true);
        else _ = update;
        return "";
    }

    /// <summary>改名（样稿 rename）：返回 '' 或别名错误。</summary>
    public string Rename(Guid id, string alias)
    {
        var rd = ById(id) ?? throw new ArgumentException("设备不存在", nameof(id));
        var err = AliasError(alias, id);
        if (err.Length > 0) return err;
        rd.Entry.Alias = ActionGuard.JsTrim(alias);
        rd.Source.Rename(rd.Entry.Alias);
        Config.Save();
        Changed?.Invoke();
        return "";
    }

    /// <summary>
    /// 「启动时自动连接」开关（设备管理表的复选框）：打开要求已经记住了令牌（D7）—— 没记住时不改、返回原因；成功返回 ''。
    /// </summary>
    public string SetAutoConnect(Guid id, bool on)
    {
        var rd = ById(id) ?? throw new ArgumentException("设备不存在", nameof(id));
        if (on && AutoConnectReason(rd.Entry) is { Length: > 0 } why) return why;
        rd.Entry.AutoConnect = on;
        Config.Save();
        Changed?.Invoke();
        return "";
    }

    /// <summary>
    /// 「启动时自动连接」被拒的原因（D7：要求令牌已经存在本机）。样稿没有这一句（演示数据不存令牌）；写法沿用表单里的两个字段名。
    /// </summary>
    public const string AutoConnectNeedsToken = "先勾选「记住令牌」：启动时自动连接要用本机保存的令牌";

    /// <summary>
    /// 勾了「记住令牌」、但本机没有令牌（记住的读不出来、或还没输入过）时「自动连接」的原因（M3 复核：以前也写「先勾选「记住令牌」」，可那一栏明明勾着）。
    /// 样稿没有这一句；写法沿用对话框名「编辑设置」。
    /// </summary>
    public const string AutoConnectTokenMissing = "令牌未保存 · 在「编辑设置」里输入";

    /// <summary>这项配置能不能勾「启动时自动连接」：'' = 能；否则 <see cref="AutoConnectNeedsToken"/> / <see cref="AutoConnectTokenMissing"/>。</summary>
    public static string AutoConnectReason(DeviceEntry e) => !e.RememberToken ? AutoConnectNeedsToken : !e.HasToken ? AutoConnectTokenMissing : "";

    /// <summary>
    /// 移除（样稿 remove）：数据源先释放本机持有的控制权（≤ 3 s）、断开、摘掉事件，再从列表和 devices.json 里删掉（令牌一起删）。设备上的程序与数据不受影响。
    /// </summary>
    public async Task RemoveAsync(Guid id)
    {
        var rd = ById(id);
        if (rd == null) return;
        _list.Remove(rd);
        Config.Devices.Remove(rd.Entry);
        Config.Save();
        Changed?.Invoke();
        await rd.Source.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>换顺序：把这台挪到第 <paramref name="index"/> 位（0 起；超出范围时放到头 / 尾）。K 不变。</summary>
    public void Move(Guid id, int index)
    {
        var rd = ById(id) ?? throw new ArgumentException("设备不存在", nameof(id));
        index = Math.Clamp(index, 0, _list.Count - 1);
        int at = _list.IndexOf(rd);
        if (at == index) return;
        _list.RemoveAt(at);
        _list.Insert(index, rd);
        Config.Devices.Remove(rd.Entry);
        Config.Devices.Insert(index, rd.Entry);
        Config.Save();
        Changed?.Invoke();
    }

    // ———————————————————— 连接 ————————————————————

    /// <summary>
    /// 启动：并行连接全部「启动时自动连接」且记住了令牌的设备（D7；没存令牌的不连 —— 设备留在列表里，「已断开」，在「编辑设置」里填了令牌再「连接」）。
    /// 返回的任务在每台的第一次连接尝试都有了结果时完成；不抛：连不上的设备各自显示原因（LiveDeviceSource.LastError + 提示）并倒计时重试。
    /// </summary>
    public Task StartAsync() => Task.WhenAll(_list.Where(d => d.Entry.CanAutoConnect).Select(d => d.Source.StartAsync()).ToArray());

    /// <summary>连接一台（「添加并连接」、设备管理「连接」）：同 <see cref="StartAsync"/> 的单台版，不抛。</summary>
    public Task ConnectAsync(Guid id) => ById(id)?.Source.StartAsync() ?? Task.CompletedTask;

    /// <summary>
    /// 退出前收尾：并行释放全部本机持有的控制权、取消正在进行的申请（每台 ≤ <paramref name="perDevice"/>，R7：这段时间里触屏批准的会立即释放），整体不超过 <paramref name="total"/>；
    /// 然后并行释放全部数据源（摘事件、断开），同样受 <paramref name="total"/> 限制。不抛。可在任意线程调用。
    /// </summary>
    public async Task ShutdownAsync(TimeSpan perDevice, TimeSpan total)
    {
        if (_disposed) return;
        _disposed = true;
        var started = DateTime.UtcNow;
        var all = _list.ToArray();
        var release = Task.WhenAll(all.Select(d => d.Source.ReleaseLeaseQuietAsync(perDevice)));
        await Task.WhenAny(release, Task.Delay(total)).ConfigureAwait(false);
        var left = total - (DateTime.UtcNow - started);
        var dispose = Task.WhenAll(all.Select(d => d.Source.DisposeAsync().AsTask()));
        if (left > TimeSpan.Zero) await Task.WhenAny(dispose, Task.Delay(left)).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(ShutdownAsync(ReleasePerDevice, ShutdownBudget));
}
