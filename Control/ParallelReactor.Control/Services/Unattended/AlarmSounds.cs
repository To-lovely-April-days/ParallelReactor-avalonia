using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using ParallelReactor.Control.Core.Unattended;

namespace ParallelReactor.Control.Services.Unattended;

/// <summary>
/// Windows：winmm PlaySound 放系统的「感叹号」提示音（SystemExclamation，Win10/11 是一声柔和的叮；异步）；没有时放系统默认提示音。
/// 不再用「严重停止」声和控制台蜂鸣（用户 2026-11-19：「点了泄漏测试为什么会叫呢 很吓人」）。
/// </summary>
public sealed class WindowsAlarmSound : IAlarmSound
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndAlias = 0x00010000;

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, uint fdwSound);

    public void Play()
    {
        try
        {
            if (PlaySound("SystemExclamation", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault)) return;
            PlaySound("SystemDefault", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        }
        catch { }
    }
}

/// <summary>macOS：afplay 一个柔和的系统声音（Glass，没有时 Ping / Tink），不等放完。</summary>
public sealed class MacAlarmSound : IAlarmSound
{
    private static readonly string[] Candidates =
    {
        "/System/Library/Sounds/Glass.aiff", "/System/Library/Sounds/Ping.aiff", "/System/Library/Sounds/Tink.aiff",
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
