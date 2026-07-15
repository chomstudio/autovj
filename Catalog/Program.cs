using AutoVJ.Services;
using Microsoft.Extensions.Logging;

if (args.Any(argument => argument is "--help" or "-h" or "help"))
{
    PrintUsage();
    return;
}

try
{
    Environment.ExitCode = await RunAsync(args);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"解析に失敗しました: {exception.Message}");
    Environment.ExitCode = 1;
}

// 設定を読み込み、単一起動を保証して差分解析を実行します。
static async Task<int> RunAsync(IReadOnlyList<string> arguments)
{
    var command = ReadCommand(arguments);
    if (!string.Equals(command, "scan", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"未対応のコマンドです: {command}");
        PrintUsage();
        return 2;
    }

    var configPath = ResolveConfigPath(ReadOption(arguments, "--config"));
    var config = ConfigService.Load(configPath);
    var databasePath = Path.GetFullPath(config.Storage.DatabasePath);
    var databaseDirectory = Path.GetDirectoryName(databasePath)!;
    Directory.CreateDirectory(databaseDirectory);
    var lockPath = Path.Combine(databaseDirectory, "catalog.lock");

    FileStream processLock;
    try
    {
        processLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException exception)
    {
        Console.Error.WriteLine($"解析アプリはすでに実行中です: {exception.Message}");
        return 3;
    }

    using (processLock)
    using (var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Information);
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
    }))
    {
        var database = new DatabaseService(config);
        await database.InitializeAsync();
        var catalog = new MediaCatalogService(
            config,
            new FfmpegService(config),
            new FingerprintService(config),
            database,
            loggerFactory.CreateLogger<MediaCatalogService>());

        Console.WriteLine($"設定: {configPath}");
        Console.WriteLine($"素材: {Path.GetFullPath(config.Media.MainVideoDir)}");
        Console.WriteLine($"DB: {databasePath}");
        var result = await catalog.ScanAsync();
        Console.WriteLine(
            $"解析完了: 対象={result.Scanned} / 追加={result.Added} / 更新={result.Updated} / " +
            $"変更なし={result.Unchanged} / 削除={result.Removed} / 失敗={result.Failed}");
        return result.Failed == 0 ? 0 : 1;
    }
}

// オプション値を除外して実行コマンドを読み取り、省略時はscanを選びます。
static string ReadCommand(IReadOnlyList<string> arguments)
{
    for (var index = 0; index < arguments.Count; index++)
    {
        if (string.Equals(arguments[index], "--config", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            continue;
        }
        if (!arguments[index].StartsWith('-'))
        {
            return arguments[index];
        }
    }
    return "scan";
}

// コマンドラインから指定された設定ファイルの値を取得します。
static string? ReadOption(IReadOnlyList<string> arguments, string optionName)
{
    for (var index = 0; index < arguments.Count - 1; index++)
    {
        if (string.Equals(arguments[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return arguments[index + 1];
        }
    }
    return null;
}

// 明示指定、現在フォルダ、実行ファイル配置先の順で設定ファイルを探します。
static string ResolveConfigPath(string? configuredPath)
{
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(configuredPath);
    }

    var currentPath = Path.GetFullPath("config.yaml");
    if (File.Exists(currentPath))
    {
        return currentPath;
    }
    return Path.Combine(AppContext.BaseDirectory, "config.yaml");
}

// 解析アプリで利用できるコマンドと引数を表示します。
static void PrintUsage()
{
    Console.WriteLine("AutoVJ.Catalog - 動画音声指紋の差分解析");
    Console.WriteLine();
    Console.WriteLine("使用方法:");
    Console.WriteLine("  AutoVJ.Catalog.exe scan [--config <config.yaml>]");
    Console.WriteLine();
    Console.WriteLine("引数なしで起動した場合も scan を実行します。");
}
