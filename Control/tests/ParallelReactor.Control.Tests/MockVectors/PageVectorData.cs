using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 读取 <see cref="PageMockVectors.Json"/>（gen-page-vectors.sh：样稿在 WebKit 里实际渲染、probe-*.js 量出来的版面与报警 / 事件文字）。只解析一次。
/// 场景名：ov-*（总览版面）、dv-* / dx-*（设备页版面）、to-*（总览报警小块 / 报警条）、td-* / tt-*（设备页「报警与事件」，1920 三列 / 1600 页签）。
/// </summary>
internal static class PageVectorData
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(PageMockVectors.Json));

    public static IEnumerable<JsonElement> Of(string kind) => Doc.Value.RootElement.GetProperty(kind).EnumerateArray();

    public static JsonElement Get(string kind, string name) => Of(kind).First(v => v.GetProperty("name").GetString() == name);

    public static IEnumerable<object[]> Names(string kind) => Of(kind).Select(v => new object[] { v.GetProperty("name").GetString()! });

    /// <summary>路由里的查询串（'?' 后面）。</summary>
    public static string Query(JsonElement v)
    {
        string r = v.GetProperty("route").GetString()!;
        int i = r.IndexOf('?');
        return i < 0 ? "" : r.Substring(i + 1);
    }

    /// <summary>
    /// 按场景的查询串建演示数据（与样稿同样的参数），再执行探针前的操作（目前只有「确认第一条报警」：
    /// <c>PR.sim.act(PR.sim.dev(k), 'alarmAck', { id: PR.sim.dev(k).alarms[0].id })</c>）。
    /// </summary>
    public static DemoFleet Fleet(JsonElement v)
    {
        var f = new DemoFleet(DemoParams.Parse(Query(v)));
        string pre = v.GetProperty("pre").GetString() ?? "";
        if (pre.Length > 0)
        {
            var m = Regex.Match(pre, @"PR\.sim\.act\(PR\.sim\.dev\((\d+)\), 'alarmAck', \{ id: PR\.sim\.dev\(\1\)\.alarms\[0\]\.id \}\);");
            if (!m.Success) throw new InvalidOperationException("不认识的探针前操作：" + pre);
            var d = f.Device(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))!;
            var r = f.Act(d, DeviceAction.AlarmAck, new ActionPayload(Id: d.Alarms[0].Id));
            if (!r.Ok) throw new InvalidOperationException("探针前操作没有执行：" + r.Message);
        }
        return f;
    }

    /// <summary>探针记下的设备（型号键、已安装、通道数、在线、连上过、急停）→ 内置型号表里的型号（核对装机与样稿一致）。</summary>
    public static (ModelProfile Model, bool Online, bool Ever, bool Estop) Device(JsonElement dev)
    {
        var m = ModelProfiles.Embedded.Get(dev.GetProperty("model").GetString())!;
        var inst = dev.GetProperty("inst").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        if (!m.Installed.SequenceEqual(inst) || m.ChannelCount != dev.GetProperty("ch").GetInt32())
            throw new InvalidOperationException("型号表与样稿不一致：" + m.Key);
        return (m, dev.GetProperty("online").GetBoolean(), dev.GetProperty("ever").GetBoolean(), dev.GetProperty("estop").GetBoolean());
    }

    public static double D(JsonElement e) => e.GetDouble();

    public static int? Len(JsonElement e) => e.ValueKind == JsonValueKind.String ? int.Parse(e.GetString()!, CultureInfo.InvariantCulture) : null;

    public static string? S(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>空白归一（样稿 textContent 里的不换行空格等）。</summary>
    public static string Norm(string? s) => s is null ? "" : Regex.Replace(s, @"\s+", " ").Trim();
}
