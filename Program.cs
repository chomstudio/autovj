using AutoVJ.Models;
using AutoVJ.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.yaml");
var config = ConfigService.Load(configPath);
var lanEnabled = args.Contains("--lan", StringComparer.OrdinalIgnoreCase);
var portOption = ReadOption(args, "--port", out var portSpecified);
var pinOption = ReadOption(args, "--pin", out var pinSpecified);
if (portSpecified)
{
    if (!int.TryParse(portOption, out var port) || port is < 1 or > 65535)
    {
        throw new ArgumentException("--port は1〜65535の整数で指定してください。");
    }
    config.Server.Port = port;
}
if (lanEnabled)
{
    config.Server.Host = "0.0.0.0";
}
var effectivePin = pinSpecified ? pinOption ?? string.Empty : config.Server.LanPin;
if (lanEnabled && !string.IsNullOrEmpty(effectivePin)
    && (effectivePin.Length != 4 || effectivePin.Any(character => !char.IsAsciiDigit(character))))
{
    throw new ArgumentException("LAN公開用PINは空欄または4桁の数字で指定してください。");
}
var pinAuthenticationRequired = lanEnabled && !string.IsNullOrEmpty(effectivePin);
const string sessionCookieName = "AutoVJ.Session";
var sessionToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Server.Host}:{config.Server.Port}");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<FingerprintService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<AudioCaptureService>();
builder.Services.AddSingleton<SelfTestService>();
builder.Services.AddSingleton<TempoBenchmarkService>();
builder.Services.AddHostedService<DetectionWorker>();

var app = builder.Build();

// LAN公開時のPIN認証が有効なら、ウェルカム画面と認証API以外を保護します。
app.Use(async (context, next) =>
{
    if (!pinAuthenticationRequired || IsPublicPath(context.Request.Path))
    {
        await next();
        return;
    }

    var authenticated = context.Request.Cookies.TryGetValue(sessionCookieName, out var cookieToken)
        && FixedTimeEquals(cookieToken, sessionToken);
    if (authenticated)
    {
        await next();
        return;
    }

    if (HttpMethods.IsGet(context.Request.Method)
        && (context.Request.Path.Equals("/output", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/output.html", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/setting", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.Equals("/setting.html", StringComparison.OrdinalIgnoreCase)))
    {
        context.Response.Redirect($"/?returnUrl={Uri.EscapeDataString(context.Request.Path)}");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { message = "PIN認証が必要です。" });
});

app.UseDefaultFiles();
app.UseStaticFiles();

// ウェルカム画面へPIN認証の要否と現在状態を返します。
app.MapGet("/api/auth/status", (HttpContext context) => Results.Ok(new
{
    required = pinAuthenticationRequired,
    authenticated = !pinAuthenticationRequired
        || (context.Request.Cookies.TryGetValue(sessionCookieName, out var cookieToken)
            && FixedTimeEquals(cookieToken, sessionToken))
}));

// 正しいPINを入力したブラウザへ、アプリ終了まで有効なセッションCookieを発行します。
app.MapPost("/api/auth/login", async (PinRequest request, HttpContext context) =>
{
    if (!pinAuthenticationRequired)
    {
        return Results.Ok(new { message = "PIN認証は無効です。" });
    }
    if (!FixedTimeEquals(request.Pin ?? string.Empty, effectivePin))
    {
        await Task.Delay(350);
        return Results.Json(new { message = "PINが正しくありません。" }, statusCode: StatusCodes.Status401Unauthorized);
    }
    context.Response.Cookies.Append(sessionCookieName, sessionToken, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        IsEssential = true,
        Path = "/"
    });
    return Results.Ok(new { message = "認証しました。" });
});

// 操作用ウェルカム画面とは別に、拡張子なしの再生・設定URLを提供します。
app.MapGet("/output", () => Results.File(Path.Combine(app.Environment.WebRootPath, "output.html"), "text/html; charset=utf-8"));
app.MapGet("/setting", () => Results.File(Path.Combine(app.Environment.WebRootPath, "setting.html"), "text/html; charset=utf-8"));

