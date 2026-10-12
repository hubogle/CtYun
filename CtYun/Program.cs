using CtYun;
using CtYun.Models;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using var globalCts = new CancellationTokenSource();

Utility.WriteLine(ConsoleColor.Green, $"版本：v {Assembly.GetEntryAssembly()?.GetName().Version}");

var runtimeConfig = LoadRuntimeConfig();
if (runtimeConfig.Accounts.Count == 0)
{
    Utility.WriteLine(ConsoleColor.Red, "未读取到账号配置。请配置 accounts.json，或设置 APP_USER/APP_PASSWORD，或使用交互输入模式。");
    return;
}

Console.CancelKeyPress += (s, e) =>
{
    e.Cancel = true;
    globalCts.Cancel();
};

Utility.WriteLine(ConsoleColor.Green, $"保活周期：{runtimeConfig.KeepAliveSeconds} 秒，随机抖动：±{runtimeConfig.KeepAliveJitterPercent}%，睡眠时段：{(runtimeConfig.SleepWindows.Count == 0 ? "未设置" : string.Join(",", runtimeConfig.SleepWindows) + $"（恢复偏移 0~{runtimeConfig.SleepJitterMinutes} 分钟）")}，时区：{TimeZoneInfo.Local.Id}");

var sessionTasks = runtimeConfig.Accounts.Select(account => RunAccountAsync(account, runtimeConfig, globalCts.Token));

try
{
    await Task.WhenAll(sessionTasks);
}
catch (OperationCanceledException)
{
    Utility.WriteLine(ConsoleColor.Yellow, "程序已停止。");
}

async Task RunAccountAsync(AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    var label = AccountLabel(account);
    var api = new CtYunApi(account.DeviceCode);

    Utility.WriteLine(ConsoleColor.Cyan, $"[{label}] 开始登录。");
    if (!await PerformLoginSequence(api, account, runtimeConfig, ct))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 登录失败，跳过该账号。");
        return;
    }

    var desktopList = await api.GetLlientListAsync();
    if (desktopList == null || desktopList.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 未获取到云电脑。");
        return;
    }

    var activeDesktops = new List<Desktop>();
    foreach (var desktop in desktopList)
    {
        if (desktop.UseStatusText != "运行中")
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] [{desktop.UseStatusText}] 电脑未开机，正在开机，请在2分钟后重新运行软件。");
        }

        var connectResult = await api.ConnectAsync(desktop.DesktopId);
        if (connectResult.Success && connectResult.Data?.DesktopInfo != null)
        {
            desktop.DesktopInfo = connectResult.Data.DesktopInfo;
            activeDesktops.Add(desktop);
        }
        else
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] Connect Error: [{desktop.DesktopId}] {connectResult.Msg}");
        }
    }

    if (activeDesktops.Count == 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 没有可保活的云电脑。");
        return;
    }

    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 保活任务启动：每 {runtimeConfig.KeepAliveSeconds} 秒强制重连一次。");
    var keepAliveTasks = activeDesktops.Select(d => KeepAliveWorkerWithForcedReset(api, account, d, runtimeConfig, ct));
    await Task.WhenAll(keepAliveTasks);
}

async Task<bool> PerformLoginSequence(CtYunApi api, AccountConfig account, RuntimeConfig runtimeConfig, CancellationToken ct)
{
    if (!await api.LoginAsync(account.User, account.Password))
    {
        return false;
    }

    if (api.LoginInfo.BondedDevice)
    {
        return true;
    }

    var label = AccountLabel(account);
    Utility.WriteLine(ConsoleColor.Yellow, $"[{label}] 当前设备未绑定，正在发送短信验证码。");
    if (!await api.GetSmsCodeAsync(account.User))
    {
        return false;
    }

    var verificationCode = ReadVerificationCode(account);
    if (string.IsNullOrWhiteSpace(verificationCode))
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 未获取到短信验证码。");
        return false;
    }

    return await api.BindingDeviceAsync(verificationCode.Trim());
}

