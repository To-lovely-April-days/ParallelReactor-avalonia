using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ParallelReactor.Control.Core.Guard;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 本机记住的一台设备（devices.json 的一项，PORT-PLAN §5 M3「ControlConfig v2」）：身份 <see cref="Id"/>、别名、地址、端口、令牌、
/// 「记住令牌」「启动时自动连接」，以及最后一次连上时读到的序列号与型号（设备管理表在设备离线时也能显示）。
/// <see cref="Token"/> 是明文，只在内存里；落盘与否、怎么落盘见 <see cref="TokenProtector"/>（D7：只有勾了「记住令牌」才存）。
/// </summary>
public sealed class DeviceEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Alias { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = ControlConfig.DefaultPort;
    /// <summary>访问令牌（明文，内存里）。没记住、或记住的解不开时为 null（要重新输入）。</summary>
    public string? Token { get; set; }
    /// <summary>用户勾了「记住令牌」（D7）：只有这时令牌才落盘。</summary>
    public bool RememberToken { get; set; }
    /// <summary>启动时自动连接（要求已经记住了令牌，见 <see cref="CanAutoConnect"/>）。</summary>
    public bool AutoConnect { get; set; }
    /// <summary>最后一次连上时 /device 的序列号。</summary>
    public string? LastSerial { get; set; }
    /// <summary>最后一次连上时的型号（型号表的键，如 PR8）。</summary>
    public string? LastModel { get; set; }

    /// <summary>有令牌可用（内存里有）。</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(Token);

    /// <summary>启动时真的会自动连接：勾了自动连接、记住了令牌、令牌也读得出来（D7：没存令牌就不自动连接）。</summary>
    public bool CanAutoConnect => AutoConnect && RememberToken && HasToken;

    /// <summary>给数据源的配置（地址、端口、令牌、别名）。</summary>
    public DeviceConfig ToDeviceConfig() => new(Id, Alias, Host, Port, Token, AutoConnect);

    public DeviceEntry Clone() => new()
    {
        Id = Id, Alias = Alias, Host = Host, Port = Port, Token = Token, RememberToken = RememberToken, AutoConnect = AutoConnect,
        LastSerial = LastSerial, LastModel = LastModel,
    };
}

/// <summary>
/// PC 端本机配置 v2：devices.json（PORT-PLAN §5 M3）——
/// <code>
/// { "version": 2, "clientName": "PC-测试工位1", "timelineMinutes": 20, "tokenStore": "dpapi" | "file",
///   "devices": [ { "id", "alias", "host", "port", "token"（只有 Windows DPAPI 的密文）, "rememberToken", "autoConnect", "lastSerial", "lastModel" }, … ] }
/// </code>
/// 设备的顺序就是数组的顺序（设备管理、资源管理器、总览按它排）。本机客户端名全局一个（样稿：「本机客户端名全局一个」）。
/// <list type="bullet">
/// <item><b>位置</b>：环境变量 PR_CONTROL_CONFIG（目录；以 .json 结尾且不是已有目录时当作 devices.json 文件本身），否则 macOS
///   ~/Library/Application Support/ParallelReactor.Control、其他平台 ApplicationData/ParallelReactor.Control。绝不写触屏 HMI 的 ParallelReactor 目录。</item>
/// <item><b>迁移</b>：没有 devices.json、但同目录有 v1 的 control.json（一台设备的 Host / Port / Token / RememberToken / AutoConnect / ClientName）时，
///   把它变成「1号机」写进 devices.json；v1 里明文的令牌在迁移成功后从 control.json 抹掉（令牌改由 <see cref="TokenProtector"/> 保存），control.json 其余内容留着。</item>
/// <item><b>不写文件</b>：设置了 PR_CONTROL_SHOT 或 PR_CONTROL_DEMO（截图、演示）时什么都不写（<see cref="CanWrite"/> = false）。</item>
/// <item><b>原子写</b>：先写同目录的临时文件（macOS / Linux 0600）再改名替换；写到一半断电也不会留下半个文件。</item>
/// <item><b>容错读</b>：允许注释与多余的逗号；字段名不分大小写；类型不对的字段用默认值；没有地址的项跳过；重复的 Id 重新生成；
///   别名为空 / 太长 / 重复时换成第一个没用过的「k号机」；端口不在 1–65535 时用 8080；整个文件坏了时从空列表开始，
///   第一次保存前把坏文件留一份 devices.json.bad。</item>
/// <item><b>令牌</b>（D7）：只存勾了「记住令牌」的；「启动时自动连接」要求令牌已经存了（没存时读出来的 autoConnect 一律当 false）。</item>
/// </list>
/// 只在 UI 线程上用（DeviceRegistry 持有它）。
/// </summary>
public sealed class ControlConfig
{
    public const int CurrentVersion = 2;
    public const string FileName = "devices.json";
    public const string LegacyFileName = "control.json";
    public const string DefaultClientName = "PC-测试工位1";
    public const int DefaultPort = 8080;