var database = app.Services.GetRequiredService<DatabaseService>();
await database.InitializeAsync();
if ((await database.GetTrackSummariesAsync()).Count == 0)
{
    app.Logger.LogWarning("登録動画がありません。別プロセスで AutoVJ.Catalog の scan を実行してください。");
}

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    var selfTest = app.Services.GetRequiredService<SelfTestService>();
    Environment.ExitCode = await selfTest.RunAsync() ? 0 : 1;
    return;
}

if (args.Contains("--benchmark-detection", StringComparer.OrdinalIgnoreCase))
{
    var selfTest = app.Services.GetRequiredService<SelfTestService>();
    Environment.ExitCode = await selfTest.RunBenchmarkAsync() ? 0 : 1;
    return;
}

if (args.Contains("--benchmark-tempo", StringComparer.OrdinalIgnoreCase))
{
    var benchmark = app.Services.GetRequiredService<TempoBenchmarkService>();
    Environment.ExitCode = await benchmark.RunAsync() ? 0 : 1;
    return;
}

// 現在の検出状態をブラウザへ返します。
app.MapGet("/api/status", (RuntimeState state) => Results.Ok(state.GetSnapshot()));

// ブラウザ側の再生制御に必要な公開設定だけを返します。
app.MapGet("/api/client-config", () => Results.Ok(new
{
    resyncToleranceSeconds = config.Playback.ResyncToleranceSeconds,
    playbackRateTolerance = config.Detection.PlaybackRateTolerance,
    randomizeCommonStart = config.Playback.RandomizeCommonStart,
    glitch = new
    {
        enabled = config.Glitch.Enabled,
        confidenceThreshold = config.Glitch.ConfidenceThreshold,
        files = config.Glitch.Files.Select((_, index) => $"/api/material/glitch/{index}").ToArray()
    },
    detectionLostTimeoutSeconds = config.Playback.DetectionLostTimeoutSeconds,
    minimumPlaybackRate = config.Playback.MinimumRate,
    maximumPlaybackRate = config.Playback.MaximumRate,
    testApiEnabled = Environment.GetEnvironmentVariable("AUTOVJ_ENABLE_TEST_API") == "1",
    transition = new
    {
        enabled = config.Transition.Enabled,
        durationMilliseconds = Math.Clamp(config.Transition.DurationMilliseconds, 0, 10000),
        blendModesEnabled = config.Transition.BlendModesEnabled,
        randomizeBlendMode = config.Transition.RandomizeBlendMode,
        blendModes = NormalizeBlendModes(config.Transition.BlendModes)
    }
}));

// 詳細設定画面で編集できる現在値とブレンドモード候補を返します。
app.MapGet("/api/settings", (RuntimeState state) =>
{
    var snapshot = state.GetSnapshot();
    return Results.Ok(new
{
    selectedInputSourceId = snapshot.SelectedInputSourceId,
    positionOffsetMilliseconds = snapshot.PositionOffsetMilliseconds,
    detectionLostTimeoutSeconds = config.Playback.DetectionLostTimeoutSeconds,
    resyncToleranceSeconds = config.Playback.ResyncToleranceSeconds,
    minimumPlaybackRate = config.Playback.MinimumRate,
    maximumPlaybackRate = config.Playback.MaximumRate,
    minimumBpm = config.Playback.MinimumBpm,
    maximumBpm = config.Playback.MaximumBpm,
    transitionEnabled = config.Transition.Enabled,
    transitionDurationMilliseconds = config.Transition.DurationMilliseconds,
    blendModesEnabled = config.Transition.BlendModesEnabled,
    randomizeBlendMode = config.Transition.RandomizeBlendMode,
    blendModes = config.Transition.BlendModes,
    availableBlendModes = new[] { "screen", "multiply", "overlay", "soft-light", "difference" },
    glitchConfidenceThreshold = config.Glitch.ConfidenceThreshold
    });
});