string ReadVerificationCode(AccountConfig account)
{
    var label = AccountLabel(account);
    if (!CanReadFromConsole())
    {
        Utility.WriteLine(ConsoleColor.Red, $"[{label}] 当前账号需要短信验证码，请使用 -it 交互模式重新运行并输入验证码。");
        return "";
    }

    Console.Write($"[{label}] 短信验证码: ");
    return Console.ReadLine();
}

async Task KeepAliveWorkerWithForcedReset(CtYunApi api, AccountConfig account, Desktop desktop, RuntimeConfig runtimeConfig, CancellationToken globalToken)
{
    var label = AccountLabel(account);
    var initialPayload = Convert.FromBase64String("UkVEUQIAAAACAAAAGgAAAAAAAAABAAEAAAABAAAAEgAAAAkAAAAECAAA");

    while (!globalToken.IsCancellationRequested)
    {
        if (GetSleepRemaining(runtimeConfig.SleepWindows, DateTime.Now) is { } sleepRemaining)
        {
            // 恢复时间加随机偏移，避免每天在同一分钟准时恢复
            if (runtimeConfig.SleepJitterMinutes > 0)
            {
                sleepRemaining += TimeSpan.FromSeconds(Random.Shared.Next(0, runtimeConfig.SleepJitterMinutes * 60 + 1));
            }
            Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 进入睡眠时段，暂停保活 {FormatDuration(sleepRemaining)}，预计 {DateTime.Now.Add(sleepRemaining):MM-dd HH:mm} 恢复。");
            await Task.Delay(sleepRemaining, globalToken);
            Utility.WriteLine(ConsoleColor.Cyan, $"[{label}][{desktop.DesktopCode}] 睡眠结束，刷新连接信息。");
            if (!await RefreshDesktopInfoAsync(api, account, desktop))
            {
                await Task.Delay(TimeSpan.FromSeconds(60), globalToken);
                continue;
            }
        }

        var keepAliveSeconds = ApplyJitter(runtimeConfig.KeepAliveSeconds, runtimeConfig.KeepAliveJitterPercent);
        var uri = new Uri($"wss://{desktop.DesktopInfo.ClinkLvsOutHost}/clinkProxy/{desktop.DesktopId}/MAIN");

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
        sessionCts.CancelAfter(TimeSpan.FromSeconds(keepAliveSeconds));

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Origin", "https://pc.ctyun.cn");
        client.Options.SetRequestHeader("User-Agent", CtYunApi.UserAgent);
        client.Options.AddSubProtocol("binary");

        try
        {
            Utility.WriteLine(ConsoleColor.Cyan, $"[{label}][{desktop.DesktopCode}] === 新周期开始，尝试连接 ===");
            await client.ConnectAsync(uri, sessionCts.Token);

            var hostParts = desktop.DesktopInfo.ClinkLvsOutHost.Split(':', 2);
            var connectMessage = new ConnecMessage
            {
                type = 1,
                ssl = 1,
                host = hostParts[0],
                port = hostParts.Length > 1 ? hostParts[1] : "443",
                ca = desktop.DesktopInfo.CaCert,
                cert = desktop.DesktopInfo.ClientCert,
                key = desktop.DesktopInfo.ClientKey,
                servername = desktop.DesktopInfo.Host + ":" + desktop.DesktopInfo.Port,
                oqs = 0
            };

            var msgBytes = JsonSerializer.SerializeToUtf8Bytes(connectMessage, AppJsonSerializerContext.Default.ConnecMessage);
            await client.SendAsync(msgBytes, WebSocketMessageType.Text, true, sessionCts.Token);

            await Task.Delay(500, sessionCts.Token);
            await client.SendAsync(initialPayload, WebSocketMessageType.Binary, true, sessionCts.Token);

            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] 连接已就绪，保持 {keepAliveSeconds} 秒...");

            try
            {
                await ReceiveLoop(api, client, account, desktop, sessionCts.Token);
            }
            catch (OperationCanceledException)
            {
                Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 周期时间到，准备重连...");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 异常: {ex.Message}");
            await Task.Delay(5000, globalToken);
        }
        finally
        {
            if (client.State == WebSocketState.Open)
            {
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Timeout Reset", CancellationToken.None);
            }
        }
    }
}

