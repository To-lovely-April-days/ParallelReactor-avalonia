using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ParallelReactor.Control.Core.Unattended;

namespace ParallelReactor.Control.Services.Unattended;

/// <summary>Windows：winmm PlaySound 放系统的「严重停止」声（异步）；放不出来时控制台蜂鸣（在线程池上，不卡界面）。</summary>
public sealed class WindowsAlarmSound : IAlarmSound
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    public void Play()
    {
        try
        {
            if (PlaySound("SystemHand", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault)) return;
        }
        catch { }
        _ = Task.Run(() =>
        {
            try { if (OperatingSystem.IsWindows()) { Console.Beep(880, 220); Console.Beep(660, 220); } } catch { }
        });
    }
}

/// <summary>macOS：afplay 一个系统声音（/System/Library/Sounds/Sosumi.aiff，没有时 Basso / Glass），不等放完。</summary>
public sealed class MacAlarmSound : IAlarmSound
{
    private static readonly string[] Candidates =
    {
        "/System/Library/Sounds/Sosumi.aiff", "/System/Library/Sounds/Basso.aiff", "/System/Library/Sounds/Glass.aiff",
    };

    public void Play()
    {
        try
        {
            string? file = null;
            foreach (var c in Candidates) if (File.Exists(c)) { file = c; break; }
            if (file == null) return;
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/afplay", "\"" + file + "\"") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch { }
    }
}

/// <summary>不出声（演示、无头、截图、不支持的系统）。</summary>
public sealed class SilentAlarmSound : IAlarmSound
{
    public int Plays { get; private set; }
    public void Play() => Plays++;
}
