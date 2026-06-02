using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Cache;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.PoEMemory.FilesInMemory;
using ExileCore2.PoEMemory.Elements;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
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
    // Raw poe.ninja prices. Display unit conversion is applied in memory only.
    public Dictionary<string, double> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> RawJsonByCategory { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class RuneHighlighterPlugin : BaseSettingsPlugin<RuneHighlighterSettings>
{
    // Known reward-list paths relative to the expedition/runes panel root.
    // Different users can have this root at root->1->39, root->1->40, etc.
    // Once the correct root panel is found, the relative path is usually:
    // 3->2->2->0 or 3->2->1->0.
    private static readonly int[][] KnownPanel40RelativePaths =
    {
        new[] { 3, 2, 2, 0 },
        new[] { 3, 2, 1, 0 },
    };

    private readonly Dictionary<string, PropertyInfo> rewardProperties = new();
    private readonly HashSet<string> enabledItemNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VisibleReward> visibleRewards = new();

    private static readonly HttpClient priceHttpClient = new();

    private sealed class PreOpenPreviewEntry
    {
        public string Name { get; set; } = string.Empty;
        public double Value { get; set; }
        public int Count { get; set; } = 1;
    }


    private readonly List<(Vector2 Position, List<PreOpenPreviewEntry> Entries)> cachedPreOpenPreviewDraws = new();
    private DateTime lastPreOpenPreviewCacheTime = DateTime.MinValue;

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
        TryLoadPriceCacheFromDisk();
        return base.Initialise();
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        try
        {
            var now = DateTime.UtcNow;
            if ((now - lastScan).TotalMilliseconds >= Settings.ScanIntervalMs.Value)
            {
                RebuildEnabledRewards();
                lastPriceHits = 0;
                lastPriceMisses = 0;
                EnsureDisplayPriceModeApplied();
                RefreshPricesIfNeeded();
                ScanPanel40();
                lastScan = now;
            }

            DrawPreOpenPreview();

            UpdatePreOpenPreviewCache();
            DrawPreOpenPreview();

            var rankedRewards = visibleRewards
                .Where(x => x.TotalValue > 0)
                .OrderByDescending(x => x.TotalValue)
                .ToList();

            var topValue = rankedRewards.Count > 0 ? rankedRewards[0].TotalValue : 0;
            var secondValue = rankedRewards.Count > 1 ? rankedRewards[1].TotalValue : 0;

            foreach (var reward in visibleRewards)
            {
                var isTopPick = Settings.HighlightMostValuableReward.Value && reward.TotalValue > 0 && Math.Abs(reward.TotalValue - topValue) < 0.001;
                var isSecondPick = Settings.HighlightMostValuableReward.Value && reward.TotalValue > 0 && !isTopPick && Math.Abs(reward.TotalValue - secondValue) < 0.001;
                var isAboveValue = false; // old standalone threshold highlight disabled; use Highlight Only Rewards Above Value instead

                if (Settings.HighlightOnlyTopTwoPicks.Value && !isTopPick && !isSecondPick)
                    continue;

                if (Settings.HighlightOnlyRewardsAboveValue.Value &&
                    (reward.TotalValue <= 0 || reward.TotalValue < Settings.MinimumValueToHighlight.Value))
                    continue;

                var frameColor = Settings.FrameColor;
                var frameThickness = Settings.FrameThickness.Value;

                if (isTopPick)
                {
                    frameColor = Settings.TopPickColor;
                    frameThickness = Settings.TopPickFrameThickness.Value;
                }
                else if (isSecondPick)
                {
                    frameColor = Settings.SecondPickColor;
                    frameThickness = Settings.TopPickFrameThickness.Value;
                }
                else if (isAboveValue)
                {
                    frameColor = Settings.FrameColor;
                    frameThickness = Settings.FrameThickness.Value;
                }

                Graphics.DrawFrame(reward.Rect, frameColor, frameThickness);

                if (Settings.ShowPriceOnReward.Value && reward.TotalValue > 0)
                {
                    var prefix = isTopPick ? "#1 " : isSecondPick ? "#2 " : string.Empty;

                    Graphics.DrawTextWithBackground(
                        prefix + (FormatRewardValue(reward.TotalValue)),
                        new Vector2(reward.Rect.X + 6, reward.Rect.Y + 4),
                        Color.White,
                        Color.FromArgb(180, 0, 0, 0));
                }
            }

            if (Settings.DebugStats.Value)
            {
                Graphics.DrawTextWithBackground(
                    $"RuneHL: enabled={enabledItemNames.Count}, matches={visibleRewards.Count}, {status}",
                    new Vector2(4, 110),
                    Color.White,
                    Color.FromArgb(180, 0, 0, 0));
            }
        }
        catch (Exception e)
        {
            if (Settings.DebugStats.Value)
                Graphics.DrawTextWithBackground("RuneHL error: " + e.Message, new Vector2(4, 140), Color.White, Color.FromArgb(180, 0, 0, 0));
        }
    }

        public override void DrawSettings()
    {
        RebuildEnabledRewards();

        DrawGeneralControls();

        ImGui.Separator();

        if (ImGui.CollapsingHeader("UI Highlight", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawUiHighlightControls();
        }

        if (ImGui.CollapsingHeader("Pre-Open Preview", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawPreOpenPreviewControls();
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

    private void DrawMainControls()
    {
        DrawGeneralControls();

        ImGui.Separator();

        if (ImGui.CollapsingHeader("UI Highlight", ImGuiTreeNodeFlags.DefaultOpen))
            DrawUiHighlightControls();

        if (ImGui.CollapsingHeader("Pre-Open Preview", ImGuiTreeNodeFlags.DefaultOpen))
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

    private void DrawColor(ExileCore2.Shared.Nodes.ColorNode node, string label)
    {
        var color = node.Value;
        var vector = new Vector4(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        var popupId = $"##{label}_color_popup";

        ImGui.Text($"{label}:");
        ImGui.SameLine(180);

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
        ImGui.Text($"Price Status: {priceStatus}");
        ImGui.Text($"Display Price Cache Items: {priceCache.Count}");
        ImGui.Text($"Raw Price Cache Items: {rawPriceCache.Count}");
        ImGui.Text($"Applied Price Unit: {appliedPriceUnitKey}");
        ImGui.Text($"Minimum Highlight Value: {Settings.MinimumValueToHighlight.Value:0.##}{GetPriceDisplaySuffix()}");
        ImGui.Text($"Minimum Value Only Filter: {Settings.HighlightOnlyRewardsAboveValue.Value}");
        ImGui.Text($"Preview Mode: {(Settings.PreviewBestRewardOnly.Value ? "Best Reward Only" : Settings.PreviewTopTwoOnly.Value ? "Top 2 Picks" : "Full List")}");
        ImGui.Text($"Pre-Open Preview Cached Labels: {cachedPreOpenPreviewDraws.Count}");
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

    private void RebuildEnabledRewards()
    {
        enabledItemNames.Clear();

        foreach (var (propertyName, itemName) in RewardCatalog.Items)
        {
            if (!rewardProperties.TryGetValue(propertyName, out var prop))
                continue;

            var node = prop.GetValue(Settings.Rewards);
            if (SafeBool(node, "Value"))
                enabledItemNames.Add(CleanupText(itemName));
        }
    }


    private void UpdatePreOpenPreviewCache()
    {
        cachedPreOpenPreviewDraws.Clear();

        if (!Settings.EnablePreOpenPreview.Value || !Settings.EnablePriceApi.Value)
        {
            return;
        }

        EnsureDisplayPriceModeApplied();

        try
        {
            var hasEncounterEntity = GameController.EntityListWrapper.Entities.Any(x =>
                x.Metadata.StartsWith("Metadata/MiscellaneousObjects/Expedition2/Expedition2Encounter", StringComparison.Ordinal));

            if (!hasEncounterEntity)
            {
                return;
            }

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

                var entries = allRecipes
                    .Where(x => x.Key <= encounterLabel.RuneCount)
                    .SelectMany(x => x)
                    .Where(x => allowedRuneCounts.Contains(x.RuneCountRequired))
                    .Where(x => x.MinLevelReq <= areaLevel && x.MaxLevelReq >= areaLevel)
                    .Where(x => x.Runes.ElementAtOrDefault(encounterLabel.FixedRunePosition)?.Equals(encounterLabel.FixedRune) == true)
                    .Select(ToPreOpenEntry)
                    .Where(x => x != null)
                    .Select(x => x!)
                    .OrderByDescending(x => x.Value)
                    .ToList();

                if (Settings.PreviewUseMinimumValueFilter.Value)
                {
                    entries = entries
                        .Where(x => x.Value >= Settings.MinimumValueToHighlight.Value)
                        .ToList();
                }
                else if (Settings.PreviewMinimumValue.Value > 0)
                {
                    entries = entries
                        .Where(x => x.Value >= Settings.PreviewMinimumValue.Value)
                        .ToList();
                }

                if (Settings.PreviewBestRewardOnly.Value)
                {
                    entries = entries.Take(1).ToList();
                }
                else if (Settings.PreviewTopTwoOnly.Value)
                {
                    entries = entries.Take(2).ToList();
                }
                else
                {
                    entries = entries.Take(Settings.PreviewMaxLines.Value).ToList();
                }

                if (entries.Count == 0)
                    continue;

                var bottomLeft = encounterLabel.GetClientRect().BottomLeft;
                var pos = new Vector2(bottomLeft.X + Settings.PreviewOffsetX.Value, bottomLeft.Y + Settings.PreviewOffsetY.Value);
                cachedPreOpenPreviewDraws.Add((pos, entries));
            }

            lastPreOpenPreviewCacheTime = DateTime.UtcNow;
        }
        catch
        {
            // Preview is best-effort. Never break the main highlighter.
        }
    }


    private void DrawPreOpenPreview()
    {
        if (!Settings.EnablePreOpenPreview.Value)
            return;

        foreach (var draw in cachedPreOpenPreviewDraws)
            DrawPreOpenPreviewText(draw.Position, draw.Entries);
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

        var name = string.IsNullOrWhiteSpace(recipe.Description) ? recipe.Reward?.BaseName : recipe.Description;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (!TryGetDisplayPriceForName(name, out var unitPrice))
            return null;

        var count = GetRecipeRewardCount(recipe);
        return new PreOpenPreviewEntry
        {
            Name = name,
            Count = count,
            Value = unitPrice * count
        };
    }


    private void DrawPreOpenPreviewBestReward(Vector2 pos, PreOpenPreviewEntry entry)
    {
        var line1 = $"[BEST] {FormatRewardValue(entry.Value)}";
        var line2 = $"{entry.Name} x{entry.Count}";

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
            var text = $"{prefix}{FormatRewardValue(entry.Value)}  {entry.Name} x{entry.Count}";
            var size = Graphics.DrawTextWithBackground(text, new Vector2(pos.X, y), color, Color.FromArgb(Settings.PreviewBackgroundOpacity.Value, 0, 0, 0));
            y += Math.Max(14, size.Y);
        }
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

        var root = GetPrimaryRoot();
        if (root == null)
        {
            status = "root not found";
            return;
        }

        var candidateRoots = GetRewardRootCandidates(root).ToList();

        if (candidateRoots.Count == 0)
        {
            status = "no candidate root panels";
            return;
        }

        // 1) Try the known cheap relative paths under every candidate root.
        foreach (var candidate in candidateRoots)
        {
            foreach (var relativePath in KnownPanel40RelativePaths)
            {
                var panel = GetByPath(candidate.Element, relativePath);
                if (panel == null)
                    continue;

                candidatePanels++;
                ScanRewardPanel(panel);

                if (visibleRewards.Count > 0)
                {
                    mode = "known-path " + candidate.Path;
                    status = "ok";
                    return;
                }
            }
        }

        // 2) If known paths do not work, do a small local scan only under candidate roots.
        foreach (var candidate in candidateRoots)
        {
            mode = "local-scan " + candidate.Path;

            var panels = FindRewardPanelsUnderPanel40(candidate.Element).ToList();
            candidatePanels += panels.Count;

            foreach (var panel in panels)
                ScanRewardPanel(panel);

            if (visibleRewards.Count > 0)
            {
                status = "ok";
                return;
            }

            if (localScannedObjects > Settings.MaxLocalObjects.Value)
                break;
        }

        status = "no reward panel under dynamic roots";
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

        // Do NOT add base aliases for Greater/Perfect orbs.
        // Example: Greater Exalted Orb must remain Greater Exalted Orb, not Exalted Orb.
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
        // Verisium category: Protective Alloy / Adaptive Alloy / Runic Alloy / Expansive Alloy.
        // Keep the full Alloy name, but add common detailsId variants.
        if (name.EndsWith(" Alloy", StringComparison.OrdinalIgnoreCase))
        {
            output[name.Replace(" ", "-").ToLowerInvariant()] = value;
            output[name.Replace(" ", "_").ToLowerInvariant()] = value;
            output[name.Replace(" ", "").ToLowerInvariant()] = value;
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

        lastPriceMisses++;
        lastPriceMiss = name;
        return (0, stack, 0);
    }

    private static IEnumerable<string> BuildPriceLookupCandidates(string name)
    {
        name = CleanPriceLookupName(name);

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

        // Critical fix:
        // Greater Exalted Orb / Greater Chaos Orb / Perfect Exalted Orb must NOT fall back to
        // Exalted Orb / Chaos Orb. That produced wrong values such as Greater Exalted Orb = 1 ex.
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
                Add(withoutGreater);
                Add(withoutLesser);
                Add(withoutGreater.Replace(" Rune of ", " Rune ", StringComparison.OrdinalIgnoreCase));
                Add(withoutGreater.Replace(" Rune", "", StringComparison.OrdinalIgnoreCase));
                Add(name.Replace(" Rune of ", " Rune ", StringComparison.OrdinalIgnoreCase));
                Add(name.Replace(" Rune", "", StringComparison.OrdinalIgnoreCase));
            }
        }

        // Exact detailsId-like variants are safe because they still represent the same full item.
        Add(name.Replace(" ", "-").ToLowerInvariant());
        Add(name.Replace(" ", "_").ToLowerInvariant());
        if (name.EndsWith(" Alloy", StringComparison.OrdinalIgnoreCase))
        {
            Add(name.Replace(" Alloy", "", StringComparison.OrdinalIgnoreCase).Trim());
            Add(name.Replace(" ", "").ToLowerInvariant());
            Add(name.Replace(" ", "-").ToLowerInvariant());
            Add(name.Replace(" ", "_").ToLowerInvariant());
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

    private static string CleanPriceLookupName(string name)
    {
        name = CleanupText(name);
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

        // poe.ninja endpoints have changed a few times; try all known variants.
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

            // Prefer indexed non-hardcore temporary leagues over Standard.
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

        // Most common positions first: yours was root->1->40, friend's is root->1->39.
        foreach (var index in new[] { 40, 39, 41, 38, 42 })
        {
            var candidate = GetChild(root1, index);
            if (candidate != null && yielded.Add(ReferenceIdentity(candidate)))
                yield return new PanelCandidate(candidate, "root->1->" + index);

            candidate = GetChild(root, index);
            if (candidate != null && yielded.Add(ReferenceIdentity(candidate)))
                yield return new PanelCandidate(candidate, "root->" + index);
        }

        // Safe fallback: small range around the known area only, not the whole UI tree.
        for (var index = 30; index <= 50; index++)
        {
            var candidate = GetChild(root1, index);
            if (candidate != null && yielded.Add(ReferenceIdentity(candidate)))
                yield return new PanelCandidate(candidate, "root->1->" + index);

            candidate = GetChild(root, index);
            if (candidate != null && yielded.Add(ReferenceIdentity(candidate)))
                yield return new PanelCandidate(candidate, "root->" + index);
        }
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

    private bool IsRewardListPanel(object element)
    {
        var childCount = GetInt(element, "ChildCount");
        var children = GetChildren(element).Take(Settings.MaxRewardRows.Value + 1).ToList();
        var count = Math.Max(childCount, children.Count);

        if (count < Settings.MinRewardPanelChildren.Value)
            return false;

        var selectedRows = 0;
        var rewardLikeRows = 0;

        foreach (var row in children.Take(Settings.MaxRewardRows.Value))
        {
            var textElement = FindTextElement(row, 3);
            var text = CleanupText(Convert.ToString(SafeGet(textElement, "TextNoTags") ?? SafeGet(textElement, "Text") ?? string.Empty) ?? string.Empty);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (enabledItemNames.Contains(NormalizeForFilter(text)))
                selectedRows++;

            if (LooksLikeRewardText(text))
                rewardLikeRows++;

            if (selectedRows >= 1 || rewardLikeRows >= 3)
                return true;
        }

        return false;
    }

    private void ScanRewardPanel(object panel)
    {
        var rows = GetChildren(panel).Take(Settings.MaxRewardRows.Value).ToList();
        scannedRows += rows.Count;

        foreach (var row in rows)
        {
            var textElement = FindTextElement(row, 3);
            if (textElement == null)
                continue;

            var text = CleanupText(Convert.ToString(SafeGet(textElement, "TextNoTags") ?? SafeGet(textElement, "Text") ?? string.Empty) ?? string.Empty);

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var itemName = NormalizeForFilter(text);
            var isSelected = enabledItemNames.Contains(itemName);
            var isRewardLike = LooksLikeRewardText(text);

            if (Settings.HighlightAllVisibleRewards.Value)
            {
                if (!isRewardLike)
                    continue;
            }
            else if (!isSelected)
            {
                continue;
            }

            if (!IsActuallyVisible(row) && !IsActuallyVisible(textElement))
                continue;

            var rectSource = Settings.DrawFullRow.Value ? row : textElement;
            var rect = GetRect(rectSource);

            if (!IsGoodRect(rect))
                rect = GetRect(textElement);

            if (!IsGoodRect(rect))
                continue;

            if (visibleRewards.Any(x => SameRect(x.Rect, rect) || string.Equals(x.Text, text, StringComparison.OrdinalIgnoreCase)))
                continue;

            var priceInfo = GetRewardPrice(text);
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

            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(obj);

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
                return field.GetValue(obj);
        }
        catch
        {
        }

        return null;
    }

    private static object? Invoke(object? obj, string name)
    {
        if (obj == null)
            return null;

        try
        {
            var method = obj.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes);
            return method?.Invoke(obj, null);
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
