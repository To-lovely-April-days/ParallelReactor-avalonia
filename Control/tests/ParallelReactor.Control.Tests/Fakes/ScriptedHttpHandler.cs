using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 脚本化的 HTTP 假服务端（纯 BCL）：路由表 (Method, PathRegex) → 响应函数（第 N 次调用可返回不同结果），记录每次请求的头与正文。
/// 响应正文用 Contracts 的 <see cref="ApiJson"/> 生成，与 HMI 同形。
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    public sealed record Call(string Method, string Path, string Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string? Lease => Header(ApiHeaders.Lease);
        public string? Idem => Header(ApiHeaders.Idempotency);
        public string? RequestKey => Header(ApiHeaders.RequestKey);
        public JsonElement Json => string.IsNullOrEmpty(Body) ? default : JsonDocument.Parse(Body).RootElement.Clone();
    }

    private sealed record Route(string Method, Regex Path, Func<Call, int, HttpResponseMessage> Respond) { public int Hits; }

    private readonly List<Route> _routes = new();
    private readonly object _lock = new();
    public List<Call> Calls { get; } = new();

    public ScriptedHttpHandler On(string method, string pathRegex, Func<Call, int, HttpResponseMessage> respond)
    {
        lock (_lock) _routes.Add(new Route(method.ToUpperInvariant(), new Regex("^" + pathRegex + "$"), respond));
        return this;
    }

    public ScriptedHttpHandler On(string method, string pathRegex, Func<Call, HttpResponseMessage> respond) => On(method, pathRegex, (c, _) => respond(c));

    public int Count(string method, string pathRegex)
    {
        var rx = new Regex("^" + pathRegex + "$");
        lock (_lock) return Calls.Count(c => c.Method == method.ToUpperInvariant() && rx.IsMatch(c.Path));
    }

    public IEnumerable<Call> Of(string method, string pathRegex)
    {
        var rx = new Regex("^" + pathRegex + "$");
        lock (_lock) return Calls.Where(c => c.Method == method.ToUpperInvariant() && rx.IsMatch(c.Path)).ToList();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
        var path = request.RequestUri!.PathAndQuery;
        const string prefix = "/api/v1";
        if (path.StartsWith(prefix, StringComparison.Ordinal)) path = path[prefix.Length..];
        var call = new Call(request.Method.Method.ToUpperInvariant(), path, body, headers);
        Route? route; int hit;
        lock (_lock)
        {
            Calls.Add(call);
            route = _routes.FirstOrDefault(r => r.Method == call.Method && r.Path.IsMatch(call.Path));
            hit = route == null ? 0 : route.Hits++;
        }
        if (route == null) return Error(OpCodes.NotFound, "脚本里没有这个路由：" + call.Method + " " + call.Path);
        return route.Respond(call, hit);
    }

    // —— 响应构造 ——

    public static HttpResponseMessage Json(int status, string body, params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (n, v) in headers) r.Headers.TryAddWithoutValidation(n, v);
        return r;
    }

    public static HttpResponseMessage Ok<T>(T dto) => Json(200, ApiJson.Serialize(dto));

    /// <summary>写命令成功：200 confirmed / 202 accepted。opId 取请求的 Idempotency-Key（服务端规则）。</summary>
    public static HttpResponseMessage Command(Call call, bool accepted, object? result, long opEventSeq = 1, bool replay = false, params string[] warnings)
    {
        var dto = new CommandResponseDto(true, call.Idem ?? Guid.NewGuid().ToString(), accepted ? "accepted" : "confirmed", ApiJson.ToElement(result), warnings, opEventSeq);
        return replay ? Json(accepted ? 202 : 200, ApiJson.Serialize(dto), (ApiHeaders.IdempotentReplay, "true")) : Json(accepted ? 202 : 200, ApiJson.Serialize(dto));
    }

    /// <summary>写命令失败（§7.1 带 opId）。状态码按 §11。</summary>
    public static HttpResponseMessage CommandError(Call call, string code, string message, long opEventSeq = 0)
    {
        var dto = new CommandErrorDto(false, call.Idem ?? Guid.NewGuid().ToString(), new CommandErrorBody(code, message, null), opEventSeq);
        var r = Json(ApiErrorStatus.Of(code), ApiJson.Serialize(dto));
        if (code is OpCodes.RateLimited or OpCodes.DownloadBusy) r.Headers.TryAddWithoutValidation("Retry-After", "1");
        return r;
    }

    /// <summary>读端点失败。</summary>
    public static HttpResponseMessage Error(string code, string message)
    {
        var r = Json(ApiErrorStatus.Of(code), ApiJson.Serialize(new ErrorDto(false, new ErrorBody(code, message, null))));
        if (code is OpCodes.RateLimited) r.Headers.TryAddWithoutValidation("Retry-After", "1");
        return r;
    }
}
