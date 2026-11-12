using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Services;

/// <summary>「测试连接」的结果：成功时带 /device 读到的设备信息与解析出的型号。<see cref="Text"/> 是对话框底栏左侧那一句。</summary>
public sealed record ConnectionTestResult(bool Ok, string Text, DeviceInfoDto? Device = null, ModelProfile? Model = null);

/// <summary>
/// 添加设备对话框的「测试连接」（PORT-PLAN §5 M3 AddDevice）：先 GET /health（<b>不带令牌</b>：HMI 的 /health 免鉴权，只看地址和端口通不通），
/// 再 GET /device（<b>带令牌</b>：验证令牌，读型号与序列号）。不建 WebSocket、不申请控制权、不写任何东西。
/// 成功：「连接成功 · 读取到 HT-PR8」（样稿 dlg-test is-ok）；失败：「连接失败：…」（同实时设备连不上时的提示，原因用 Fleet/DeviceActions.Describe）。
/// </summary>
public static class ConnectionTester
{
    /// <summary>每一步的超时（地址不通时别让对话框等太久）。</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);

    /// <param name="handlerFactory">每个请求一个 HTTP 处理器（测试给脚本化的假服务端；null = 系统默认）。</param>
    public static async Task<ConnectionTestResult> TestAsync(string host, int port, string token, string clientName, ModelProfiles models,
        Func<HttpMessageHandler>? handlerFactory = null, CancellationToken ct = default)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return new ConnectionTestResult(false, "请填写地址");
        if (port is <= 0 or > 65535) return new ConnectionTestResult(false, "端口无效");
        Uri baseUrl;
        try { baseUrl = new Uri("http://" + host + ":" + port); }
        catch (UriFormatException) { return new ConnectionTestResult(false, "连接失败：地址无效"); }
        try
        {
            // 1. /health：不带令牌
            using (var http = handlerFactory == null ? new HttpClient() : new HttpClient(handlerFactory(), disposeHandler: true))
            {
                http.Timeout = StepTimeout;
                using var resp = await http.GetAsync(new Uri(baseUrl, ApiHeaders.Prefix.TrimStart('/') + "/health"), ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw ApiException.Parse((int)resp.StatusCode, body, "GET health 失败");
            }
            // 2. /device：带令牌（验证令牌）
            if (string.IsNullOrWhiteSpace(token)) return new ConnectionTestResult(false, "请输入访问令牌");
            var opt = new ClientOptions(baseUrl, token.Trim(), string.IsNullOrWhiteSpace(clientName) ? ControlConfig.DefaultClientName : clientName) { HttpTimeout = StepTimeout };
            using var rest = new ReactorApiClient(opt, handlerFactory?.Invoke());
            var info = await rest.DeviceAsync(ct).ConfigureAwait(false);
            var model = models.Resolve(info);
            return new ConnectionTestResult(true, "连接成功 · 读取到 " + model.Label, info, model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, "连接失败：" + Fleet.DeviceActions.Describe(ex));
        }
    }
}