    private readonly List<string> _notes = new();
    private bool _corrupt;

    private ControlConfig(string dir, string file, bool canWrite, TokenStoreMode mode)
    {
        DirectoryPath = dir;
        FilePath = file;
        CanWrite = canWrite;
        TokenMode = mode;
    }

    /// <summary>本机客户端名（申请控制权时报给触屏；菜单栏右端「本机：…」）。</summary>
    public string ClientName { get; set; } = DefaultClientName;

    /// <summary>时间轴窗口（分钟，v1 带过来的，默认 20）。</summary>
    public int TimelineMinutes { get; set; } = 20;

    /// <summary>全部设备，按顺序。</summary>
    public List<DeviceEntry> Devices { get; } = new();

    /// <summary>配置目录（drafts.json、tokens.json、models.json 也在这里）。</summary>
    public string DirectoryPath { get; }

    /// <summary>devices.json 的完整路径。</summary>
    public string FilePath { get; }

    /// <summary>能写文件（没有设置 PR_CONTROL_SHOT / PR_CONTROL_DEMO，也不是测试用的内存配置）。</summary>
    public bool CanWrite { get; }

    /// <summary>令牌落盘方式（Windows DPAPI / 其它 0600 文件）。</summary>
    public TokenStoreMode TokenMode { get; }

    /// <summary>这次加载是从 v1 的 control.json 迁移来的。</summary>
    public bool Migrated { get; private set; }

    /// <summary>加载时值得告诉用户 / 写进日志的事（迁移了、文件坏了、令牌解不开、项被跳过…）。</summary>
    public IReadOnlyList<string> LoadNotes => _notes;

    /// <summary>tokens.json 的完整路径（macOS / Linux）。</summary>
    public string TokenFilePath => Path.Combine(DirectoryPath, TokenProtector.FileName);

    /// <summary>容错的 JSON 读法（注释、多余的逗号）。</summary>
    internal static readonly JsonDocumentOptions TolerantJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // ———————————————————— 位置 ————————————————————

    /// <summary>当前配置目录（PR_CONTROL_CONFIG 或系统默认）。程序草稿（drafts.json）等也放这里。</summary>
    public static string Directory => Locate(Environment.GetEnvironmentVariable("PR_CONTROL_CONFIG")).Dir;