// 睡眠结束后刷新云电脑连接信息；如果会话已失效则尝试重新登录一次。
async Task<bool> RefreshDesktopInfoAsync(CtYunApi api, AccountConfig account, Desktop desktop)
{
    var label = AccountLabel(account);
    var result = await api.ConnectAsync(desktop.DesktopId);
    if (!result.Success || result.Data?.DesktopInfo == null)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"[{label}][{desktop.DesktopCode}] 刷新连接失败（{result.Msg}），尝试重新登录。");
        if (!await api.LoginAsync(account.User, account.Password))
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}] 重新登录失败，60 秒后重试。");
            return false;
        }
        result = await api.ConnectAsync(desktop.DesktopId);
        if (!result.Success || result.Data?.DesktopInfo == null)
        {
            Utility.WriteLine(ConsoleColor.Red, $"[{label}][{desktop.DesktopCode}] 重新登录后刷新连接仍失败：{result.Msg}，60 秒后重试。");
            return false;
        }
    }
    desktop.DesktopInfo = result.Data.DesktopInfo;
    return true;
}

async Task ReceiveLoop(CtYunApi api, ClientWebSocket ws, AccountConfig account, Desktop desktop, CancellationToken ct)
{
    var buffer = new byte[8192];
    var encryptor = new Encryption();
    var label = AccountLabel(account);

    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        if (result.MessageType == WebSocketMessageType.Close) break;

        if (result.Count == 0)
        {
            continue;
        }

        var data = buffer.AsSpan(0, result.Count).ToArray();
        var hex = BitConverter.ToString(data).Replace("-", "");
        if (hex.StartsWith("52454451", StringComparison.OrdinalIgnoreCase))
        {
            Utility.WriteLine(ConsoleColor.Green, $"[{label}][{desktop.DesktopCode}] -> 收到保活校验");
            var response = encryptor.Execute(data);
            await ws.SendAsync(response, WebSocketMessageType.Binary, true, ct);
            Utility.WriteLine(ConsoleColor.DarkGreen, $"[{label}][{desktop.DesktopCode}] -> 发送保活响应成功");
            continue;
        }

        try
        {
            var infos = SendInfo.FromBuffer(data);
            foreach (var info in infos)
            {
                if (info.Type == 103)
                {
                    var payload = Encoding.UTF8.GetBytes("{\"type\":1,\"userName\":\"" + api.LoginInfo.UserName + "\",\"userInfo\":\"\",\"userId\":" + api.LoginInfo.UserId + "}");
                    var byUserName = new SendInfo { Type = 118, Data = payload }.ToBuffer(true);
                    await ws.SendAsync(byUserName, WebSocketMessageType.Binary, true, ct);
                }
            }
        }
        catch (Exception ex)
        {
            Utility.WriteLine(ConsoleColor.DarkYellow, $"[{label}][{desktop.DesktopCode}] 消息解析失败: {ex.Message}");
        }
    }
}

