using System;

namespace ParallelReactor.Services;

/// <summary>
/// 某通道某档 PID 的来历（随档位 PID 一起持久化，v0.6 起）。
/// <list type="bullet">
/// <item><see cref="TunedSpC"/> / <see cref="TunedAt"/>：最近一次<b>完成并存档</b>的自整定——整定目标温度（℃）与完成时刻（UTC）。
/// 被取消 / 急停 / 超温切断打断的整定不记录；仪表自己发起、软件不知道目标温度的整定 TunedSpC 为 null。</item>
/// <item><see cref="ManualEditedAt"/>：整定之后又被手改（触屏键盘或 PUT /channels/{id}/pid/{band}，数值确有变化）的最近时刻（UTC）。
/// 手改不清除整定记录，只打这个标记；下一次整定完成时整条记录被替换（标记随之清除）。从未整定过的档只手改过时，只有这一项。</item>
/// </list>
/// 三项都为 null 的记录不会存在（没有来历 = 记录本身为 null，即出厂默认或首次开机从仪表读来的种子值）。
/// </summary>
public sealed record BandTuneMeta(double? TunedSpC, DateTime? TunedAt, DateTime? ManualEditedAt);

/// <summary>
/// 每通道自整定的进行状态 + 每通道每档 PID 的来历（<see cref="BandTuneMeta"/>）。从 TempService 抽出的纯逻辑（无 UI 依赖，测试工程直接链接）。
/// <para>
/// 进行状态：整定发起时记下目标档位与目标温度（整定期间用户可能切档 / 改分界，完成回读必须存回「发起时」的档）；
/// 被取消 / 急停 / 超温切断打断时记 aborted——仪表 At 从 1 变成 4 时轮询同样会看到「整定结束」，但仪表里没有新结果，不能回读存档。
/// </para>
/// <para>
/// 线程：与原 TempService 的普通数组相同——发起 / 完成 / 手改在 UI 线程；急停快速路径可在任意线程调用 <see cref="Abort"/>。
/// 来历记录是不可变 record，数组元素整体替换，快照构建（UI 线程）与持久化读取不会读到半条。
/// </para>
/// </summary>
public sealed class AutotuneBook
{
    private readonly int[] _band;
    private readonly double?[] _sp;
    private readonly bool[] _aborted;
    private readonly BandTuneMeta?[][] _meta;   // [通道][档]

    public int ChannelCount { get; }
    public int BandCount { get; }

    public AutotuneBook(int channels, int bands)
    {
        ChannelCount = channels;
        BandCount = bands;
        _band = new int[channels];
        Array.Fill(_band, -1);
        _sp = new double?[channels];
        _aborted = new bool[channels];
        _meta = new BandTuneMeta?[channels][];
        for (int i = 0; i < channels; i++) _meta[i] = new BandTuneMeta?[bands];
    }

    // ===== 进行中的整定（i = 通道下标 0 起）=====

    /// <summary>进行中的整定要存回的档位（-1 = 没有软件发起的进行中整定）。</summary>
    public int TuneBand(int i) => _band[i];

    /// <summary>进行中的整定的目标温度（℃；没有为 null）。</summary>
    public double? TuneSp(int i) => _sp[i];

    /// <summary>是否已标记「被打断」（轮询看到结束时不回读）。</summary>
    public bool IsAborted(int i) => _aborted[i];

    /// <summary>启动整定前清掉上一次的「被打断」标记（仪表写入之前调用）。</summary>
    public void ClearAbort(int i) => _aborted[i] = false;

    /// <summary>仪表已进入整定（At=1 写入成功）：记下目标档位与目标温度。</summary>
    public void Begin(int i, int band, double sp)
    {
        _band[i] = band;
        _sp[i] = sp;
    }

    /// <summary>整定被取消 / 急停 / 超温切断打断：之后轮询看到「整定结束」时不回读、不存档、不记来历。</summary>
    public void Abort(int i)
    {
        _aborted[i] = true;
        _band[i] = -1;
        _sp[i] = null;
    }

    /// <summary>撤销 <see cref="Abort"/>（超温切断的 At=4 写入失败：仪表上的整定仍在进行，完成后的新 PID 不能丢）。</summary>
    public void Restore(int i, int band, double? sp)
    {
        _aborted[i] = false;
        _band[i] = band;
        _sp[i] = sp;
    }

    /// <summary>
    /// 轮询看到该通道「整定结束」（At 从 1 变为非 1）。返回 true = 正常完成，应回读 PID 并 <see cref="Complete"/>；
    /// false = 被打断（清掉标记，什么都不记）。
    /// </summary>
    public bool OnFinished(int i)
    {
        if (!_aborted[i]) return true;
        _aborted[i] = false;
        return false;
    }

    /// <summary>
    /// 整定结果已回读并存档：返回存入的档位（进行中的档位；没有时用 fallbackBand = 界面显示档），
    /// 并把该档来历替换为 {整定目标温度, 完成时刻, 手改 = null}。之后进行状态清空。
    /// </summary>
    public int Complete(int i, int fallbackBand, DateTime utcNow)
    {
        int band = _band[i] >= 0 ? _band[i] : fallbackBand;
        double? sp = _band[i] >= 0 ? _sp[i] : null;   // 没有进行中的档位 = 不是软件发起的整定，目标温度未知
        _band[i] = -1;
        _sp[i] = null;
        if (band >= 0 && band < BandCount)
            _meta[i][band] = new BandTuneMeta(sp, Utc(utcNow), null);
        return band;
    }

    // ===== 来历 =====

    /// <summary>手改了某档 PID（数值确有变化且已保存）：保留整定记录，只更新手改时刻。</summary>
    public void MarkManual(int i, int band, DateTime utcNow)
    {
        if (band < 0 || band >= BandCount) return;
        var old = _meta[i][band];
        _meta[i][band] = old == null ? new BandTuneMeta(null, null, Utc(utcNow)) : old with { ManualEditedAt = Utc(utcNow) };
    }

    /// <summary>某通道某档的来历（没有为 null）。</summary>
    public BandTuneMeta? Meta(int i, int band) => _meta[i][band];

    /// <summary>导出 [通道][档] 的来历副本（持久化用；记录本身不可变，可以共享）。</summary>
    public BandTuneMeta?[][] Export()
    {
        var all = new BandTuneMeta?[ChannelCount][];
        for (int i = 0; i < ChannelCount; i++) all[i] = (BandTuneMeta?[])_meta[i].Clone();
        return all;
    }

    /// <summary>
    /// 从持久化数据恢复来历（开机调用）。向后兼容：null（旧存档没有这一项）什么都不做；通道 / 档位数不足的只恢复已有部分，多出的忽略；
    /// 三项全空的记录当作没有。时间一律按 UTC 解释。
    /// </summary>
    public void Import(BandTuneMeta?[][]? data)
    {
        if (data == null) return;
        for (int i = 0; i < ChannelCount && i < data.Length; i++)
        {
            var row = data[i];
            if (row == null) continue;
            for (int b = 0; b < BandCount && b < row.Length; b++)
            {
                var m = row[b];
                _meta[i][b] = m is null || (m.TunedSpC == null && m.TunedAt == null && m.ManualEditedAt == null)
                    ? null
                    : new BandTuneMeta(m.TunedSpC is { } s && double.IsFinite(s) ? s : null, UtcOrNull(m.TunedAt), UtcOrNull(m.ManualEditedAt));
            }
        }
    }

    private static DateTime Utc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    private static DateTime? UtcOrNull(DateTime? t) => t is { } v ? Utc(v) : null;
}