    /// <summary>
    /// 配置位置：PR_CONTROL_CONFIG 是目录（同 v1）；以 .json 结尾且不是已经存在的目录时当作 devices.json 文件本身（目录 = 它所在的目录）。
    /// 没设置时用系统默认目录。
    /// </summary>
    public static (string Dir, string File) Locate(string? env)
    {
        if (!string.IsNullOrWhiteSpace(env))
        {
            var p = env.Trim();
            if (p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !System.IO.Directory.Exists(p))
                return (Path.GetDirectoryName(Path.GetFullPath(p)) ?? ".", Path.GetFullPath(p));
            return (p, Path.Combine(p, FileName));
        }
        string dir = RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "ParallelReactor.Control")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParallelReactor.Control");
        return (dir, Path.Combine(dir, FileName));
    }

    /// <summary>截图 / 演示：PR_CONTROL_SHOT 或 PR_CONTROL_DEMO 有值时不写任何配置文件。</summary>
    public static bool WritesDisabledByEnvironment =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT")) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_DEMO"));

    // ———————————————————— 加载 ————————————————————

    /// <summary>按环境变量加载（程序启动用）。PR_CONTROL_CLIENT 覆盖客户端名（截图 / 验收）。</summary>
    public static ControlConfig Load()
    {
        var (dir, file) = Locate(Environment.GetEnvironmentVariable("PR_CONTROL_CONFIG"));
        var cfg = Load(dir, file, !WritesDisabledByEnvironment, TokenProtector.CurrentMode);
        var name = NormalizeClientName(Environment.GetEnvironmentVariable("PR_CONTROL_CLIENT"));
        if (name.Length > 0) cfg.ClientName = name;
        return cfg;
    }

    /// <summary>
    /// 从指定位置加载（测试直接用）：有 devices.json 读它；没有时迁移同目录的 control.json；都没有时是空列表。
    /// 迁移之后马上保存一次（<paramref name="canWrite"/> 为 false 时不写）。
    /// </summary>
    public static ControlConfig Load(string dir, string file, bool canWrite, TokenStoreMode mode)
    {
        var cfg = new ControlConfig(dir, file, canWrite, mode);
        if (File.Exists(file)) cfg.ReadV2(file);
        else
        {
            var legacy = Path.Combine(dir, LegacyFileName);
            if (File.Exists(legacy) && cfg.MigrateV1(legacy) is { } hadToken)
            {
                if (cfg.Save() && hadToken) ScrubLegacyToken(legacy);
            }
            else
            {
                // 第一次运行（两个文件都没有）：本机客户端名取「PC-计算机名」并立即写进 devices.json —— 每台电脑默认就不一样
                // （触屏弹窗、审计记录、R8 的持有者比较都靠它区分；M3 复核：以前每台都是「PC-测试工位1」）
                cfg.ClientName = MachineClientName();
                cfg.Save();
            }
        }
        return cfg;
    }

    /// <summary>
    /// 这台电脑的默认客户端名：「PC-」+ 计算机名（Environment.MachineName），按 HMI 的规则清洗（Contracts 的 ClientNames.Sanitize：去掉控制 / 格式字符、折叠空白、
    /// 最多 64 个字符）；取不到计算机名时用 <see cref="DefaultClientName"/>。
    /// </summary>
    public static string MachineClientName()
    {
        string m;
        try { m = (Environment.MachineName ?? "").Trim(); } catch { m = ""; }
        var name = ParallelReactor.Api.ClientNames.Sanitize("PC-" + m);
        return m.Length == 0 || name.Length <= 3 ? DefaultClientName : name;
    }

    /// <summary>
    /// 客户端名检查（本机客户端名对话框）：'' = 可用；「客户端名不能为空」/「客户端名太长 · 最多 64 个字符」（HMI 的 ClientNames.Max）。
    /// 样稿没有这两句（客户端名在第二阶段的设置页里改）；写法同别名检查「名称不能为空」「名称太长 · …」。
    /// </summary>
    public static string ClientNameError(string? name)
    {
        var t = (name ?? "").Trim();
        if (ParallelReactor.Api.ClientNames.Sanitize(t).Length == 0) return "客户端名不能为空";
        if (t.Length > ParallelReactor.Api.ClientNames.Max) return "客户端名太长 · 最多 " + ParallelReactor.Api.ClientNames.Max + " 个字符";
        return "";
    }

    /// <summary>客户端名按 HMI 的规则清洗后的样子（存盘、报给 HMI 的都是它：HMI 认的持有者名与本机比较时才一致）。</summary>
    public static string NormalizeClientName(string? name) => ParallelReactor.Api.ClientNames.Sanitize((name ?? "").Trim());

    /// <summary>只在内存里的配置（测试、演示）：不读也不写任何文件。</summary>
    public static ControlConfig InMemory(string clientName = DefaultClientName) =>
        new(Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "pr-control-inmemory-" + FileName), false, TokenStoreMode.File) { ClientName = clientName };

    private void ReadV2(string file)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(file), TolerantJson); }
        catch (Exception ex)
        {
            _corrupt = true;
            _notes.Add("devices.json 无法读取（" + ex.GetType().Name + "）· 从空的设备列表开始，下次保存前留一份 devices.json.bad");
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _corrupt = true;
                _notes.Add("devices.json 格式不对 · 从空的设备列表开始");
                return;
            }
            // 没写客户端名（或清洗后为空）：用这台电脑的默认名（下次保存时写进去）
            ClientName = NormalizeClientName(Str(root, "clientName")) is { Length: > 0 } cn ? cn : MachineClientName();
            if (Int(root, "timelineMinutes") is int tm && tm is > 0 and <= 24 * 60) TimelineMinutes = tm;
            var tokens = TokenMode == TokenStoreMode.File ? TokenProtector.ReadFile(TokenFilePath) : new Dictionary<Guid, string>();
            if (Prop(root, "devices") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var el in arr.EnumerateArray()) ReadDevice(el, tokens);
        }
    }

    private void ReadDevice(JsonElement el, Dictionary<Guid, string> tokens)
    {
        if (el.ValueKind != JsonValueKind.Object) { _notes.Add("devices.json 里有一项不是设备 · 已跳过"); return; }
        var host = (Str(el, "host") ?? "").Trim();
        if (host.Length == 0) { _notes.Add("devices.json 里有一项没有地址 · 已跳过"); return; }
        var id = Guid.TryParse(Str(el, "id"), out var g) && g != Guid.Empty && Devices.All(d => d.Id != g) ? g : Guid.NewGuid();
        var alias = ActionGuard.JsTrim(Str(el, "alias") ?? "");
        if (ActionGuard.AliasError(alias, Devices.Select(d => new PeerDevice(d.Alias, d.Host))).Length > 0)
        {
            var fixedAlias = ActionGuard.FreeAlias(Devices.Select(d => d.Alias));
            if (alias.Length > 0) _notes.Add("设备别名「" + alias + "」不可用 · 改成「" + fixedAlias + "」");
            alias = fixedAlias;
        }
        int port = Int(el, "port") is int p && p is > 0 and <= 65535 ? p : DefaultPort;
        bool remember = Bool(el, "rememberToken") ?? false;
        string? token = null;
        if (remember)
        {
            token = TokenMode == TokenStoreMode.Dpapi
                ? (OperatingSystem.IsWindows() ? TokenProtector.Unprotect(Str(el, "token")) : null)
                : tokens.TryGetValue(id, out var t) ? t : null;
            if (token == null) _notes.Add(alias + " 记住的令牌读不出来 · 连接前请在「编辑设置」里重新输入");
        }
        Devices.Add(new DeviceEntry
        {
            Id = id, Alias = alias, Host = host, Port = port, Token = token, RememberToken = remember,
            // D7：「启动时自动连接」要求已经存了令牌；读出来的 autoConnect 在没有令牌时不生效（设置保留，令牌补上之后照常）
            AutoConnect = Bool(el, "autoConnect") ?? false,
            LastSerial = Str(el, "lastSerial"), LastModel = Str(el, "lastModel"),
        });
    }

    /// <summary>v1（control.json，一台设备）→ v2 的「1号机」。返回 v1 里是否存了明文令牌（迁移成功后要抹掉）；读不了返回 null。</summary>
    private bool? MigrateV1(string legacy)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy), TolerantJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // v1 的客户端名：用户改过的照搬；没写、或还是 v1 的默认名「PC-测试工位1」（每台电脑都一样）→ 这台电脑的默认名，并提示一次（M3 复核）
            var v1Name = NormalizeClientName(Str(root, "clientName"));
            if (v1Name.Length > 0 && v1Name != DefaultClientName) ClientName = v1Name;
            else
            {
                ClientName = MachineClientName();
                if (ClientName != DefaultClientName) _notes.Add("本机客户端名改为「" + ClientName + "」· 可在「设备 › 本机客户端名…」中修改");
            }
            if (Int(root, "timelineMinutes") is int tm && tm is > 0 and <= 24 * 60) TimelineMinutes = tm;
            var host = (Str(root, "host") ?? "").Trim();
            var token = Str(root, "token") is { Length: > 0 } t ? t.Trim() : null;
            bool remember = (Bool(root, "rememberToken") ?? false) && token != null;
            if (host.Length > 0)
            {
                Devices.Add(new DeviceEntry
                {
                    Alias = "1号机", Host = host,
                    Port = Int(root, "port") is int p && p is > 0 and <= 65535 ? p : DefaultPort,
                    Token = remember ? token : null, RememberToken = remember,
                    AutoConnect = (Bool(root, "autoConnect") ?? false) && remember,
                });
            }
            Migrated = true;
            _notes.Add("已把 control.json 迁移为 devices.json" + (host.Length > 0 ? "（1号机 " + host + "）" : ""));
            return token != null;
        }
        catch (Exception ex)
        {
            _notes.Add("control.json 无法读取（" + ex.GetType().Name + "）· 没有迁移");
            return null;
        }
    }

    /// <summary>迁移成功后把 v1 文件里的明文令牌抹掉（令牌已改由 TokenProtector 保存；其余字段原样留着）。</summary>
    private static void ScrubLegacyToken(string legacy)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(legacy), null, TolerantJson) as System.Text.Json.Nodes.JsonObject;
            if (node == null) return;
            foreach (var key in node.Select(kv => kv.Key).Where(k => k.Equals("token", StringComparison.OrdinalIgnoreCase)).ToList()) node[key] = null;
            WriteAtomic(legacy, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ownerOnly: true);
        }
        catch { }
    }

    // ———————————————————— 保存 ————————————————————

    /// <summary>
    /// 保存 devices.json（和 macOS / Linux 的 tokens.json）。不能写（截图、演示、内存配置）时什么都不做、返回 false；
    /// 写失败（只读目录…）也返回 false，不抛。
    /// </summary>
    public bool Save()
    {
        if (!CanWrite) return false;
        try
        {
            if (_corrupt && File.Exists(FilePath))
            {
                File.Copy(FilePath, FilePath + ".bad", overwrite: true);
                _corrupt = false;
            }
            WriteAtomic(FilePath, ToJson(), ownerOnly: true);
            if (TokenMode == TokenStoreMode.File)
                TokenProtector.WriteFile(TokenFilePath, Devices.Where(d => d.RememberToken && d.HasToken).ToDictionary(d => d.Id, d => d.Token!));
            return true;
        }
        catch (Exception ex)
        {
            var msg = "配置保存失败：" + ex.Message;
            _notes.Add(msg);
            SaveFailed?.Invoke(msg);
            return false;
        }
    }

    /// <summary>
    /// 运行中保存失败（只读目录、磁盘满…）：参数是要给用户看的一句「配置保存失败：…」。FleetVm 把它 toast 出来 —— 不然添加 / 编辑 / 改名 / 移除看起来成功了、
    /// 下次启动却没了（M3 复核；加载时的失败照旧在 <see cref="LoadNotes"/> 里）。不能写（截图、演示、内存配置）不算失败、不触发。
    /// </summary>
    public event Action<string>? SaveFailed;

    /// <summary>devices.json 的内容（令牌：DPAPI 时是密文，文件方式时不在这里）。</summary>
    public string ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteString("clientName", ClientName);
            w.WriteNumber("timelineMinutes", TimelineMinutes);
            w.WriteString("tokenStore", TokenMode == TokenStoreMode.Dpapi ? "dpapi" : "file");
            w.WriteStartArray("devices");
            foreach (var d in Devices)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id.ToString("D"));
                w.WriteString("alias", d.Alias);
                w.WriteString("host", d.Host);
                w.WriteNumber("port", d.Port);
                w.WriteBoolean("rememberToken", d.RememberToken);
                w.WriteBoolean("autoConnect", d.AutoConnect && d.RememberToken);
                if (TokenMode == TokenStoreMode.Dpapi && d.RememberToken && d.HasToken && OperatingSystem.IsWindows())
                    w.WriteString("token", TokenProtector.Protect(d.Token!));
                if (d.LastSerial != null) w.WriteString("lastSerial", d.LastSerial);
                if (d.LastModel != null) w.WriteString("lastModel", d.LastModel);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    /// <summary>
    /// 原子写：同目录的临时文件写完、刷盘，再改名替换目标（改名在同一个卷上是原子的）。<paramref name="ownerOnly"/>：macOS / Linux 上
    /// 临时文件一开始就以 0600 新建（改名后目标也是 0600）。失败时删掉临时文件再抛出。
    /// </summary>
    public static void WriteAtomic(string path, string content, bool ownerOnly)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        System.IO.Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (ownerOnly && !OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var fs = new FileStream(tmp, opts))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
            if (ownerOnly && !OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    // ———————————————————— 容错读取的小工具（字段名不分大小写，类型不对返回 null） ————————————————————

    private static JsonElement? Prop(JsonElement o, string name)
    {
        foreach (var p in o.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement o, string name) => Prop(o, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        }
        : null;

    private static int? Int(JsonElement o, string name) => Prop(o, name) is { } v
        ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
          : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var j) ? j : null
        : null;

    private static bool? Bool(JsonElement o, string name) => Prop(o, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => null,
        }
        : null;
}
