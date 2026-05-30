using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using System.Reflection;
using ExileCore2;
using ImGuiNET;

namespace RuneHighlighter;

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
                ScanPanel40();
                lastScan = now;
            }

            foreach (var reward in visibleRewards)
                Graphics.DrawFrame(reward.Rect, Settings.FrameColor, Settings.FrameThickness.Value);

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

        DrawMainControls();

        ImGui.Separator();

        if (ImGui.CollapsingHeader("Reward Selection", ImGuiTreeNodeFlags.DefaultOpen))
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
        DrawToggle(Settings.Enable, "Enable Plugin");
        DrawToggle(Settings.HighlightAllVisibleRewards, "Highlight Every Visible Reward (disables Item Filter and highlights all rewards)");
    }

    private void DrawDiagnosticsControls()
    {
        DrawToggle(Settings.DebugStats, "Enable Debug Overlay");
        DrawToggle(Settings.Panel40FastMode, "Dynamic Root Fast Mode");
        DrawToggle(Settings.DrawFullRow, "Draw Full Row");

        DrawIntSlider(Settings.ScanIntervalMs, "Scan Interval (ms)");
        DrawIntSlider(Settings.Panel40LocalDepth, "Panel 40 Local Depth");
        DrawIntSlider(Settings.MaxLocalObjects, "Max Local Objects");
        DrawIntSlider(Settings.MaxRewardRows, "Max Reward Rows");
        DrawIntSlider(Settings.MinRewardPanelChildren, "Min Reward Panel Children");
        DrawIntSlider(Settings.FrameThickness, "Frame Thickness");

        DrawColor(Settings.FrameColor, "Frame Color");

        ImGui.Separator();
        ImGui.Text($"Enabled Reward Filters: {enabledItemNames.Count}");
        ImGui.Text($"Visible Reward Matches: {visibleRewards.Count}");
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

            visibleRewards.Add(new VisibleReward(rect, text));
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

    private readonly record struct VisibleReward(ExileCore2.Shared.RectangleF Rect, string Text);
    private readonly record struct PanelCandidate(object Element, string Path);
}
