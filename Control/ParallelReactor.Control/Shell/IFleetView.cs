using System;
using System.Collections.Generic;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 外框需要的设备数据（M2：菜单、页签、图标栏、状态栏、全局停止格、资源管理器 / 检查器 / 时间轴的标题栏、新页签弹出框）。
/// 只读、扁平；每次数据变化后 <see cref="Changed"/>（UI 线程上、合并过的）—— 外框就地刷新文字和标记，不重建集合。
/// 实现：Fleet/FleetVm（演示 = Core 的 DemoFleet，实时 = LiveDeviceSource）；设备是 Fleet/DeviceVm，反应釜是 Fleet/ReactorVm。
/// </summary>
public interface IFleetView
{
    /// <summary>本机客户端名（菜单栏右端「本机：…」、控制权说明）。</summary>
    string ClientName { get; }

    /// <summary>演示数据（窗口标题与状态栏标「演示数据」，R17）。</summary>
    bool IsDemo { get; }

    /// <summary>有设备用的是假设参数的示例型号（状态栏「示例型号 · 参数为假设」，D2）。</summary>
    bool HasSampleModels { get; }

    /// <summary>当前时间（状态栏时钟；演示冻结在 2026-11-08 10:12:00）。本地时间。</summary>
    DateTime Now { get; }

    /// <summary>全部设备，按位置排序（1号机、2号机 …）。</summary>
    IReadOnlyList<IFleetDevice> Devices { get; }

    /// <summary>按位置找设备；没有返回 null。</summary>
    IFleetDevice? Device(int k);

    /// <summary>
    /// 设备动作（菜单「释放控制权」「立即重连」「全部停止」等，名字同样稿 ACTIONS：leaseRelease / leaseCancel / reconnect / stopAll …）。
    /// 返回要 toast 的结果文字（null = 不提示）。守卫（原因）先由 <see cref="IFleetDevice.Why"/> 给出；实现里必须再检查一次。
    /// </summary>
    string? Act(int k, string action);

    event EventHandler? Changed;
}

/// <summary>一台设备在外框里的样子（样稿 Device 的子集，API.md §3.1）。</summary>
public interface IFleetDevice
{
    /// <summary>位置（样稿 d.k）：页签、路由、选择都用它，设备在列表里的身份不变。</summary>
    int K { get; }
    string Alias { get; }
    /// <summary>型号标签（「HT-PR8 · 现场」）。</summary>
    string ModelLabel { get; }
    /// <summary>已安装的范围（「RV1–RV4」，悬停提示用）。</summary>
    string InstalledText { get; }
    string Host { get; }
    int Port { get; }
    string Sn { get; }

    ConnState Conn { get; }
    /// <summary>连上过（断线时「离线」，从未连上「未连接」）。</summary>
    bool EverOnline { get; }
    /// <summary>连接说明（「已连接」「连接中断 · 重连中 12 s」…，样稿 connInfo.text）。</summary>
    string ConnText { get; }
    /// <summary>断线时数据停在哪（「数据停在 10:11:40」）；在线时空串。</summary>
    string DataText { get; }

    LeaseState Lease { get; }
    bool EstopActive { get; }
    /// <summary>「急停中」之类（样稿 estopText）；没有急停时空串。</summary>
    string EstopText { get; }

    /// <summary>运行中的反应釜数（含报警，样稿 counts.running）。</summary>
    int Running { get; }
    int Installed { get; }
    /// <summary>未清除的错误报警数（不数「急停已触发」那一条，样稿 ui.alarmCounts）。</summary>
    int ErrAlarms { get; }
    /// <summary>提醒数。</summary>
    int WarnAlarms { get; }

    /// <summary>第一条（计入计数的）错误报警或提醒所在的反应釜 RV 号；设备级报警或没有时 0（状态栏报警项点击时选中它）。</summary>
    int AlarmRv(bool error);

    /// <summary>记录中的会话号；没有时 null。</summary>
    string? SessionId { get; }

    /// <summary>全部位置（含未安装），按 RV 号。</summary>
    IReadOnlyList<IFleetChannel> Channels { get; }

    /// <summary>这个动作现在为什么不能做（样稿 PR.sim.why）；能做返回空串。</summary>
    string Why(string action);
}

/// <summary>一个反应釜位置：RV 号、是否安装、状态键与状态字（StateMap 的结果；实现 = Fleet/ReactorVm）。</summary>
public interface IFleetChannel
{
    int N { get; }
    bool Installed { get; }
    /// <summary>界面上的状态（设备断线时是「离线」）。</summary>
    StateKey Key { get; }
    string Word { get; }
    /// <summary>反应釜自己的状态（断线时是断线前的，样稿 ch.state）：反应釜页签的报警红点与悬停提示看它。</summary>
    StateKey Own { get; }
    string OwnWord { get; }
}