// 詳細設定画面の対象項目をconfig.yamlへ保存し、実行中設定へ即時反映します。
app.MapPost("/api/settings", (UiSettingsRequest settings, RuntimeState state) =>
{
    try
    {
        ConfigService.SaveUiSettings(configPath, config, settings);
        state.ApplySettings();
        return Results.Ok(new { message = "設定を保存して反映しました。" });
    }
    catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
    }
});

// DBに登録されている音源と動画の対応一覧を指紋BLOBなしで返します。
app.MapGet("/api/tracks", async (DatabaseService service) => Results.Ok(await service.GetTrackSummariesAsync()));

// Windowsで利用できる録音デバイス名を返します。
app.MapGet("/api/audio/devices", (AudioCaptureService capture) => Results.Ok(capture.GetDevices()));

// 選択中または要求された録音デバイスの監視を開始します。
app.MapPost("/api/capture/start", (AudioDeviceRequest request, AudioCaptureService capture) =>
{
    try
    {
        capture.Start(request.SourceId ?? request.DeviceName);
        return Results.Ok(new { message = "音声入力を開始しました。" });
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

// 入力デバイスを変更し、監視中の場合は新しいデバイスで録音を再開します。
app.MapPost("/api/capture/device", (AudioDeviceRequest request, AudioCaptureService capture) =>
{
    try
    {
        var requestedSource = request.SourceId ?? request.DeviceName;
        if (string.IsNullOrWhiteSpace(requestedSource))
        {
            return Results.BadRequest(new { message = "入力元を指定してください。" });
        }
        var source = capture.GetDevices().FirstOrDefault(candidate =>
            candidate.Id.Equals(requestedSource, StringComparison.OrdinalIgnoreCase)
            || candidate.Name.Equals(requestedSource, StringComparison.OrdinalIgnoreCase));
        if (source is null) return Results.BadRequest(new { message = "指定された入力元が見つかりません。" });
        capture.SelectDevice(source.Id);
        ConfigService.SaveAudioSource(configPath, config, source);
        return Results.Ok(new { message = $"入力元を {source.Label} に変更しました。" });
    }
    catch (Exception exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
    }
});

// 動作中の音声入力監視を停止します。
app.MapPost("/api/capture/stop", (AudioCaptureService capture) =>
{
    capture.Stop();
    return Results.Ok(new { message = "音声入力を停止しました。" });
});

// 登録済み動画をIDで特定し、Range要求対応でブラウザへ配信します。
app.MapGet("/api/media/{id:long}", async (long id, DatabaseService service) =>
{
    var track = (await service.GetTracksAsync()).FirstOrDefault(candidate => candidate.Id == id);
    return track is null || !File.Exists(track.VideoPath)
        ? Results.NotFound()
        : Results.File(track.VideoPath, "video/mp4", enableRangeProcessing: true);
});

// 未検出時に常時ループする汎用動画をRange要求対応で配信します。
app.MapGet("/api/material/common", () =>
{
    var path = Path.GetFullPath(Path.Combine(config.Media.MaterialVideoDir, config.Media.CommonVideoFile));
    return !File.Exists(path)
        ? Results.NotFound()
        : Results.File(path, "video/mp4", enableRangeProcessing: true);
});

// 設定されたグリッチ素材だけを番号で特定し、Range要求対応で配信します。
app.MapGet("/api/material/glitch/{index:int}", (int index) =>
{
    if (index < 0 || index >= config.Glitch.Files.Count) return Results.NotFound();
    var fileName = config.Glitch.Files[index];
    if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)) return Results.NotFound();
    var path = Path.GetFullPath(Path.Combine(config.Media.MaterialVideoDir, fileName));
    return !File.Exists(path)
        ? Results.NotFound()
        : Results.File(path, "video/mp4", enableRangeProcessing: true);
});

if (Environment.GetEnvironmentVariable("AUTOVJ_ENABLE_TEST_API") == "1")
{
    // 自動UI試験時だけ、任意の登録動画を検出済み状態へ切り替えます。
    app.MapPost("/api/test/match/{id:long}", async (long id, DatabaseService service, RuntimeState state) =>
    {
        var track = (await service.GetTracksAsync()).FirstOrDefault(candidate => candidate.Id == id);
        if (track is null)
        {
            return Results.NotFound();
        }
        state.SetCapture(true, "UI試験中");
        var tempoRatio = track.Bpm is null ? 1.0 : 1.10;
        state.SetMatch(new MatchResult(
            track, 0.99, 15, tempoRatio, track.Bpm, track.Bpm * tempoRatio,
            track.FingerprintMethod, track.FingerprintVersion),
            TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
        return Results.Ok();
    });

    // 自動UI試験時だけ、汎用動画へ戻る状態を作ります。
    app.MapPost("/api/test/fallback", (RuntimeState state) =>
    {
        state.SetCapture(false, "UI試験・汎用動画");
        return Results.Ok();
    });

    // 自動UI試験時だけ、現在曲を維持したまま信頼度が低下した状態を作ります。
    app.MapPost("/api/test/weak", (RuntimeState state) =>
    {
        state.SetMatch(new MatchResult(null, 0.20, 0), TimeSpan.FromSeconds(config.Playback.DetectionLostTimeoutSeconds));
        return Results.Ok();
    });
}

var browserHost = config.Server.Host == "0.0.0.0" ? "127.0.0.1" : config.Server.Host;
var operationUrl = $"http://{browserHost}:{config.Server.Port}";
Console.WriteLine($"AutoVJ操作画面: {operationUrl}");
Console.WriteLine($"公開モード: {(lanEnabled ? "LAN公開" : "ローカルのみ")} / PIN認証: {(pinAuthenticationRequired ? "有効" : "無効")}");
Console.WriteLine($"音声入力: {config.Audio.PreferredInputType} / {config.Audio.PreferredInput}");

// 通常起動では既定デバイスの監視を自動開始し、失敗してもWeb UIは起動します。
var captureService = app.Services.GetRequiredService<AudioCaptureService>();
try
{
    captureService.Start();
}
catch (Exception exception)
{
    app.Logger.LogWarning(exception, "既定の音声入力を自動開始できませんでした。");
    app.Services.GetRequiredService<RuntimeState>().SetCapture(false, $"自動開始失敗: {exception.Message}");
}

// サーバー待受開始後に、OS既定ブラウザで操作画面を一度だけ開きます。
app.Lifetime.ApplicationStarted.Register(() =>
{
    if (!config.Server.AutoOpenBrowser || Environment.GetEnvironmentVariable("AUTOVJ_NO_BROWSER") == "1")
    {
        return;
    }
    try
    {
        Process.Start(new ProcessStartInfo(operationUrl) { UseShellExecute = true });
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "操作画面をブラウザで自動表示できませんでした。");
    }
});

await app.RunAsync();

// 設定値からブラウザで安全に利用できるCSSブレンドモードだけを残します。
static string[] NormalizeBlendModes(IEnumerable<string> configuredModes)
{
    var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "multiply", "screen", "overlay", "darken", "lighten",
        "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion"
    };
    var modes = configuredModes
        .Select(mode => mode.Trim().ToLowerInvariant())
        .Where(allowed.Contains)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    return modes.Length > 0 ? modes : ["normal"];
}

// `--name value` と `--name=value` の両形式から起動オプションを読み取ります。
static string? ReadOption(string[] arguments, string optionName, out bool specified)
{
    for (var index = 0; index < arguments.Length; index++)
    {
        if (arguments[index].Equals(optionName, StringComparison.OrdinalIgnoreCase))
        {
            if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{optionName} の値を指定してください。");
            }
            specified = true;
            return arguments[index + 1];
        }
        var prefix = $"{optionName}=";
        if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            specified = true;
            return arguments[index][prefix.Length..];
        }
    }
    specified = false;
    return null;
}

// PINやセッショントークンを処理時間から推測されにくい固定時間比較で照合します。
static bool FixedTimeEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return leftBytes.Length == rightBytes.Length
        && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}

// PIN認証前にもウェルカム画面とその最低限の静的ファイルだけを公開します。
static bool IsPublicPath(PathString path)
{
    return path.Equals("/")
        || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/style.css", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/welcome.js", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/status", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase);
}