RuntimeConfig LoadRuntimeConfig()
{
    var dataDir = GetDataDir();
    Directory.CreateDirectory(dataDir);

    var config = LoadAccountsFromFile(dataDir) ?? LoadAccountsFromEnvironment();
    if (config == null || config.Accounts.Count == 0)
    {
        config = LoadAccountsFromConsole(dataDir);
    }

    foreach (var account in config.Accounts)
    {
        account.Name = FirstNotEmpty(account.Name, account.User);
        account.DeviceCode = ResolveDeviceCode(account, dataDir);
    }

    // 环境变量优先级高于 accounts.json
    var keepAliveSeconds = Math.Max(10, ReadIntEnv("KEEP_ALIVE_SECONDS") ?? config.KeepAliveSeconds);
    var jitterPercent = Math.Clamp(ReadIntEnv("KEEP_ALIVE_JITTER") ?? config.KeepAliveJitterPercent, 0, 90);
    var sleepWindows = ParseSleepWindows(FirstNotEmpty(Environment.GetEnvironmentVariable("SLEEP_WINDOW"), config.SleepWindow));
    var sleepJitterMinutes = Math.Clamp(ReadIntEnv("SLEEP_JITTER_MINUTES") ?? config.SleepJitterMinutes, 0, 180);

    return new RuntimeConfig(config.Accounts, keepAliveSeconds, jitterPercent, sleepWindows, sleepJitterMinutes, dataDir);
}

static int? ReadIntEnv(string name)
{
    var raw = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(raw))
    {
        return null;
    }
    if (int.TryParse(raw.Trim(), out var value))
    {
        return value;
    }
    Utility.WriteLine(ConsoleColor.Yellow, $"环境变量 {name} 不是合法整数，已忽略：{raw}");
    return null;
}

// 解析 "HH:mm-HH:mm[,HH:mm-HH:mm...]"，支持跨午夜，例如 "23:30-06:45"
static List<SleepWindow> ParseSleepWindows(string raw)
{
    var windows = new List<SleepWindow>();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return windows;
    }

    foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var range = part.Split('-', 2, StringSplitOptions.TrimEntries);
        if (range.Length != 2
            || !TimeOnly.TryParseExact(range[0], ["HH:mm", "H:mm"], out var start)
            || !TimeOnly.TryParseExact(range[1], ["HH:mm", "H:mm"], out var end))
        {
            Utility.WriteLine(ConsoleColor.Yellow, $"睡眠时段格式错误，已忽略：{part}（应为 HH:mm-HH:mm）");
            continue;
        }
        if (start == end)
        {
            Utility.WriteLine(ConsoleColor.Yellow, $"睡眠时段起止相同，已忽略：{part}");
            continue;
        }
        windows.Add(new SleepWindow(start, end));
    }
    return windows;
}

// 当前处于睡眠时段时返回距离该时段结束的时长，否则返回 null
static TimeSpan? GetSleepRemaining(List<SleepWindow> windows, DateTime now)
{
    var current = TimeOnly.FromDateTime(now);
    foreach (var window in windows)
    {
        if (!window.Contains(current))
        {
            continue;
        }
        var end = now.Date.Add(window.End.ToTimeSpan());
        if (end <= now)
        {
            end = end.AddDays(1);
        }
        // 多等 1 秒，确保醒来时已经离开该时段
        return end - now + TimeSpan.FromSeconds(1);
    }
    return null;
}

static int ApplyJitter(int baseSeconds, int jitterPercent)
{
    if (jitterPercent <= 0)
    {
        return baseSeconds;
    }
    var delta = baseSeconds * jitterPercent / 100;
    return Math.Max(10, baseSeconds + Random.Shared.Next(-delta, delta + 1));
}

static string FormatDuration(TimeSpan span) => span.TotalHours >= 1
    ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分钟"
    : $"{(int)Math.Ceiling(span.TotalMinutes)} 分钟";

AppConfig LoadAccountsFromEnvironment()
{
    var user = Environment.GetEnvironmentVariable("APP_USER");
    var password = Environment.GetEnvironmentVariable("APP_PASSWORD");
    if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    return new AppConfig
    {
        Accounts =
        [
            new AccountConfig
            {
                Name = Environment.GetEnvironmentVariable("APP_NAME"),
                User = user,
                Password = password,
                DeviceCode = Environment.GetEnvironmentVariable("DEVICECODE")
            }
        ]
    };
}

