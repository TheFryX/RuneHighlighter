using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Cache;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.Elements;
using System;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
using System.Diagnostics;
using System.IO;
using System.Text;
using ExileCore2;
using ImGuiNET;

namespace RuneHighlighter;

public class PoeNinjaPriceSnapshot
{
    public string League { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; }
    public bool DisplayInExaltedOrbs { get; set; }
    public double ExaltedOrbRawValue { get; set; }
    public double DivineOrbRawValue { get; set; }
    
    public Dictionary<string, double> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RawJsonByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class RuneHighlighterPlugin : BaseSettingsPlugin<RuneHighlighterSettings>
{
    
    
    
    
    private static readonly int[][] KnownPanel40RelativePaths =
    {
        new[] { 3, 2, 2, 0 },
        new[] { 3, 2, 1, 0 },
    };

    private readonly Dictionary<string, PropertyInfo> rewardProperties = new();
    private readonly HashSet<string> enabledItemNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> enabledItemLooseKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VisibleReward> visibleRewards = new();
    private readonly Dictionary<string, (VisibleReward Reward, DateTime LastSeenUtc)> stableVisibleRewardCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, DirectOptionCacheEntry> directOptionCache = new();
    private readonly List<int> directOptionScratchIds = new(128);
    private readonly HashSet<string> directOptionSeenDedup = new(StringComparer.OrdinalIgnoreCase);
    private string lastDirectOptionFilterSignature = string.Empty;
    private int lastDirectOptionCount = -1;
    private int consecutiveEmptyRewardScans;
    private const int VisibleRewardStickyMs = 350;
    private const int MaxDirectOptionsForLiveScan = 40;

    private sealed class DirectOptionCacheEntry
    {
        public int OptionId { get; init; }
        public int RecipeId { get; set; }
        public ExileCore2.Shared.RectangleF Rect { get; set; }
        public bool IsValidHighlight { get; set; }
        public bool IsUndiscovered { get; set; }
        public bool IsFiltered { get; set; }
        public bool HasBadRect { get; set; }
        public bool HasNoRecipe { get; set; }
        public VisibleReward Reward { get; set; }
        public string DedupKey { get; set; } = string.Empty;
        public DateTime LastSeenUtc { get; set; }
    }

    private static readonly HttpClient priceHttpClient = new();
    private static readonly object ReflectionCacheMiss = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), object> ReflectionMemberCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), object> ReflectionMethodCache = new();

    private sealed class SpikeProfiler
    {
        private readonly Dictionary<string, Metric> metrics = new(StringComparer.Ordinal);

        public IDisposable Track(string name) => new Scope(this, name);

        public IEnumerable<(string Name, double LastMs, double AvgMs, double MaxMs, long Count, long LastAllocatedBytes, long AvgAllocatedBytes, long MaxAllocatedBytes)> Snapshot()
        {
            foreach (var pair in metrics.OrderByDescending(x => x.Value.LastTicks))
            {
                var metric = pair.Value;
                yield return (pair.Key, TicksToMs(metric.LastTicks), TicksToMs(metric.AvgTicks), TicksToMs(metric.MaxTicks), metric.Count, metric.LastAllocatedBytes, metric.AvgAllocatedBytes, metric.MaxAllocatedBytes);
            }
        }

        private void Add(string name, long elapsedTicks, long allocatedBytes)
        {
            if (!metrics.TryGetValue(name, out var metric))
            {
                metric = new Metric();
                metrics[name] = metric;
            }

            metric.Count++;
            metric.LastTicks = elapsedTicks;
            metric.MaxTicks = Math.Max(metric.MaxTicks, elapsedTicks);
            metric.AvgTicks = metric.AvgTicks == 0
                ? elapsedTicks
                : (long)((metric.AvgTicks * 0.90) + (elapsedTicks * 0.10));
            metric.LastAllocatedBytes = Math.Max(0, allocatedBytes);
            metric.MaxAllocatedBytes = Math.Max(metric.MaxAllocatedBytes, metric.LastAllocatedBytes);
            metric.AvgAllocatedBytes = metric.AvgAllocatedBytes == 0
                ? metric.LastAllocatedBytes
                : (long)((metric.AvgAllocatedBytes * 0.90) + (metric.LastAllocatedBytes * 0.10));
        }

        private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private sealed class Metric
        {
            public long LastTicks;
            public long AvgTicks;
            public long MaxTicks;
            public long Count;
            public long LastAllocatedBytes;
            public long AvgAllocatedBytes;
            public long MaxAllocatedBytes;
        }

        private readonly struct Scope : IDisposable
        {
            private readonly SpikeProfiler owner;
            private readonly string name;
            private readonly long start;
            private readonly long allocatedStart;

            public Scope(SpikeProfiler owner, string name)
            {
                this.owner = owner;
                this.name = name;
                start = Stopwatch.GetTimestamp();
                allocatedStart = GC.GetAllocatedBytesForCurrentThread();
            }

            public void Dispose() => owner.Add(name, Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocatedStart);
        }
    }

    private sealed class PreOpenPreviewEntry
    {
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
        public int Count { get; set; } = 1;
    }

    private sealed class PreOpenRecipePreviewCacheEntry
    {
        public List<PreOpenPreviewEntry> AllEntries { get; init; } = new();
        public DateTime LastUsedUtc { get; set; }
    }


    private sealed class PreOpenUiAnchor
    {
        public object RectSource { get; init; } = null!;
        public PreOpenPreviewEntry Entry { get; init; } = null!;
    }

    private sealed class PreOpenPreviewDraw
    {
        public Expedition2EncounterLabel EncounterLabel { get; init; } = null!;
        public Vector2 FallbackPosition { get; init; }
        public List<PreOpenPreviewEntry> Entries { get; init; } = new();
    }

    private sealed class StableScreenAnchor
    {
        public Vector2 Position { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool HasPosition { get; set; }
    }

    private const float StableTooltipHardSnapPixels = 260f;
    private const float StableTooltipLerpFactor = 0.35f;
    private static readonly TimeSpan StableTooltipKeepAlive = TimeSpan.FromSeconds(6);

    private readonly Dictionary<string, StableScreenAnchor> stablePreOpenPreviewPositions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StableScreenAnchor> stableExpeditionTooltipPositions = new(StringComparer.Ordinal);

    private readonly List<PreOpenPreviewDraw> cachedPreOpenPreviewDraws = new();
    private readonly List<PreOpenPreviewEntry> cachedPreOpenHighlightRewards = new();
    private readonly Dictionary<string, PreOpenPreviewEntry> cachedPreOpenRewardByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PreOpenRecipePreviewCacheEntry> preOpenRecipePreviewCache = new(StringComparer.Ordinal);
    private readonly List<PreOpenUiAnchor> cachedPreOpenUiAnchors = new();
    private DateTime lastPreOpenPreviewCacheTime = DateTime.MinValue;
    private string lastPreOpenPreviewSignature = string.Empty;
    private DateTime lastHeavyUiTextScanUtc = DateTime.MinValue;

    private sealed class ExpeditionModeEncounterEntry
    {
        public int Index { get; set; }
        public Expedition2EncounterLabel? LabelSource { get; set; }
        public Vector2 ScreenPosition { get; set; }
        public Vector2 GridPosition { get; set; }
        public int RuneCount { get; set; }
        public string FixedRune { get; set; } = string.Empty;
        public List<PreOpenPreviewEntry> Rewards { get; set; } = new();
    }

    private readonly List<ExpeditionModeEncounterEntry> cachedExpeditionModeEntries = new();
    private DateTime lastExpeditionModeCacheTime = DateTime.MinValue;

    private readonly Dictionary<string, double> priceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> rawPriceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object priceLock = new();
    private string appliedPriceUnitKey = string.Empty;
    private DateTime lastPriceRefreshUtc = DateTime.MinValue;
    private DateTime nextAllowedPriceRefreshUtc = DateTime.MinValue;
    private bool priceRefreshRunning;
    private string priceStatus = "not loaded";
    private string detectedLeagueName = string.Empty;
    private string lastPriceMiss = string.Empty;
    private string leagueDetectStatus = "not checked";
    private double exaltedOrbRawValue = 0;
    private double divineOrbRawValue = 0;
    private bool forcePriceRefreshRequested;
    private int lastDownloadedCategories;
    private int lastFailedCategories;
    private int lastPriceHits;
    private int lastPriceMisses;

    private DateTime lastScan = DateTime.MinValue;
    private DateTime lastExpeditionWindowLocalScan = DateTime.MinValue;
    private const int HealthyUiScanMinIntervalMs = 250;
    private const int FailedExpeditionPanelScanMinIntervalMs = 1000;
    private const int ExpeditionWindowLocalFallbackMinIntervalMs = 2000;
    private DateTime lastEnabledRewardsRebuildUtc = DateTime.MinValue;
    private int lastEnabledRewardsFingerprint;
    private readonly SpikeProfiler profiler = new();
    private DateTime lastProfilerTextLogUtc = DateTime.MinValue;
    private string profilerLogPath = string.Empty;
    private string lastSettingsSignature = string.Empty;
    private bool profilerLogPathInitialized;
    private int localScannedObjects;
    private int candidatePanels;
    private int scannedRows;
    private string status = "not scanned";
    private string mode = "";
    private string rewardFilter = string.Empty;

    public override bool Initialise()
    {
        Name = "RuneHighlighter";
        CacheRewardProperties();
        InitializeProfilerTextLog();
        TryLoadPriceCacheFromDisk();
        return base.Initialise();
    }

    private void ApplyVisibleRewardFlickerProtection()
    {
        
        
        
        var now = DateTime.UtcNow;

        if (visibleRewards.Count == 0)
        {
            consecutiveEmptyRewardScans++;

            if (consecutiveEmptyRewardScans >= 2)
                stableVisibleRewardCache.Clear();

            return;
        }

        consecutiveEmptyRewardScans = 0;

        var isScrolling = IsRewardPanelScrolling();

        if (isScrolling)
        {
            
            
            stableVisibleRewardCache.Clear();

            foreach (var reward in visibleRewards)
            {
                var key = GetVisibleRewardStableKey(reward);
                if (!string.IsNullOrWhiteSpace(key))
                    stableVisibleRewardCache[key] = (reward, now);
            }

            return;
        }

        foreach (var reward in visibleRewards)
        {
            var key = GetVisibleRewardStableKey(reward);
            if (!string.IsNullOrWhiteSpace(key))
                stableVisibleRewardCache[key] = (reward, now);
        }

        var expired = stableVisibleRewardCache
            .Where(x => (now - x.Value.LastSeenUtc).TotalMilliseconds > VisibleRewardStickyMs)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in expired)
            stableVisibleRewardCache.Remove(key);

        foreach (var cached in stableVisibleRewardCache.Values
                     .Select(x => x.Reward)
                     .OrderBy(x => x.Rect.Y)
                     .ThenBy(x => x.Rect.X))
        {
            if (visibleRewards.Any(x =>
                    string.Equals(x.Text, cached.Text, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(x.Rect.Y - cached.Rect.Y) < 20 &&
                    Math.Abs(x.Rect.X - cached.Rect.X) < 40))
                continue;

            visibleRewards.Add(cached);
        }
    }

    private bool IsRewardPanelScrolling()
    {
        
        
        foreach (var reward in visibleRewards)
        {
            foreach (var cached in stableVisibleRewardCache.Values)
            {
                if (!string.Equals(cached.Reward.Text, reward.Text, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Math.Abs(cached.Reward.Rect.Y - reward.Rect.Y) > 28)
                    return true;
            }
        }

        return false;
    }

    private static string GetVisibleRewardStableKey(VisibleReward reward)
    {
        
        
        if (string.IsNullOrWhiteSpace(reward.Text))
            return string.Empty;

        var yBucket = (int)Math.Round(reward.Rect.Y / 24f);
        return reward.Text.Trim() + "|" + yBucket;
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        try
        {
            using (profiler.Track("Render total"))
            {
                var now = DateTime.UtcNow;
                var effectiveScanIntervalMs = GetEffectiveUiScanIntervalMs();
                if ((now - lastScan).TotalMilliseconds >= effectiveScanIntervalMs)
                {
                    using (profiler.Track("Scan tick"))
                    {
                        RebuildEnabledRewardsIfNeeded(now);
                        lastPriceHits = 0;
                        lastPriceMisses = 0;

                        using (profiler.Track("Prices/check"))
                        {
                            EnsureDisplayPriceModeApplied();
                            RefreshPricesIfNeeded();
                        }

                        using (profiler.Track("UI reward scan"))
                            ScanPanel40();

                        using (profiler.Track("Flicker cache"))
                            ApplyVisibleRewardFlickerProtection();

                        lastScan = now;
                    }
                }

                using (profiler.Track("Preview cache"))
                    UpdatePreOpenPreviewCache();

                using (profiler.Track("Preview draw"))
                    DrawPreOpenPreview();

                using (profiler.Track("Expedition cache"))
                    UpdateExpeditionModeCache();

                using (profiler.Track("Expedition draw"))
                {
                    DrawExpeditionModeWindow();
                    DrawExpeditionModeTooltips();
                }

                var topValue = 0d;
                var secondValue = 0d;
                foreach (var reward in visibleRewards)
                {
                    var value = reward.TotalValue;
                    if (value <= 0)
                        continue;

                    if (value > topValue + 0.001)
                    {
                        secondValue = topValue;
                        topValue = value;
                    }
                    else if (value < topValue - 0.001 && value > secondValue + 0.001)
                    {
                        secondValue = value;
                    }
                }

                using (profiler.Track("Highlight draw"))
                {
                    foreach (var reward in visibleRewards)
                    {
                        var isTopPick = Settings.HighlightMostValuableReward.Value && reward.TotalValue > 0 && Math.Abs(reward.TotalValue - topValue) < 0.001;
                        var isSecondPick = Settings.HighlightMostValuableReward.Value && reward.TotalValue > 0 && !isTopPick && Math.Abs(reward.TotalValue - secondValue) < 0.001;

                        if (Settings.HighlightOnlyTopTwoPicks.Value && !isTopPick && !isSecondPick)
                            continue;

                        if (Settings.HighlightOnlyRewardsAboveValue.Value &&
                            (reward.TotalValue <= 0 || reward.TotalValue < Settings.MinimumValueToHighlight.Value))
                            continue;

                        var frameColor = isTopPick
                            ? Settings.TopPickColor
                            : isSecondPick
                                ? Settings.SecondPickColor
                                : Settings.FrameColor;

                        var frameThickness = isTopPick || isSecondPick
                            ? Settings.TopPickFrameThickness.Value
                            : Settings.FrameThickness.Value;

                        Graphics.DrawFrame(reward.Rect, frameColor, frameThickness);

                        if (Settings.ShowPriceOnReward.Value && reward.TotalValue > 0)
                        {
                            var prefix = isTopPick ? "#1 " : isSecondPick ? "#2 " : string.Empty;
                            Graphics.DrawTextWithBackground(
                                prefix + FormatRewardValue(reward.TotalValue),
                                new Vector2(reward.Rect.X + 6, reward.Rect.Y + 4),
                                Color.White,
                                Color.FromArgb(180, 0, 0, 0));
                        }
                    }
                }

                if (Settings.DebugStats.Value)
                    DrawDebugOverlay();

                WriteProfilerTextLogIfNeeded(now);
            }
        }
        catch (Exception e)
        {
            WriteProfilerException(e);

            if (Settings.DebugStats.Value)
                Graphics.DrawTextWithBackground("RuneHL error: " + e.Message, new Vector2(4, 140), Color.White, Color.FromArgb(180, 0, 0, 0));
        }
    }

    public override void DrawSettings()
    {
        RebuildEnabledRewards();

        DrawGeneralControls();

        ImGui.Separator();

        if (ImGui.CollapsingHeader("UI Highlight"))
        {
            DrawUiHighlightControls();
        }

        if (ImGui.CollapsingHeader("Pre-Open Preview"))
        {
            DrawPreOpenPreviewControls();
        }

        if (ImGui.CollapsingHeader("ExpeditionMode"))
        {
            DrawExpeditionModeControls();
        }

        if (ImGui.CollapsingHeader("Reward Selection"))
        {
            ImGui.Text("Checked rewards will be highlighted. Unchecked rewards will be ignored.");
            ImGui.Text("Type a name, for example divine, exalted, rune, or flux, to filter the list.");

            DrawRewardFilterList();
        }

        if (ImGui.CollapsingHeader("Diagnostics"))
        {
            DrawDiagnosticsControls();
        }
    }

    private void InitializeProfilerTextLog()
    {
        if (!Settings.EnableProfilerTextLog.Value)
            return;

        var path = GetProfilerLogPath();
        TryCreateProfilerDirectory(path);

        var header = new StringBuilder(1024);
        header.AppendLine("============================================================");
        header.AppendLine($"RuneHighlighter profiler TXT started UTC={DateTime.UtcNow:O}");
        header.AppendLine($"Log path: {path}");
        header.AppendLine("Purpose: identify which RuneHighlighter option group causes CPU spikes.");
        header.AppendLine("Columns: UTC | section | last/avg/max/count | allocation delta | option attribution | runtime counters");
        header.AppendLine("============================================================");
        header.AppendLine(BuildSettingsSnapshot("initial settings"));

        TryAppendProfilerText(header.ToString());
    }

    private void WriteProfilerTextLogIfNeeded(DateTime nowUtc)
    {
        if (!Settings.EnableProfilerTextLog.Value)
            return;

        var intervalMs = Math.Max(250, Settings.ProfilerTextLogIntervalMs.Value);
        if ((nowUtc - lastProfilerTextLogUtc).TotalMilliseconds < intervalMs)
            return;

        lastProfilerTextLogUtc = nowUtc;

        var warnMs = Math.Max(1, Settings.SpikeProfilerWarnMs.Value);
        var onlySpikes = Settings.ProfilerTextLogOnlySpikes.Value;
        var metrics = profiler.Snapshot()
            .Where(x => !onlySpikes || x.LastMs >= warnMs || x.MaxMs >= warnMs)
            .Take(16)
            .ToList();

        var settingsSignature = BuildSettingsSignature();
        var settingsChanged = !string.Equals(settingsSignature, lastSettingsSignature, StringComparison.Ordinal);
        if (metrics.Count == 0 && !settingsChanged)
            return;

        var sb = new StringBuilder(4096);
        sb.AppendLine($"[{nowUtc:O}] RuneHighlighter profiler sample");
        sb.AppendLine($"Runtime: matches={visibleRewards.Count}, enabledRewards={enabledItemNames.Count}, rows={scannedRows}, objects={localScannedObjects}, panels={candidatePanels}, priceHits={lastPriceHits}, priceMisses={lastPriceMisses}, status={status}, mode={mode}, priceStatus={priceStatus}, league={GetEffectiveLeagueNameForLog()}");

        if (settingsChanged)
        {
            lastSettingsSignature = settingsSignature;
            sb.AppendLine(BuildSettingsSnapshot("settings changed/current settings"));
        }

        foreach (var metric in metrics)
        {
            var spikeTag = metric.LastMs >= warnMs ? " SPIKE" : string.Empty;
            sb.AppendLine(
                $"{spikeTag} section=\"{metric.Name}\" last={metric.LastMs:0.00}ms avg={metric.AvgMs:0.00}ms max={metric.MaxMs:0.00}ms n={metric.Count} allocLast={metric.LastAllocatedBytes}B allocAvg={metric.AvgAllocatedBytes}B allocMax={metric.MaxAllocatedBytes}B options=\"{GetProfilerAttribution(metric.Name)}\"");
        }

        sb.AppendLine();
        TryAppendProfilerText(sb.ToString());
    }

    private void WriteProfilerException(Exception exception)
    {
        if (!Settings.EnableProfilerTextLog.Value)
            return;

        var sb = new StringBuilder(2048);
        sb.AppendLine($"[{DateTime.UtcNow:O}] RuneHighlighter exception");
        sb.AppendLine(exception.ToString());
        sb.AppendLine(BuildSettingsSnapshot("settings at exception"));
        sb.AppendLine();
        TryAppendProfilerText(sb.ToString());
    }

    private string GetProfilerLogPath()
    {
        if (!string.IsNullOrWhiteSpace(profilerLogPath))
            return profilerLogPath;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            assemblyDirectory = Environment.CurrentDirectory;

        
        
        profilerLogPath = Path.Combine(assemblyDirectory, "Profiler", "runehighlighter_debug.txt");
        return profilerLogPath;
    }

    private void TryCreateProfilerDirectory(string logPath)
    {
        if (profilerLogPathInitialized)
            return;

        profilerLogPathInitialized = true;

        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }
        catch
        {
            
        }
    }

    private void TryAppendProfilerText(string text)
    {
        var path = GetProfilerLogPath();
        TryCreateProfilerDirectory(path);

        try
        {
            File.AppendAllText(path, text, Encoding.UTF8);
            return;
        }
        catch
        {
            
        }

        try
        {
            var fallbackDirectory = Path.Combine(Environment.CurrentDirectory, "RuneHighlighter", "Profiler");
            Directory.CreateDirectory(fallbackDirectory);
            var fallbackPath = Path.Combine(fallbackDirectory, "runehighlighter_debug.txt");
            File.AppendAllText(fallbackPath, text, Encoding.UTF8);
            profilerLogPath = fallbackPath;
        }
        catch
        {
            
        }
    }

    private string BuildSettingsSnapshot(string title)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine($"-- {title} --");
        sb.AppendLine($"General: Enable={Settings.Enable.Value}, ScanIntervalMs={Settings.ScanIntervalMs.Value}, DebugOverlay={Settings.DebugStats.Value}, SpikeProfiler={Settings.EnableSpikeProfiler.Value}, TxtLog={Settings.EnableProfilerTextLog.Value}, TxtOnlySpikes={Settings.ProfilerTextLogOnlySpikes.Value}, WarnMs={Settings.SpikeProfilerWarnMs.Value}");
        sb.AppendLine($"Scan/UI: Panel40FastMode={Settings.Panel40FastMode.Value}, OnlyIsVisible={Settings.OnlyIsVisible.Value}, HighlightAllVisibleRewards={Settings.HighlightAllVisibleRewards.Value}, DrawFullRow={Settings.DrawFullRow.Value}, UsePreOpenCacheForUiHighlights={Settings.UsePreOpenCacheForUiHighlights.Value}, PreOpenUiFullRescanMs={Settings.PreOpenUiFullRescanIntervalMs.Value}, MaxLocalObjects={Settings.MaxLocalObjects.Value}, MaxRewardRows={Settings.MaxRewardRows.Value}, Panel40LocalDepth={Settings.Panel40LocalDepth.Value}, MinRewardPanelChildren={Settings.MinRewardPanelChildren.Value}");
        sb.AppendLine($"Price: EnablePriceApi={Settings.EnablePriceApi.Value}, ShowPriceOnReward={Settings.ShowPriceOnReward.Value}, DisplayExalted={Settings.DisplayPricesInExaltedOrbs.Value}, DisplayDivine={Settings.DisplayPricesInDivineOrbs.Value}, AutoLeague={Settings.AutoDetectPoeNinjaLeague.Value}, RefreshMin={Settings.PriceRefreshIntervalMinutes.Value}, SafeMode={Settings.PriceApiSafeMode.Value}, RequestDelayMs={Settings.PriceApiRequestDelayMs.Value}, Cooldown429Min={Settings.PriceApi429CooldownMinutes.Value}");
        sb.AppendLine($"Highlight: MostValuable={Settings.HighlightMostValuableReward.Value}, Top2Only={Settings.HighlightOnlyTopTwoPicks.Value}, AboveValue={Settings.HighlightOnlyRewardsAboveValue.Value}, MinimumValue={Settings.MinimumValueToHighlight.Value}, FrameThickness={Settings.FrameThickness.Value}, TopPickThickness={Settings.TopPickFrameThickness.Value}");
        sb.AppendLine($"Preview: Enable={Settings.EnablePreOpenPreview.Value}, BestOnly={Settings.PreviewBestRewardOnly.Value}, Top2Only={Settings.PreviewTopTwoOnly.Value}, UseMinimumFilter={Settings.PreviewUseMinimumValueFilter.Value}, MaxLines={Settings.PreviewMaxLines.Value}, MinimumValue={Settings.PreviewMinimumValue.Value}");
        sb.AppendLine($"ExpeditionMode: Window={Settings.EnableExpeditionMode.Value}, Tooltip={Settings.EnableExpeditionModeTooltip.Value}, TooltipBestOnly={Settings.ExpeditionModeTooltipBestOnly.Value}, TooltipTop2Only={Settings.ExpeditionModeTooltipTopTwoOnly.Value}, MaxRewards={Settings.ExpeditionModeMaxRewardsPerEncounter.Value}, MinimumValue={Settings.ExpeditionModeMinimumValue.Value}, ShowZero={Settings.ExpeditionModeShowZeroPriceRewards.Value}, FallbackList={Settings.ExpeditionModeTooltipFallbackList.Value}");
        sb.AppendLine($"Rewards: enabledRewardCount={enabledItemNames.Count}");
        return sb.ToString();
    }

    private string BuildSettingsSignature()
    {
        return string.Join('|',
            Settings.Enable.Value,
            Settings.ScanIntervalMs.Value,
            Settings.DebugStats.Value,
            Settings.EnableSpikeProfiler.Value,
            Settings.EnableProfilerTextLog.Value,
            Settings.ProfilerTextLogOnlySpikes.Value,
            Settings.ProfilerTextLogIntervalMs.Value,
            Settings.SpikeProfilerWarnMs.Value,
            Settings.Panel40FastMode.Value,
            Settings.OnlyIsVisible.Value,
            Settings.HighlightAllVisibleRewards.Value,
            Settings.DrawFullRow.Value,
            Settings.UsePreOpenCacheForUiHighlights.Value,
            Settings.PreOpenUiFullRescanIntervalMs.Value,
            Settings.EnablePriceApi.Value,
            Settings.ShowPriceOnReward.Value,
            Settings.DisplayPricesInExaltedOrbs.Value,
            Settings.DisplayPricesInDivineOrbs.Value,
            Settings.HighlightMostValuableReward.Value,
            Settings.HighlightOnlyTopTwoPicks.Value,
            Settings.HighlightOnlyRewardsAboveValue.Value,
            Settings.MinimumValueToHighlight.Value,
            Settings.EnablePreOpenPreview.Value,
            Settings.PreviewBestRewardOnly.Value,
            Settings.PreviewTopTwoOnly.Value,
            Settings.PreviewUseMinimumValueFilter.Value,
            Settings.EnableExpeditionMode.Value,
            Settings.EnableExpeditionModeTooltip.Value,
            Settings.ExpeditionModeTooltipBestOnly.Value,
            Settings.ExpeditionModeTooltipTopTwoOnly.Value,
            Settings.ExpeditionModeTooltipFallbackList.Value,
            enabledItemNames.Count);
    }

    private string GetProfilerAttribution(string sectionName)
    {
        return sectionName switch
        {
            "Prices/check" => "Enable Price API, Display Prices In Exalted/Divine Orbs, League Name, Auto Detect League, Price Refresh Interval, Price API Safe Mode, Price API Request Delay",
            "UI reward scan" => "Use Pre-Open Cache For UI Highlights, Dynamic Root Fast Mode, Only IsVisible, Highlight Every Visible Reward, Reward Selection, Panel40 Local Depth, Max Local Objects, Max Reward Rows, Min Reward Panel Children",
            "Flicker cache" => "Visible reward sticky/flicker protection; affected by UI reward scan results and scrolling",
            "Preview cache" => "Enable Pre-Open Preview, Preview Best Reward Only, Preview Top 2 Picks Only, Preview Use Minimum Value Filter, Preview Max Lines, Preview Minimum Value",
            "Preview draw" => "Enable Pre-Open Preview, Preview offsets/background, Preview Best/Top2 mode, Preview Max Lines",
            "Expedition cache" => "ExpeditionMode, ExpeditionMode Max Rewards Per Encounter, ExpeditionMode Minimum Value, ExpeditionMode Show Zero Price Rewards",
            "Expedition draw" => "ExpeditionMode Window, ExpeditionMode Tooltip Overlay, Tooltip Best Reward Only, Tooltip Top 2 Only, Tooltip Fallback List, Tooltip offsets/background",
            "Highlight draw" => "Highlight Most Valuable Reward, Highlight Rewards Above Value, Highlight Only Top 2 Picks, Highlight Only Rewards Above Value, Show Price On Reward, Draw Full Row, Frame Thickness/Colors",
            "Scan tick" => "Scan Interval plus Prices/check, UI reward scan and Flicker cache combined",
            "Render total" => "All enabled RuneHighlighter options active this frame",
            _ => "Unmapped RuneHighlighter section"
        };
    }

    private string GetEffectiveLeagueNameForLog()
    {
        if (!string.IsNullOrWhiteSpace(Settings.LeagueName.Value))
            return Settings.LeagueName.Value;

        if (!string.IsNullOrWhiteSpace(detectedLeagueName))
            return detectedLeagueName;

        return "auto/unknown";
    }

    private void DrawDebugOverlay()
    {
        var y = 110f;
        Graphics.DrawTextWithBackground(
            $"RuneHL: enabled={enabledItemNames.Count}, matches={visibleRewards.Count}, rows={scannedRows}, objects={localScannedObjects}, panels={candidatePanels}, {status}",
            new Vector2(4, y),
            Color.White,
            Color.FromArgb(180, 0, 0, 0));

        if (!Settings.EnableSpikeProfiler.Value)
            return;

        y += 18;
        var warnMs = Settings.SpikeProfilerWarnMs.Value;
        foreach (var metric in profiler.Snapshot().Take(10))
        {
            var color = metric.LastMs >= warnMs ? Color.OrangeRed : Color.White;
            Graphics.DrawTextWithBackground(
                $"RuneHL profiler: {metric.Name} last={metric.LastMs:0.00}ms avg={metric.AvgMs:0.00}ms max={metric.MaxMs:0.00}ms alloc={metric.LastAllocatedBytes / 1024.0:0.0}KB n={metric.Count}",
                new Vector2(4, y),
                color,
                Color.FromArgb(180, 0, 0, 0));
            y += 16;
        }
    }

    private void DrawMainControls()
    {
        DrawGeneralControls();

        ImGui.Separator();

        if (ImGui.CollapsingHeader("UI Highlight"))
            DrawUiHighlightControls();

        if (ImGui.CollapsingHeader("Pre-Open Preview"))
            DrawPreOpenPreviewControls();
    }

    private void DrawGeneralControls()
    {
        DrawToggle(Settings.Enable, "Enable Plugin");
        DrawToggle(Settings.EnablePriceApi, "Enable Price API");
        DrawToggle(Settings.ShowPriceOnReward, "Show Price On Reward");
        DrawToggle(Settings.DisplayPricesInExaltedOrbs, "Display Prices In Exalted Orbs");
        DrawToggle(Settings.DisplayPricesInDivineOrbs, "Display Prices In Divine Orbs");
    }

    private void DrawUiHighlightControls()
    {
        DrawToggle(Settings.HighlightAllVisibleRewards, "Highlight Every Visible Reward (disables Item Filter and highlights all rewards)");
        DrawToggle(Settings.HighlightMostValuableReward, "Highlight Most Valuable Reward");
        DrawToggle(Settings.HighlightOnlyTopTwoPicks, "Highlight Only Top 2 Picks");
        DrawToggle(Settings.HighlightOnlyRewardsAboveValue, "Highlight Only Rewards Above Value");
        DrawIntSlider(Settings.MinimumValueToHighlight, "Minimum Value To Highlight");
        DrawToggle(Settings.UsePreOpenCacheForUiHighlights, "Use Pre-Open Cache For UI Highlights");
        DrawIntSlider(Settings.PreOpenUiFullRescanIntervalMs, "Pre-Open UI Full Rescan Interval (ms)");
        ImGui.TextDisabled("Pre-Open mode keeps filters/prices from the cheap preview cache and reduces heavy opened-UI scans.");

        ImGui.Separator();

        ImGui.Separator();
        ImGui.Text("Colors");
        DrawColor(Settings.FrameColor, "Frame Color");
        DrawColor(Settings.TopPickColor, "Top Pick Color");
        DrawColor(Settings.SecondPickColor, "Second Pick Color");

        if (ImGui.Button("Reset Default Colors"))
        {
            Settings.FrameColor.Value = Color.FromArgb(255, 0, 255, 0);
            Settings.TopPickColor.Value = Color.FromArgb(255, 192, 2, 250);
            Settings.SecondPickColor.Value = Color.FromArgb(255, 25, 203, 232);
        }
    }

    private void DrawPreOpenPreviewControls()
    {
        DrawToggle(Settings.EnablePreOpenPreview, "Enable Pre-Open Preview");
        DrawToggle(Settings.PreviewBestRewardOnly, "Preview Best Reward Only");
        DrawToggle(Settings.PreviewTopTwoOnly, "Preview Top 2 Picks Only");
        DrawToggle(Settings.PreviewUseMinimumValueFilter, "Preview Use Minimum Value Filter");
        ImGui.TextDisabled("UI filters do not affect Pre-Open Preview. Use Preview options here.");

        ImGui.Separator();

        DrawIntSlider(Settings.PreviewOffsetX, "Pre-Open Preview Offset X");
        DrawIntSlider(Settings.PreviewOffsetY, "Pre-Open Preview Offset Y");
        DrawIntSlider(Settings.PreviewBackgroundOpacity, "Pre-Open Background Opacity");
    }


    private void DrawExpeditionModeControls()
    {
        DrawToggle(Settings.EnableExpeditionMode, "Enable ExpeditionMode Window");
        DrawToggle(Settings.EnableExpeditionModeTooltip, "Enable ExpeditionMode Tooltip Overlay");
        DrawToggle(Settings.ExpeditionModeTooltipBestOnly, "Tooltip Best Reward Only");
        DrawToggle(Settings.ExpeditionModeTooltipTopTwoOnly, "Tooltip Top 2 Only");
        DrawIntSlider(Settings.ExpeditionModeMaxRewardsPerEncounter, "Max Rewards Per Encounter");
        DrawIntSlider(Settings.ExpeditionModeMinimumValue, "Minimum Value");
        DrawToggle(Settings.ExpeditionModeShowZeroPriceRewards, "Show Zero Price / Unknown Rewards");
        DrawToggle(Settings.ExpeditionModeTooltipFallbackList, "Tooltip Fallback List");
        DrawIntSlider(Settings.ExpeditionModeTooltipFallbackX, "Tooltip Fallback X");
        DrawIntSlider(Settings.ExpeditionModeTooltipFallbackY, "Tooltip Fallback Y");
        DrawIntSlider(Settings.ExpeditionModeTooltipBackgroundOpacity, "Tooltip Fallback Background Opacity");
        DrawColor(Settings.ExpeditionModeHeaderColor, "Expedition Header Color");

        ImGui.TextDisabled("Window lists all encounters. Tooltip fallback controls position the on-screen list and BEST PICK line.");
        ImGui.Text($"Detected encounters: {cachedExpeditionModeEntries.Count}");
    }

    private void DrawColor(ExileCore2.Shared.Nodes.ColorNode node, string label)
    {
        var color = node.Value;
        var vector = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        var popupId = $"##{label}_color_popup";

        ImGui.Text($"{label}:");
        ImGui.SameLine(260);

        if (ImGui.ColorButton($"##{label}_button", vector, ImGuiColorEditFlags.NoTooltip, new Vector2(24, 18)))
            ImGui.OpenPopup(popupId);

        if (ImGui.BeginPopup(popupId))
        {
            if (ImGui.ColorPicker4($"##{label}_picker", ref vector,
                    ImGuiColorEditFlags.NoSidePreview |
                    ImGuiColorEditFlags.NoSmallPreview))
            {
                node.Value = Color.FromArgb(
                    (int)Math.Clamp(vector.W * 255f, 0, 255),
                    (int)Math.Clamp(vector.X * 255f, 0, 255),
                    (int)Math.Clamp(vector.Y * 255f, 0, 255),
                    (int)Math.Clamp(vector.Z * 255f, 0, 255));
            }

            ImGui.EndPopup();
        }
    }

    private void DrawDiagnosticsControls()
    {
        DrawToggle(Settings.DebugStats, "Enable Debug Overlay");
        DrawToggle(Settings.Panel40FastMode, "Dynamic Root Fast Mode");
        DrawToggle(Settings.DrawFullRow, "Draw Full Row");
        DrawToggle(Settings.EnableSpikeProfiler, "Enable Spike Profiler");
        DrawToggle(Settings.EnableProfilerTextLog, "Enable Profiler TXT Log");
        DrawToggle(Settings.ProfilerTextLogOnlySpikes, "Profiler TXT Log Only Spikes");
        DrawIntSlider(Settings.ProfilerTextLogIntervalMs, "Profiler TXT Log Interval (ms)");
        DrawIntSlider(Settings.SpikeProfilerWarnMs, "Spike Profiler Warn Ms");
        ImGui.TextWrapped($"Profiler TXT: {GetProfilerLogPath()}");

        DrawIntSlider(Settings.ScanIntervalMs, "Scan Interval (ms)");
        DrawLeagueInput();
        DrawIntSlider(Settings.PriceRefreshIntervalMinutes, "Price Refresh Interval Minutes");
        DrawIntSlider(Settings.CacheAgeWarningMinutes, "Cache Age Warning Minutes");
        if (ImGui.Button("Force Refresh Prices Now"))
        {
            forcePriceRefreshRequested = true;
            nextAllowedPriceRefreshUtc = DateTime.MinValue;
            lastPriceRefreshUtc = DateTime.MinValue;
            priceStatus = "force refresh requested";
        }
        DrawToggle(Settings.PriceApiSafeMode, "Price API Safe Mode");
        DrawIntSlider(Settings.PriceApiRequestDelayMs, "Price API Request Delay (ms)");
        DrawIntSlider(Settings.PriceApi429CooldownMinutes, "429 Cooldown Minutes");
        DrawIntSlider(Settings.Panel40LocalDepth, "Panel 40 Local Depth");
        DrawIntSlider(Settings.MaxLocalObjects, "Max Local Objects");
        DrawIntSlider(Settings.MaxRewardRows, "Max Reward Rows");
        DrawIntSlider(Settings.MinRewardPanelChildren, "Min Reward Panel Children");


        ImGui.Separator();
        ImGui.Text($"Enabled Reward Filters: {enabledItemNames.Count}");
        ImGui.Text($"Visible Reward Matches: {visibleRewards.Count}");
        ImGui.Text($"Sticky Reward Session Cache: {stableVisibleRewardCache.Count}");
        ImGui.Text($"Price Status: {priceStatus}");
        ImGui.Text($"Display Price Cache Items: {priceCache.Count}");
        ImGui.Text($"Raw Price Cache Items: {rawPriceCache.Count}");
        ImGui.Text($"Applied Price Unit: {appliedPriceUnitKey}");
        ImGui.Text($"Minimum Highlight Value: {Settings.MinimumValueToHighlight.Value:0.##}{GetPriceDisplaySuffix()}");
        ImGui.Text($"Minimum Value Only Filter: {Settings.HighlightOnlyRewardsAboveValue.Value}");
        ImGui.Text($"Preview Mode: {(Settings.PreviewBestRewardOnly.Value ? "Best Reward Only" : Settings.PreviewTopTwoOnly.Value ? "Top 2 Picks" : "Full List")}");
        ImGui.Text($"Pre-Open Preview Cached Labels: {cachedPreOpenPreviewDraws.Count}");
        ImGui.Text($"Pre-Open UI Highlight Rewards: {cachedPreOpenRewardByName.Count}");
        ImGui.Text($"Pre-Open Preview Cache UTC: {(lastPreOpenPreviewCacheTime == DateTime.MinValue ? "never" : lastPreOpenPreviewCacheTime.ToString("u"))}");
        ImGui.Text($"Downloaded Categories: {lastDownloadedCategories}, Failed Categories: {lastFailedCategories}");
        ImGui.TextWrapped($"Cache File: {GetPriceCachePath()}");
        ImGui.Text($"Exalted Orb Raw Value: {(exaltedOrbRawValue <= 0 ? "unknown" : exaltedOrbRawValue.ToString("0.####"))}");
        ImGui.Text($"Divine Orb Raw Value: {(divineOrbRawValue <= 0 ? "unknown - use Force Refresh Prices Now after enabling Divine mode" : divineOrbRawValue.ToString("0.####"))}");
        var cacheAge = GetPriceCacheAge();
        if (cacheAge != TimeSpan.MaxValue)
            ImGui.Text($"Price Cache Age: {cacheAge.TotalMinutes:0} minutes");
        if (cacheAge != TimeSpan.MaxValue && cacheAge.TotalMinutes > Settings.CacheAgeWarningMinutes.Value)
            ImGui.TextColored(new Vector4(1f, 0.25f, 0.25f, 1f), "WARNING: price cache is old. Use Force Refresh Prices Now.");

        ImGui.Text($"Last Price Refresh Local: {(lastPriceRefreshUtc == DateTime.MinValue ? "never" : lastPriceRefreshUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}");
        ImGui.Text($"Last Price Refresh UTC: {(lastPriceRefreshUtc == DateTime.MinValue ? "never" : lastPriceRefreshUtc.ToString("u"))}");
        ImGui.Text($"Next Allowed Refresh Local: {(nextAllowedPriceRefreshUtc == DateTime.MinValue ? "now" : nextAllowedPriceRefreshUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}");
        ImGui.Text($"League: {(string.IsNullOrWhiteSpace(detectedLeagueName) ? GetLeagueNameManualOrFallback() : detectedLeagueName)}");
        ImGui.Text($"League Detect: {leagueDetectStatus}");
        ImGui.Text($"Price Hits/Misses: {lastPriceHits}/{lastPriceMisses}");
        if (!string.IsNullOrWhiteSpace(lastPriceMiss))
            ImGui.Text($"Last Price Miss: {lastPriceMiss}");
        ImGui.Text($"Mode: {mode}");
        ImGui.Text($"Status: {status}");
        ImGui.Text($"Reward Panels Checked: {candidatePanels}");
        ImGui.Text($"Rows Scanned: {scannedRows}");
        ImGui.Text($"Objects Scanned: {localScannedObjects}");
    }

    private void DrawRewardFilterList()
    {
        var rewardSettings = Settings.Rewards;

        ImGui.PushItemWidth(520);
        ImGui.InputText("Filter", ref rewardFilter, 128);
        ImGui.PopItemWidth();

        var visibleItems = new List<(string PropertyName, string DisplayName, object? Node, bool Enabled)>();

        foreach (var (propertyName, displayName) in RewardCatalog.Items)
        {
            if (!rewardProperties.TryGetValue(propertyName, out var property))
                continue;

            if (!string.IsNullOrWhiteSpace(rewardFilter) &&
                displayName.IndexOf(rewardFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var node = property.GetValue(rewardSettings);
            visibleItems.Add((propertyName, displayName, node, SafeBool(node, "Value")));
        }

        var enabledCount = 0;
        foreach (var (propertyName, displayName) in RewardCatalog.Items)
        {
            if (!rewardProperties.TryGetValue(propertyName, out var property))
                continue;

            var node = property.GetValue(rewardSettings);
            if (SafeBool(node, "Value"))
                enabledCount++;
        }

        ImGui.Text($"Enabled Rewards: {enabledCount} / {RewardCatalog.Items.Length}");
        ImGui.SameLine();
        ImGui.Text($"Search Results: {visibleItems.Count}");

        if (ImGui.Button("Enable Search Results"))
        {
            foreach (var item in visibleItems)
                SetToggleValue(item.Node, true);
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable Search Results"))
        {
            foreach (var item in visibleItems)
                SetToggleValue(item.Node, false);
        }

        ImGui.SameLine();

        if (ImGui.Button("Disable All Rewards"))
        {
            foreach (var (propertyName, displayName) in RewardCatalog.Items)
            {
                if (!rewardProperties.TryGetValue(propertyName, out var property))
                    continue;

                SetToggleValue(property.GetValue(rewardSettings), false);
            }
        }

        ImGui.Separator();

        foreach (var item in visibleItems)
        {
            var value = item.Enabled;
            if (ImGui.Checkbox(item.DisplayName, ref value))
                SetToggleValue(item.Node, value);
        }
    }


    private void DrawLeagueInput()
    {
        var value = Convert.ToString(SafeGet(Settings.LeagueName, "Value")) ?? string.Empty;
        ImGui.PushItemWidth(260);
        if (ImGui.InputText("League Name", ref value, 128))
            SetNodeValue(Settings.LeagueName, value);
        ImGui.PopItemWidth();
    }

    private static void DrawToggle(object? node, string label)
    {
        var value = SafeBool(node, "Value");
        if (ImGui.Checkbox(label, ref value))
            SetToggleValue(node, value);
    }

    private static void DrawIntSlider(object? node, string label)
    {
        var value = GetInt(node, "Value");
        var min = GetInt(node, "Min");
        var max = GetInt(node, "Max");

        if (max <= min)
        {
            min = 0;
            max = Math.Max(value * 2, value + 1);
        }

        if (ImGui.SliderInt(label, ref value, min, max))
            SetNodeValue(node, value);
    }

    private static void DrawColor(object? node, string label)
    {
        var colorObj = SafeGet(node, "Value");

        if (colorObj is not Color color)
            color = Color.Lime;

        var rgba = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);

        if (ImGui.ColorEdit4(label, ref rgba))
        {
            var newColor = Color.FromArgb(
                Math.Clamp((int)(rgba.W * 255), 0, 255),
                Math.Clamp((int)(rgba.X * 255), 0, 255),
                Math.Clamp((int)(rgba.Y * 255), 0, 255),
                Math.Clamp((int)(rgba.Z * 255), 0, 255));

            SetNodeValue(node, newColor);
        }
    }

    private static void SetToggleValue(object? node, bool value)
    {
        SetNodeValue(node, value);
    }

    private static void SetNodeValue(object? node, object value)
    {
        if (node == null)
            return;

        try
        {
            var type = node.GetType();
            var prop = type.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(node, value);
                return;
            }

            var field = type.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                field.SetValue(node, value);
        }
        catch
        {
        }
    }

    private void CacheRewardProperties()
    {
        rewardProperties.Clear();
        foreach (var property in typeof(RewardItemSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            rewardProperties[property.Name] = property;
    }

    private void RebuildEnabledRewardsIfNeeded(DateTime now)
    {
        var fingerprint = ComputeEnabledRewardsFingerprint();
        if (fingerprint == lastEnabledRewardsFingerprint && (now - lastEnabledRewardsRebuildUtc).TotalMilliseconds < 1000)
            return;

        RebuildEnabledRewards();
        lastEnabledRewardsFingerprint = fingerprint;
        lastEnabledRewardsRebuildUtc = now;
    }

    private int ComputeEnabledRewardsFingerprint()
    {
        var hash = new HashCode();
        foreach (var (propertyName, _) in RewardCatalog.Items)
        {
            if (!rewardProperties.TryGetValue(propertyName, out var prop))
                continue;

            hash.Add(propertyName, StringComparer.Ordinal);
            hash.Add(SafeBool(prop.GetValue(Settings.Rewards), "Value"));
        }

        return hash.ToHashCode();
    }

    private void RebuildEnabledRewards()
    {
        enabledItemNames.Clear();
        enabledItemLooseKeys.Clear();

        foreach (var (propertyName, itemName) in RewardCatalog.Items)
        {
            if (!rewardProperties.TryGetValue(propertyName, out var prop))
                continue;

            var node = prop.GetValue(Settings.Rewards);
            if (!SafeBool(node, "Value"))
                continue;

            AddEnabledRewardName(itemName);
        }

        lastEnabledRewardsFingerprint = ComputeEnabledRewardsFingerprint();
        lastEnabledRewardsRebuildUtc = DateTime.UtcNow;
    }


    private static string NormalizeLooseRewardKey(string text)
    {
        text = CleanupText(text);
        text = Regex.Replace(text, @"^\s*\d+\s*x\s+", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"^(Skill|Support):\s*", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase).Trim();
        return Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]+", "");
    }

    private void AddEnabledRewardName(string itemName)
    {
        var clean = CleanupText(itemName);
        if (string.IsNullOrWhiteSpace(clean))
            return;

        var normalized = NormalizeRewardSelectionName(clean);

        void AddAlias(string? value)
        {
            value = CleanupText(value ?? string.Empty);
            if (string.IsNullOrWhiteSpace(value))
                return;

            enabledItemNames.Add(value);

            var looseKey = NormalizeLooseRewardKey(value);
            if (!string.IsNullOrWhiteSpace(looseKey))
                enabledItemLooseKeys.Add(looseKey);
        }

        AddAlias(clean);
        AddAlias(normalized);
        AddAlias("1x " + normalized);
        AddAlias(NormalizeRecipeDisplayName(normalized, 1));

        if (clean.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Support:", StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(normalized);
            AddAlias("1x " + normalized);
        }
        else
        {
            AddAlias("Skill: " + normalized);
            AddAlias("Support: " + normalized);
        }

        
        if (normalized.Contains("Unique", StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(normalized.Replace("Very Rare Unique item", "Very Rare Unique item", StringComparison.OrdinalIgnoreCase));
            AddAlias(normalized.Replace("Rare Unique Item", "Rare Unique Item", StringComparison.OrdinalIgnoreCase));
            AddAlias(normalized.Replace("Unique Item", "Unique Item", StringComparison.OrdinalIgnoreCase));
        }

        if (normalized.Contains("Orb of Transmutation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("Orb of Augmentation", StringComparison.OrdinalIgnoreCase))
        {
            AddAlias(normalized.Replace("Orb of ", "Orb ", StringComparison.OrdinalIgnoreCase));
        }
    }

    private const float OffscreenHideMarginPixels = 6f;

    private static bool IsDrawableScreenPosition(Vector2 position)
    {
        return position.X > 1 && position.Y > 1 && position.X < 10000 && position.Y < 10000;
    }

    private static bool IsWithinViewport(Vector2 position, float margin = OffscreenHideMarginPixels)
    {
        if (!IsDrawableScreenPosition(position))
            return false;

        try
        {
            var displaySize = ImGui.GetIO().DisplaySize;
            if (displaySize.X > 0 && displaySize.Y > 0)
            {
                return position.X >= -margin &&
                       position.Y >= -margin &&
                       position.X <= displaySize.X + margin &&
                       position.Y <= displaySize.Y + margin;
            }
        }
        catch
        {
        }

        return true;
    }

    private static bool TryGetCurrentVisibleEncounterBottomLeft(Expedition2EncounterLabel? label, out Vector2 bottomLeft)
    {
        bottomLeft = Vector2.Zero;

        try
        {
            if (label is not { IsValid: true })
                return false;

            var rect = label.GetClientRect();
            if (rect.Width <= 1 || rect.Height <= 1)
                return false;

            bottomLeft = rect.BottomLeft;
            return IsWithinViewport(bottomLeft);
        }
        catch
        {
            bottomLeft = Vector2.Zero;
            return false;
        }
    }

    private static string? TryReadStableObjectIdentity(object? value)
    {
        if (value == null)
            return null;

        try
        {
            var type = value.GetType();
            foreach (var memberName in new[] { "Address", "Id", "EntityId" })
            {
                var prop = type.GetProperty(memberName);
                if (prop != null)
                {
                    var propValue = prop.GetValue(value);
                    if (propValue != null)
                        return Convert.ToString(propValue, CultureInfo.InvariantCulture);
                }

                var field = type.GetField(memberName);
                if (field != null)
                {
                    var fieldValue = field.GetValue(value);
                    if (fieldValue != null)
                        return Convert.ToString(fieldValue, CultureInfo.InvariantCulture);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string BuildEncounterStableKey(object? entity, Expedition2EncounterLabel encounterLabel, int ordinal)
    {
        var identity = TryReadStableObjectIdentity(entity);
        if (!string.IsNullOrWhiteSpace(identity))
            return "entity:" + identity;

        try
        {
            var gridProp = entity?.GetType().GetProperty("GridPos");
            var grid = gridProp?.GetValue(entity);
            if (grid != null)
            {
                var xValue = grid.GetType().GetProperty("X")?.GetValue(grid);
                var yValue = grid.GetType().GetProperty("Y")?.GetValue(grid);
                if (xValue != null && yValue != null)
                {
                    var x = Convert.ToInt32(xValue, CultureInfo.InvariantCulture);
                    var y = Convert.ToInt32(yValue, CultureInfo.InvariantCulture);
                    return $"grid:{x}:{y}:rune:{encounterLabel.FixedRunePosition}:{encounterLabel.FixedRune}:count:{encounterLabel.RuneCount}";
                }
            }
        }
        catch
        {
        }

        return $"fallback:{ordinal}:rune:{encounterLabel.FixedRunePosition}:{encounterLabel.FixedRune}:count:{encounterLabel.RuneCount}";
    }

    private static void CleanupStableAnchors(Dictionary<string, StableScreenAnchor> anchors, DateTime now)
    {
        if (anchors.Count == 0)
            return;

        var dead = anchors
            .Where(x => now - x.Value.LastSeenUtc > StableTooltipKeepAlive)
            .Select(x => x.Key)
            .ToList();

        foreach (var key in dead)
            anchors.Remove(key);
    }

    private static Vector2 StabilizeScreenPosition(Dictionary<string, StableScreenAnchor> anchors, string key, Vector2 measured, DateTime now)
    {
        if (!anchors.TryGetValue(key, out var anchor))
        {
            anchor = new StableScreenAnchor();
            anchors[key] = anchor;
        }

        if (!IsDrawableScreenPosition(measured))
        {
            if (anchor.HasPosition && now - anchor.LastSeenUtc <= StableTooltipKeepAlive)
                return anchor.Position;

            anchor.HasPosition = false;
            anchor.LastSeenUtc = now;
            return Vector2.Zero;
        }

        if (!anchor.HasPosition)
        {
            anchor.Position = measured;
            anchor.HasPosition = true;
            anchor.LastSeenUtc = now;
            return anchor.Position;
        }

        var delta = measured - anchor.Position;
        var distanceSquared = delta.LengthSquared();
        var hardSnapSquared = StableTooltipHardSnapPixels * StableTooltipHardSnapPixels;

        
        
        if (distanceSquared > hardSnapSquared && now - anchor.LastSeenUtc <= StableTooltipKeepAlive)
        {
            anchor.LastSeenUtc = now;
            return anchor.Position;
        }

        anchor.Position = Vector2.Lerp(anchor.Position, measured, StableTooltipLerpFactor);
        anchor.LastSeenUtc = now;
        return anchor.Position;
    }

    private void UpdatePreOpenPreviewCache()
    {
        if (!Settings.EnablePreOpenPreview.Value || !Settings.EnablePriceApi.Value)
        {
            cachedPreOpenPreviewDraws.Clear();
            cachedPreOpenHighlightRewards.Clear();
            cachedPreOpenRewardByName.Clear();
            cachedPreOpenUiAnchors.Clear();
            preOpenRecipePreviewCache.Clear();
            lastPreOpenPreviewSignature = string.Empty;
            return;
        }

        var previewNow = DateTime.UtcNow;
        if ((previewNow - lastPreOpenPreviewCacheTime).TotalMilliseconds < 1500)
            return;

        lastPreOpenPreviewCacheTime = previewNow;
        cachedPreOpenPreviewDraws.Clear();
        cachedPreOpenHighlightRewards.Clear();
        cachedPreOpenRewardByName.Clear();
        cachedPreOpenUiAnchors.Clear();
        EnsureDisplayPriceModeApplied();

        try
        {
            
            
            var labels = GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .Where(x => x?.ItemOnGround?.Metadata?.StartsWith("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.Ordinal) == true)
                .Select(x => (GroundLabel: x, EncounterLabel: x.Label.AsObject<Expedition2EncounterLabel>()))
                .Where(x => x.EncounterLabel != null)
                .ToList();

            if (labels.Count == 0)
            {
                PrunePreOpenRecipePreviewCache(previewNow);
                return;
            }

            var areaLevel = GameController.IngameState.Data.CurrentAreaLevel;
            var previewSignature = BuildPreOpenPreviewSettingsSignature(areaLevel);
            if (!string.Equals(previewSignature, lastPreOpenPreviewSignature, StringComparison.Ordinal))
            {
                preOpenRecipePreviewCache.Clear();
                lastPreOpenPreviewSignature = previewSignature;
            }

            var allRecipes = GameController.Files.Expedition2Recipes.EntriesList;
            var runeWeights = GameController.Files.Expedition2RunesWeights.EntriesList;

            foreach (var (groundLabel, encounterLabel) in labels)
            {
                var entity = groundLabel.ItemOnGround;
                if (entity == null || encounterLabel == null)
                    continue;

                var rawBottomLeft = encounterLabel.GetClientRect().BottomLeft;
                if (!IsDrawableScreenPosition(rawBottomLeft))
                    continue;

                var rawEntries = GetOrBuildPreOpenPreviewEntries(encounterLabel, areaLevel, allRecipes, runeWeights, previewNow);
                if (rawEntries.Count == 0)
                    continue;

                foreach (var entry in rawEntries)
                    AddPreOpenHighlightCandidate(entry);

                var entries = rawEntries;

                if (Settings.PreviewUseMinimumValueFilter.Value)
                {
                    entries = entries
                        .Where(x => x.Value >= Settings.MinimumValueToHighlight.Value)
                        .OrderByDescending(x => x.Value)
                        .Take(GetPreviewLineLimit())
                        .ToList();
                }
                else if (Settings.PreviewMinimumValue.Value > 0)
                {
                    entries = entries
                        .Where(x => x.Value >= Settings.PreviewMinimumValue.Value)
                        .OrderByDescending(x => x.Value)
                        .Take(GetPreviewLineLimit())
                        .ToList();
                }
                else
                {
                    entries = entries.Take(GetPreviewLineLimit()).ToList();
                }

                if (entries.Count == 0)
                    continue;

                cachedPreOpenPreviewDraws.Add(new PreOpenPreviewDraw
                {
                    EncounterLabel = encounterLabel,
                    FallbackPosition = rawBottomLeft,
                    Entries = entries
                });
            }

            PrunePreOpenRecipePreviewCache(previewNow);
            RebuildPreOpenRewardIndex();
        }
        catch
        {
            
        }
    }

    private int GetPreviewLineLimit()
    {
        if (Settings.PreviewBestRewardOnly.Value)
            return 1;

        if (Settings.PreviewTopTwoOnly.Value)
            return 2;

        return Math.Max(1, Settings.PreviewMaxLines.Value);
    }

    private string BuildPreOpenPreviewSettingsSignature(int areaLevel)
    {
        return string.Join("|",
            areaLevel,
            GetPriceDisplayUnitKey(),
            rawPriceCache.Count,
            priceCache.Count,
            Settings.PreviewBestRewardOnly.Value,
            Settings.PreviewTopTwoOnly.Value,
            Settings.PreviewUseMinimumValueFilter.Value,
            Settings.PreviewMinimumValue.Value,
            Settings.MinimumValueToHighlight.Value,
            Settings.PreviewMaxLines.Value);
    }

    private string BuildPreOpenRecipeCacheKey(Expedition2EncounterLabel encounterLabel, int areaLevel)
    {
        return string.Join("|",
            areaLevel,
            encounterLabel.FixedRunePosition,
            Convert.ToString(encounterLabel.FixedRune, CultureInfo.InvariantCulture) ?? string.Empty,
            encounterLabel.RuneCount,
            GetPriceDisplayUnitKey(),
            rawPriceCache.Count,
            priceCache.Count);
    }

    private List<PreOpenPreviewEntry> GetOrBuildPreOpenPreviewEntries(
        Expedition2EncounterLabel encounterLabel,
        int areaLevel,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        IEnumerable<dynamic> runeWeights,
        DateTime now)
    {
        var key = BuildPreOpenRecipeCacheKey(encounterLabel, areaLevel);
        if (preOpenRecipePreviewCache.TryGetValue(key, out var cached))
        {
            cached.LastUsedUtc = now;
            return cached.AllEntries;
        }

        var allowedRuneCounts = new HashSet<int>();
        foreach (var weight in runeWeights)
        {
            try
            {
                if (weight.RuneSlot - 1 == encounterLabel.FixedRunePosition &&
                    Equals(weight.Rune, encounterLabel.FixedRune) &&
                    weight.Level <= areaLevel)
                {
                    allowedRuneCounts.Add(weight.SlotCount);
                }
            }
            catch
            {
                
            }
        }

        var rawEntries = new List<PreOpenPreviewEntry>(64);
        foreach (var recipe in allRecipes)
        {
            if (recipe == null)
                continue;

            if (recipe.RuneCountRequired > encounterLabel.RuneCount)
                continue;

            if (!allowedRuneCounts.Contains(recipe.RuneCountRequired))
                continue;

            if (recipe.MinLevelReq > areaLevel || recipe.MaxLevelReq < areaLevel)
                continue;

            if (recipe.Runes.ElementAtOrDefault(encounterLabel.FixedRunePosition)?.Equals(encounterLabel.FixedRune) != true)
                continue;

            var entry = ToPreOpenEntry(recipe);
            if (entry != null)
                rawEntries.Add(entry);
        }

        rawEntries.Sort(static (a, b) => b.Value.CompareTo(a.Value));

        var cacheEntry = new PreOpenRecipePreviewCacheEntry
        {
            AllEntries = rawEntries,
            LastUsedUtc = now
        };
        preOpenRecipePreviewCache[key] = cacheEntry;
        return rawEntries;
    }

    private void PrunePreOpenRecipePreviewCache(DateTime now)
    {
        if (preOpenRecipePreviewCache.Count <= 64)
            return;

        var removeBefore = now.AddMinutes(-3);
        foreach (var pair in preOpenRecipePreviewCache.ToArray())
        {
            if (pair.Value.LastUsedUtc < removeBefore)
                preOpenRecipePreviewCache.Remove(pair.Key);
        }
    }


    private void AddPreOpenHighlightCandidate(PreOpenPreviewEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
            return;

        var isSelected = Settings.HighlightAllVisibleRewards.Value || IsEnabledRewardText(entry.Name) || IsEnabledAlloyRewardText(entry.Name);
        var wantsPriceBasedHighlight =
            Settings.HighlightMostValuableReward.Value ||
            Settings.HighlightOnlyTopTwoPicks.Value ||
            Settings.HighlightOnlyRewardsAboveValue.Value;

        var includeByPriceMode = wantsPriceBasedHighlight && entry.Value > 0;
        if (!isSelected && !includeByPriceMode)
            return;

        if (Settings.HighlightOnlyRewardsAboveValue.Value && entry.Value < Settings.MinimumValueToHighlight.Value)
            return;

        cachedPreOpenHighlightRewards.Add(entry);
    }

    private void RebuildPreOpenRewardIndex()
    {
        cachedPreOpenRewardByName.Clear();

        foreach (var entry in cachedPreOpenHighlightRewards
                     .OrderByDescending(x => x.Value))
        {
            AddPreOpenRewardIndexKey(entry.Name, entry);
            AddPreOpenRewardIndexKey(NormalizeRewardSelectionName(entry.Name), entry);
            AddPreOpenRewardIndexKey(NormalizeLooseRewardKey(entry.Name), entry);
        }
    }

    private void AddPreOpenRewardIndexKey(string key, PreOpenPreviewEntry entry)
    {
        key = CleanupText(key);
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!cachedPreOpenRewardByName.ContainsKey(key))
            cachedPreOpenRewardByName[key] = entry;
    }

    private bool TryGetPreOpenReward(string text, out PreOpenPreviewEntry entry)
    {
        text = CleanupText(text);

        if (cachedPreOpenRewardByName.TryGetValue(text, out entry))
            return true;

        var normalized = NormalizeRewardSelectionName(text);
        if (cachedPreOpenRewardByName.TryGetValue(normalized, out entry))
            return true;

        var loose = NormalizeLooseRewardKey(text);
        if (!string.IsNullOrWhiteSpace(loose) && cachedPreOpenRewardByName.TryGetValue(loose, out entry))
            return true;

        entry = null!;
        return false;
    }

    private bool ShouldUsePreOpenCacheForUiHighlights()
    {
        return Settings.UsePreOpenCacheForUiHighlights.Value &&
               Settings.EnablePreOpenPreview.Value &&
               cachedPreOpenRewardByName.Count > 0;
    }

    private bool ShouldAllowHeavyUiTextScan()
    {
        if (!ShouldUsePreOpenCacheForUiHighlights())
            return true;

        return (DateTime.UtcNow - lastHeavyUiTextScanUtc).TotalMilliseconds >= Settings.PreOpenUiFullRescanIntervalMs.Value;
    }

    private void UpdateExpeditionModeCache()
    {
        if ((!Settings.EnableExpeditionMode.Value && !Settings.EnableExpeditionModeTooltip.Value) || !Settings.EnablePriceApi.Value)
        {
            cachedExpeditionModeEntries.Clear();
            return;
        }

        if ((DateTime.UtcNow - lastExpeditionModeCacheTime).TotalMilliseconds < 1000)
            return;

        cachedExpeditionModeEntries.Clear();
        lastExpeditionModeCacheTime = DateTime.UtcNow;

        EnsureDisplayPriceModeApplied();

        try
        {
            var hasEncounterEntity = GameController.EntityListWrapper.Entities.Any(x =>
                x.Metadata.StartsWith("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.Ordinal));

            if (!hasEncounterEntity)
                return;

            var labels = GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .Where(x => x?.ItemOnGround?.Metadata?.StartsWith("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.Ordinal) == true)
                .Select(x => (GroundLabel: x, EncounterLabel: x.Label.AsObject<Expedition2EncounterLabel>()))
                .Where(x => x.EncounterLabel != null)
                .ToList();

            if (labels.Count == 0)
                return;

            var areaLevel = GameController.IngameState.Data.CurrentAreaLevel;
            var allRecipes = GameController.Files.Expedition2Recipes.EntriesList.ToLookup(x => x.RuneCountRequired);
            var runeWeights = GameController.Files.Expedition2RunesWeights.EntriesList;
            var index = 1;

            foreach (var (groundLabel, encounterLabel) in labels)
            {
                var entity = groundLabel.ItemOnGround;
                if (entity == null)
                    continue;

                var allowedRuneCounts = runeWeights
                    .Where(x => x.RuneSlot - 1 == encounterLabel.FixedRunePosition)
                    .Where(x => x.Rune.Equals(encounterLabel.FixedRune))
                    .Where(x => x.Level <= areaLevel)
                    .Select(x => x.SlotCount)
                    .ToHashSet();

                var rewards = allRecipes
                    .Where(x => x.Key <= encounterLabel.RuneCount)
                    .SelectMany(x => x)
                    .Where(x => allowedRuneCounts.Contains(x.RuneCountRequired))
                    .Where(x => x.MinLevelReq <= areaLevel && x.MaxLevelReq >= areaLevel)
                    .Where(x => x.Runes.ElementAtOrDefault(encounterLabel.FixedRunePosition)?.Equals(encounterLabel.FixedRune) == true)
                    .Select(ToPreOpenEntry)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .Where(x => Settings.ExpeditionModeShowZeroPriceRewards.Value || x.Value > 0)
                    .Where(x => Settings.ExpeditionModeMinimumValue.Value <= 0 || x.Value >= Settings.ExpeditionModeMinimumValue.Value)
                    .OrderByDescending(x => x.Value)
                    .Take(Math.Max(1, Settings.ExpeditionModeMaxRewardsPerEncounter.Value))
                    .ToList();

                if (rewards.Count == 0)
                    continue;

                var rect = encounterLabel.GetClientRect();
                var rawScreenPos = rect.BottomLeft;

                if (rect.Width <= 1 || rect.Height <= 1 || rawScreenPos.X <= 0 || rawScreenPos.Y <= 0)
                    rawScreenPos = Vector2.Zero;

                var grid = new Vector2(entity.GridPos.X, entity.GridPos.Y);

                cachedExpeditionModeEntries.Add(new ExpeditionModeEncounterEntry
                {
                    Index = index++,
                    LabelSource = encounterLabel,
                    ScreenPosition = rawScreenPos,
                    GridPosition = grid,
                    RuneCount = encounterLabel.RuneCount,
                    FixedRune = Convert.ToString(encounterLabel.FixedRune) ?? string.Empty,
                    Rewards = rewards
                });
            }

        }
        catch
        {
            
        }
    }


    private void DrawExpeditionModeTooltips()
    {
        if (!Settings.EnableExpeditionModeTooltip.Value)
            return;

        if (cachedExpeditionModeEntries.Count == 0)
            return;

        var fallbackY = Settings.ExpeditionModeTooltipFallbackY.Value;
        var fallbackX = Settings.ExpeditionModeTooltipFallbackX.Value;
        var fallbackUsed = false;

        foreach (var entry in cachedExpeditionModeEntries
                     .OrderBy(x => x.Index))
        {
            var rewards = entry.Rewards
                .OrderByDescending(x => x.Value)
                .ToList();

            if (Settings.ExpeditionModeTooltipBestOnly.Value)
                rewards = rewards.Take(1).ToList();
            else if (Settings.ExpeditionModeTooltipTopTwoOnly.Value)
                rewards = rewards.Take(2).ToList();
            else
                rewards = rewards.Take(Math.Max(1, Settings.ExpeditionModeMaxRewardsPerEncounter.Value)).ToList();

            if (rewards.Count == 0)
                continue;

            var hasCurrentPosition = TryGetCurrentVisibleEncounterBottomLeft(entry.LabelSource, out var pos);
            var useFallback = Settings.ExpeditionModeTooltipFallbackList.Value;

            if (!hasCurrentPosition)
            {
                if (!useFallback)
                    continue;

                pos = new Vector2(fallbackX, fallbackY);
                fallbackUsed = true;
            }
            else if (useFallback)
            {
                pos = new Vector2(fallbackX, fallbackY);
                fallbackUsed = true;
            }

            var y = pos.Y;
            var bg = Color.FromArgb(Settings.ExpeditionModeTooltipBackgroundOpacity.Value, 0, 0, 0);

            if (useFallback)
            {
                var headerSize = Graphics.DrawTextWithBackground(
                    $"Expedition #{entry.Index}  RuneCount {entry.RuneCount}",
                    new Vector2(pos.X, y),
                    Settings.ExpeditionModeHeaderColor,
                    bg);

                y += Math.Max(14, headerSize.Y);
            }

            for (var i = 0; i < rewards.Count; i++)
            {
                var reward = rewards[i];
                var prefix = i == 0 ? "#1 " : i == 1 ? "#2 " : string.Empty;
                var color = i == 0 ? Settings.TopPickColor : i == 1 ? Settings.SecondPickColor : Settings.FrameColor;
                var text = $"{prefix}{FormatRewardValue(reward.Value)}  {reward.Name}";
                var size = Graphics.DrawTextWithBackground(text, new Vector2(pos.X, y), color, bg);
                y += Math.Max(14, size.Y);
            }

            if (useFallback)
                fallbackY = (int)y + 8;
        }

        DrawExpeditionModeBestPickLine(fallbackY);

        if (Settings.DebugStats.Value)
        {
            Graphics.DrawTextWithBackground(
                $"ExpeditionMode tooltip entries: {cachedExpeditionModeEntries.Count}" + (fallbackUsed ? " fallback" : ""),
                new Vector2(4, 170),
                Color.White,
                Color.FromArgb(180, 0, 0, 0));
        }
    }

    private void DrawExpeditionModeBestPickLine(int fallbackY)
    {
        var best = cachedExpeditionModeEntries
            .Where(x => x.Rewards.Count > 0)
            .Select(x => new
            {
                Entry = x,
                Reward = x.Rewards.OrderByDescending(r => r.Value).First()
            })
            .OrderByDescending(x => x.Reward.Value)
            .FirstOrDefault();

        if (best == null)
            return;

        var bg = Color.FromArgb(Settings.ExpeditionModeTooltipBackgroundOpacity.Value, 0, 0, 0);

        
        var pos = new Vector2(
            Settings.ExpeditionModeTooltipFallbackX.Value,
            fallbackY);

        var text = $"BEST PICK EXPEDITION {best.Entry.Index} most valuable item : {FormatRewardValue(best.Reward.Value)}";
        Graphics.DrawTextWithBackground(text, pos, Settings.TopPickColor, bg);
    }

    private void DrawExpeditionModeWindow()
    {
        if (!Settings.EnableExpeditionMode.Value)
            return;

        ImGui.SetNextWindowSize(new Vector2(520, 520), ImGuiCond.FirstUseEver);

        if (!ImGui.Begin("RuneHighlighter ExpeditionMode"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"Detected encounters: {cachedExpeditionModeEntries.Count}");
        ImGui.TextDisabled("Lists rewards from all detected expedition encounters on this map.");
        ImGui.Separator();

        if (cachedExpeditionModeEntries.Count == 0)
        {
            ImGui.TextDisabled("No expedition encounters detected yet.");
            ImGui.End();
            return;
        }

        foreach (var entry in cachedExpeditionModeEntries
                     .OrderByDescending(x => x.Rewards.Count > 0 ? x.Rewards[0].Value : 0))
        {
            var best = entry.Rewards.Count > 0 ? entry.Rewards[0].Value : 0;
            var header = $"#{entry.Index}  Best {FormatRewardValue(best)}  RuneCount {entry.RuneCount}  Rune {entry.FixedRune}";

            if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
                continue;

            ImGui.TextDisabled($"Grid: {entry.GridPosition.X:0}, {entry.GridPosition.Y:0}");

            for (var i = 0; i < entry.Rewards.Count; i++)
            {
                var reward = entry.Rewards[i];
                var prefix = i == 0 ? "#1 " : i == 1 ? "#2 " : "   ";
                ImGui.Text($"{prefix}{FormatRewardValue(reward.Value)}  {reward.Name}");
            }

            ImGui.Separator();
        }

        ImGui.End();
    }

    private void DrawPreOpenPreview()
    {
        if (!Settings.EnablePreOpenPreview.Value)
            return;

        foreach (var draw in cachedPreOpenPreviewDraws)
        {
            
            
            
            
            if (!TryGetCurrentVisibleEncounterBottomLeft(draw.EncounterLabel, out var bottomLeft))
                continue;

            var pos = new Vector2(
                bottomLeft.X + Settings.PreviewOffsetX.Value,
                bottomLeft.Y + Settings.PreviewOffsetY.Value);

            if (!IsWithinViewport(pos))
                continue;

            DrawPreOpenPreviewText(pos, draw.Entries);
        }
    }

    private static Vector2 GetCurrentEncounterBottomLeft(Expedition2EncounterLabel? label, Vector2 fallback)
    {
        try
        {
            if (label is { IsValid: true })
            {
                var rect = label.GetClientRect();
                var bottomLeft = rect.BottomLeft;
                if (rect.Width > 1 && rect.Height > 1 && IsDrawableScreenPosition(bottomLeft))
                    return bottomLeft;
            }
        }
        catch
        {
        }

        return fallback;
    }


    private static int GetRecipeRewardCount(Expedition2Recipe recipe)
    {
        try
        {
            var prop = recipe.GetType().GetProperty("RewardCount");
            if (prop?.GetValue(recipe) is int i)
                return Math.Max(1, i);

            if (prop?.GetValue(recipe) is long l)
                return (int)Math.Max(1, l);

            if (prop?.GetValue(recipe) is double d)
                return (int)Math.Max(1, d);

            var method = recipe.GetType().GetMethod("RewardCount", Type.EmptyTypes);
            if (method?.Invoke(recipe, null) is int mi)
                return Math.Max(1, mi);

            if (method?.Invoke(recipe, null) is long ml)
                return (int)Math.Max(1, ml);

            if (method?.Invoke(recipe, null) is double md)
                return (int)Math.Max(1, md);
        }
        catch
        {
        }

        return 1;
    }

    private PreOpenPreviewEntry? ToPreOpenEntry(Expedition2Recipe recipe)
    {
        if (recipe == null)
            return null;

        var rawName = string.IsNullOrWhiteSpace(recipe.Description) ? recipe.Reward?.BaseName : recipe.Description;
        if (string.IsNullOrWhiteSpace(rawName))
            return null;

        var count = GetRecipeRewardCount(recipe);
        var name = GetDumpBasedDisplayName(rawName, count);

        if (!TryGetDisplayPriceForName(name, out var unitPrice) &&
            !TryGetDisplayPriceForName(rawName, out unitPrice))
            unitPrice = 0;

        return new PreOpenPreviewEntry
        {
            Name = name,
            Count = 1,
            Value = unitPrice * Math.Max(1, count)
        };
    }

    private static string GetDumpBasedDisplayName(string rawName, int count)
    {
        rawName = CleanupText(rawName);
        if (string.IsNullOrWhiteSpace(rawName))
            return string.Empty;

        rawName = Regex.Replace(rawName, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase).Trim();

        var baseName = NormalizeRewardSelectionName(rawName);

        var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "animus exchange", "animus splinters", "bitter dead", "conductive runes", "detonate living", "eternal march", "explosive transmutation", "fragments of the past", "frostflame nova", "grim pillars", "hollow shell", "leylines", "powered by verisium", "refutation", "remnants of kalguur", "repulsion", "runic reprieve", "skyfall", "triskelion cascade", "verisium manifestations", "voltaic barrier", "wardbound minions" };
        if (skillNames.Contains(baseName))
            return "Skill: " + baseName;

        var supportNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "concussive runes", "fist of kalguur", "healing runes", "runeforged blades", "runic extraction", "runic infusion", "scouring flame" };
        if (supportNames.Contains(baseName))
            return "Support: " + baseName;

        return NormalizeRecipeDisplayName(rawName, count);
    }

    private void DrawPreOpenPreviewBestReward(Vector2 pos, PreOpenPreviewEntry entry)
    {
        var line1 = $"[BEST] {FormatRewardValue(entry.Value)}";
        var line2 = entry.Count > 1 ? $"{entry.Name} x{entry.Count}" : entry.Name;

        var color = Settings.TopPickColor;
        var bg = Color.FromArgb(225, 0, 0, 0);

        var size1 = Graphics.DrawTextWithBackground(line1, pos, color, bg);
        Graphics.DrawTextWithBackground(line2, new Vector2(pos.X, pos.Y + Math.Max(16, size1.Y)), color, bg);
    }

    private void DrawPreOpenPreviewText(Vector2 pos, List<PreOpenPreviewEntry> entries)
    {
        if (entries.Count == 1 && Settings.PreviewBestRewardOnly.Value)
        {
            DrawPreOpenPreviewBestReward(pos, entries[0]);
            return;
        }

        var y = pos.Y;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var prefix = i == 0 ? "#1 " : i == 1 ? "#2 " : string.Empty;
            var color = i == 0 ? Settings.TopPickColor : i == 1 ? Settings.SecondPickColor : Settings.FrameColor;
            var text = entry.Count > 1 ? $"{prefix}{FormatRewardValue(entry.Value)}  {entry.Name} x{entry.Count}" : $"{prefix}{FormatRewardValue(entry.Value)}  {entry.Name}";
            var size = Graphics.DrawTextWithBackground(text, new Vector2(pos.X, y), color, Color.FromArgb(Settings.PreviewBackgroundOpacity.Value, 0, 0, 0));
            y += Math.Max(14, size.Y);
        }
    }


    private int GetEffectiveUiScanIntervalMs()
    {
        var configured = Settings.ScanIntervalMs.Value;

        
        
        if (status.Contains("no reward panel under Expedition2Window", StringComparison.OrdinalIgnoreCase) ||
            mode.Contains("expedition-window-local-scan", StringComparison.OrdinalIgnoreCase))
            return Math.Max(configured, FailedExpeditionPanelScanMinIntervalMs);

        return Math.Max(configured, HealthyUiScanMinIntervalMs);
    }

    private void ScanPanel40()
    {
        visibleRewards.Clear();
        localScannedObjects = 0;
        candidatePanels = 0;
        scannedRows = 0;
        mode = "";

        if (!Settings.HighlightAllVisibleRewards.Value && enabledItemNames.Count == 0)
        {
            status = "no selected rewards";
            return;
        }

        
        
        
        var expeditionWindow = GetExpedition2Window();
        if (!IsExpeditionWindowOpen(expeditionWindow))
        {
            visibleRewards.Clear();
            directOptionCache.Clear();
            lastDirectOptionCount = -1;
            status = "Expedition2Window not open; UI scan skipped";
            mode = "idle-no-expedition-window";
            return;
        }

        ScanExpedition2WindowOptions(expeditionWindow);
    }

    private object? GetExpedition2Window()
    {
        var state = SafeGet(GameController, "IngameState") ?? SafeGet(SafeGet(GameController, "Game"), "IngameState");
        var ui = SafeGet(state, "IngameUi") ?? SafeGet(state, "IngameUI");
        return SafeGet(ui, "Expedition2Window");
    }

    private static bool IsExpeditionWindowOpen(object? expeditionWindow)
    {
        if (expeditionWindow == null)
            return false;

        if (SafeGet(expeditionWindow, "IsValid") is bool valid && !valid)
            return false;

        if (SafeGet(expeditionWindow, "IsVisible") is bool visible)
            return visible;

        return IsActuallyVisible(expeditionWindow);
    }

    private void ScanExpedition2WindowOptions(object expeditionWindow)
    {
        candidatePanels = 1;
        mode = "expedition2window-options-direct-cached";

        var optionsObj = SafeGet(expeditionWindow, "Options");
        if (optionsObj is not IEnumerable options)
        {
            status = "Expedition2Window.Options unavailable; UI scan skipped";
            return;
        }

        var now = DateTime.UtcNow;
        var filterSignature = BuildDirectOptionsFilterSignature();
        if (!string.Equals(filterSignature, lastDirectOptionFilterSignature, StringComparison.Ordinal))
        {
            directOptionCache.Clear();
            lastDirectOptionFilterSignature = filterSignature;
            lastDirectOptionCount = -1;
        }

        directOptionSeenDedup.Clear();
        var windowRect = GetRect(expeditionWindow);
        var optionCount = 0;
        var skippedNoRecipe = 0;
        var skippedFiltered = 0;
        var skippedRect = 0;
        var skippedUndiscovered = 0;
        var skippedOutOfWindow = 0;
        var cacheHits = 0;
        var cacheMisses = 0;

        directOptionScratchIds.Clear();

        var optionCollectionCount = options is ICollection collection ? collection.Count : -1;

        
        
        if (optionCollectionCount > MaxDirectOptionsForLiveScan)
        {
            visibleRewards.Clear();
            directOptionCache.Clear();
            lastDirectOptionCount = optionCollectionCount;
            scannedRows = optionCollectionCount;
            status = $"overview-panel-skip options={optionCollectionCount} threshold={MaxDirectOptionsForLiveScan}";
            return;
        }

        if (optionCollectionCount >= 0 && optionCollectionCount != lastDirectOptionCount)
        {
            directOptionCache.Clear();
            lastDirectOptionCount = optionCollectionCount;
        }

        foreach (var option in options)
        {
            if (option == null)
                continue;

            optionCount++;
            scannedRows++;

            
            
            
            var optionId = optionCount;
            directOptionScratchIds.Add(optionId);

            if (SafeGet(option, "IsValid") is bool valid && !valid)
                continue;

            if (SafeGet(option, "IsVisible") is bool visible && !visible)
                continue;

            if (SafeGet(option, "IsVisibleLocal") is bool visibleLocal && !visibleLocal)
                continue;

            var rect = GetRect(option);

            if (!directOptionCache.TryGetValue(optionId, out var cached))
            {
                cacheMisses++;
                var recipeObj = SafeGet(option, "Recipe");
                cached = BuildDirectOptionCacheEntry(optionId, rect, recipeObj, option);
                directOptionCache[optionId] = cached;
            }
            else
            {
                cacheHits++;
                UpdateDirectOptionCacheEntryRect(cached, rect);
            }

            cached.LastSeenUtc = now;

            if (cached.HasNoRecipe)
            {
                skippedNoRecipe++;
                continue;
            }

            if (cached.IsFiltered)
            {
                skippedFiltered++;
                continue;
            }

            if (cached.HasBadRect)
            {
                skippedRect++;
                continue;
            }

            if (IsGoodRect(windowRect) && !IsRectInsideWindow(cached.Rect, windowRect))
            {
                skippedOutOfWindow++;
                continue;
            }

            if (cached.IsUndiscovered)
            {
                skippedUndiscovered++;
                continue;
            }

            if (!cached.IsValidHighlight)
                continue;

            if (!string.IsNullOrEmpty(cached.DedupKey) && !directOptionSeenDedup.Add(cached.DedupKey))
                continue;

            visibleRewards.Add(cached.Reward);
        }

        PruneDirectOptionCache(now);

        status = visibleRewards.Count > 0
            ? $"ok-direct-options-cached options={optionCount} cacheHit={cacheHits} cacheMiss={cacheMisses} filtered={skippedFiltered} noRecipe={skippedNoRecipe} badRect={skippedRect} undiscovered={skippedUndiscovered} outOfWindow={skippedOutOfWindow}"
            : $"direct options found no highlightable rewards options={optionCount} cacheHit={cacheHits} cacheMiss={cacheMisses} filtered={skippedFiltered} noRecipe={skippedNoRecipe} badRect={skippedRect} undiscovered={skippedUndiscovered} outOfWindow={skippedOutOfWindow}";
    }


    private string BuildDirectOptionsFilterSignature()
    {
        return string.Join("|",
            enabledItemNames.Count,
            Settings.HighlightAllVisibleRewards.Value,
            Settings.HighlightMostValuableReward.Value,
            Settings.HighlightOnlyTopTwoPicks.Value,
            Settings.HighlightOnlyRewardsAboveValue.Value,
            Settings.MinimumValueToHighlight.Value,
            Settings.ShowPriceOnReward.Value,
            Settings.DisplayPricesInExaltedOrbs.Value,
            Settings.DisplayPricesInDivineOrbs.Value);
    }

    private DirectOptionCacheEntry BuildDirectOptionCacheEntry(int optionId, ExileCore2.Shared.RectangleF rect, object? recipeObj, object option)
    {
        var result = new DirectOptionCacheEntry
        {
            OptionId = optionId,
            RecipeId = 0,
            Rect = rect,
            HasBadRect = !IsGoodRect(rect),
            HasNoRecipe = recipeObj is not Expedition2Recipe
        };

        if (result.HasNoRecipe || recipeObj is not Expedition2Recipe recipe)
            return result;

        var entry = ToPreOpenEntry(recipe);
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name))
        {
            result.HasNoRecipe = true;
            return result;
        }

        if (!ShouldHighlightRecipeEntry(entry))
        {
            result.IsFiltered = true;
            return result;
        }

        result.IsUndiscovered = IsUndiscoveredOption(option);
        result.IsValidHighlight = !result.HasBadRect && !result.IsUndiscovered;
        result.Reward = new VisibleReward(
            rect,
            entry.Name,
            entry.Value / Math.Max(1, entry.Count),
            Math.Max(1, entry.Count),
            entry.Value);
        result.DedupKey = $"{optionId}:{NormalizeLooseRewardKey(entry.Name)}";
        return result;
    }

    private static void UpdateDirectOptionCacheEntryRect(DirectOptionCacheEntry entry, ExileCore2.Shared.RectangleF rect)
    {
        entry.Rect = rect;
        entry.HasBadRect = !IsGoodRect(rect);

        if (!entry.HasNoRecipe && !entry.IsFiltered && !entry.IsUndiscovered && entry.Reward.Text.Length > 0)
        {
            entry.Reward = entry.Reward with { Rect = rect };
            entry.IsValidHighlight = !entry.HasBadRect;
        }
        else
        {
            entry.IsValidHighlight = false;
        }
    }

    private void PruneDirectOptionCache(DateTime now)
    {
        if (directOptionCache.Count <= Math.Max(128, directOptionScratchIds.Count * 3))
            return;

        var removeBefore = now.AddSeconds(-3);
        foreach (var pair in directOptionCache.ToArray())
        {
            if (pair.Value.LastSeenUtc < removeBefore)
                directOptionCache.Remove(pair.Key);
        }
    }

    private static bool IsRectInsideWindow(ExileCore2.Shared.RectangleF rect, ExileCore2.Shared.RectangleF windowRect)
    {
        
        return rect.X >= windowRect.X - 2 &&
               rect.Y >= windowRect.Y - 2 &&
               rect.X <= windowRect.X + windowRect.Width + 2 &&
               rect.Y <= windowRect.Y + windowRect.Height + 2;
    }

    private static bool IsUndiscoveredOption(object option)
    {
        return ContainsUndiscoveredText(option, 3);
    }

    private static bool ContainsUndiscoveredText(object? element, int depth)
    {
        if (element == null || depth < 0)
            return false;

        var text = CleanupText(Convert.ToString(SafeGet(element, "TextNoTags") ?? SafeGet(element, "Text") ?? string.Empty) ?? string.Empty);
        if (text.Equals("Undiscovered", StringComparison.OrdinalIgnoreCase))
            return true;

        if (depth == 0)
            return false;

        foreach (var child in GetChildren(element))
        {
            if (ContainsUndiscoveredText(child, depth - 1))
                return true;
        }

        return false;
    }

    private bool ShouldHighlightRecipeEntry(PreOpenPreviewEntry entry)
    {
        var includeByFilter = Settings.HighlightAllVisibleRewards.Value ||
                              IsEnabledRewardText(entry.Name) ||
                              IsEnabledAlloyRewardText(entry.Name);

        var wantsPriceBasedHighlight =
            Settings.HighlightMostValuableReward.Value ||
            Settings.HighlightOnlyTopTwoPicks.Value ||
            Settings.HighlightOnlyRewardsAboveValue.Value;

        var includeByPriceMode = wantsPriceBasedHighlight && entry.Value > 0;

        if (!includeByFilter && !includeByPriceMode)
            return false;

        if (Settings.HighlightOnlyRewardsAboveValue.Value && entry.Value < Settings.MinimumValueToHighlight.Value)
            return false;

        return true;
    }



    private void RefreshPricesIfNeeded()
    {
        if (!Settings.EnablePriceApi.Value)
            return;

        var now = DateTime.UtcNow;

        if (!forcePriceRefreshRequested && nextAllowedPriceRefreshUtc > now)
            return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, Settings.PriceRefreshIntervalMinutes.Value));

        if (!forcePriceRefreshRequested && priceCache.Count > 0 && now - lastPriceRefreshUtc < interval)
            return;

        forcePriceRefreshRequested = false;

        if (priceRefreshRunning)
            return;

        priceRefreshRunning = true;
        priceStatus = "refreshing...";

        Task.Run(async () =>
        {
            try
            {
                await RefreshPricesAsync();
            }
            catch (HttpRequestException ex)
            {
                priceStatus = "refresh failed: " + ex.Message;
                nextAllowedPriceRefreshUtc = DateTime.UtcNow.AddMinutes(Settings.PriceApi429CooldownMinutes.Value);
                TryLoadPriceCacheFromDisk();
            }
            catch (Exception ex)
            {
                priceStatus = "refresh failed: " + ex.Message;
                nextAllowedPriceRefreshUtc = DateTime.UtcNow.AddMinutes(5);
                TryLoadPriceCacheFromDisk();
            }
            finally
            {
                priceRefreshRunning = false;
            }
        });
    }

    private async Task RefreshPricesAsync()
    {
        var league = await GetLeagueNameAsync();
        var rawPrices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new PoeNinjaPriceSnapshot
        {
            League = league,
            CreatedUtc = DateTime.UtcNow,
            DisplayInExaltedOrbs = Settings.DisplayPricesInExaltedOrbs.Value || Settings.DisplayPricesInDivineOrbs.Value
        };

        var exchangeTypes = Settings.PriceApiSafeMode.Value
            ? new[] { "Currency", "Runes", "Fragments", "Expedition", "UncutGems", "SoulCores", "Breach", "Ritual", "Verisium" }
            : new[]
            {
                "Currency", "Runes", "Fragments", "Expedition", "UncutGems", "SoulCores",
                "Breach", "Delirium", "Essences", "Ritual", "Abyss", "Verisium",
                "LineageSupportGems", "Idols"
            };

        var stashTypes = Settings.PriceApiSafeMode.Value
            ? Array.Empty<string>()
            : new[]
            {
                "UniqueAccessory", "UniqueArmour", "UniqueWeapon", "UniqueFlask",
                "UniqueJewel", "SkillGem", "Waystone", "Tablet"
            };

        lastDownloadedCategories = 0;
        lastFailedCategories = 0;

        foreach (var type in exchangeTypes)
        {
            var url = $"https://poe.ninja/poe2/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";

            try
            {
                var json = await DownloadPoeNinjaStringAsync(url);
                snapshot.RawJsonByCategory["exchange:" + type] = json;
                ParseExchangeOverview(json, rawPrices);
                lastDownloadedCategories++;
            }
            catch
            {
                lastFailedCategories++;
                throw;
            }

            await Task.Delay(Settings.PriceApiRequestDelayMs.Value);
        }

        foreach (var type in stashTypes)
        {
            var url = $"https://poe.ninja/poe2/api/economy/stash/current/item/overview?league={Uri.EscapeDataString(league)}&type={Uri.EscapeDataString(type)}";

            try
            {
                var json = await DownloadPoeNinjaStringAsync(url);
                snapshot.RawJsonByCategory["stash:" + type] = json;
                ParseStashOverview(json, rawPrices);
                lastDownloadedCategories++;
            }
            catch
            {
                lastFailedCategories++;
                throw;
            }

            await Task.Delay(Settings.PriceApiRequestDelayMs.Value);
        }

        UpdateRawCurrencyValues(rawPrices);

        snapshot.Prices = rawPrices;
        snapshot.ExaltedOrbRawValue = exaltedOrbRawValue;
        snapshot.DivineOrbRawValue = divineOrbRawValue;

        lock (priceLock)
        {
            rawPriceCache.Clear();
            foreach (var kv in rawPrices)
                rawPriceCache[kv.Key] = kv.Value;
        }

        ApplyDisplayPriceModeFromRawCache();

        lastPriceRefreshUtc = snapshot.CreatedUtc;
        priceStatus = $"loaded {rawPrices.Count} raw prices / {lastDownloadedCategories} categories for {league} ({GetPriceDisplayUnitLabel()})";
        SavePriceSnapshotToDisk(snapshot);
    }

    private async Task<string> DownloadPoeNinjaStringAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("RuneHighlighter/1.0 ExileCore2 plugin");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await priceHttpClient.SendAsync(request);

        if ((int)response.StatusCode == 429)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            nextAllowedPriceRefreshUtc = DateTime.UtcNow.Add(retryAfter ?? TimeSpan.FromMinutes(Settings.PriceApi429CooldownMinutes.Value));
            throw new HttpRequestException("429 Too Many Requests. Cooldown until " + nextAllowedPriceRefreshUtc.ToString("u"));
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static double FindRawPrice(Dictionary<string, double> prices, params string[] names)
    {
        foreach (var name in names)
        {
            if (prices.TryGetValue(name, out var value) && value > 0)
                return value;
        }

        foreach (var kv in prices)
        {
            foreach (var name in names)
            {
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && kv.Value > 0)
                    return kv.Value;

                if (kv.Key.Contains(name, StringComparison.OrdinalIgnoreCase) && kv.Value > 0)
                    return kv.Value;
            }
        }

        return 0;
    }


    private bool TryGetDisplayPriceForName(string name, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        foreach (var candidate in BuildPriceLookupCandidates(name))
        {
            lock (priceLock)
            {
                if (priceCache.TryGetValue(candidate, out value) && value > 0)
                    return true;
            }
        }

        return false;
    }

    private void UpdateRawCurrencyValues(Dictionary<string, double> rawPrices)
    {
        exaltedOrbRawValue = FindRawPrice(rawPrices, "Exalted Orb", "exalted-orb", "exalted_orb");
        divineOrbRawValue = FindRawPrice(rawPrices, "Divine Orb", "divine-orb", "divine_orb");
    }

    private Dictionary<string, double> BuildDisplayPricesFromRaw(Dictionary<string, double> rawPrices)
    {
        var prices = new Dictionary<string, double>(rawPrices, StringComparer.OrdinalIgnoreCase);

        UpdateRawCurrencyValues(rawPrices);

        if (Settings.DisplayPricesInDivineOrbs.Value)
        {
            if (divineOrbRawValue <= 0)
            {
                priceStatus = "divine mode failed: Divine Orb raw value not found in raw poe.ninja cache";
                return prices;
            }

            var keys = prices.Keys.ToList();
            foreach (var key in keys)
                prices[key] = prices[key] / divineOrbRawValue;

            prices["Divine Orb"] = 1.0;
            if (exaltedOrbRawValue > 0)
                prices["Exalted Orb"] = exaltedOrbRawValue / divineOrbRawValue;

            return prices;
        }

        if (Settings.DisplayPricesInExaltedOrbs.Value)
        {
            if (exaltedOrbRawValue <= 0)
            {
                priceStatus = "exalted mode failed: Exalted Orb raw value not found in raw poe.ninja cache";
                return prices;
            }

            var keys = prices.Keys.ToList();
            foreach (var key in keys)
                prices[key] = prices[key] / exaltedOrbRawValue;

            prices["Exalted Orb"] = 1.0;
            if (divineOrbRawValue > 0)
                prices["Divine Orb"] = divineOrbRawValue / exaltedOrbRawValue;

            return prices;
        }

        return prices;
    }

    private void ApplyDisplayPriceModeFromRawCache()
    {
        var unit = GetPriceDisplayUnitKey();

        Dictionary<string, double> rawCopy;
        lock (priceLock)
        {
            if (rawPriceCache.Count == 0)
                return;

            rawCopy = new Dictionary<string, double>(rawPriceCache, StringComparer.OrdinalIgnoreCase);
        }

        var displayPrices = BuildDisplayPricesFromRaw(rawCopy);

        lock (priceLock)
        {
            priceCache.Clear();
            foreach (var kv in displayPrices)
                priceCache[kv.Key] = kv.Value;

            appliedPriceUnitKey = unit;
        }
    }

    private void EnsureDisplayPriceModeApplied()
    {
        var unit = GetPriceDisplayUnitKey();
        if (string.Equals(unit, appliedPriceUnitKey, StringComparison.OrdinalIgnoreCase))
            return;

        ApplyDisplayPriceModeFromRawCache();
    }

    private void ParseExchangeOverview(string json, Dictionary<string, double> output)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            !doc.RootElement.TryGetProperty("lines", out var lines) ||
            items.ValueKind != JsonValueKind.Array ||
            lines.ValueKind != JsonValueKind.Array)
            return;

        var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var name = ReadString(item, "name");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                namesById[id] = name;
        }

        foreach (var line in lines.EnumerateArray())
        {
            var id = ReadString(line, "id");
            if (string.IsNullOrWhiteSpace(id) || !namesById.TryGetValue(id, out var name))
                continue;

            var value = ReadDouble(line, "primaryValue") ?? ReadDouble(line, "chaosEquivalent") ?? ReadDouble(line, "chaosValue");
            if (value == null || value <= 0)
                continue;

            AddPriceAlias(output, name.Trim(), value.Value);

            var detailsId = ReadString(line, "detailsId") ?? ReadString(line, "detailId");
            if (!string.IsNullOrWhiteSpace(detailsId))
                AddPriceAlias(output, detailsId.Trim(), value.Value);

            var payName = ReadString(line, "payCurrencyTypeName");
            if (!string.IsNullOrWhiteSpace(payName))
                AddPriceAlias(output, payName.Trim(), value.Value);

            var receiveName = ReadString(line, "receiveCurrencyTypeName");
            if (!string.IsNullOrWhiteSpace(receiveName))
                AddPriceAlias(output, receiveName.Trim(), value.Value);
        }
    }

    private void ParseStashOverview(string json, Dictionary<string, double> output)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            return;

        foreach (var line in lines.EnumerateArray())
        {
            var name = ReadString(line, "baseType") ?? ReadString(line, "name");
            var value = ReadDouble(line, "primaryValue") ?? ReadDouble(line, "chaosEquivalent") ?? ReadDouble(line, "chaosValue");

            if (string.IsNullOrWhiteSpace(name) || value == null || value <= 0)
                continue;

            AddPriceAlias(output, name.Trim(), value.Value);

            var detailsId = ReadString(line, "detailsId") ?? ReadString(line, "detailId");
            if (!string.IsNullOrWhiteSpace(detailsId))
                AddPriceAlias(output, detailsId.Trim(), value.Value);
        }
    }

    private static void AddPriceAlias(Dictionary<string, double> output, string name, double value)
    {
        if (string.IsNullOrWhiteSpace(name) || value <= 0)
            return;

        name = CleanupText(name);
        output[name] = value;

        var normalized = NormalizePriceKey(name);
        if (!string.IsNullOrWhiteSpace(normalized))
            output[normalized] = value;

        var isGreaterOrPerfectOrb =
            name.EndsWith(" Orb", StringComparison.OrdinalIgnoreCase) &&
            (name.StartsWith("Greater ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Perfect ", StringComparison.OrdinalIgnoreCase));

        
        
        if (!isGreaterOrPerfectOrb && name.Contains(" Rune", StringComparison.OrdinalIgnoreCase))
        {
            var noGreater = name.Replace("Greater ", "", StringComparison.OrdinalIgnoreCase).Trim();
            var noLesser = name.Replace("Lesser ", "", StringComparison.OrdinalIgnoreCase).Trim();
            output[noGreater] = value;
            output[noLesser] = value;
            output[noGreater.Replace(" Rune of ", " Rune ", StringComparison.OrdinalIgnoreCase)] = value;
            output[noGreater.Replace(" Rune", "", StringComparison.OrdinalIgnoreCase)] = value;
        }

        var pretty = name.Replace("-", " ").Replace("_", " ").Trim();
        if (!string.Equals(pretty, name, StringComparison.OrdinalIgnoreCase))
            output[pretty] = value;
        
        
        if (name.EndsWith(" Alloy", StringComparison.OrdinalIgnoreCase))
        {
            
            output[name.Replace(" ", "-").ToLowerInvariant()] = value;
            output[name.Replace(" ", "_").ToLowerInvariant()] = value;
            output[name.Replace(" ", "").ToLowerInvariant()] = value;
            output[name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim()] = value;
            output[name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant()] = value;
        }


    }

    private static string NormalizePriceKey(string name)
    {
        name = CleanupText(name);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\d+\s*x\s+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\(Level\s+\d+\)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(prop.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }


    private bool TryGetAlloyFallbackPrice(string name, out double price)
    {
        price = 0;

        var alloyNames = new[]
        {
            "Expansive Alloy",
            "Protective Alloy",
            "Adaptive Alloy",
            "Runic Alloy",
            "Cyclonic Alloy",
            "Prismatic Alloy",
            "Mystic Alloy",
            "Sovereign Alloy",
            "Celestial Alloy",
            "Transcendent Alloy",
            "The Runefather's Alloy",
            "The Runebinder's Alloy"
        };

        var clean = CleanupText(name);

        foreach (var alloy in alloyNames)
        {
            if (!clean.Contains(alloy, StringComparison.OrdinalIgnoreCase))
                continue;

            lock (priceLock)
            {
                if (priceCache.TryGetValue(alloy, out price) && price > 0)
                    return true;

                if (priceCache.TryGetValue(alloy.Replace(" ", "-").ToLowerInvariant(), out price) && price > 0)
                    return true;

                if (priceCache.TryGetValue(alloy.Replace(" ", "_").ToLowerInvariant(), out price) && price > 0)
                    return true;

                if (priceCache.TryGetValue(alloy.Replace(" ", "").ToLowerInvariant(), out price) && price > 0)
                    return true;
            }
        }

        return false;
    }

    private (double UnitPrice, int StackSize, double TotalValue) GetRewardPrice(string rewardText)
    {
        if (!Settings.EnablePriceApi.Value)
            return (0, 1, 0);

        var (stack, name) = SplitRewardStack(rewardText);
        if (string.IsNullOrWhiteSpace(name))
            return (0, stack, 0);

        var candidates = BuildPriceLookupCandidates(name);

        lock (priceLock)
        {
            foreach (var candidate in candidates)
            {
                if (priceCache.TryGetValue(candidate, out var price))
                {
                    lastPriceHits++;
                    return (price, stack, price * stack);
                }
            }
        }

        if (TryGetAlloyFallbackPrice(name, out var alloyPrice))
        {
            lastPriceHits++;
            return (alloyPrice, stack, alloyPrice * stack);
        }

        lastPriceMisses++;
        lastPriceMiss = name;
        return (0, stack, 0);
    }

    private static IEnumerable<string> BuildPriceLookupCandidates(string name)
    {
        name = StripRewardDisplayPrefix(CleanPriceLookupName(name));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yieldBuffer = new List<string>();

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            value = CleanupText(value).Trim();
            if (seen.Add(value))
                yieldBuffer.Add(value);
        }

        Add(name);
        Add(NormalizePriceKey(name));

        var isGreaterOrPerfectOrb =
            name.EndsWith(" Orb", StringComparison.OrdinalIgnoreCase) &&
            (name.StartsWith("Greater ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Perfect ", StringComparison.OrdinalIgnoreCase));

        
        
        
        if (!isGreaterOrPerfectOrb)
        {
            var withoutGreater = name.Replace("Greater ", "", StringComparison.OrdinalIgnoreCase).Trim();
            var withoutLesser = name.Replace("Lesser ", "", StringComparison.OrdinalIgnoreCase).Trim();
            var withoutPerfect = name.Replace("Perfect ", "", StringComparison.OrdinalIgnoreCase).Trim();

            Add(withoutGreater);
            Add(withoutLesser);
            Add(withoutPerfect);

            if (name.Contains(" Rune", StringComparison.OrdinalIgnoreCase))
            {
                if (name.Contains("Rune of Hollowing", StringComparison.OrdinalIgnoreCase))
                    Add(name.Replace("Rune of Hollowing", "Rune Hollowing", StringComparison.OrdinalIgnoreCase));
                Add(withoutGreater);
                Add(withoutLesser);
                Add(withoutGreater.Replace(" Rune of ", " Rune ", StringComparison.OrdinalIgnoreCase));
                Add(withoutGreater.Replace(" Rune", "", StringComparison.OrdinalIgnoreCase));
                Add(name.Replace(" Rune of ", " Rune ", StringComparison.OrdinalIgnoreCase));
                Add(name.Replace(" Rune", "", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (name.Contains("Orb of Transmutation", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Orb of Augmentation", StringComparison.OrdinalIgnoreCase))
        {
            Add(name.Replace("Orb of ", "Orb ", StringComparison.OrdinalIgnoreCase));
            Add(name.Replace("Greater Orb of ", "Greater Orb ", StringComparison.OrdinalIgnoreCase));
            Add(name.Replace("Perfect Orb of ", "Perfect Orb ", StringComparison.OrdinalIgnoreCase));
        }

        
        Add(name.Replace(" ", "-").ToLowerInvariant());
        Add(name.Replace(" ", "_").ToLowerInvariant());
        if (name.EndsWith(" Alloy", StringComparison.OrdinalIgnoreCase))
        {
            
            Add(name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim());
            Add(name.Replace(" ", "").ToLowerInvariant());
            Add(name.Replace(" ", "-").ToLowerInvariant());
            Add(name.Replace(" ", "_").ToLowerInvariant());
            Add(name.ToLowerInvariant());
            Add(name.Replace(" ", " ").Trim());
        }



        foreach (var item in yieldBuffer)
            yield return item;
    }

    private static (int Stack, string Name) SplitRewardStack(string rewardText)
    {
        rewardText = CleanupText(rewardText);

        var match = System.Text.RegularExpressions.Regex.Match(rewardText, @"^\s*(\d+)\s*x\s+(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var stack))
            return (Math.Max(1, stack), match.Groups[2].Value.Trim());

        return (1, rewardText);
    }


    private static string StripRewardDisplayPrefix(string name)
    {
        name = CleanupText(name);
        name = Regex.Replace(name, @"^(Skill|Support):\s*", "", RegexOptions.IgnoreCase).Trim();
        return name;
    }

    private static string CleanPriceLookupName(string name)
    {
        name = CleanupText(name);
        name = Regex.Replace(name, @"^\s*\d+\s*x\s+", "", RegexOptions.IgnoreCase).Trim();
        name = Regex.Replace(name, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase).Trim();
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+\(Level\s+\d+\)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private string GetLeagueNameManualOrFallback()
    {
        var configured = Convert.ToString(SafeGet(Settings.LeagueName, "Value")) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        if (!string.IsNullOrWhiteSpace(detectedLeagueName))
            return detectedLeagueName;

        var fromGame = TryGetLeagueFromGame();
        if (!string.IsNullOrWhiteSpace(fromGame))
            return fromGame;

        return "Standard";
    }

    private async Task<string> GetLeagueNameAsync()
    {
        var configured = Convert.ToString(SafeGet(Settings.LeagueName, "Value")) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            detectedLeagueName = configured.Trim();
            leagueDetectStatus = "manual";
            return detectedLeagueName;
        }

        var fromGame = TryGetLeagueFromGame();
        if (!string.IsNullOrWhiteSpace(fromGame))
        {
            detectedLeagueName = fromGame;
            leagueDetectStatus = "game";
            return detectedLeagueName;
        }

        if (!Settings.AutoDetectPoeNinjaLeague.Value)
        {
            detectedLeagueName = "Standard";
            leagueDetectStatus = "auto disabled fallback";
            return detectedLeagueName;
        }

        
        foreach (var url in new[]
        {
            "https://poe.ninja/poe2/api/economy/leagues",
            "https://poe.ninja/api/data/getindexstate",
            "https://poe.ninja/poe2/api/data/getindexstate"
        })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("RuneHighlighter/1.0 ExileCore2 plugin");
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await priceHttpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var league = TryParseLeagueFromJson(json);

                if (!string.IsNullOrWhiteSpace(league))
                {
                    detectedLeagueName = league;
                    leagueDetectStatus = "poe.ninja " + url;
                    return detectedLeagueName;
                }
            }
            catch (Exception ex)
            {
                leagueDetectStatus = "failed " + url + ": " + ex.Message;
            }
        }

        detectedLeagueName = "Standard";
        leagueDetectStatus = "fallback Standard";
        return detectedLeagueName;
    }

    private string TryGetLeagueFromGame()
    {
        try
        {
            var candidates = new[]
            {
                SafeGet(GameController, "League"),
                SafeGet(SafeGet(GameController, "Game"), "League"),
                SafeGet(SafeGet(GameController, "Area"), "League"),
                SafeGet(SafeGet(SafeGet(GameController, "Game"), "IngameState"), "League"),
                SafeGet(SafeGet(SafeGet(GameController, "Game"), "IngameState"), "CurrentLeague"),
                SafeGet(SafeGet(SafeGet(SafeGet(GameController, "Game"), "IngameState"), "ServerData"), "League"),
                SafeGet(SafeGet(SafeGet(SafeGet(GameController, "Game"), "IngameState"), "Data"), "League")
            };

            foreach (var candidate in candidates)
            {
                var text = Convert.ToString(candidate);
                if (IsValidLeagueName(text))
                    return text!.Trim();
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsValidLeagueName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();

        if (value.Equals("Standard", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Length < 3 || value.Length > 80)
            return false;

        if (value.Contains("System.", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private static string TryParseLeagueFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;

        foreach (var propName in new[] { "economyLeagues", "leagues", "currentLeagues" })
        {
            if (root.TryGetProperty(propName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var league = PickLeagueFromArray(arr);
                if (!string.IsNullOrWhiteSpace(league))
                    return league;
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            var league = PickLeagueFromArray(root);
            if (!string.IsNullOrWhiteSpace(league))
                return league;
        }

        return string.Empty;
    }

    private static string PickLeagueFromArray(JsonElement arr)
    {
        string first = string.Empty;

        foreach (var item in arr.EnumerateArray())
        {
            var name = ReadString(item, "name") ?? ReadString(item, "displayName") ?? ReadString(item, "id");
            if (!IsValidLeagueName(name))
                continue;

            first = string.IsNullOrWhiteSpace(first) ? name!.Trim() : first;

            var indexed = !item.TryGetProperty("indexed", out var indexedProp) || indexedProp.ValueKind != JsonValueKind.False;
            var hardcore = item.TryGetProperty("hardcore", out var hcProp) && hcProp.ValueKind == JsonValueKind.True;

            
            if (indexed && !hardcore && !name!.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                return name.Trim();
        }

        return first;
    }

    private string GetPriceDisplayUnitKey()
    {
        if (Settings.DisplayPricesInDivineOrbs.Value)
            return "divine";

        if (Settings.DisplayPricesInExaltedOrbs.Value)
            return "exalt";

        return "raw";
    }

    private string GetPriceDisplayUnitLabel()
    {
        if (Settings.DisplayPricesInDivineOrbs.Value)
            return "divine units";

        if (Settings.DisplayPricesInExaltedOrbs.Value)
            return "exalted units";

        return "raw poe.ninja units";
    }

    private string GetPriceDisplaySuffix()
    {
        if (Settings.DisplayPricesInDivineOrbs.Value)
            return " div";

        if (Settings.DisplayPricesInExaltedOrbs.Value)
            return " ex";

        return string.Empty;
    }

    private string FormatRewardValue(double value)
    {
        return $"{value:0.##}{GetPriceDisplaySuffix()}";
    }

    private TimeSpan GetPriceCacheAge()
    {
        if (lastPriceRefreshUtc == DateTime.MinValue)
            return TimeSpan.MaxValue;

        return DateTime.UtcNow - lastPriceRefreshUtc;
    }

    private string GetPriceCachePath()
    {
        var league = GetLeagueNameManualOrFallback();
        var safeLeague = System.Text.RegularExpressions.Regex.Replace(league, @"[^A-Za-z0-9_\-]+", "_");
        var dir = System.IO.Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "config",
            "RuneHighlighter",
            "PriceCache");

        System.IO.Directory.CreateDirectory(dir);

        return System.IO.Path.Combine(dir, $"{safeLeague}_raw.json");
    }

    private void SavePriceSnapshotToDisk(PoeNinjaPriceSnapshot snapshot)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            System.IO.File.WriteAllText(GetPriceCachePath(), JsonSerializer.Serialize(snapshot, options));
        }
        catch
        {
        }
    }

    private void TryLoadPriceCacheFromDisk()
    {
        try
        {
            var path = GetPriceCachePath();
            if (!System.IO.File.Exists(path))
                return;

            var loaded = JsonSerializer.Deserialize<PoeNinjaPriceSnapshot>(System.IO.File.ReadAllText(path));
            if (loaded?.Prices == null || loaded.Prices.Count == 0)
                return;

            lock (priceLock)
            {
                rawPriceCache.Clear();
                foreach (var kv in loaded.Prices)
                    rawPriceCache[kv.Key] = kv.Value;
            }

            detectedLeagueName = loaded.League;
            lastPriceRefreshUtc = loaded.CreatedUtc;
            exaltedOrbRawValue = loaded.ExaltedOrbRawValue;
            divineOrbRawValue = loaded.DivineOrbRawValue;
            ApplyDisplayPriceModeFromRawCache();
            priceStatus = $"loaded full JSON raw cache: {rawPriceCache.Count} prices / {loaded.RawJsonByCategory.Count} categories ({GetPriceDisplayUnitLabel()})";
        }
        catch
        {
        }
    }

    private IEnumerable<PanelCandidate> GetRewardRootCandidates(object root)
    {
        var yielded = new HashSet<int>();

        object? root1 = GetChild(root, 1);

        
        
        foreach (var candidate in EnumerateLikelyExpeditionWindowRoots(root, root1))
        {
            if (candidate.Element != null && IsExpedition2Window(candidate.Element) && yielded.Add(ReferenceIdentity(candidate.Element)))
                yield return candidate;
        }
    }

    private IEnumerable<PanelCandidate> EnumerateLikelyExpeditionWindowRoots(object root, object? root1)
    {
        
        foreach (var index in new[] { 39, 40, 41, 38, 42, 44 })
        {
            var candidate = GetChild(root, index);
            if (candidate != null)
                yield return new PanelCandidate(candidate, "root->" + index);

            candidate = GetChild(root1, index);
            if (candidate != null)
                yield return new PanelCandidate(candidate, "root->1->" + index);
        }

        
        for (var index = 30; index <= 50; index++)
        {
            var candidate = GetChild(root, index);
            if (candidate != null)
                yield return new PanelCandidate(candidate, "root->" + index);

            candidate = GetChild(root1, index);
            if (candidate != null)
                yield return new PanelCandidate(candidate, "root->1->" + index);
        }
    }

    private static bool IsExpedition2Window(object element)
    {
        var path = Convert.ToString(SafeGet(element, "PathFromRoot") ?? string.Empty) ?? string.Empty;
        if (path.Contains("Expedition2Window", StringComparison.OrdinalIgnoreCase))
            return true;

        
        var text = Convert.ToString(SafeGet(element, "TextNoTags") ?? SafeGet(element, "Text") ?? string.Empty) ?? string.Empty;
        if (text.Contains("Expedition", StringComparison.OrdinalIgnoreCase) && GetInt(element, "ChildCount") >= 3)
            return true;

        return false;
    }

    private IEnumerable<object> FindRewardPanelsUnderPanel40(object panel40)
    {
        var visited = new HashSet<int>();

        foreach (var element in WalkLimited(panel40, 0, Settings.Panel40LocalDepth.Value, visited))
        {
            localScannedObjects++;

            if (localScannedObjects > Settings.MaxLocalObjects.Value)
                yield break;

            if (IsRewardListPanel(element))
                yield return element;
        }
    }



    private bool IsEnabledAlloyRewardText(string text)
    {
        var clean = CleanupText(text);
        if (string.IsNullOrWhiteSpace(clean))
            return false;

        var alloyNames = new[]
        {
            "Expansive Alloy",
            "Protective Alloy",
            "Adaptive Alloy",
            "Runic Alloy",
            "Cyclonic Alloy",
            "Prismatic Alloy",
            "Mystic Alloy",
            "Sovereign Alloy",
            "Celestial Alloy",
            "Transcendent Alloy",
            "The Runefather's Alloy",
            "The Runebinder's Alloy"
        };

        foreach (var alloy in alloyNames)
        {
            if (!clean.Contains(alloy, StringComparison.OrdinalIgnoreCase))
                continue;

            
            if (enabledItemNames.Contains(alloy) || enabledItemNames.Contains("1x " + alloy) || enabledItemNames.Contains(clean))
                return true;
        }

        return false;
    }

    private bool IsEnabledRewardText(string text)
    {
        var clean = CleanupText(text);
        if (string.IsNullOrWhiteSpace(clean))
            return false;

        if (enabledItemNames.Contains(clean))
            return true;

        var normalized = NormalizeRewardSelectionName(clean);
        if (enabledItemNames.Contains(normalized))
            return true;

        if (clean.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase) ||
            clean.StartsWith("Support:", StringComparison.OrdinalIgnoreCase))
        {
            var withoutPrefix = Regex.Replace(clean, @"^(Skill|Support):\s*", "", RegexOptions.IgnoreCase).Trim();
            if (enabledItemNames.Contains(withoutPrefix) || enabledItemNames.Contains("1x " + withoutPrefix))
                return true;
        }

        
        if (enabledItemNames.Contains("Skill: " + normalized) || enabledItemNames.Contains("Support: " + normalized))
            return true;

        if (enabledItemNames.Contains("1x " + normalized))
            return true;

        if (enabledItemNames.Contains(NormalizeRecipeDisplayName(normalized, 1)))
            return true;

        var looseKey = NormalizeLooseRewardKey(clean);
        if (!string.IsNullOrWhiteSpace(looseKey) && enabledItemLooseKeys.Contains(looseKey))
            return true;

        var uniqueNormalized = Regex.Replace(clean, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase);
        if (enabledItemNames.Contains(uniqueNormalized) || enabledItemNames.Contains(NormalizeRewardSelectionName(uniqueNormalized)))
            return true;

        if (IsEnabledAlloyRewardText(clean))
            return true;

        return false;
    }

    private static string NormalizeRewardSelectionName(string text)
    {
        text = CleanupText(text);
        text = Regex.Replace(text, @"^\s*\d+\s*x\s+", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"^(Skill|Support):\s*", "", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase).Trim();
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return CleanupText(text);
    }

    private static string NormalizeRecipeDisplayName(string name, int count = 1)
    {
        name = CleanupText(name);
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        name = Regex.Replace(name, @"\[Rarity\|Unique\]", "Unique", RegexOptions.IgnoreCase).Trim();

        if (name.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Support:", StringComparison.OrdinalIgnoreCase))
            return name;

        if (Regex.IsMatch(name, @"^\d+\s*x\s+", RegexOptions.IgnoreCase))
            return CleanupText(name);

        
        if (name.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Krillson's Bay Key", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Verisium Pile", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "5x Random Currency", StringComparison.OrdinalIgnoreCase))
            return name;

        if (count > 1)
            return $"{count}x {name}";

        return "1x " + name;
    }


    private string ExtractRewardTextFromRow(object row, object? preferredTextElement = null)
    {
        var preferred = CleanupText(Convert.ToString(SafeGet(preferredTextElement, "TextNoTags") ?? SafeGet(preferredTextElement, "Text") ?? string.Empty) ?? string.Empty);
        if (IsStrongRewardText(preferred))
            return preferred;

        var candidates = new List<string>();
        CollectRewardTextCandidates(row, 5, candidates);

        return candidates
            .Select(CleanupText)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(IsStrongRewardText)
            .OrderByDescending(x => IsEnabledRewardText(x) || IsEnabledAlloyRewardText(x))
            .ThenByDescending(x => GetRewardPrice(x).TotalValue)
            .ThenByDescending(x => x.Length)
            .FirstOrDefault() ?? preferred;
    }

    private void CollectRewardTextCandidates(object? element, int depth, List<string> output)
    {
        if (element == null || depth < 0 || output.Count > 64)
            return;

        var text = CleanupText(Convert.ToString(SafeGet(element, "TextNoTags") ?? SafeGet(element, "Text") ?? string.Empty) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(text))
            output.Add(text);

        foreach (var child in GetChildren(element))
            CollectRewardTextCandidates(child, depth - 1, output);
    }

    private static bool IsStrongRewardText(string text)
    {
        text = CleanupText(text);

        if (string.IsNullOrWhiteSpace(text) || text.Length > 140)
            return false;

        if (text.Contains("Metadata/", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Art/Textures", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("Undiscovered", StringComparison.OrdinalIgnoreCase))
            return false;

        if (Regex.IsMatch(text, @"^\s*\d+\s*x\s+.+", RegexOptions.IgnoreCase))
            return true;

        return text.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase) ||
               text.StartsWith("Support:", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Rune", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Orb", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Alloy", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Flux", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Gem", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Unique", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Whetstone", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Scrap", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Etcher", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Bauble", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsRewardListPanel(object element)
    {
        var childCount = GetInt(element, "ChildCount");
        if (childCount < Settings.MinRewardPanelChildren.Value)
            return false;

        var selectedRows = 0;
        var rewardLikeRows = 0;
        var rowIndex = 0;

        foreach (var row in GetChildren(element))
        {
            if (rowIndex++ >= Settings.MaxRewardRows.Value)
                break;

            var textElement = FindTextElement(row, 5);
            var text = ExtractRewardTextFromRow(row, textElement);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (IsEnabledRewardText(text) || IsEnabledAlloyRewardText(text))
                selectedRows++;

            if (LooksLikeRewardText(text))
                rewardLikeRows++;

            if (selectedRows >= 1 || rewardLikeRows >= 3)
                return true;
        }

        return false;
    }


    private void ScanRewardPanelOptimized(object panel)
    {
        if (ShouldUsePreOpenCacheForUiHighlights())
        {
            if (TryUsePreOpenUiAnchorCache())
            {
                status = "ok-preopen-ui-anchor-cache";
                return;
            }

            
            
            
            if (!ShouldAllowHeavyUiTextScan())
            {
                status = "preopen cache active; waiting for UI anchor rescan";
                return;
            }

            lastHeavyUiTextScanUtc = DateTime.UtcNow;
            if (BuildPreOpenUiAnchorMap(panel))
            {
                status = "ok-preopen-ui-anchor-build";
                return;
            }

            status = "preopen cache active; no UI anchors matched";
            return;
        }

        lastHeavyUiTextScanUtc = DateTime.UtcNow;
        ScanRewardPanel(panel);
    }

    private bool TryUsePreOpenUiAnchorCache()
    {
        if (cachedPreOpenUiAnchors.Count == 0)
            return false;

        var added = 0;
        foreach (var anchor in cachedPreOpenUiAnchors)
        {
            if (anchor.Entry == null || anchor.RectSource == null)
                continue;

            if (!IsActuallyVisible(anchor.RectSource))
                continue;

            var rect = GetRect(anchor.RectSource);
            if (!IsGoodRect(rect))
                continue;

            visibleRewards.Add(new VisibleReward(
                rect,
                anchor.Entry.Name,
                anchor.Entry.Value / Math.Max(1, anchor.Entry.Count),
                Math.Max(1, anchor.Entry.Count),
                anchor.Entry.Value));
            added++;
        }

        return added > 0;
    }

    private bool BuildPreOpenUiAnchorMap(object panel)
    {
        cachedPreOpenUiAnchors.Clear();

        var rowIndex = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in GetChildren(panel))
        {
            if (rowIndex++ >= Settings.MaxRewardRows.Value)
                break;

            scannedRows++;
            if (!IsActuallyVisible(row))
                continue;

            
            
            var textElement = FindTextElement(row, 2);
            var text = ExtractRewardTextFromRowPreOpen(row, textElement);

            if (string.IsNullOrWhiteSpace(text) || !TryGetPreOpenReward(text, out var entry))
                continue;

            var rectSource = Settings.DrawFullRow.Value || textElement == null ? row : textElement;
            var rect = GetRect(rectSource);
            if (!IsGoodRect(rect) && textElement != null)
            {
                rectSource = textElement;
                rect = GetRect(rectSource);
            }

            if (!IsGoodRect(rect))
                continue;

            var key = $"{Math.Round(rect.X)}:{Math.Round(rect.Y)}:{NormalizeLooseRewardKey(entry.Name)}";
            if (!seen.Add(key))
                continue;

            cachedPreOpenUiAnchors.Add(new PreOpenUiAnchor
            {
                RectSource = rectSource,
                Entry = entry
            });

            visibleRewards.Add(new VisibleReward(
                rect,
                entry.Name,
                entry.Value / Math.Max(1, entry.Count),
                Math.Max(1, entry.Count),
                entry.Value));
        }

        return cachedPreOpenUiAnchors.Count > 0;
    }

    private string ExtractRewardTextFromRowPreOpen(object row, object? preferredTextElement)
    {
        var preferred = CleanupText(Convert.ToString(SafeGet(preferredTextElement, "TextNoTags") ?? SafeGet(preferredTextElement, "Text") ?? string.Empty) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(preferred) && TryGetPreOpenReward(preferred, out _))
            return preferred;

        return FindPreOpenRewardTextCandidate(row, 2) ?? preferred;
    }

    private string? FindPreOpenRewardTextCandidate(object? element, int depth)
    {
        if (element == null || depth < 0)
            return null;

        var text = CleanupText(Convert.ToString(SafeGet(element, "TextNoTags") ?? SafeGet(element, "Text") ?? string.Empty) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(text) && TryGetPreOpenReward(text, out _))
            return text;

        foreach (var child in GetChildren(element))
        {
            var result = FindPreOpenRewardTextCandidate(child, depth - 1);
            if (!string.IsNullOrWhiteSpace(result))
                return result;
        }

        return null;
    }

    private void ScanRewardPanel(object panel)
    {
        var rowIndex = 0;

        foreach (var row in GetChildren(panel))
        {
            if (rowIndex++ >= Settings.MaxRewardRows.Value)
                break;

            scannedRows++;
            var textElement = FindTextElement(row, 5);
            var text = ExtractRewardTextFromRow(row, textElement);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var itemName = NormalizeForFilter(text);
            var isSelected = IsEnabledRewardText(text) || IsEnabledAlloyRewardText(text);
            var isRewardLike = LooksLikeRewardText(text);

            if (!isRewardLike)
                continue;

            var priceInfo = GetRewardPrice(text);
            var wantsPriceBasedHighlight =
                Settings.HighlightMostValuableReward.Value ||
                Settings.HighlightOnlyTopTwoPicks.Value ||
                Settings.HighlightOnlyRewardsAboveValue.Value;

            var includeByFilter = Settings.HighlightAllVisibleRewards.Value || isSelected;
            var includeByPriceMode = wantsPriceBasedHighlight && priceInfo.TotalValue > 0;

            if (!includeByFilter && !includeByPriceMode)
                continue;

            if (!IsActuallyVisible(row) && !IsActuallyVisible(textElement))
                continue;

            var rectSource = Settings.DrawFullRow.Value || textElement == null ? row : textElement;
            var rect = GetRect(rectSource);

            if (!IsGoodRect(rect) && textElement != null)
                rect = GetRect(textElement);

            if (!IsGoodRect(rect))
                continue;

            if (visibleRewards.Any(x => SameRect(x.Rect, rect) ||
                (string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase) &&
                 Math.Abs(x.Rect.Y - rect.Y) < 20 &&
                 Math.Abs(x.Rect.X - rect.X) < 40)))
                continue;

            visibleRewards.Add(new VisibleReward(rect, text, priceInfo.UnitPrice, priceInfo.StackSize, priceInfo.TotalValue));
        }
    }

    private object? GetPrimaryRoot()
    {
        var game = SafeGet(GameController, "Game");
        var state1 = SafeGet(GameController, "IngameState");
        var state2 = SafeGet(game, "IngameState");
        var ui1 = SafeGet(state1, "IngameUi") ?? SafeGet(state1, "IngameUI");
        var ui2 = SafeGet(state2, "IngameUi") ?? SafeGet(state2, "IngameUI");

        return SafeGet(ui1, "Root") ?? SafeGet(ui2, "Root") ?? ui1 ?? ui2;
    }

    private static object? GetByPath(object? root, int[] path)
    {
        object? current = root;

        foreach (var index in path)
        {
            current = GetChild(current, index);
            if (current == null)
                return null;
        }

        return current;
    }

    private static object? GetChild(object? obj, int index)
    {
        var i = 0;
        foreach (var child in GetChildren(obj))
        {
            if (i == index)
                return child;

            i++;
        }

        return null;
    }

    private static object? FindTextElement(object? element, int maxDepth)
    {
        if (element == null || maxDepth < 0)
            return null;

        var text = CleanupText(Convert.ToString(SafeGet(element, "TextNoTags") ?? SafeGet(element, "Text") ?? string.Empty) ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(text))
            return element;

        foreach (var child in GetChildren(element))
        {
            var found = FindTextElement(child, maxDepth - 1);
            if (found != null)
                return found;
        }

        return null;
    }

    private static IEnumerable<object> WalkLimited(object root, int depth, int maxDepth, HashSet<int> visited)
    {
        if (root == null || depth > maxDepth)
            yield break;

        var id = ReferenceIdentity(root);
        if (!visited.Add(id))
            yield break;

        yield return root;

        foreach (var child in GetChildren(root))
        {
            foreach (var item in WalkLimited(child, depth + 1, maxDepth, visited))
                yield return item;
        }
    }

    private static IEnumerable<object> GetChildren(object? obj)
    {
        if (obj == null)
            yield break;

        var children = SafeGet(obj, "Children") ?? SafeGet(obj, "Childrens");
        if (children is IEnumerable enumerable && children is not string)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                    yield return item;
            }
        }
    }


    private string NormalizeForFilter(string text)
    {
        return CleanupText(text);
    }

    private static bool LooksLikeRewardText(string text)
    {
        text = CleanupText(text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 140)
            return false;

        if (text.Contains("Metadata/", StringComparison.OrdinalIgnoreCase) || text.Contains("Art/Textures", StringComparison.OrdinalIgnoreCase))
            return false;

        return System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*\d+\s*x\s+.+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            || text.StartsWith("Skill:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Support:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Uncut Support Gem", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Uncut Skill Gem", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unique", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Rune", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Orb", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Flux", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Whetstone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Scrap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Etcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsActuallyVisible(object? element)
    {
        if (element == null)
            return false;

        if (SafeGet(element, "IsActive") is bool active && !active)
            return false;

        if (SafeGet(element, "IsVisible") is bool visible)
            return visible;

        return false;
    }

    private static ExileCore2.Shared.RectangleF GetRect(object? obj)
    {
        if (obj == null)
            return new ExileCore2.Shared.RectangleF();

        var rectObj = Invoke(obj, "GetClientRect") ?? SafeGet(obj, "GetClientRectCache") ?? SafeGet(obj, "ClientRect") ?? SafeGet(obj, "Rect");

        if (rectObj != null)
        {
            var x = SafeFloat(rectObj, "X");
            var y = SafeFloat(rectObj, "Y");
            var w = SafeFloat(rectObj, "Width");
            var h = SafeFloat(rectObj, "Height");
            if (w > 0 && h > 0)
                return new ExileCore2.Shared.RectangleF(x, y, w, h);
        }

        var pos = SafeGet(obj, "Position");
        var width = SafeFloat(obj, "Width");
        var height = SafeFloat(obj, "Height");

        if (pos != null && width > 0 && height > 0)
            return new ExileCore2.Shared.RectangleF(SafeFloat(pos, "X"), SafeFloat(pos, "Y"), width, height);

        return new ExileCore2.Shared.RectangleF();
    }

    private static bool IsGoodRect(ExileCore2.Shared.RectangleF rect)
    {
        return rect.Width >= 20 && rect.Height >= 10 && rect.Width <= 1200 && rect.Height <= 180 && rect.X >= -50 && rect.Y >= -50;
    }

    private static bool SameRect(ExileCore2.Shared.RectangleF a, ExileCore2.Shared.RectangleF b)
    {
        return Math.Abs(a.X - b.X) < 2 && Math.Abs(a.Y - b.Y) < 2 && Math.Abs(a.Width - b.Width) < 2 && Math.Abs(a.Height - b.Height) < 2;
    }

    private static string CleanupText(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        return text;
    }

    private static object? SafeGet(object? obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var type = obj.GetType();
            var member = ReflectionMemberCache.GetOrAdd((type, name), static key =>
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var prop = key.Type.GetProperty(key.Name, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return prop;

                return key.Type.GetField(key.Name, flags) ?? ReflectionCacheMiss;
            });

            return member switch
            {
                PropertyInfo prop => prop.GetValue(obj),
                FieldInfo field => field.GetValue(obj),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static object? Invoke(object? obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var type = obj.GetType();
            var method = ReflectionMethodCache.GetOrAdd((type, name), static key =>
                key.Type.GetMethod(
                    key.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null,
                    types: Type.EmptyTypes,
                    modifiers: null) ?? ReflectionCacheMiss);

            return method is MethodInfo methodInfo ? methodInfo.Invoke(obj, null) : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeBool(object? obj, string name)
    {
        var value = SafeGet(obj, name);

        if (value is bool b)
            return b;

        if (value != null && bool.TryParse(value.ToString(), out var parsed))
            return parsed;

        return false;
    }

    private static int GetInt(object? obj, string name)
    {
        var value = SafeGet(obj, name);
        if (value == null)
            return 0;

        try
        {
            return Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static float SafeFloat(object? obj, string name)
    {
        var value = SafeGet(obj, name);

        if (value == null)
            return 0;

        try
        {
            return Convert.ToSingle(value);
        }
        catch
        {
            return 0;
        }
    }

    private static int ReferenceIdentity(object obj)
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private readonly record struct VisibleReward(ExileCore2.Shared.RectangleF Rect, string Text, double UnitPrice, int StackSize, double TotalValue);
    private readonly record struct PanelCandidate(object Element, string Path);
}
