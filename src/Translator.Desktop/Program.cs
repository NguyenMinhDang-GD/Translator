using Avalonia;
using Translator.Core.Exporting;
using Translator.Core.Indexing;
using Translator.Core.Models;
using Translator.Engines.Unity;

namespace Translator.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0].Equals("--cli-scan", StringComparison.OrdinalIgnoreCase))
        {
            return RunHeadlessCliAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunHeadlessCliAsync(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string gamePath = args[1];
        string? extractOutDir = null;

        for (int i = 2; i + 1 < args.Length; i++)
        {
            if (args[i].Equals("--extract", StringComparison.OrdinalIgnoreCase))
            {
                extractOutDir = args[i + 1];
            }
        }

        using var unityAdapter = new UnityEngineAdapter();
        var coordinator = new TwoPhaseScanCoordinator([unityAdapter]);

        var detected = await coordinator.DetectGameAsync(gamePath);
        if (detected is null)
        {
            Console.Error.WriteLine($"[ERROR] Không nhận diện được Game Unity tại: {gamePath}");
            return 1;
        }

        (var adapter, GameProfile profile) = detected.Value;
        Console.WriteLine($"[DETECTED] {profile.GameName} | {profile.EngineName} {profile.EngineVersion} | {profile.ScriptingBackend}");

        ScanPhase1Result phase1 = await coordinator.RunPhase1DiscoveryAsync(adapter, profile);
        Console.WriteLine($"[PHASE 1] Tìm thấy {phase1.Targets.Count} nguồn text, {phase1.EncryptedWarnings.Count} cảnh báo file mã hóa.");

        await coordinator.RunPhase2DeepIndexingAsync(adapter, profile, phase1.Targets);

        foreach (ScannedTarget t in phase1.Targets.Where(x => !x.IsLikelyNoise))
        {
            Console.WriteLine($"  + [{t.SourceKind}] {t.DisplayName} ({t.ContainerRelativePath}) -> {t.ValidEntryCount} câu hợp lệ (Score={t.RelevanceScore})");
        }

        foreach (EncryptedFileWarning w in phase1.EncryptedWarnings)
        {
            Console.WriteLine($"  ! [ENCRYPTED?] {w.RelativePath}: {w.Reason}");
        }

        if (!string.IsNullOrWhiteSpace(extractOutDir))
        {
            var exporter = new ExtractionService();
            List<ScannedTarget> toExport = phase1.Targets.Where(t => !t.IsLikelyNoise).ToList();
            ExportSummary summary = await exporter.ExportTargetsAsync(
                adapter,
                profile,
                coordinator,
                toExport,
                phase1.Targets,
                new ExportOptions
                {
                    OutputDirectory = extractOutDir,
                    ExportTranslatorJson = true,
                    ExportCsv = true,
                    IncludeRawFile = true
                });

            Console.WriteLine($"[EXTRACTED] Đã xuất {summary.ExportedTargetCount} mục ({summary.ExportedEntryCount} câu) ra: {summary.OutputDirectory}");
        }

        return 0;
    }
}