AppConfig LoadAccountsFromFile(string dataDir)
{
    var configPath = Environment.GetEnvironmentVariable("CTYUN_CONFIG");
    if (string.IsNullOrWhiteSpace(configPath))
    {
        configPath = Path.Combine(dataDir, "accounts.json");
    }

    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppConfig);
        Utility.WriteLine(ConsoleColor.Green, $"已读取配置文件：{configPath}");
        return config;
    }
    catch (Exception ex)
    {
        Utility.WriteLine(ConsoleColor.Red, $"读取配置文件失败：{ex.Message}");
        return null;
    }
}

AppConfig LoadAccountsFromConsole(string dataDir)
{
    if (!CanReadFromConsole())
    {
        return new AppConfig();
    }

    var accounts = new List<AccountConfig>();
    while (true)
    {
        Console.Write("账号: ");
        var user = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(user))
        {
            break;
        }

        Console.Write("密码: ");
        var password = ReadPassword();
        accounts.Add(new AccountConfig { Name = user, User = user, Password = password });

        Console.Write("继续添加账号? (y/N): ");
        var answer = Console.ReadLine();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }
    }

    if (accounts.Count > 0)
    {
        Utility.WriteLine(ConsoleColor.Yellow, $"交互输入模式已读取 {accounts.Count} 个账号。设备码会保存到 {Path.Combine(dataDir, "devices")}。");
    }

    return new AppConfig { Accounts = accounts };
}

string ResolveDeviceCode(AccountConfig account, string dataDir)
{
    if (!string.IsNullOrWhiteSpace(account.DeviceCode))
    {
        return account.DeviceCode.Trim();
    }

    var devicesDir = Path.Combine(dataDir, "devices");
    Directory.CreateDirectory(devicesDir);
    var deviceCodePath = Path.Combine(devicesDir, SafeName(account.Name ?? account.User) + ".txt");
    if (!File.Exists(deviceCodePath))
    {
        File.WriteAllText(deviceCodePath, "web_" + GenerateRandomString(32));
    }

    return File.ReadAllText(deviceCodePath).Trim();
}

string GetDataDir()
{
    var dataDir = Environment.GetEnvironmentVariable("CTYUN_DATA_DIR");
    if (!string.IsNullOrWhiteSpace(dataDir))
    {
        return dataDir;
    }

    return IsRunningInContainer() ? "/app/data" : AppContext.BaseDirectory;
}

static string GenerateRandomString(int length)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    return new string(Enumerable.Repeat(chars, length).Select(s => s[RandomNumberGenerator.GetInt32(s.Length)]).ToArray());
}

static string ReadPassword()
{
    var sb = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            return sb.ToString();
        }

        if (key.Key == ConsoleKey.Backspace && sb.Length > 0)
        {
            sb.Remove(sb.Length - 1, 1);
            Console.Write("\b \b");
        }
        else if (!char.IsControl(key.KeyChar))
        {
            sb.Append(key.KeyChar);
            Console.Write("*");
        }
    }
}

static string AccountLabel(AccountConfig account) => account.Name ?? account.User;

static string SafeName(string value)
{
    var source = string.IsNullOrWhiteSpace(value) ? "default" : value;
    var builder = new StringBuilder(source.Length);
    foreach (var ch in source)
    {
        builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
    }
    return builder.ToString();
}

static string FirstNotEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

static bool CanReadFromConsole() => !Console.IsInputRedirected && !Console.IsOutputRedirected;

static bool IsRunningInContainer() => File.Exists("/.dockerenv");

record RuntimeConfig(
    List<AccountConfig> Accounts,
    int KeepAliveSeconds,
    int KeepAliveJitterPercent,
    List<SleepWindow> SleepWindows,
    int SleepJitterMinutes,
    string DataDir);

record SleepWindow(TimeOnly Start, TimeOnly End)
{
    // Start < End 为当日区间；Start > End 为跨午夜区间
    public bool Contains(TimeOnly time) => Start < End
        ? time >= Start && time < End
        : time >= Start || time < End;

    public override string ToString() => $"{Start:HH:mm}-{End:HH:mm}";
}
