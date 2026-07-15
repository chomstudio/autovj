using AutoVJ.Models;
using AutoVJ.Services;

var configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.yaml");
var config = ConfigService.Load(configPath);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://{config.Server.Host}:{config.Server.Port}");
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<RuntimeState>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<FingerprintService>();
builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddSingleton<MediaCatalogService>();
builder.Services.AddSingleton<AudioCaptureService>();
builder.Services.AddSingleton<SelfTestService>();
builder.Services.AddHostedService<DetectionWorker>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var database = app.Services.GetRequiredService<DatabaseService>();
var catalog = app.Services.GetRequiredService<MediaCatalogService>();
await database.InitializeAsync();
if ((await catalog.GetSummariesAsync()).Count == 0)
{
    await catalog.RebuildAsync();
}

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    var selfTest = app.Services.GetRequiredService<SelfTestService>();
    Environment.ExitCode = await selfTest.RunAsync() ? 0 : 1;
    return;
}

// 現在の検出状態をブラウザへ返します。
app.MapGet("/api/status", (RuntimeState state) => Results.Ok(state.GetSnapshot()));

// DBに登録されている音源と動画の対応一覧を返します。
app.MapGet("/api/tracks", async (MediaCatalogService service) => Results.Ok(await service.GetSummariesAsync()));

// 設定した素材フォルダを再走査して指紋DBを作り直します。
app.MapPost("/api/catalog/rebuild", async (MediaCatalogService service, CancellationToken token) =>
    Results.Ok(await service.RebuildAsync(token)));

// Windowsで利用できる録音デバイス名を返します。
app.MapGet("/api/audio/devices", (AudioCaptureService capture) => Results.Ok(capture.GetDevices()));

// 設定されたLINE入力デバイスの監視を開始します。
app.MapPost("/api/capture/start", (AudioCaptureService capture) =>
{
    try
    {
        capture.Start();
        return Results.Ok(new { message = "音声入力を開始しました。" });
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

Console.WriteLine($"AutoVJ操作画面: http://{config.Server.Host}:{config.Server.Port}");
Console.WriteLine($"音声入力: {config.Audio.PreferredInput}");
await app.RunAsync();
