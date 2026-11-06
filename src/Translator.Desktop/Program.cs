using System.Runtime.InteropServices;
using Avalonia;
using Translator.Core.Backup;
using Translator.Core.Exporting;
using Translator.Core.Indexing;
using Translator.Core.Models;
using Translator.Core.Patching;
using Translator.Engines.Unity;
using Translator.Engines.Unity.Patching;

namespace Translator.Desktop;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 2 &&
            (args[0].Equals("--cli-scan", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--patch", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase)))
        {
            if (OperatingSystem.IsWindows())
            {
                AttachConsole(-1);
            }

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
        string? gamePath = null;
        string? extractOutDir = null;
        string? patchSourcePath = null;
        bool restoreOriginal = false;
        SourceLanguageFilter langFilter = SourceLanguageFilter.All;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--cli-scan", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                gamePath = args[++i];
            }
            else if (arg.Equals("--game", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                gamePath = args[++i];
            }
            else if (arg.Equals("--extract", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                extractOutDir = args[++i];
            }
            else if (arg.Equals("--patch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                patchSourcePath = args[++i];
            }
            else if (arg.Equals("--restore", StringComparison.OrdinalIgnoreCase))
            {
                restoreOriginal = true;
            }
            else if (arg.Equals("--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                langFilter = ParseCliLanguage(args[++i]);
            }
        }

        if (string.IsNullOrWhiteSpace(gamePath))
        {
            Console.Error.WriteLine("[ERROR] Thiếu đường dẫn thư mục Game (--game hoặc --cli-scan).");
            return 1;
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

        var backupVault = new BackupVaultService();

        if (restoreOriginal)
        {
            RestoreSummary restoreResult = await backupVault.RestoreOriginalAsync(profile);
            foreach (string err in restoreResult.Errors)
            {
                Console.Error.WriteLine($"  ! [RESTORE ERROR] {err}");
            }

            Console.WriteLine($"[RESTORED] Đã khôi phục nguyên trạng {restoreResult.RestoredFileCount} file gốc ({restoreResult.TotalBytesRestored:N0} bytes) từ .translator_backup/.");
            return restoreResult.Errors.Count > 0 && restoreResult.RestoredFileCount == 0 ? 1 : 0;
        }

        if (!string.IsNullOrWhiteSpace(patchSourcePath))
        {
            var loader = new TranslationPackageLoader();
            IReadOnlyList<PatchPayload> payloads = Directory.Exists(patchSourcePath)
                ? await loader.LoadFromDirectoryAsync(patchSourcePath)
                : await loader.LoadFromFileAsync(patchSourcePath, fallbackProfile: profile);

            var patcher = new UnityPatcher();
            PatchSummary patchResult = await patcher.PatchAsync(profile, payloads, backupVault);

            foreach (string warn in patchResult.Warnings)
            {
                Console.WriteLine($"  ! [PATCH WARN] {warn}");
            }

            foreach (string err in patchResult.Errors)
            {
                Console.Error.WriteLine($"  ! [PATCH ERROR] {err}");
            }

            Console.WriteLine($"[PATCHED] Đã vá {patchResult.AppliedEntryCount} câu dịch vào {patchResult.PatchedContainerCount} file tài nguyên.");
            return patchResult.Errors.Count > 0 && patchResult.PatchedContainerCount == 0 ? 1 : 0;
        }

        ScanPhase1Result phase1 = await coordinator.RunPhase1DiscoveryAsync(adapter, profile);
        Console.WriteLine($"[PHASE 1] Tìm thấy {phase1.Targets.Count} nguồn text, {phase1.EncryptedWarnings.Count} cảnh báo file mã hóa.");

        await coordinator.RunPhase2DeepIndexingAsync(adapter, profile, phase1.Targets);

        foreach (ScannedTarget t in phase1.Targets.Where(x => !x.IsLikelyNoise && x.GetValidCountForFilter(langFilter) > 0))
        {
            Console.WriteLine($"  + [{t.SourceKind}] {t.DisplayName} ({t.ContainerRelativePath}) -> {t.GetValidCountForFilter(langFilter)} câu [{langFilter}] (Score={t.RelevanceScore})");
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
                    IncludeRawFile = false,
                    LanguageFilter = langFilter
                });

            Console.WriteLine($"[EXTRACTED] Đã xuất [{langFilter}] {summary.ExportedTargetCount} mục ({summary.ExportedEntryCount} câu) ra: {summary.OutputDirectory}");
        }

        return 0;
    }

    private static SourceLanguageFilter ParseCliLanguage(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "1" or "en" or "english" => SourceLanguageFilter.English,
            "2" or "cn" or "zh" or "china" or "chinese" => SourceLanguageFilter.Chinese,
            "3" or "ru" or "russian" => SourceLanguageFilter.Russian,
            "4" or "jp" or "ja" or "japan" or "japanese" => SourceLanguageFilter.Japanese,
            _ => SourceLanguageFilter.All
        };
}
