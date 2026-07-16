using AutoVJ.Models;
using AutoVJ.Services;
using System.Diagnostics;

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.yaml");
var config = ConfigService.Load(configPath);

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
app.UseDefaultFiles();
app.UseStaticFiles();

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
    detectionLostTimeoutSeconds = config.Playback.DetectionLostTimeoutSeconds,
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

// DBに登録されている音源と動画の対応一覧を指紋BLOBなしで返します。
app.MapGet("/api/tracks", async (DatabaseService service) => Results.Ok(await service.GetTrackSummariesAsync()));

// Windowsで利用できる録音デバイス名を返します。
app.MapGet("/api/audio/devices", (AudioCaptureService capture) => Results.Ok(capture.GetDevices()));

// 選択中または要求された録音デバイスの監視を開始します。
app.MapPost("/api/capture/start", (AudioDeviceRequest request, AudioCaptureService capture) =>
{
    try
    {
        capture.Start(request.DeviceName);
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
        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            return Results.BadRequest(new { message = "デバイス名を指定してください。" });
        }
        capture.SelectDevice(request.DeviceName);
        return Results.Ok(new { message = $"入力デバイスを {request.DeviceName} に変更しました。" });
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
}

var operationUrl = $"http://{config.Server.Host}:{config.Server.Port}";
Console.WriteLine($"AutoVJ操作画面: {operationUrl}");
Console.WriteLine($"音声入力: {config.Audio.PreferredInput}");

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
