using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Cache;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Components;
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
using System.Runtime.CompilerServices;
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
    private const int MaxDirectOptionsForLiveScan = 4096;
    private const int MaxReasonableExpeditionRuneSockets = 512;

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

    private sealed class RerollAdvice
    {
        public string Text { get; set; } = string.Empty;
        public Color Color { get; set; } = Color.White;

        public bool HasText => !string.IsNullOrWhiteSpace(Text);
    }

    private sealed class ObservedRerollState
    {
        public string BaselineSignature { get; set; } = string.Empty;
        public string LastSignature { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public int SeenCount { get; set; }
        public bool UsedByObservedStateChange { get; set; }
    }

    private sealed class RuneEconomicScore
    {
        public string Signature { get; init; } = string.Empty;
        public double MaxValue { get; set; }
        public double WeightedMaxValue { get; set; }
        public double TotalValue { get; set; }
        public double TotalWeightedValue { get; set; }
        public int SeenCount { get; set; }

        public double AverageValue => SeenCount > 0 ? TotalValue / SeenCount : 0;
        public double AverageWeightedValue => SeenCount > 0 ? TotalWeightedValue / SeenCount : 0;
    }

    private sealed class RunePositionInsight
    {
        public string Text { get; set; } = string.Empty;
        public Color Color { get; set; } = Color.White;
        public double ScoreValue { get; set; }
        public double AggregateScore { get; set; }
        public int Slot { get; set; }
        public double SlotWeight { get; set; }
        public string Grade { get; set; } = string.Empty;
        public string PrimaryRune { get; set; } = string.Empty;
        public int ActiveWaves { get; set; }
        public int TotalWaves { get; set; }
        public int UniqueValuableRuneCount { get; set; }
        public int DuplicateIgnoredCount { get; set; }
        public bool HasText => !string.IsNullOrWhiteSpace(Text);
        public bool IsHighCover { get; set; }
        public bool IsMidCover { get; set; }
        public bool IsLowCover { get; set; }
        public bool HasCoverData { get; set; }
    }

    private sealed class RuneCoverageCandidate
    {
        public string Signature { get; init; } = string.Empty;
        public int Slot { get; init; }
        public int ActiveWaves { get; init; }
        public int TotalWaves { get; init; }
        public double SlotWeight { get; init; }
        public double BaseValue { get; init; }
        public double ScoreValue { get; init; }
        public bool IsTransferred { get; init; }
    }

    private static readonly RerollAdvice EmptyRerollAdvice = new();
    private static readonly RunePositionInsight EmptyRunePositionInsight = new();
    private readonly Dictionary<string, RuneEconomicScore> cachedRuneEconomicScores = new(StringComparer.OrdinalIgnoreCase);
    private string cachedRuneEconomicScoreSignature = string.Empty;

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
        public RerollAdvice Advice { get; init; } = EmptyRerollAdvice;
    }

    private sealed class StableScreenAnchor
    {
        public Vector2 Position { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public bool HasPosition { get; set; }
    }

    private const float StableTooltipHardSnapPixels = 260f;
    private const float StableTooltipLerpFactor = 0.35f;

    // ExpeditionMode is intentionally a fixed on-screen summary/list.
    // Near-rune previews are owned by Pre-Open Preview to avoid duplicated labels/highlights.
    private const bool ExpeditionModeAlwaysUseFallbackList = true;
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
        public Vector3 WorldPosition { get; set; }
        public bool HasWorldPosition { get; set; }
        public int RuneCount { get; set; }
        public string FixedRune { get; set; } = string.Empty;
        public List<PreOpenPreviewEntry> Rewards { get; set; } = new();
        public RerollAdvice Advice { get; set; } = EmptyRerollAdvice;
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
    private string rerollDebugLogPath = string.Empty;
    private string lastSettingsSignature = string.Empty;
    private bool profilerLogPathInitialized;
    private bool rerollDebugLogPathInitialized;
    private bool rerollDebugHeaderWritten;
    private int rerollDebugSnapshotCount;
    private readonly object rerollDebugLogLock = new();
    private readonly Dictionary<uint, string> rerollDebugLastSignatureByEntity = new();
    private readonly Dictionary<string, ObservedRerollState> observedRerollStateByKey = new(StringComparer.Ordinal);
    private DateTime lastObservedRerollStatePruneUtc = DateTime.MinValue;
    private static readonly TimeSpan ObservedRerollStateKeepAlive = TimeSpan.FromMinutes(15);
    private const int ObservedRerollStateStabilizeMs = 1500;
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
        InitializeRerollDebugLog();
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
                    DrawExpeditionModeEncounterLines();
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
                        var shouldRankTopPicks = Settings.HighlightMostValuableReward.Value || Settings.HighlightOnlyTopTwoPicks.Value;
                        var isTopPick = shouldRankTopPicks && reward.TotalValue > 0 && Math.Abs(reward.TotalValue - topValue) < 0.001;
                        var isSecondPick = shouldRankTopPicks && reward.TotalValue > 0 && !isTopPick && Math.Abs(reward.TotalValue - secondValue) < 0.001;

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


    private sealed class ReferenceObjectComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private void InitializeRerollDebugLog()
    {
        if (!Settings.RerollDebugLogEncounterChanges.Value)
            return;

        EnsureRerollDebugLogHeader();
    }

    private string GetRerollDebugLogPath()
    {
        if (!string.IsNullOrWhiteSpace(rerollDebugLogPath))
            return rerollDebugLogPath;

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            assemblyDirectory = Environment.CurrentDirectory;

        rerollDebugLogPath = Path.Combine(assemblyDirectory, "Profiler", "runehighlighter_reroll_debug.txt");
        return rerollDebugLogPath;
    }

    private void TryCreateRerollDebugDirectory(string logPath)
    {
        if (rerollDebugLogPathInitialized)
            return;

        rerollDebugLogPathInitialized = true;

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

    private void EnsureRerollDebugLogHeader()
    {
        if (rerollDebugHeaderWritten)
            return;

        lock (rerollDebugLogLock)
        {
            if (rerollDebugHeaderWritten)
                return;

            var path = GetRerollDebugLogPath();
            TryCreateRerollDebugDirectory(path);

            try
            {
                if (Settings.RerollDebugClearLogOnStart.Value && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }

            var sb = new StringBuilder(2048);
            sb.AppendLine("============================================================");
            sb.AppendLine($"RuneHighlighter reroll debug started UTC={DateTime.UtcNow:O}");
            sb.AppendLine($"Log path: {path}");
            sb.AppendLine("Purpose: capture Expedition encounter data before/after roll changes and detect whether next recipes are exposed in memory.");
            sb.AppendLine("Trigger: snapshot is written only when an encounter signature changes.");
            sb.AppendLine("============================================================");
            sb.AppendLine(BuildSettingsSnapshot("reroll debug settings"));
            sb.AppendLine();

            TryAppendRerollDebugText(sb.ToString());
            rerollDebugHeaderWritten = true;
        }
    }

    private void TryAppendRerollDebugText(string text)
    {
        var path = GetRerollDebugLogPath();
        TryCreateRerollDebugDirectory(path);

        try
        {
            lock (rerollDebugLogLock)
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
            var fallbackPath = Path.Combine(fallbackDirectory, "runehighlighter_reroll_debug.txt");
            lock (rerollDebugLogLock)
                File.AppendAllText(fallbackPath, text, Encoding.UTF8);
            rerollDebugLogPath = fallbackPath;
        }
        catch
        {
        }
    }

    private void MaybeWriteRerollDebugSnapshot(
        string source,
        Entity? entity,
        Expedition2EncounterLabel? encounterLabel,
        IReadOnlyList<PreOpenPreviewEntry> rawRewards,
        RerollAdvice advice,
        int areaLevel)
    {
        if (!Settings.RerollDebugLogEncounterChanges.Value)
            return;

        if (entity == null || encounterLabel == null)
            return;

        if (rerollDebugSnapshotCount >= Math.Max(1, Settings.RerollDebugMaxSnapshots.Value))
            return;

        try
        {
            EnsureRerollDebugLogHeader();

            var data = TryGetEncounterData(encounterLabel);
            var signature = BuildRerollDebugSignature(entity, encounterLabel, data, rawRewards, advice, areaLevel);
            var key = entity.Id;
            if (key == 0)
                key = unchecked((uint)Math.Abs(BuildStableObjectKey(encounterLabel).GetHashCode()));

            if (rerollDebugLastSignatureByEntity.TryGetValue(key, out var previousSignature) &&
                string.Equals(previousSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            var eventName = rerollDebugLastSignatureByEntity.ContainsKey(key) ? "CHANGED" : "FIRST_SEEN";
            rerollDebugLastSignatureByEntity[key] = signature;
            rerollDebugSnapshotCount++;

            var sb = new StringBuilder(Settings.RerollDebugDeepDump.Value ? 65536 : 8192);
            AppendRerollDebugSnapshotText(sb, source, eventName, entity, encounterLabel, data, rawRewards, advice, areaLevel, previousSignature, signature);
            TryAppendRerollDebugText(sb.ToString());
        }
        catch
        {
        }
    }

    private string BuildRerollDebugSignature(
        Entity entity,
        Expedition2EncounterLabel encounterLabel,
        Expedition2EncounterData? data,
        IReadOnlyList<PreOpenPreviewEntry> rawRewards,
        RerollAdvice advice,
        int areaLevel)
    {
        var rewardSignature = rawRewards == null || rawRewards.Count == 0
            ? "no-rewards"
            : string.Join(";", rawRewards.Take(8).Select(x => $"{x.Count}x{x.Name}:{x.Value:0.####}"));

        return string.Join("|",
            areaLevel,
            entity.Id,
            entity.GridPos.X,
            entity.GridPos.Y,
            BuildEncounterDataSignature(data),
            encounterLabel.RuneCount,
            encounterLabel.FixedRunePosition,
            BuildRuneSignature(SafeRead(() => encounterLabel.FixedRune)),
            rewardSignature,
            advice?.Text ?? string.Empty);
    }

    private static T? SafeRead<T>(Func<T?> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return default;
        }
    }

    private static object? TryReadObjectMember(object? value, string memberName)
    {
        if (value == null || string.IsNullOrWhiteSpace(memberName))
            return null;

        try
        {
            var type = value.GetType();
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(value);

            var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            return field?.GetValue(value);
        }
        catch
        {
            return null;
        }
    }

    private void AppendRerollDebugSnapshotText(
        StringBuilder sb,
        string source,
        string eventName,
        Entity entity,
        Expedition2EncounterLabel encounterLabel,
        Expedition2EncounterData? data,
        IReadOnlyList<PreOpenPreviewEntry> rawRewards,
        RerollAdvice advice,
        int areaLevel,
        string? previousSignature,
        string signature)
    {
        sb.AppendLine("============================================================");
        sb.AppendLine($"[{DateTime.UtcNow:O}] REROLL_DEBUG {eventName} source={source} snapshot={rerollDebugSnapshotCount}/{Math.Max(1, Settings.RerollDebugMaxSnapshots.Value)}");
        sb.AppendLine($"AreaLevel={areaLevel} EntityId={entity.Id} Grid=({entity.GridPos.X},{entity.GridPos.Y}) Path={SafeValueToString(TryReadObjectMember(entity, "Path"))}");
        sb.AppendLine($"PreviousSignature={previousSignature ?? "<none>"}");
        sb.AppendLine($"CurrentSignature ={signature}");
        sb.AppendLine();

        AppendEncounterSummary(sb, encounterLabel, data, rawRewards, advice, areaLevel);

        if (Settings.RerollDebugDeepDump.Value)
        {
            sb.AppendLine();
            sb.AppendLine("-- Deep object dump --");
            AppendObjectDump(sb, "EncounterLabel", encounterLabel, maxDepth: 2, maxLines: 220);
            AppendObjectDump(sb, "EncounterData", data, maxDepth: 4, maxLines: 520);
            AppendObjectDump(sb, "SelectedRecipe", ReadEncounterDataSelectedRecipe(data), maxDepth: 3, maxLines: 260);
            AppendObjectDump(sb, "FixedRune", ReadEncounterDataFixedRune(data), maxDepth: 3, maxLines: 160);
            AppendObjectDump(sb, "Entity", entity, maxDepth: 1, maxLines: 180);
        }

        sb.AppendLine();
    }

    private void AppendEncounterSummary(
        StringBuilder sb,
        Expedition2EncounterLabel encounterLabel,
        Expedition2EncounterData? data,
        IReadOnlyList<PreOpenPreviewEntry> rawRewards,
        RerollAdvice advice,
        int areaLevel)
    {
        var selectedRecipe = ReadEncounterDataSelectedRecipe(data);
        var selectedRunes = ReadRecipeRunes(selectedRecipe);
        var passedOn = ReadPassedOnRunePositions(data);
        var isRerolledText = TryReadBoolMember(data, "IsRerolled", out var isRerolled)
            ? isRerolled.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        var rollUsedDetected = IsEncounterRerollUsed(data, encounterLabel);

        sb.AppendLine("-- Summary --");
        sb.AppendLine($"Advice={advice?.Text ?? string.Empty}");
        sb.AppendLine($"DataSignature={BuildEncounterDataSignature(data)}");
        sb.AppendLine($"Data.RuneCount={ReadEncounterDataRuneCount(data)} Data.FixedRunePosition={ReadEncounterDataFixedRunePosition(data)} Data.FixedRune={BuildRuneSignature(ReadEncounterDataFixedRune(data))} Data.IsRerolled={isRerolledText} RollUsedDetected={rollUsedDetected}");
        sb.AppendLine($"Label.RuneCount={SafeRead(() => encounterLabel.RuneCount)} Label.FixedRunePosition={SafeRead(() => encounterLabel.FixedRunePosition)} Label.FixedRune={BuildRuneSignature(SafeRead(() => encounterLabel.FixedRune))}");
        sb.AppendLine($"PassedOnRunePositions={(passedOn.Count == 0 ? "-" : string.Join(",", passedOn.Select(x => (x + 1).ToString(CultureInfo.InvariantCulture))))}");
        sb.AppendLine($"SelectedRecipe={DescribeRecipe(selectedRecipe, areaLevel)}");
        sb.AppendLine($"SelectedRecipeRunes={FormatRuneList(selectedRunes)}");
        sb.AppendLine("TopCalculatedRewards=" + FormatDebugRewards(rawRewards, 12));
        AppendSuspiciousRecipeLikeMembers(sb, data, "EncounterData");
        AppendSuspiciousRecipeLikeMembers(sb, selectedRecipe, "SelectedRecipe");
    }

    private string DescribeRecipe(Expedition2Recipe? recipe, int areaLevel)
    {
        if (recipe == null)
            return "<null>";

        var entry = ToPreOpenEntry(recipe);
        var runes = ReadRecipeRunes(recipe);
        return string.Join(" | ",
            $"Type={recipe.GetType().Name}",
            $"Id={SafeValueToString(SafeRead(() => recipe.Id))}",
            $"RuneCountRequired={SafeValueToString(SafeRead(() => recipe.RuneCountRequired))}",
            $"Level={SafeValueToString(SafeRead(() => recipe.MinLevelReq))}-{SafeValueToString(SafeRead(() => recipe.MaxLevelReq))}",
            $"AreaLevel={areaLevel}",
            $"Reward={(entry == null ? "<no-price-entry>" : $"{entry.Count}x {entry.Name} value={FormatRewardValue(entry.Value)}")}",
            $"Runes={FormatRuneList(runes)}");
    }

    private static string FormatRuneList(IReadOnlyList<object> runes)
    {
        if (runes == null || runes.Count == 0)
            return "-";

        return string.Join(" ", runes.Select((rune, index) => $"S{index + 1}:{BuildRuneSignature(rune)}"));
    }

    private string FormatDebugRewards(IReadOnlyList<PreOpenPreviewEntry> rewards, int max)
    {
        if (rewards == null || rewards.Count == 0)
            return "-";

        return string.Join("; ", rewards
            .OrderByDescending(x => x.Value)
            .Take(Math.Max(1, max))
            .Select(x => $"{x.Count}x {x.Name} = {FormatRewardValue(x.Value)}"));
    }

    private void AppendSuspiciousRecipeLikeMembers(StringBuilder sb, object? value, string title)
    {
        if (value == null)
            return;

        try
        {
            var type = value.GetType();
            var members = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0)
                    continue;

                if (!LooksLikeRerollRelatedMember(prop.Name))
                    continue;

                object? memberValue = null;
                try { memberValue = prop.GetValue(value); } catch { }
                members.Add($"{prop.Name}={SafeValueToString(memberValue)}");
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!LooksLikeRerollRelatedMember(field.Name))
                    continue;

                object? memberValue = null;
                try { memberValue = field.GetValue(value); } catch { }
                members.Add($"{field.Name}={SafeValueToString(memberValue)}");
            }

            if (members.Count > 0)
            {
                sb.AppendLine($"{title}.RerollRelatedMembers:");
                foreach (var line in members.Take(80))
                    sb.AppendLine("  " + line);
            }
        }
        catch
        {
        }
    }

    private static bool LooksLikeRerollRelatedMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.IndexOf("recipe", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("roll", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("rune", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("transfer", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("pass", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("next", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("preview", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("selected", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildStableObjectKey(object? value)
    {
        if (value == null)
            return "null";

        return value.GetType().FullName + ":" + RuntimeHelpers.GetHashCode(value).ToString(CultureInfo.InvariantCulture);
    }

    private static string SafeValueToString(object? value)
    {
        if (value == null)
            return "<null>";

        try
        {
            if (value is string s)
                return s;

            if (value is bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

            if (value is DateTime dt)
                return dt.ToString("O", CultureInfo.InvariantCulture);

            if (value is Vector2 v2)
                return $"({v2.X:0.##},{v2.Y:0.##})";

            if (value is Vector3 v3)
                return $"({v3.X:0.##},{v3.Y:0.##},{v3.Z:0.##})";

            if (value is IEnumerable enumerable && value is not string)
            {
                var items = new List<string>();
                var count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 12)
                        break;
                    items.Add(SafeValueToString(item));
                }
                return "[" + string.Join(", ", items) + (count > 12 ? ", ..." : string.Empty) + "]";
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.GetType().Name;
        }
        catch
        {
            try { return value.GetType().FullName ?? value.GetType().Name; } catch { return "<error>"; }
        }
    }

    private void AppendObjectDump(StringBuilder sb, string title, object? value, int maxDepth, int maxLines)
    {
        var remainingLines = Math.Max(20, maxLines);
        var visited = new HashSet<object>(new ReferenceObjectComparer());
        sb.AppendLine($"[{title}]");
        AppendObjectDumpCore(sb, value, title, 0, Math.Max(0, maxDepth), ref remainingLines, visited);
        if (remainingLines <= 0)
            sb.AppendLine($"  ... dump truncated after {maxLines} lines");
    }

    private void AppendObjectDumpCore(StringBuilder sb, object? value, string path, int depth, int maxDepth, ref int remainingLines, HashSet<object> visited)
    {
        if (remainingLines <= 0)
            return;

        var indent = new string(' ', Math.Min(40, depth * 2));

        if (value == null)
        {
            sb.AppendLine($"{indent}{path}=<null>");
            remainingLines--;
            return;
        }

        var type = value.GetType();
        if (IsSimpleDebugValue(type))
        {
            sb.AppendLine($"{indent}{path}={SafeValueToString(value)}");
            remainingLines--;
            return;
        }

        if (!type.IsValueType)
        {
            if (!visited.Add(value))
            {
                sb.AppendLine($"{indent}{path}=<cycle {type.FullName}>");
                remainingLines--;
                return;
            }
        }

        if (value is IEnumerable enumerable && value is not string)
        {
            sb.AppendLine($"{indent}{path}: {type.FullName}");
            remainingLines--;
            var index = 0;
            foreach (var item in enumerable)
            {
                if (remainingLines <= 0)
                    return;
                if (index >= 32)
                {
                    sb.AppendLine($"{indent}  ... enumerable truncated");
                    remainingLines--;
                    return;
                }
                AppendObjectDumpCore(sb, item, $"[{index}]", depth + 1, maxDepth, ref remainingLines, visited);
                index++;
            }
            return;
        }

        sb.AppendLine($"{indent}{path}: {type.FullName}");
        remainingLines--;

        if (depth >= maxDepth)
            return;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        {
            if (remainingLines <= 0)
                return;
            if (prop.GetIndexParameters().Length > 0)
                continue;
            if (ShouldSkipDebugMember(prop.Name))
                continue;

            object? memberValue = null;
            var ok = false;
            try
            {
                memberValue = prop.GetValue(value);
                ok = true;
            }
            catch
            {
            }

            if (!ok)
            {
                sb.AppendLine($"{indent}  {prop.Name}=<getter failed>");
                remainingLines--;
                continue;
            }

            if (memberValue == null || IsSimpleDebugValue(memberValue.GetType()))
            {
                sb.AppendLine($"{indent}  {prop.Name}={SafeValueToString(memberValue)}");
                remainingLines--;
            }
            else
            {
                AppendObjectDumpCore(sb, memberValue, prop.Name, depth + 1, maxDepth, ref remainingLines, visited);
            }
        }

        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
        {
            if (remainingLines <= 0)
                return;
            if (ShouldSkipDebugMember(field.Name))
                continue;

            object? memberValue = null;
            var ok = false;
            try
            {
                memberValue = field.GetValue(value);
                ok = true;
            }
            catch
            {
            }

            if (!ok)
            {
                sb.AppendLine($"{indent}  {field.Name}=<field read failed>");
                remainingLines--;
                continue;
            }

            if (memberValue == null || IsSimpleDebugValue(memberValue.GetType()))
            {
                sb.AppendLine($"{indent}  {field.Name}={SafeValueToString(memberValue)}");
                remainingLines--;
            }
            else
            {
                AppendObjectDumpCore(sb, memberValue, field.Name, depth + 1, maxDepth, ref remainingLines, visited);
            }
        }
    }

    private static bool IsSimpleDebugValue(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(Vector2) ||
               type == typeof(Vector3);
    }

    private static bool ShouldSkipDebugMember(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return string.Equals(name, "GameController", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "TheGame", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Owner", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Parent", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Root", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "Children", StringComparison.OrdinalIgnoreCase);
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
        sb.AppendLine($"RerollAdvisor: Enable={Settings.EnableRerollAdvisor.Value}, RerollBelow={Settings.RerollAdvisorRerollBelowValue.Value}, KeepFrom={Settings.RerollAdvisorKeepValue.Value}, LockFrom={Settings.RerollAdvisorLockValue.Value}, ShowTransferSlots={Settings.RerollAdvisorShowTransferSlots.Value}, CoverScore={Settings.RerollAdvisorUseRunePositionScore.Value}, ShowCoverScore={Settings.RerollAdvisorShowRuneScore.Value}, ShowUsedStatus={Settings.RerollAdvisorShowRollUsedStatus.Value}, HideAfterUsed={Settings.RerollAdvisorHideAfterRollUsed.Value}, TransferBonus={Settings.RerollAdvisorTransferBonusPercent.Value}");
        sb.AppendLine($"RerollDebug: Enable={Settings.RerollDebugLogEncounterChanges.Value}, DeepDump={Settings.RerollDebugDeepDump.Value}, ClearOnStart={Settings.RerollDebugClearLogOnStart.Value}, MaxSnapshots={Settings.RerollDebugMaxSnapshots.Value}");
        sb.AppendLine($"ExpeditionMode: Window={Settings.EnableExpeditionMode.Value}, Tooltip={Settings.EnableExpeditionModeTooltip.Value}, TooltipBestOnly={Settings.ExpeditionModeTooltipBestOnly.Value}, TooltipTop2Only={Settings.ExpeditionModeTooltipTopTwoOnly.Value}, MaxRewards={Settings.ExpeditionModeMaxRewardsPerEncounter.Value}, MinimumValue={Settings.ExpeditionModeMinimumValue.Value}, ShowZero={Settings.ExpeditionModeShowZeroPriceRewards.Value}, FallbackList=AlwaysOn");
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
            Settings.EnableRerollAdvisor.Value,
            Settings.RerollAdvisorRerollBelowValue.Value,
            Settings.RerollAdvisorKeepValue.Value,
            Settings.RerollAdvisorLockValue.Value,
            Settings.RerollAdvisorShowTransferSlots.Value,
            Settings.RerollAdvisorUseRunePositionScore.Value,
            Settings.RerollAdvisorShowRuneScore.Value,
            Settings.RerollAdvisorShowWaveCoverageDetails.Value,
            Settings.RerollAdvisorProtectHighCoverage.Value,
            Settings.RerollAdvisorShowRollUsedStatus.Value,
            Settings.RerollAdvisorHideAfterRollUsed.Value,
            Settings.RerollAdvisorTransferBonusPercent.Value,
            Settings.RerollDebugLogEncounterChanges.Value,
            Settings.RerollDebugDeepDump.Value,
            Settings.RerollDebugMaxSnapshots.Value,
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
            "Expedition draw" => "ExpeditionMode Window, ExpeditionMode Tooltip Overlay, Tooltip Best Reward Only, Tooltip Top 2 Only, Tooltip fixed list offsets/background, ExpeditionMode Draw Lines To Encounters",
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


    private static bool TryGetEntityWorldPosition(Entity? entity, out Vector3 position)
    {
        position = default;

        try
        {
            var render = entity?.GetComponent<Render>();
            if (render == null)
                return false;

            position = render.Pos;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetExpeditionModeLineTarget(ExpeditionModeEncounterEntry entry, float offscreenMargin, out Vector2 screenPosition)
    {
        screenPosition = default;

        try
        {
            if (entry.LabelSource != null && entry.LabelSource.IsVisible)
            {
                var rect = entry.LabelSource.GetClientRect();
                if (rect.Width > 1 && rect.Height > 1)
                {
                    screenPosition = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
                    return IsWithinLineViewport(screenPosition, offscreenMargin);
                }
            }

            if (IsWithinLineViewport(entry.ScreenPosition, offscreenMargin))
            {
                screenPosition = entry.ScreenPosition;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void DrawExpeditionModeEncounterLines()
    {
        if (!Settings.ExpeditionModeDrawLinesToEncounters.Value)
            return;

        if (cachedExpeditionModeEntries.Count == 0)
            return;

        try
        {
            var playerRender = GameController?.Player?.GetComponent<Render>();
            var camera = GameController?.IngameState?.Camera;
            if (playerRender == null || camera == null)
                return;

            var playerScreen = camera.WorldToScreen(playerRender.Pos);
            var ranked = cachedExpeditionModeEntries
                .Select(entry => new
                {
                    Entry = entry,
                    BestValue = entry.Rewards.Count > 0 ? entry.Rewards.Max(reward => reward.Value) : 0d
                })
                .OrderByDescending(x => x.BestValue)
                .ToList();

            var topPickEntry = ranked.Count > 0 && ranked[0].BestValue > 0 ? ranked[0].Entry : null;
            var secondPickEntry = ranked.Count > 1 && ranked[1].BestValue > 0 ? ranked[1].Entry : null;
            var drawOnlyBest = Settings.ExpeditionModeLinesOnlyBestPicks.Value;
            var thickness = Math.Max(1, Settings.ExpeditionModeLineThickness.Value);
            var offscreenMargin = Math.Max(0, Settings.ExpeditionModeLineOffscreenMargin.Value);

            foreach (var entry in cachedExpeditionModeEntries)
            {
                var isTopPick = ReferenceEquals(entry, topPickEntry);
                var isSecondPick = ReferenceEquals(entry, secondPickEntry);
                if (drawOnlyBest && !isTopPick && !isSecondPick)
                    continue;

                Vector2 targetScreen;
                if (!TryGetExpeditionModeLineTarget(entry, offscreenMargin, out targetScreen))
                {
                    if (!entry.HasWorldPosition)
                        continue;

                    targetScreen = camera.WorldToScreen(entry.WorldPosition);
                    if (!IsWithinLineViewport(targetScreen, offscreenMargin))
                        continue;
                }

                var color = isTopPick
                    ? Settings.TopPickColor.Value
                    : isSecondPick
                        ? Settings.SecondPickColor.Value
                        : Settings.FrameColor.Value;

                Graphics.DrawLine(playerScreen, targetScreen, thickness, color);
            }
        }
        catch
        {
        }
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

        ImGui.Separator();
        ImGui.TextDisabled("Reroll Advisor settings are shared with ExpeditionMode.");
        ImGui.TextDisabled("Configure them here once; ExpeditionMode uses the same values.");
        DrawToggle(Settings.EnableRerollAdvisor, "Enable Reroll Advisor");
        DrawToggle(Settings.RerollAdvisorShowTransferSlots, "Show Transfer Slots");
        DrawToggle(Settings.RerollAdvisorUseRunePositionScore, "Use Cover Score");
        DrawToggle(Settings.RerollAdvisorShowRuneScore, "Show Cover Score");
        DrawToggle(Settings.RerollAdvisorShowWaveCoverageDetails, "Show Wave Coverage Details");
        DrawToggle(Settings.RerollAdvisorProtectHighCoverage, "Protect High Coverage Runes");
        DrawToggle(Settings.RerollAdvisorShowRollUsedStatus, "Show Roll Used Status");
        DrawToggle(Settings.RerollAdvisorHideAfterRollUsed, "Hide After Roll Used");
        DrawIntSlider(Settings.RerollAdvisorTransferBonusPercent, "Transfer Bonus %");
        DrawIntSlider(Settings.RerollAdvisorRerollBelowValue, "Reroll Below Value");
        DrawIntSlider(Settings.RerollAdvisorKeepValue, "Keep From Value");
        DrawIntSlider(Settings.RerollAdvisorLockValue, "Lock From Value");
    }


    private void DrawExpeditionModeControls()
    {
        // Migrate old configs where this hidden option may have been saved as false.
        Settings.ExpeditionModeTooltipFallbackList.Value = true;

        DrawToggle(Settings.EnableExpeditionMode, "Enable ExpeditionMode Window");
        DrawToggle(Settings.EnableExpeditionModeTooltip, "Enable ExpeditionMode Tooltip Overlay");
        DrawToggle(Settings.ExpeditionModeTooltipBestOnly, "Tooltip Best Reward Only");
        DrawToggle(Settings.ExpeditionModeTooltipTopTwoOnly, "Tooltip Top 2 Only");
        DrawIntSlider(Settings.ExpeditionModeMaxRewardsPerEncounter, "Max Rewards Per Encounter");
        DrawIntSlider(Settings.ExpeditionModeMinimumValue, "Minimum Value");
        DrawToggle(Settings.ExpeditionModeShowZeroPriceRewards, "Show Zero Price / Unknown Rewards");
        ImGui.TextDisabled("ExpeditionMode tooltip is always rendered as a fixed on-screen list.");
        ImGui.TextDisabled("Near-rune previews/highlights are controlled only by Pre-Open Preview.");
        DrawIntSlider(Settings.ExpeditionModeTooltipFallbackX, "Tooltip List X");
        DrawIntSlider(Settings.ExpeditionModeTooltipFallbackY, "Tooltip List Y");
        DrawIntSlider(Settings.ExpeditionModeTooltipBackgroundOpacity, "Tooltip List Background Opacity");
        DrawColor(Settings.ExpeditionModeHeaderColor, "Expedition Header Color");

        ImGui.Separator();
        ImGui.TextDisabled("Reroll Advisor settings are configured in Pre-Open Preview.");
        ImGui.TextDisabled("Those settings also apply to ExpeditionMode.");

        ImGui.Separator();
        DrawToggle(Settings.ExpeditionModeDrawLinesToEncounters, "Draw Lines To Encounters");
        DrawToggle(Settings.ExpeditionModeLinesOnlyBestPicks, "Lines Only Best Picks");
        DrawIntSlider(Settings.ExpeditionModeLineThickness, "Line Thickness");
        DrawIntSlider(Settings.ExpeditionModeLineOffscreenMargin, "Line Offscreen Margin");

        ImGui.TextDisabled("Window lists all encounters. Tooltip List X/Y controls the fixed left-side overlay and BEST PICK line.");
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
        DrawToggle(Settings.RerollDebugLogEncounterChanges, "Reroll Debug: Log Encounter Changes");
        DrawToggle(Settings.RerollDebugDeepDump, "Reroll Debug: Deep Object Dump");
        DrawToggle(Settings.RerollDebugClearLogOnStart, "Reroll Debug: Clear Log On Start");
        DrawIntSlider(Settings.RerollDebugMaxSnapshots, "Reroll Debug: Max Snapshots");
        ImGui.TextWrapped($"Reroll Debug TXT: {GetRerollDebugLogPath()}");

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
        ImGui.TextWrapped($"Reroll Debug File: {GetRerollDebugLogPath()}");
        ImGui.Text($"Reroll Debug Snapshots This Session: {rerollDebugSnapshotCount}");
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
        text = Regex.Replace(text, @"\s+\(Level\s+\d+\)$", "", RegexOptions.IgnoreCase).Trim();
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

    private static bool IsFiniteScreenPosition(Vector2 position)
    {
        return !float.IsNaN(position.X) &&
               !float.IsNaN(position.Y) &&
               !float.IsInfinity(position.X) &&
               !float.IsInfinity(position.Y) &&
               Math.Abs(position.X) < 100000 &&
               Math.Abs(position.Y) < 100000;
    }

    private static bool IsProbablyInvalidOriginProjection(Vector2 position)
    {
        return Math.Abs(position.X) <= 2 && Math.Abs(position.Y) <= 2;
    }

    private static bool IsWithinLineViewport(Vector2 position, float margin)
    {
        if (!IsFiniteScreenPosition(position) || IsProbablyInvalidOriginProjection(position))
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

    private static bool IsStrictlyInsideViewport(Vector2 position, float edgeMargin = 12f)
    {
        if (!IsDrawableScreenPosition(position))
            return false;

        try
        {
            var displaySize = ImGui.GetIO().DisplaySize;
            if (displaySize.X > 0 && displaySize.Y > 0)
            {
                return position.X >= edgeMargin &&
                       position.Y >= edgeMargin &&
                       position.X <= displaySize.X - edgeMargin &&
                       position.Y <= displaySize.Y - edgeMargin;
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
        if ((previewNow - lastPreOpenPreviewCacheTime).TotalMilliseconds < 500)
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
                .Where(x => IsExpedition2Encounter(x?.ItemOnGround))
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

                var rawEntries = GetOrBuildPreOpenPreviewEntries(encounterLabel, entity, areaLevel, allRecipes, runeWeights, previewNow);
                var rawAdvice = BuildRerollAdvice(entity, encounterLabel, rawEntries, allRecipes, areaLevel);
                MaybeWriteRerollDebugSnapshot("PreOpen", entity, encounterLabel, rawEntries, rawAdvice, areaLevel);
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
                    Entries = entries,
                    Advice = rawAdvice
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
            Settings.PreviewMaxLines.Value,
            Settings.EnableRerollAdvisor.Value,
            Settings.RerollAdvisorRerollBelowValue.Value,
            Settings.RerollAdvisorKeepValue.Value,
            Settings.RerollAdvisorLockValue.Value,
            Settings.RerollAdvisorShowTransferSlots.Value,
            Settings.RerollAdvisorUseRunePositionScore.Value,
            Settings.RerollAdvisorShowRuneScore.Value,
            Settings.RerollAdvisorShowWaveCoverageDetails.Value,
            Settings.RerollAdvisorProtectHighCoverage.Value,
            Settings.RerollAdvisorShowRollUsedStatus.Value,
            Settings.RerollAdvisorHideAfterRollUsed.Value,
            Settings.RerollAdvisorTransferBonusPercent.Value,
            Settings.RerollDebugLogEncounterChanges.Value,
            Settings.RerollDebugDeepDump.Value,
            Settings.RerollDebugMaxSnapshots.Value,
            lastPriceRefreshUtc.Ticks);
    }

    private static Expedition2EncounterData? TryGetEncounterData(Expedition2EncounterLabel? encounterLabel)
    {
        try
        {
            return encounterLabel?.Data;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadEncounterDataRuneCount(Expedition2EncounterData? data)
    {
        try
        {
            var runeCount = data?.RuneCount ?? 0;
            return runeCount > 0 && runeCount <= MaxReasonableExpeditionRuneSockets ? runeCount : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int ReadEncounterDataFixedRunePosition(Expedition2EncounterData? data)
    {
        try
        {
            return data?.FixedRunePosition ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private static object? ReadEncounterDataFixedRune(Expedition2EncounterData? data)
    {
        try
        {
            return data?.FixedRune;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildEncounterDataSignature(Expedition2EncounterData? data)
    {
        if (data == null)
            return "no-data";

        var selectedRecipeId = string.Empty;
        var passedOn = string.Empty;
        try
        {
            selectedRecipeId = data.SelectedRecipe?.Id ?? string.Empty;
        }
        catch
        {
        }

        try
        {
            if (data.PassedOnRunePositions is { Count: > 0 })
                passedOn = string.Join(",", data.PassedOnRunePositions.OrderBy(x => x));
        }
        catch
        {
        }

        return string.Join(":",
            ReadEncounterDataRuneCount(data),
            ReadEncounterDataFixedRunePosition(data),
            BuildRuneSignature(ReadEncounterDataFixedRune(data)),
            IsEncounterRerollUsed(data) ? "used" : "unused",
            selectedRecipeId,
            passedOn);
    }

    private static bool TryReadBoolMember(object? value, string memberName, out bool result)
    {
        result = false;
        if (value == null)
            return false;

        try
        {
            var type = value.GetType();
            var prop = type.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            var memberValue = prop?.GetValue(value);

            if (memberValue == null)
            {
                var field = type.GetField(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                memberValue = field?.GetValue(value);
            }

            if (memberValue is bool b)
            {
                result = b;
                return true;
            }

            if (memberValue == null)
                return false;

            var text = Convert.ToString(memberValue, CultureInfo.InvariantCulture);
            return bool.TryParse(text, out result);
        }
        catch
        {
            result = false;
            return false;
        }
    }

    private static bool TryReadIntMember(object? value, string memberName, out int result)
    {
        result = 0;
        if (value == null)
            return false;

        try
        {
            var memberValue = TryReadObjectMember(value, memberName);
            if (memberValue == null)
                return false;

            switch (memberValue)
            {
                case int i:
                    result = i;
                    return true;
                case long l when l >= int.MinValue && l <= int.MaxValue:
                    result = (int)l;
                    return true;
                case uint ui when ui <= int.MaxValue:
                    result = (int)ui;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case bool b:
                    result = b ? 1 : 0;
                    return true;
            }

            var text = Convert.ToString(memberValue, CultureInfo.InvariantCulture);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
                return true;
        }
        catch
        {
            result = 0;
        }

        return false;
    }

    private static bool IsEncounterRerollUsed(Expedition2EncounterData? data, Expedition2EncounterLabel? encounterLabel = null)
    {
        return IsRerollUsedFromObject(data) || IsRerollUsedFromObject(encounterLabel);
    }

    private static bool IsRerollUsedFromObject(object? value)
    {
        if (value == null)
            return false;

        // ExileCore/PoE memory names have changed between builds. Check a compact list of
        // likely state flags instead of relying only on Data.IsRerolled.
        foreach (var memberName in new[]
                 {
                     "IsRerolled", "Rerolled", "WasRerolled", "HasRerolled", "HasBeenRerolled",
                     "RerollUsed", "UsedReroll", "RollUsed", "IsRollUsed", "WasRolled",
                     "ScrollUsed", "CurrencyUsed", "IsModified"
                 })
        {
            if (TryReadBoolMember(value, memberName, out var flag) && flag)
                return true;
        }

        foreach (var memberName in new[] { "RerollCount", "RerollsUsed", "RollsUsed", "RerollAttempts", "RollAttempts" })
        {
            if (TryReadIntMember(value, memberName, out var count) && count > 0)
                return true;
        }

        foreach (var memberName in new[] { "RemainingRerolls", "RerollsRemaining", "RollsRemaining", "AvailableRerolls", "RerollsAvailable" })
        {
            if (TryReadIntMember(value, memberName, out var remaining) && remaining <= 0)
                return true;
        }

        foreach (var memberName in new[] { "CanReroll", "CanBeRerolled", "IsRerollAvailable", "CanRoll" })
        {
            if (TryReadBoolMember(value, memberName, out var canReroll) && !canReroll)
                return true;
        }

        return false;
    }

    private bool IsEncounterRerollUsedByObservedState(
        Entity? entity,
        Expedition2EncounterLabel? encounterLabel,
        Expedition2EncounterData? data,
        IReadOnlyList<PreOpenPreviewEntry> entries)
    {
        if (encounterLabel == null)
            return false;

        var now = DateTime.UtcNow;
        PruneObservedRerollStates(now);

        var key = BuildObservedRerollStableKey(entity, encounterLabel);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var signature = BuildObservedRerollContentSignature(encounterLabel, data, entries);
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        if (!observedRerollStateByKey.TryGetValue(key, out var state))
        {
            state = new ObservedRerollState
            {
                BaselineSignature = signature,
                LastSignature = signature,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                SeenCount = 1
            };
            observedRerollStateByKey[key] = state;
            return false;
        }

        state.LastSeenUtc = now;
        state.SeenCount++;

        if (!string.Equals(state.BaselineSignature, signature, StringComparison.Ordinal))
        {
            var baselineAgeMs = (now - state.FirstSeenUtc).TotalMilliseconds;

            if (!state.UsedByObservedStateChange &&
                (baselineAgeMs < ObservedRerollStateStabilizeMs || state.SeenCount < 2))
            {
                // Let ExileCore/PoE settle lazy recipe fields after the label first appears.
                // A real currency reroll happens after a previously stable visible state changes.
                state.BaselineSignature = signature;
                state.FirstSeenUtc = now;
                state.SeenCount = 1;
                state.LastSignature = signature;
                return false;
            }

            state.UsedByObservedStateChange = true;
        }

        state.LastSignature = signature;
        return state.UsedByObservedStateChange;
    }

    private void PruneObservedRerollStates(DateTime now)
    {
        if (now - lastObservedRerollStatePruneUtc < TimeSpan.FromSeconds(20))
            return;

        lastObservedRerollStatePruneUtc = now;
        if (observedRerollStateByKey.Count == 0)
            return;

        var removeBefore = now - ObservedRerollStateKeepAlive;
        var deadKeys = observedRerollStateByKey
            .Where(x => x.Value.LastSeenUtc < removeBefore)
            .Select(x => x.Key)
            .ToList();

        foreach (var deadKey in deadKeys)
            observedRerollStateByKey.Remove(deadKey);
    }

    private static string BuildObservedRerollStableKey(Entity? entity, Expedition2EncounterLabel encounterLabel)
    {
        try
        {
            if (entity != null)
            {
                var entityId = entity.Id;
                if (entityId != 0)
                    return $"entity:{entityId}:grid:{entity.GridPos.X}:{entity.GridPos.Y}";

                return $"grid:{entity.GridPos.X}:{entity.GridPos.Y}";
            }
        }
        catch
        {
        }

        var identity = TryReadStableObjectIdentity(encounterLabel);
        if (!string.IsNullOrWhiteSpace(identity))
            return "label:" + identity;

        return "label-object:" + BuildStableObjectKey(encounterLabel);
    }

    private string BuildObservedRerollContentSignature(
        Expedition2EncounterLabel encounterLabel,
        Expedition2EncounterData? data,
        IReadOnlyList<PreOpenPreviewEntry> entries)
    {
        var selectedRecipe = ReadEncounterDataSelectedRecipe(data);
        var selectedRunes = ReadRecipeRunes(selectedRecipe);
        var passedOn = ReadPassedOnRunePositions(data);
        var rewardSignature = string.Empty;

        if (entries != null && entries.Count > 0)
        {
            rewardSignature = string.Join(";", entries
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .Select(x => $"{x.Count}x{CleanupText(x.Name)}"));
        }

        var selectedRecipeReward = string.Empty;
        if (selectedRecipe != null)
        {
            try
            {
                var entry = ToPreOpenEntry(selectedRecipe);
                if (entry != null)
                    selectedRecipeReward = $"{entry.Count}x{CleanupText(entry.Name)}";
            }
            catch
            {
            }
        }

        var runeSignature = selectedRunes.Count == 0
            ? string.Empty
            : string.Join(",", selectedRunes.Select(BuildRuneSignature));

        var selectedRecipeId = string.Empty;
        try
        {
            selectedRecipeId = selectedRecipe?.Id ?? string.Empty;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(selectedRecipeId) &&
            string.IsNullOrWhiteSpace(selectedRecipeReward) &&
            string.IsNullOrWhiteSpace(runeSignature) &&
            string.IsNullOrWhiteSpace(rewardSignature))
        {
            return string.Empty;
        }

        var contentSignature = string.Join("|",
            SafeValueToString(selectedRecipeId),
            selectedRecipeReward,
            runeSignature,
            ReadEncounterDataRuneCount(data),
            ReadEncounterDataFixedRunePosition(data),
            BuildRuneSignature(ReadEncounterDataFixedRune(data)),
            SafeRead(() => encounterLabel.RuneCount),
            SafeRead(() => encounterLabel.FixedRunePosition),
            BuildRuneSignature(SafeRead(() => encounterLabel.FixedRune)),
            passedOn.Count == 0 ? string.Empty : string.Join(",", passedOn),
            rewardSignature);

        return CleanupText(contentSignature);
    }

    private static List<int> ReadPassedOnRunePositions(Expedition2EncounterData? data)
    {
        try
        {
            if (data?.PassedOnRunePositions == null || data.PassedOnRunePositions.Count == 0)
                return new List<int>();

            return data.PassedOnRunePositions
                .Select(x => Convert.ToInt32(x, CultureInfo.InvariantCulture))
                .Where(x => x >= 0 && x <= MaxReasonableExpeditionRuneSockets)
                .Distinct()
                .OrderBy(x => x)
                .ToList();
        }
        catch
        {
            return new List<int>();
        }
    }

    private string BuildTransferSlotText(Expedition2EncounterData? data)
    {
        if (!Settings.RerollAdvisorShowTransferSlots.Value)
            return string.Empty;

        var positions = ReadPassedOnRunePositions(data);
        if (positions.Count == 0)
            return string.Empty;

        return "T:" + string.Join(",", positions.Select(x => (x + 1).ToString(CultureInfo.InvariantCulture)));
    }

    private RerollAdvice BuildRerollAdvice(
        Entity? entity,
        Expedition2EncounterLabel? encounterLabel,
        IReadOnlyList<PreOpenPreviewEntry> entries,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        int areaLevel)
    {
        if (!Settings.EnableRerollAdvisor.Value)
            return EmptyRerollAdvice;

        var bestValue = entries.Count > 0 ? entries.Max(x => x.Value) : 0;
        var data = TryGetEncounterData(encounterLabel);
        var rollUsed = IsEncounterRerollUsed(data, encounterLabel) ||
                       IsEncounterRerollUsedByObservedState(entity, encounterLabel, data, entries);

        if (rollUsed)
        {
            if (Settings.RerollAdvisorHideAfterRollUsed.Value || !Settings.RerollAdvisorShowRollUsedStatus.Value)
                return EmptyRerollAdvice;

            return new RerollAdvice
            {
                Text = "ROLL: USED",
                Color = Color.LightGray
            };
        }

        var transferText = BuildTransferSlotText(data);
        var coverInsight = BuildRunePositionInsight(data, allRecipes, areaLevel);

        var rerollBelow = Math.Max(0, Settings.RerollAdvisorRerollBelowValue.Value);
        var keepFrom = Math.Max(rerollBelow, Settings.RerollAdvisorKeepValue.Value);
        var lockFrom = Math.Max(keepFrom, Settings.RerollAdvisorLockValue.Value);

        var decision = DecideReroll(bestValue, coverInsight, rerollBelow, keepFrom, lockFrom);
        var text = "ROLL: " + decision.Text;

        if (coverInsight.HasText)
            text += " | " + coverInsight.Text;

        if (!string.IsNullOrWhiteSpace(transferText))
            text += " | " + transferText;

        return new RerollAdvice
        {
            Text = text,
            Color = decision.Color
        };
    }

    private (string Text, Color Color) DecideReroll(
        double bestValue,
        RunePositionInsight coverInsight,
        double rerollBelow,
        double keepFrom,
        double lockFrom)
    {
        var protectCoverage = Settings.RerollAdvisorProtectHighCoverage.Value;
        var hardRollBelow = Math.Max(1.0, rerollBelow * 0.25);

        if (bestValue <= 0 && !coverInsight.HasCoverData)
            return ("WAIT", Color.LightGray);

        if (bestValue >= lockFrom)
            return ("NO/LOCK", Settings.TopPickColor.Value);

        if (bestValue >= keepFrom)
            return (coverInsight.IsHighCover ? "NO/LOCK" : "NO/KEEP", Color.Lime);

        if (protectCoverage && coverInsight.IsHighCover)
        {
            // A high-value rune that is active in many monster waves is a real opportunity cost.
            // Do not show a confident ROLL YES even when the direct reward price is below threshold.
            return (bestValue <= rerollBelow ? "MAYBE/RISK" : "NO/KEEP", Color.Yellow);
        }

        if (protectCoverage && coverInsight.IsMidCover)
        {
            if (bestValue <= hardRollBelow)
                return ("MAYBE/RISK", Color.Yellow);

            return bestValue <= rerollBelow
                ? ("MAYBE", Color.Yellow)
                : ("NO/KEEP", Color.Lime);
        }

        if (bestValue <= hardRollBelow)
            return ("YES", Color.Orange);

        if (bestValue <= rerollBelow)
            return ("YES", Color.Orange);

        return ("MAYBE", Color.Yellow);
    }

    private RunePositionInsight BuildRunePositionInsight(
        Expedition2EncounterData? data,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        int areaLevel)
    {
        if (!Settings.RerollAdvisorUseRunePositionScore.Value)
            return EmptyRunePositionInsight;

        var transferSlots = new HashSet<int>(ReadPassedOnRunePositions(data));
        var selectedRecipe = ReadEncounterDataSelectedRecipe(data);
        var selectedRunes = ReadRecipeRunes(selectedRecipe);
        var runeCount = ReadEncounterDataRuneCount(data);

        if (runeCount <= 0)
            runeCount = selectedRecipe != null ? GetRecipeRuneCount(selectedRecipe, selectedRunes.Count) : 0;

        if (runeCount <= 0)
            runeCount = selectedRunes.Count;

        var candidates = new List<RuneCoverageCandidate>(selectedRunes.Count + 1);

        // Monster waves receive rune prefixes from left to right. Duplicates do not stack, so later
        // copies of the same rune are only relevant if they are the stronger effective occurrence.
        if (selectedRunes.Count > 0 && runeCount > 0)
        {
            for (var i = 0; i < selectedRunes.Count; i++)
                AddRuneCoverageCandidate(candidates, selectedRunes[i], i, Math.Max(runeCount, selectedRunes.Count), transferSlots);
        }

        if (candidates.Count == 0)
        {
            var fixedRune = ReadEncounterDataFixedRune(data);
            var fixedRunePosition = ReadEncounterDataFixedRunePosition(data);
            if (fixedRunePosition >= 0 && runeCount > 0)
                AddRuneCoverageCandidate(candidates, fixedRune, fixedRunePosition, runeCount, transferSlots);
        }

        if (candidates.Count == 0)
            return BuildCoverageInsight("LOW", Color.Orange, 0, 0, 0, true);

        var duplicateIgnoredCount = 0;
        var strongestByRune = new Dictionary<string, RuneCoverageCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Signature))
                continue;

            if (strongestByRune.TryGetValue(candidate.Signature, out var existing))
            {
                duplicateIgnoredCount++;
                if (candidate.ScoreValue > existing.ScoreValue)
                    strongestByRune[candidate.Signature] = candidate;
            }
            else
            {
                strongestByRune[candidate.Signature] = candidate;
            }
        }

        if (strongestByRune.Count == 0)
            return BuildCoverageInsight("LOW", Color.Orange, 0, 0, 0, true);

        var ordered = strongestByRune.Values
            .OrderByDescending(x => x.ScoreValue)
            .ThenBy(x => x.Slot)
            .ToList();

        var best = ordered[0];
        var aggregateScore = best.ScoreValue + ordered.Skip(1).Take(2).Sum(x => x.ScoreValue * 0.35);
        var graded = GradeCoverageScore(best, aggregateScore);

        return BuildCoverageInsight(
            graded.Grade,
            graded.Color,
            best.ScoreValue,
            best.Slot,
            best.SlotWeight,
            true,
            best.Signature,
            best.ActiveWaves,
            best.TotalWaves,
            aggregateScore,
            ordered.Count,
            duplicateIgnoredCount);
    }

    private void AddRuneCoverageCandidate(
        List<RuneCoverageCandidate> candidates,
        object? rune,
        int zeroBasedSlot,
        int runeCount,
        HashSet<int> transferSlots)
    {
        var signature = BuildRuneSignature(rune);
        var baseValue = GetRuneCoverageBaseScore(signature);
        if (string.IsNullOrWhiteSpace(signature) || baseValue <= 0 || runeCount <= 0)
            return;

        var slotWeight = GetRuneSlotWaveWeight(zeroBasedSlot, runeCount);
        var activeWaves = GetRuneSlotActiveWaveCount(zeroBasedSlot, runeCount);
        var totalWaves = GetRuneTotalWaveCount(runeCount);
        var isTransferred = transferSlots.Contains(zeroBasedSlot);

        if (isTransferred)
            slotWeight *= 1.0 + (Math.Max(0, Settings.RerollAdvisorTransferBonusPercent.Value) / 100.0);

        slotWeight = Math.Min(1.5, Math.Max(0.05, slotWeight));

        candidates.Add(new RuneCoverageCandidate
        {
            Signature = signature,
            Slot = zeroBasedSlot + 1,
            ActiveWaves = activeWaves,
            TotalWaves = totalWaves,
            SlotWeight = slotWeight,
            BaseValue = baseValue,
            ScoreValue = baseValue * slotWeight,
            IsTransferred = isTransferred
        });
    }

    private RunePositionInsight BuildCoverageInsight(
        string grade,
        Color color,
        double scoreValue,
        int slot,
        double slotWeight,
        bool hasCoverData,
        string primaryRune = "",
        int activeWaves = 0,
        int totalWaves = 0,
        double aggregateScore = 0,
        int uniqueValuableRuneCount = 0,
        int duplicateIgnoredCount = 0)
    {
        var normalizedGrade = string.IsNullOrWhiteSpace(grade) ? "NONE" : grade.ToUpperInvariant();
        var text = Settings.RerollAdvisorShowRuneScore.Value
            ? $"COVER: {normalizedGrade}"
            : string.Empty;

        if (Settings.RerollAdvisorShowWaveCoverageDetails.Value && !string.IsNullOrWhiteSpace(primaryRune) && slot > 0)
        {
            var runeLabel = ToCompactRuneLabel(primaryRune);
            var waveText = totalWaves > 0 ? $" {activeWaves}/{totalWaves}" : string.Empty;
            text += (text.Length > 0 ? " " : string.Empty) + $"{runeLabel} S{slot}{waveText}";
        }

        if (Settings.RerollAdvisorShowWaveCoverageDetails.Value && duplicateIgnoredCount > 0)
            text += $" dup-{duplicateIgnoredCount}";

        if (Settings.RerollAdvisorShowRuneScore.Value && aggregateScore > 0 && Settings.DebugStats.Value)
            text += " " + FormatRewardValue(aggregateScore);

        return new RunePositionInsight
        {
            Text = text,
            Color = color,
            ScoreValue = scoreValue,
            AggregateScore = aggregateScore,
            Slot = slot,
            SlotWeight = slotWeight,
            Grade = normalizedGrade,
            PrimaryRune = primaryRune,
            ActiveWaves = activeWaves,
            TotalWaves = totalWaves,
            UniqueValuableRuneCount = uniqueValuableRuneCount,
            DuplicateIgnoredCount = duplicateIgnoredCount,
            HasCoverData = hasCoverData,
            IsHighCover = string.Equals(normalizedGrade, "HIGH", StringComparison.OrdinalIgnoreCase),
            IsMidCover = string.Equals(normalizedGrade, "MID", StringComparison.OrdinalIgnoreCase),
            IsLowCover = string.Equals(normalizedGrade, "LOW", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static string ToCompactRuneLabel(string signature)
    {
        signature = CleanupText(signature);
        if (string.IsNullOrWhiteSpace(signature))
            return string.Empty;

        return signature.Length <= 12
            ? signature.ToUpperInvariant()
            : signature[..12].ToUpperInvariant();
    }

    private Dictionary<string, RuneEconomicScore> BuildRuneEconomicScores(IReadOnlyList<Expedition2Recipe> allRecipes, int areaLevel)
    {
        var signature = BuildRuneEconomicScoreCacheSignature(areaLevel);
        if (string.Equals(signature, cachedRuneEconomicScoreSignature, StringComparison.Ordinal) && cachedRuneEconomicScores.Count > 0)
            return cachedRuneEconomicScores;

        cachedRuneEconomicScores.Clear();
        cachedRuneEconomicScoreSignature = signature;

        foreach (var recipe in allRecipes)
        {
            if (recipe == null)
                continue;

            if (recipe.MinLevelReq > areaLevel || recipe.MaxLevelReq < areaLevel)
                continue;

            var entry = ToPreOpenEntry(recipe);
            if (entry == null || entry.Value <= 0)
                continue;

            var runes = ReadRecipeRunes(recipe);
            if (runes.Count == 0)
                continue;

            var runeCount = GetRecipeRuneCount(recipe, runes.Count);
            var seenInRecipe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < runes.Count; i++)
            {
                var signatureKey = BuildRuneSignature(runes[i]);
                if (string.IsNullOrWhiteSpace(signatureKey) || !seenInRecipe.Add(signatureKey))
                    continue;

                if (!cachedRuneEconomicScores.TryGetValue(signatureKey, out var score))
                {
                    score = new RuneEconomicScore { Signature = signatureKey };
                    cachedRuneEconomicScores[signatureKey] = score;
                }

                var slotWeight = GetRuneSlotWaveWeight(i, runeCount);
                score.SeenCount++;
                score.TotalValue += entry.Value;
                score.TotalWeightedValue += entry.Value * slotWeight;
                score.MaxValue = Math.Max(score.MaxValue, entry.Value);
                score.WeightedMaxValue = Math.Max(score.WeightedMaxValue, entry.Value * slotWeight);
            }
        }

        return cachedRuneEconomicScores;
    }

    private string BuildRuneEconomicScoreCacheSignature(int areaLevel)
    {
        return string.Join("|",
            areaLevel,
            GetPriceDisplayUnitKey(),
            rawPriceCache.Count,
            priceCache.Count,
            lastPriceRefreshUtc.Ticks,
            Settings.RerollAdvisorRerollBelowValue.Value,
            Settings.RerollAdvisorKeepValue.Value,
            Settings.RerollAdvisorLockValue.Value);
    }

    private static double GetEffectiveRuneEconomicValue(RuneEconomicScore score)
    {
        if (score.SeenCount <= 0)
            return 0;

        var average = Math.Max(score.AverageValue, score.AverageWeightedValue);
        if (average <= 0)
            return 0;

        // Keep a single jackpot recipe from making every occurrence of that rune look great.
        var cappedMax = Math.Min(score.MaxValue, average * 3.0);
        return Math.Max(average, cappedMax);
    }

    private static double GetRuneCoverageBaseScore(string runeSignature)
    {
        if (string.IsNullOrWhiteSpace(runeSignature))
            return 0;

        // Coverage score is intentionally conservative. It tracks runes that are expected to matter
        // when propagated across monster waves. Blue filler/combat runes are left at 0.
        return runeSignature.Trim().ToLowerInvariant() switch
        {
            // Propagation / loot-oriented runes.
            "opulent" => 100,
            "bait" => 90,

            // Strong multiplier runes. Good early, but not enough to call a late slot HIGH.
            "power" => 80,
            "time" => 75,
            "soul" => 72,
            "death" => 70,
            "life" => 68,
            "bond" => 65,
            "oath" => 65,

            // Useful but not a primary keep reason by itself.
            "wisdom" => 45,
            _ => 0
        };
    }

    private (string Grade, Color Color) GradeCoverageScore(RuneCoverageCandidate best, double aggregateScore)
    {
        if (best.ScoreValue <= 0 || best.BaseValue <= 0)
            return ("LOW", Color.Orange);

        // HIGH means an actually valuable rune is active early/broadly enough to matter across monster waves.
        // Opulent/Bait early can be HIGH. A jackpot recipe association no longer affects this grade.
        if ((best.BaseValue >= 90 && best.SlotWeight >= 0.70) || (best.BaseValue >= 75 && best.SlotWeight >= 0.90))
            return ("HIGH", Color.Lime);

        // Multiple good unique runes can make an otherwise medium row risky to reroll, but not every late
        // single rune should be protected. Duplicates are removed before aggregate scoring.
        if ((best.BaseValue >= 65 && best.SlotWeight >= 0.45) ||
            (best.BaseValue >= 90 && best.SlotWeight >= 0.30) ||
            (best.BaseValue >= 45 && best.SlotWeight >= 0.90) ||
            aggregateScore >= 85)
            return ("MID", Color.Yellow);

        return ("LOW", Color.Orange);
    }

    private static Expedition2Recipe? ReadEncounterDataSelectedRecipe(Expedition2EncounterData? data)
    {
        try
        {
            return data?.SelectedRecipe;
        }
        catch
        {
            return null;
        }
    }

    private static List<object> ReadRecipeRunes(Expedition2Recipe? recipe)
    {
        var result = new List<object>();
        if (recipe == null)
            return result;

        try
        {
            if (recipe.Runes is IEnumerable enumerable)
            {
                foreach (var rune in enumerable)
                {
                    if (rune != null)
                        result.Add(rune);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static int GetRecipeRuneCount(Expedition2Recipe recipe, int fallback)
    {
        try
        {
            var count = recipe.RuneCountRequired;
            if (count > 0 && count <= MaxReasonableExpeditionRuneSockets)
                return count;
        }
        catch
        {
        }

        return Math.Max(0, fallback);
    }

    private static double GetRuneSlotWaveWeight(int zeroBasedSlot, int runeCount)
    {
        var totalWaves = GetRuneTotalWaveCount(runeCount);
        if (totalWaves <= 0)
            return 1.0;

        var activeWaves = GetRuneSlotActiveWaveCount(zeroBasedSlot, runeCount);
        return Math.Clamp(activeWaves / (double)totalWaves, 0.05, 1.0);
    }

    private static int GetRuneTotalWaveCount(int runeCount)
    {
        if (runeCount <= 1)
            return 1;

        return Math.Max(1, runeCount - 1);
    }

    private static int GetRuneSlotActiveWaveCount(int zeroBasedSlot, int runeCount)
    {
        var totalWaves = GetRuneTotalWaveCount(runeCount);
        if (runeCount <= 1)
            return totalWaves;

        // Expedition monster waves get one extra left-to-right rune each wave:
        // wave 1 receives slots 1-2, wave 2 receives 1-3, etc. Therefore slots 1 and 2
        // are active for every wave, while later slots cover progressively fewer waves.
        return zeroBasedSlot <= 1
            ? totalWaves
            : Math.Clamp(runeCount - zeroBasedSlot, 1, totalWaves);
    }

    private static string BuildRuneSignature(object? rune)
    {
        if (rune == null)
            return string.Empty;

        foreach (var memberName in new[] { "Id", "Index", "Row", "Address", "BaseName", "Name", "Metadata" })
        {
            if (TryReadComparableMember(rune, memberName, out var value))
                return value;
        }

        return CleanupText(Convert.ToString(rune, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    private string BuildPreOpenRecipeCacheKey(Expedition2EncounterLabel encounterLabel, Entity? entity, int areaLevel)
    {
        var entityId = entity?.Id ?? 0u;
        var gridX = entity?.GridPos.X ?? 0;
        var gridY = entity?.GridPos.Y ?? 0;
        var data = TryGetEncounterData(encounterLabel);
        var labelRuneCount = Math.Max(0, encounterLabel.RuneCount);
        var stateRuneCount = TryGetExpeditionStateSocketCount(entity, out var socketsFromState)
            ? Math.Max(0, socketsFromState)
            : 0;

        return string.Join("|",
            areaLevel,
            entityId,
            gridX,
            gridY,
            BuildEncounterDataSignature(data),
            encounterLabel.FixedRunePosition,
            Convert.ToString(encounterLabel.FixedRune, CultureInfo.InvariantCulture) ?? string.Empty,
            labelRuneCount,
            stateRuneCount,
            GetPriceDisplayUnitKey(),
            rawPriceCache.Count,
            priceCache.Count,
            lastPriceRefreshUtc.Ticks);
    }

    private int GetEffectiveEncounterRuneCount(Expedition2EncounterLabel encounterLabel, Entity? entity)
    {
        var dataRuneCount = ReadEncounterDataRuneCount(TryGetEncounterData(encounterLabel));
        if (dataRuneCount > 0)
            return dataRuneCount;

        var labelRuneCount = Math.Max(0, encounterLabel.RuneCount);
        if (TryGetExpeditionStateSocketCount(entity, out var stateRuneCount))
            return Math.Max(labelRuneCount, stateRuneCount);

        return labelRuneCount;
    }

    private List<PreOpenPreviewEntry> GetOrBuildPreOpenPreviewEntries(
        Expedition2EncounterLabel encounterLabel,
        Entity? entity,
        int areaLevel,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        IEnumerable<dynamic> runeWeights,
        DateTime now)
    {
        // Pre-open preview is calculated from the current encounter state to avoid stale reward data after rerolls.
        var effectiveRuneCount = GetEffectiveEncounterRuneCount(encounterLabel, entity);
        return BuildBestPreOpenPreviewEntries(encounterLabel, effectiveRuneCount, areaLevel, allRecipes, runeWeights);
    }

    private enum PreOpenRecipeMatchMode
    {
        StrictRunePosition,
        AnyRunePosition,
        NoRuneFilter
    }

    private List<PreOpenPreviewEntry> BuildBestPreOpenPreviewEntries(
        Expedition2EncounterLabel encounterLabel,
        int effectiveRuneCount,
        int areaLevel,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        IEnumerable<dynamic> runeWeights)
    {
        var data = TryGetEncounterData(encounterLabel);

        // Current encounter data is the safest source for runes after rerolls.
        var result = BuildEncounterDataPreviewEntries(data, areaLevel, allRecipes, runeWeights);

        // Fallback for labels that do not expose current encounter data yet.
        if (result.Count == 0 && data == null)
        {
            var allowedRuneCounts = BuildAllowedRuneCounts(encounterLabel, areaLevel, runeWeights);
            result = BuildPreOpenPreviewEntries(
                encounterLabel,
                effectiveRuneCount,
                areaLevel,
                allRecipes,
                allowedRuneCounts,
                useAllowedRuneCounts: allowedRuneCounts.Count > 0,
                matchMode: PreOpenRecipeMatchMode.StrictRunePosition);
        }

        result.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        return result;
    }

    private List<PreOpenPreviewEntry> BuildEncounterDataPreviewEntries(
        Expedition2EncounterData? data,
        int areaLevel,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        IEnumerable<dynamic> runeWeights)
    {
        var rawEntries = new List<PreOpenPreviewEntry>(64);
        var runeCount = ReadEncounterDataRuneCount(data);
        var fixedRunePosition = ReadEncounterDataFixedRunePosition(data);
        var fixedRune = ReadEncounterDataFixedRune(data);

        if (data == null)
            return rawEntries;

        // If the game exposes the current selected recipe, prefer it. This makes Pre-Open and
        // ExpeditionMode advice exact instead of showing the best theoretical recipe that merely
        // shares the fixed rune/slot.
        var selectedRecipe = ReadEncounterDataSelectedRecipe(data);
        if (selectedRecipe != null)
        {
            try
            {
                if (selectedRecipe.MinLevelReq <= areaLevel && selectedRecipe.MaxLevelReq >= areaLevel)
                {
                    var selectedEntry = ToPreOpenEntry(selectedRecipe);
                    if (selectedEntry != null)
                    {
                        rawEntries.Add(selectedEntry);
                        return rawEntries;
                    }
                }
            }
            catch
            {
            }
        }

        if (runeCount <= 0 || fixedRunePosition < 0 || fixedRune == null)
            return rawEntries;

        var allowedRuneCounts = BuildAllowedRuneCounts(data, areaLevel, runeWeights);
        if (allowedRuneCounts.Count == 0)
            return rawEntries;

        foreach (var recipe in allRecipes)
        {
            if (recipe == null)
                continue;

            if (recipe.RuneCountRequired > runeCount)
                continue;

            if (!allowedRuneCounts.Contains(recipe.RuneCountRequired))
                continue;

            if (recipe.MinLevelReq > areaLevel || recipe.MaxLevelReq < areaLevel)
                continue;

            try
            {
                if (!RunesMatch(recipe.Runes.ElementAtOrDefault(fixedRunePosition), fixedRune))
                    continue;
            }
            catch
            {
                continue;
            }

            var entry = ToPreOpenEntry(recipe);
            if (entry != null)
                rawEntries.Add(entry);
        }

        return rawEntries;
    }

    private static List<PreOpenPreviewEntry> PreferPreOpenFallbackOnlyWhenEmpty(List<PreOpenPreviewEntry> current, List<PreOpenPreviewEntry> fallback)
    {
        if (current.Count > 0)
            return current;

        return fallback.Count > 0 ? fallback : current;
    }

    private static bool TryReadComparableMember(object value, string memberName, out string result)
    {
        result = string.Empty;

        try
        {
            var type = value.GetType();
            var prop = type.GetProperty(memberName);
            var memberValue = prop?.GetValue(value);

            if (memberValue == null)
            {
                var field = type.GetField(memberName);
                memberValue = field?.GetValue(value);
            }

            if (memberValue == null)
                return false;

            result = CleanupText(Convert.ToString(memberValue, CultureInfo.InvariantCulture) ?? string.Empty);
            return !string.IsNullOrWhiteSpace(result) && !string.Equals(result, "0", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            result = string.Empty;
            return false;
        }
    }

    private static bool RunesMatch(object? left, object? right)
    {
        if (left == null || right == null)
            return false;

        if (ReferenceEquals(left, right) || Equals(left, right))
            return true;

        foreach (var memberName in new[] { "Id", "Index", "Row", "Address", "BaseName", "Name", "Metadata" })
        {
            if (TryReadComparableMember(left, memberName, out var leftValue) &&
                TryReadComparableMember(right, memberName, out var rightValue) &&
                string.Equals(leftValue, rightValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var leftText = CleanupText(Convert.ToString(left, CultureInfo.InvariantCulture) ?? string.Empty);
        var rightText = CleanupText(Convert.ToString(right, CultureInfo.InvariantCulture) ?? string.Empty);

        return !string.IsNullOrWhiteSpace(leftText) &&
               string.Equals(leftText, rightText, StringComparison.OrdinalIgnoreCase);
    }

    private HashSet<int> BuildAllowedRuneCounts(
        Expedition2EncounterData? data,
        int areaLevel,
        IEnumerable<dynamic> runeWeights)
    {
        var allowedRuneCounts = new HashSet<int>();
        var fixedRunePosition = ReadEncounterDataFixedRunePosition(data);
        var fixedRune = ReadEncounterDataFixedRune(data);

        if (fixedRunePosition < 0 || fixedRune == null)
            return allowedRuneCounts;

        foreach (var weight in runeWeights)
        {
            try
            {
                var runeSlot = Convert.ToInt32(weight.RuneSlot, CultureInfo.InvariantCulture) - 1;
                var level = Convert.ToInt32(weight.Level, CultureInfo.InvariantCulture);
                if (runeSlot == fixedRunePosition &&
                    RunesMatch(weight.Rune, fixedRune) &&
                    level <= areaLevel)
                {
                    var slotCount = Convert.ToInt32(weight.SlotCount, CultureInfo.InvariantCulture);
                    if (slotCount > 0 && slotCount <= MaxReasonableExpeditionRuneSockets)
                        allowedRuneCounts.Add(slotCount);
                }
            }
            catch
            {
            }
        }

        return allowedRuneCounts;
    }

    private HashSet<int> BuildAllowedRuneCounts(
        Expedition2EncounterLabel encounterLabel,
        int areaLevel,
        IEnumerable<dynamic> runeWeights)
    {
        var allowedRuneCounts = new HashSet<int>();
        foreach (var weight in runeWeights)
        {
            try
            {
                if (weight.RuneSlot - 1 == encounterLabel.FixedRunePosition &&
                    RunesMatch(weight.Rune, encounterLabel.FixedRune) &&
                    weight.Level <= areaLevel)
                {
                    var slotCount = Convert.ToInt32(weight.SlotCount, CultureInfo.InvariantCulture);
                    if (slotCount > 0 && slotCount <= MaxReasonableExpeditionRuneSockets)
                        allowedRuneCounts.Add(slotCount);
                }
            }
            catch
            {
            }
        }

        return allowedRuneCounts;
    }

    private List<PreOpenPreviewEntry> BuildPreOpenPreviewEntries(
        Expedition2EncounterLabel encounterLabel,
        int effectiveRuneCount,
        int areaLevel,
        IReadOnlyList<Expedition2Recipe> allRecipes,
        HashSet<int> allowedRuneCounts,
        bool useAllowedRuneCounts,
        PreOpenRecipeMatchMode matchMode)
    {
        var rawEntries = new List<PreOpenPreviewEntry>(64);
        var maxRuneCount = Math.Max(0, effectiveRuneCount);

        foreach (var recipe in allRecipes)
        {
            if (recipe == null)
                continue;

            if (maxRuneCount > 0 && recipe.RuneCountRequired > maxRuneCount)
                continue;

            if (useAllowedRuneCounts && allowedRuneCounts.Count > 0 && !allowedRuneCounts.Contains(recipe.RuneCountRequired))
                continue;

            if (recipe.MinLevelReq > areaLevel || recipe.MaxLevelReq < areaLevel)
                continue;

            if (!RecipeMatchesFixedRune(recipe, encounterLabel, matchMode))
                continue;

            var entry = ToPreOpenEntry(recipe);
            if (entry != null)
                rawEntries.Add(entry);
        }

        return rawEntries;
    }

    private static bool RecipeMatchesFixedRune(Expedition2Recipe recipe, Expedition2EncounterLabel encounterLabel, PreOpenRecipeMatchMode matchMode)
    {
        if (matchMode == PreOpenRecipeMatchMode.NoRuneFilter)
            return true;

        try
        {
            if (matchMode == PreOpenRecipeMatchMode.StrictRunePosition)
            {
                var fixedRunePosition = encounterLabel.FixedRunePosition;
                if (fixedRunePosition < 0)
                    return false;

                var runeAtPosition = recipe.Runes.ElementAtOrDefault(fixedRunePosition);
                return RunesMatch(runeAtPosition, encounterLabel.FixedRune);
            }

            foreach (var rune in recipe.Runes)
            {
                if (RunesMatch(rune, encounterLabel.FixedRune))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void PrunePreOpenRecipePreviewCache(DateTime now)
    {
        if (preOpenRecipePreviewCache.Count <= 256)
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

    private static bool TryGetExpeditionStateSocketCount(Entity? entity, out int socketCount)
    {
        socketCount = 0;

        try
        {
            var states = entity?.GetComponent<StateMachine>()?.States;
            if (states == null)
                return false;

            foreach (var state in states)
            {
                if (!string.Equals(state.Name, "sockets", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Convert.ToInt32(state.Value, CultureInfo.InvariantCulture);
                if (value <= 0 || value > MaxReasonableExpeditionRuneSockets)
                    return false;

                socketCount = value;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }


    private static bool IsExpeditionEncounterActivated(Entity? entity)
    {
        try
        {
            var states = entity?.GetComponent<StateMachine>()?.States;
            if (states == null)
                return false;

            foreach (var state in states)
            {
                if (!string.Equals(state.Name, "activated", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Convert.ToInt32(state.Value, CultureInfo.InvariantCulture);
                return value >= 6;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool IsExpedition2Encounter(Entity? entity)
    {
        var metadata = entity?.Metadata;
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        if (metadata.StartsWith("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.Ordinal))
            return true;

        return metadata.Contains("Expedition2", StringComparison.OrdinalIgnoreCase) &&
               metadata.Contains("Encounter", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateExpeditionModeCache()
    {
        if (!Settings.EnableExpeditionMode.Value &&
            !Settings.EnableExpeditionModeTooltip.Value &&
            !Settings.ExpeditionModeDrawLinesToEncounters.Value)
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
            var encounterEntities = GameController.EntityListWrapper.Entities
                .Where(IsExpedition2Encounter)
                .ToList();

            if (encounterEntities.Count == 0)
                return;

            var labels = GameController.IngameState.IngameUi.ItemsOnGroundLabelsVisible
                .Where(x => IsExpedition2Encounter(x?.ItemOnGround))
                .Select(x => (GroundLabel: x, EncounterLabel: x.Label.AsObject<Expedition2EncounterLabel>()))
                .Where(x => x.EncounterLabel != null)
                .ToList();

            var areaLevel = GameController.IngameState.Data.CurrentAreaLevel;
            var allRecipes = GameController.Files.Expedition2Recipes.EntriesList;
            var runeWeights = GameController.Files.Expedition2RunesWeights.EntriesList;
            var index = 1;
            var handledEntityIds = new HashSet<uint>();

            foreach (var (groundLabel, encounterLabel) in labels)
            {
                var entity = groundLabel.ItemOnGround;
                if (entity == null)
                    continue;

                handledEntityIds.Add(entity.Id);

                var stateRuneCount = GetEffectiveEncounterRuneCount(encounterLabel, entity);

                var rawRewards = Settings.EnablePriceApi.Value
                    ? BuildBestPreOpenPreviewEntries(encounterLabel, stateRuneCount, areaLevel, allRecipes, runeWeights)
                        .OrderByDescending(x => x.Value)
                        .ToList()
                    : new List<PreOpenPreviewEntry>();

                var rawAdvice = BuildRerollAdvice(entity, encounterLabel, rawRewards, allRecipes, areaLevel);
                MaybeWriteRerollDebugSnapshot("ExpeditionMode", entity, encounterLabel, rawRewards, rawAdvice, areaLevel);

                var rewards = rawRewards
                    .Where(x => Settings.ExpeditionModeShowZeroPriceRewards.Value || x.Value > 0)
                    .Where(x => Settings.ExpeditionModeMinimumValue.Value <= 0 || x.Value >= Settings.ExpeditionModeMinimumValue.Value)
                    .OrderByDescending(x => x.Value)
                    .Take(Math.Max(1, Settings.ExpeditionModeMaxRewardsPerEncounter.Value))
                    .ToList();

                var rect = encounterLabel.GetClientRect();
                var rawScreenPos = rect.BottomLeft;

                if (rect.Width <= 1 || rect.Height <= 1 || rawScreenPos.X <= 0 || rawScreenPos.Y <= 0)
                    rawScreenPos = Vector2.Zero;

                var grid = new Vector2(entity.GridPos.X, entity.GridPos.Y);
                var hasWorldPosition = TryGetEntityWorldPosition(entity, out var worldPosition);

                cachedExpeditionModeEntries.Add(new ExpeditionModeEncounterEntry
                {
                    Index = index++,
                    LabelSource = encounterLabel,
                    ScreenPosition = rawScreenPos,
                    GridPosition = grid,
                    WorldPosition = worldPosition,
                    HasWorldPosition = hasWorldPosition,
                    RuneCount = stateRuneCount,
                    FixedRune = BuildRuneSignature(ReadEncounterDataFixedRune(TryGetEncounterData(encounterLabel))),
                    Rewards = rewards,
                    Advice = rawAdvice
                });
            }

            foreach (var entity in encounterEntities)
            {
                if (handledEntityIds.Contains(entity.Id) || IsExpeditionEncounterActivated(entity))
                    continue;

                if (!TryGetExpeditionStateSocketCount(entity, out var socketsFromState))
                    continue;

                var grid = new Vector2(entity.GridPos.X, entity.GridPos.Y);
                var hasWorldPosition = TryGetEntityWorldPosition(entity, out var worldPosition);

                cachedExpeditionModeEntries.Add(new ExpeditionModeEncounterEntry
                {
                    Index = index++,
                    LabelSource = null,
                    ScreenPosition = Vector2.Zero,
                    GridPosition = grid,
                    WorldPosition = worldPosition,
                    HasWorldPosition = hasWorldPosition,
                    RuneCount = socketsFromState,
                    FixedRune = string.Empty,
                    Rewards = new List<PreOpenPreviewEntry>(),
                    Advice = EmptyRerollAdvice
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

            // ExpeditionMode never anchors text to the rune/encounter in world space.
            // That behavior belongs to Pre-Open Preview; ExpeditionMode should remain a clean fixed list.
            var hasCurrentPosition = TryGetCurrentVisibleEncounterBottomLeft(entry.LabelSource, out var pos);
            var useFallback = ExpeditionModeAlwaysUseFallbackList;

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
                    entry.Advice.HasText
                        ? $"Expedition #{entry.Index} Rune {entry.RuneCount} Socket : {entry.Advice.Text}"
                        : $"Expedition #{entry.Index} Rune {entry.RuneCount} Socket",
                    new Vector2(pos.X, y),
                    entry.Advice.HasText ? entry.Advice.Color : Settings.ExpeditionModeHeaderColor.Value,
                    bg);

                y += Math.Max(14, headerSize.Y);
            }

            if (!useFallback && entry.Advice.HasText)
            {
                var adviceSize = Graphics.DrawTextWithBackground(entry.Advice.Text, new Vector2(pos.X, y), entry.Advice.Color, bg);
                y += Math.Max(14, adviceSize.Y);
            }

            if (rewards.Count == 0)
            {
                var waitingSize = Graphics.DrawTextWithBackground(
                    "Waiting for expedition reward UI",
                    new Vector2(pos.X, y),
                    Color.Lime,
                    bg);

                y += Math.Max(14, waitingSize.Y);
            }
            else
            {
                for (var i = 0; i < rewards.Count; i++)
                {
                    var reward = rewards[i];
                    var prefix = i == 0 ? "#1 " : i == 1 ? "#2 " : string.Empty;
                    var color = i == 0 ? Settings.TopPickColor : i == 1 ? Settings.SecondPickColor : Settings.FrameColor;
                    var text = $"{prefix}{FormatRewardValue(reward.Value)}  {reward.Name}";
                    var size = Graphics.DrawTextWithBackground(text, new Vector2(pos.X, y), color, bg);
                    y += Math.Max(14, size.Y);
                }
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
            if (entry.Advice.HasText)
                ImGui.Text(entry.Advice.Text);

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

            DrawPreOpenPreviewText(pos, draw.Entries, draw.Advice);
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

    private void DrawPreOpenPreviewText(Vector2 pos, List<PreOpenPreviewEntry> entries, RerollAdvice advice)
    {
        var y = pos.Y;
        var bg = Color.FromArgb(Settings.PreviewBackgroundOpacity.Value, 0, 0, 0);

        if (advice.HasText)
        {
            var adviceSize = Graphics.DrawTextWithBackground(advice.Text, new Vector2(pos.X, y), advice.Color, bg);
            y += Math.Max(14, adviceSize.Y);
        }

        if (entries.Count == 1 && Settings.PreviewBestRewardOnly.Value)
        {
            DrawPreOpenPreviewBestReward(new Vector2(pos.X, y), entries[0]);
            return;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var prefix = i == 0 ? "#1 " : i == 1 ? "#2 " : string.Empty;
            var color = i == 0 ? Settings.TopPickColor : i == 1 ? Settings.SecondPickColor : Settings.FrameColor;
            var text = entry.Count > 1 ? $"{prefix}{FormatRewardValue(entry.Value)}  {entry.Name} x{entry.Count}" : $"{prefix}{FormatRewardValue(entry.Value)}  {entry.Name}";
            var size = Graphics.DrawTextWithBackground(text, new Vector2(pos.X, y), color, bg);
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

        var wantsPriceBasedUiScan = Settings.HighlightMostValuableReward.Value ||
                                Settings.HighlightOnlyTopTwoPicks.Value ||
                                Settings.HighlightOnlyRewardsAboveValue.Value;

        if (!Settings.HighlightAllVisibleRewards.Value && enabledItemNames.Count == 0 && !wantsPriceBasedUiScan)
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
        var shownUndiscovered = 0;
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

            
            
            
            var optionId = ReferenceIdentity(option);
            directOptionScratchIds.Add(optionId);

            if (SafeGet(option, "IsValid") is bool valid && !valid)
                continue;

            if (SafeGet(option, "IsVisible") is bool visible && !visible)
                continue;

            if (SafeGet(option, "IsVisibleLocal") is bool visibleLocal && !visibleLocal)
                continue;

            var rect = GetRect(option);
            var recipeObj = SafeGet(option, "Recipe");
            var recipeId = recipeObj != null ? ReferenceIdentity(recipeObj) : 0;

            var shouldRebuildCache = !directOptionCache.TryGetValue(optionId, out var cached) ||
                                     cached!.RecipeId != recipeId;

            if (shouldRebuildCache)
            {
                cacheMisses++;
                cached = BuildDirectOptionCacheEntry(optionId, rect, recipeObj, option);
                directOptionCache[optionId] = cached;
            }
            else
            {
                cacheHits++;
                UpdateDirectOptionCacheEntryRect(cached!, rect);
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
                shownUndiscovered++;

            if (!cached.IsValidHighlight)
                continue;

            if (!string.IsNullOrEmpty(cached.DedupKey) && !directOptionSeenDedup.Add(cached.DedupKey))
                continue;

            visibleRewards.Add(cached.Reward);
        }

        PruneDirectOptionCache(now);

        status = visibleRewards.Count > 0
            ? $"ok-direct-options-cached options={optionCount} cacheHit={cacheHits} cacheMiss={cacheMisses} filtered={skippedFiltered} noRecipe={skippedNoRecipe} badRect={skippedRect} undiscoveredShown={shownUndiscovered} outOfWindow={skippedOutOfWindow}"
            : $"direct options found no highlightable rewards options={optionCount} cacheHit={cacheHits} cacheMiss={cacheMisses} filtered={skippedFiltered} noRecipe={skippedNoRecipe} badRect={skippedRect} undiscoveredShown={shownUndiscovered} outOfWindow={skippedOutOfWindow}";
    }


    private string BuildDirectOptionsFilterSignature()
    {
        return string.Join("|",
            enabledItemNames.Count,
            lastEnabledRewardsFingerprint,
            Settings.HighlightAllVisibleRewards.Value,
            Settings.HighlightMostValuableReward.Value,
            Settings.HighlightOnlyTopTwoPicks.Value,
            Settings.HighlightOnlyRewardsAboveValue.Value,
            Settings.MinimumValueToHighlight.Value,
            Settings.ShowPriceOnReward.Value,
            Settings.DisplayPricesInExaltedOrbs.Value,
            Settings.DisplayPricesInDivineOrbs.Value,
            priceCache.Count,
            rawPriceCache.Count,
            lastPriceRefreshUtc.Ticks);
    }

    private DirectOptionCacheEntry BuildDirectOptionCacheEntry(int optionId, ExileCore2.Shared.RectangleF rect, object? recipeObj, object option)
    {
        var result = new DirectOptionCacheEntry
        {
            OptionId = optionId,
            RecipeId = recipeObj != null ? ReferenceIdentity(recipeObj) : 0,
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
        result.IsValidHighlight = !result.HasBadRect;
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

        if (!entry.HasNoRecipe && !entry.IsFiltered && entry.Reward.Text.Length > 0)
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

        void Add(string? alias)
        {
            alias = CleanupText(alias ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(alias))
                return;

            output[alias] = value;
        }

        Add(name);
        Add(NormalizePriceKey(name));
        Add(name.Replace(" ", "-").ToLowerInvariant());
        Add(name.Replace(" ", "_").ToLowerInvariant());
        AddLevelSpecificPriceAliases(name, Add);
        AddRuneSpellingAliases(name, Add);

        var pretty = name.Replace("-", " ").Replace("_", " ").Trim();
        if (!string.Equals(pretty, name, StringComparison.OrdinalIgnoreCase))
            Add(pretty);

        if (name.EndsWith(" Alloy", StringComparison.OrdinalIgnoreCase))
        {
            Add(name.Replace(" ", "-").ToLowerInvariant());
            Add(name.Replace(" ", "_").ToLowerInvariant());
            Add(name.Replace(" ", "").ToLowerInvariant());
            Add(name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim());
            Add(name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim().ToLowerInvariant());
        }
    }

    private static void AddLevelSpecificPriceAliases(string name, Action<string?> add)
    {
        var match = Regex.Match(name, @"^\s*(.+?)\s+\(Level\s+(\d+)\)\s*$", RegexOptions.IgnoreCase);
        if (!match.Success)
            return;

        var baseName = CleanupText(match.Groups[1].Value);
        var level = match.Groups[2].Value;
        if (string.IsNullOrWhiteSpace(baseName) || string.IsNullOrWhiteSpace(level))
            return;

        add($"{baseName} Level {level}");
        add($"{baseName} level {level}");
        add($"{baseName.ToLowerInvariant().Replace(" ", "-")}-level-{level}");
        add($"{baseName.ToLowerInvariant().Replace(" ", "_")}_level_{level}");
        add($"{baseName.ToLowerInvariant().Replace(" ", "")}level{level}");
    }

    private static void AddRuneSpellingAliases(string name, Action<string?> add)
    {
        if (!Regex.IsMatch(name, @"\bRunes?\b", RegexOptions.IgnoreCase))
            return;

        // Keep Lesser/Greater/Perfect/Ancient and other rune tiers intact.
        // The previous code stripped these words and overwrote prices such as
        // Robust Rune with Lesser Robust Rune or Greater Robust Rune prices.
        var runeOfVariant = Regex.Replace(name, @"\bRune\s+of\s+", "Rune ", RegexOptions.IgnoreCase).Trim();
        if (!string.Equals(runeOfVariant, name, StringComparison.OrdinalIgnoreCase))
        {
            add(runeOfVariant);
            add(runeOfVariant.Replace(" ", "-").ToLowerInvariant());
            add(runeOfVariant.Replace(" ", "_").ToLowerInvariant());
        }
    }

    private static string NormalizePriceKey(string name)
    {
        name = CleanupText(name);
        name = Regex.Replace(name, @"^\s*\d+\s*x\s+", "", RegexOptions.IgnoreCase).Trim();
        name = Regex.Replace(name, @"^(Skill|Support):\s*", "", RegexOptions.IgnoreCase).Trim();
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
        AddLevelSpecificPriceAliases(name, Add);

        var isRune = Regex.IsMatch(name, @"\bRunes?\b", RegexOptions.IgnoreCase);
        var isLevelSpecificUncutGem = Regex.IsMatch(name, @"\bUncut\s+(Skill|Spirit|Support)\s+Gem\s+\(Level\s+\d+\)", RegexOptions.IgnoreCase);
        var isGreaterOrPerfectOrb =
            name.EndsWith(" Orb", StringComparison.OrdinalIgnoreCase) &&
            (name.StartsWith("Greater ", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("Perfect ", StringComparison.OrdinalIgnoreCase));

        if (!isRune && !isLevelSpecificUncutGem && !isGreaterOrPerfectOrb)
        {
            Add(name.Replace("Greater ", "", StringComparison.OrdinalIgnoreCase).Trim());
            Add(name.Replace("Lesser ", "", StringComparison.OrdinalIgnoreCase).Trim());
            Add(name.Replace("Perfect ", "", StringComparison.OrdinalIgnoreCase).Trim());
        }

        if (isRune)
            AddRuneSpellingAliases(name, Add);

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

    private Dictionary<string, double>? RebuildRawPricesFromSnapshotJson(PoeNinjaPriceSnapshot snapshot)
    {
        if (snapshot.RawJsonByCategory == null || snapshot.RawJsonByCategory.Count == 0)
            return null;

        var rebuilt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in snapshot.RawJsonByCategory)
        {
            if (string.IsNullOrWhiteSpace(pair.Value))
                continue;

            try
            {
                if (pair.Key.StartsWith("exchange:", StringComparison.OrdinalIgnoreCase))
                    ParseExchangeOverview(pair.Value, rebuilt);
                else if (pair.Key.StartsWith("stash:", StringComparison.OrdinalIgnoreCase))
                    ParseStashOverview(pair.Value, rebuilt);
            }
            catch
            {
            }
        }

        return rebuilt.Count > 0 ? rebuilt : null;
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
            if (loaded == null)
                return;

            var rebuiltFromJson = RebuildRawPricesFromSnapshotJson(loaded);
            var pricesToLoad = rebuiltFromJson ?? loaded.Prices;
            if (pricesToLoad == null || pricesToLoad.Count == 0)
                return;

            lock (priceLock)
            {
                rawPriceCache.Clear();
                foreach (var kv in pricesToLoad)
                    rawPriceCache[kv.Key] = kv.Value;
            }

            detectedLeagueName = loaded.League;
            lastPriceRefreshUtc = loaded.CreatedUtc;
            exaltedOrbRawValue = loaded.ExaltedOrbRawValue;
            divineOrbRawValue = loaded.DivineOrbRawValue;
            ApplyDisplayPriceModeFromRawCache();

            var source = rebuiltFromJson != null ? "rebuilt JSON raw cache" : "legacy raw cache";
            var categoryCount = loaded.RawJsonByCategory?.Count ?? 0;
            if (rebuiltFromJson == null)
                forcePriceRefreshRequested = true;

            priceStatus = $"loaded {source}: {rawPriceCache.Count} prices / {categoryCount} categories ({GetPriceDisplayUnitLabel()})";
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
            || text.Contains("Uncut Spirit Gem", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Unique", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Rune", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Orb", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Flux", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Whetstone", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Scrap", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Etcher", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Prism", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Bauble", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Shard", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Essence", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Catalyst", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tablet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Currency", StringComparison.OrdinalIgnoreCase);
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
