using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ParallelReactor.Services;

/// <summary>急停锁存内容：触发时刻与来源（触屏 / 远程 / 开机恢复…）。</summary>
public sealed record LatchInfo(DateTimeOffset Time, string Source);

/// <summary>
/// 急停锁存文件（纯逻辑，单元测试直接链接）。急停触发时写入，<b>只有本地复位才删除</b>；
/// 开机时若存在，直接进入急停锁定（见 MainViewModel 构造函数），防止 HMI 崩溃被 systemd 拉起后锁定丢失。
/// <para>
/// 位置：数据目录（<see cref="DataLogger.DefaultDirFor"/>：PR_DATA_DIR，默认 ~/ParallelReactor-data），
/// 真机文件名 <c>estop.latch</c>，模拟模式 <c>estop-sim.latch</c>——设置了 PR_DATA_DIR 时真机与模拟共用同一目录，
/// 文件名必须分开，模拟里按的急停不能锁住真机。
/// </para>
/// 写入是原子的（先写 .tmp 并刷盘，再改名覆盖）；文件存在但内容损坏时按「已锁存、来源未知」处理（失效安全）。
/// Linux 上改名 / 删除之后再对所在目录 fsync（尽力而为）：ext4 上新建文件名的改名若未落盘，急停后立即断电可能丢失锁存。
/// 写入会刷盘，可能较慢——调用方不要放在停止帧之前、也不要放在 UI 线程上。
/// </summary>
public sealed class EstopLatch
{
    public string Path { get; }

    public EstopLatch(string path) => Path = path;

    /// <summary>锁存文件路径：数据目录下 estop.latch（真机）/ estop-sim.latch（模拟）。</summary>
    public static string PathFor(string dataDir, bool sim) => System.IO.Path.Combine(dataDir, sim ? "estop-sim.latch" : "estop.latch");

    public bool Exists => File.Exists(Path);

    /// <summary>原子写入（覆盖已有锁存）。失败抛异常，由调用方记事件。</summary>
    public void Write(LatchInfo info)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = Path + ".tmp";
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dto { Time = info.Time, Source = info.Source });
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(json, 0, json.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, Path, overwrite: true);
        SyncDir(dir);
    }

    /// <summary>读锁存：不存在返回 false；存在但损坏 / 读不出时仍返回 true（来源标为损坏）。</summary>
    public bool TryRead(out LatchInfo? info)
    {
        info = null;
        if (!File.Exists(Path)) return false;
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllBytes(Path));
            if (d == null || d.Source == null) throw new InvalidDataException();
            info = new LatchInfo(d.Time, d.Source);
        }
        catch
        {
            DateTimeOffset t;
            try { t = File.GetLastWriteTime(Path); } catch { t = DateTimeOffset.Now; }
            info = new LatchInfo(t, "未知（锁存文件损坏）");
        }
        return true;
    }

    /// <summary>删除锁存（本地复位时）。返回之后文件是否已不存在；删除失败返回 false，不抛异常。</summary>
    public bool Clear()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            try { File.Delete(Path + ".tmp"); } catch { }
            SyncDir(System.IO.Path.GetDirectoryName(Path));
            return !File.Exists(Path);
        }
        catch { return false; }
    }

    /// <summary>目录项落盘（Linux，尽力而为，失败忽略）：open(dir, O_RDONLY) + fsync。其他平台不做。</summary>
    private static void SyncDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !OperatingSystem.IsLinux()) return;
        try
        {
            int fd = open(dir, 0);
            if (fd < 0) return;
            try { fsync(fd); } finally { close(fd); }
        }
        catch { /* libc 不可用等：只是少了一层保证 */ }
    }

    [DllImport("libc", SetLastError = true)] private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int fsync(int fd);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    private sealed class Dto
    {
        public DateTimeOffset Time { get; set; }
        public string? Source { get; set; }
    }
}
