using System.Drawing;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace RuneHighlighter;

public class RuneHighlighterSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Dynamic Root Fast Mode", "Finds the reward panel root dynamically, e.g. root->1->39 or root->1->40, without scanning the whole UI.")]
    public ToggleNode Panel40FastMode { get; set; } = new ToggleNode(true);

    [Menu("Highlight Every Visible Reward", "ON = ignores the reward list and highlights everything visible. OFF = highlights only checked rewards.")]
    public ToggleNode HighlightAllVisibleRewards { get; set; } = new ToggleNode(false);
[Menu("Only IsVisible = true")]
    public ToggleNode OnlyIsVisible { get; set; } = new ToggleNode(true);

    [Menu("Scan Interval (ms)")]
    public RangeNode<int> ScanIntervalMs { get; set; } = new RangeNode<int>(250, 80, 2000);

    [Menu("Dynamic Root Local Depth", "Small local scan under detected reward root candidates only, used when known paths do not work.")]
    public RangeNode<int> Panel40LocalDepth { get; set; } = new RangeNode<int>(3, 2, 8);

    [Menu("Max Local Objects")]
    public RangeNode<int> MaxLocalObjects { get; set; } = new RangeNode<int>(650, 100, 5000);

    [Menu("Max Reward Rows")]
    public RangeNode<int> MaxRewardRows { get; set; } = new RangeNode<int>(180, 50, 700);

    [Menu("Min Reward Panel Children")]
    public RangeNode<int> MinRewardPanelChildren { get; set; } = new RangeNode<int>(120, 50, 500);

    [Menu("Frame Thickness")]
    public RangeNode<int> FrameThickness { get; set; } = new RangeNode<int>(4, 1, 10);

    [Menu("Frame Color")]
    public ColorNode FrameColor { get; set; } = new ColorNode(Color.Lime);

    [Menu("Draw Full Row")]
    public ToggleNode DrawFullRow { get; set; } = new ToggleNode(true);

    [Menu("Use Pre-Open Cache For UI Highlights", "ON = opened UI highlighter reuses cheap Pre-Open reward/pricing cache and only scans visible row anchors. This avoids expensive per-row price/text evaluation.")]
    public ToggleNode UsePreOpenCacheForUiHighlights { get; set; } = new ToggleNode(true);

    [Menu("Pre-Open UI Full Rescan Interval (ms)", "Minimum interval for expensive opened-UI text rescans when Pre-Open cache is available. Higher values are smoother.")]
    public RangeNode<int> PreOpenUiFullRescanIntervalMs { get; set; } = new RangeNode<int>(1500, 500, 5000);

    [Menu("Show Debug Overlay")]
    public ToggleNode DebugStats { get; set; } = new ToggleNode(false);

    [Menu("Enable Spike Profiler", "Shows per-section timing in the debug overlay so costly hot paths are visible in-game.")]
    public ToggleNode EnableSpikeProfiler { get; set; } = new ToggleNode(false);

    [Menu("Enable Profiler TXT Log", "Writes RuneHighlighter profiler samples to Plugins/RuneHighlighter/Profiler/runehighlighter_debug.txt.")]
    public ToggleNode EnableProfilerTextLog { get; set; } = new ToggleNode(false);

    [Menu("Profiler TXT Log Only Spikes", "ON = write only sections above Spike Profiler Warn Ms. OFF = write regular full samples.")]
    public ToggleNode ProfilerTextLogOnlySpikes { get; set; } = new ToggleNode(false);

    [Menu("Profiler TXT Log Interval (ms)", "How often the TXT profiler writes a sample. Higher values reduce disk writes.")]
    public RangeNode<int> ProfilerTextLogIntervalMs { get; set; } = new RangeNode<int>(1000, 250, 10000);

    [Menu("Spike Profiler Warn Ms", "Profiler rows at or above this last-frame time are drawn in orange/red and marked as SPIKE in TXT.")]
    public RangeNode<int> SpikeProfilerWarnMs { get; set; } = new RangeNode<int>(3, 1, 100);

    [Menu("Enable Price API", "Standalone poe.ninja price cache. No NinjaPricer bridge.")]
    public ToggleNode EnablePriceApi { get; set; } = new ToggleNode(true);

    [Menu("Auto Detect poe.ninja League", "When ON and League Name is empty, downloads poe.ninja league list and uses the first indexed PoE2 economy league.")]
    public ToggleNode AutoDetectPoeNinjaLeague { get; set; } = new ToggleNode(true);

    [Menu("League Name", "Optional manual poe.ninja PoE2 league name. Leave empty for auto-detect.")]
    public TextNode LeagueName { get; set; } = new TextNode("");

    [Menu("Price Refresh Interval Minutes")]
    public RangeNode<int> PriceRefreshIntervalMinutes { get; set; } = new RangeNode<int>(30, 1, 240);

    [Menu("Price API Safe Mode", "Downloads only the most relevant categories first to avoid poe.ninja 429 limits.")]
    public ToggleNode PriceApiSafeMode { get; set; } = new ToggleNode(true);

    [Menu("Price API Request Delay (ms)", "Delay between poe.ninja category requests.")]
    public RangeNode<int> PriceApiRequestDelayMs { get; set; } = new RangeNode<int>(1500, 500, 8000);

    [Menu("429 Cooldown Minutes", "How long to wait after poe.ninja returns Too Many Requests.")]
    public RangeNode<int> PriceApi429CooldownMinutes { get; set; } = new RangeNode<int>(30, 5, 180);

    [Menu("Cache Age Warning Minutes", "Diagnostics warning if price cache is older than this value.")]
    public RangeNode<int> CacheAgeWarningMinutes { get; set; } = new RangeNode<int>(120, 30, 720);

    [Menu("Show Price On Reward")]
    public ToggleNode ShowPriceOnReward { get; set; } = new ToggleNode(true);

    [Menu("ExpeditionMode", "Shows a movable window with all detected Expedition reward lists on the current map.")]
    public ToggleNode EnableExpeditionMode { get; set; } = new ToggleNode(false);

    [Menu("ExpeditionMode Max Rewards Per Encounter")]
    public RangeNode<int> ExpeditionModeMaxRewardsPerEncounter { get; set; } = new RangeNode<int>(6, 1, 20);

    [Menu("ExpeditionMode Minimum Value")]
    public RangeNode<int> ExpeditionModeMinimumValue { get; set; } = new RangeNode<int>(0, 0, 500);

    [Menu("ExpeditionMode Show Zero Price Rewards")]
    public ToggleNode ExpeditionModeShowZeroPriceRewards { get; set; } = new ToggleNode(true);

    [Menu("ExpeditionMode Tooltip Overlay", "Shows a small reward tooltip near every detected Expedition encounter.")]
    public ToggleNode EnableExpeditionModeTooltip { get; set; } = new ToggleNode(false);

    [Menu("ExpeditionMode Tooltip Best Reward Only")]
    public ToggleNode ExpeditionModeTooltipBestOnly { get; set; } = new ToggleNode(false);

    [Menu("ExpeditionMode Tooltip Top 2 Only")]
    public ToggleNode ExpeditionModeTooltipTopTwoOnly { get; set; } = new ToggleNode(true);

    [Menu("ExpeditionMode Tooltip Offset X")]
    public RangeNode<int> ExpeditionModeTooltipOffsetX { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("ExpeditionMode Tooltip Offset Y")]
    public RangeNode<int> ExpeditionModeTooltipOffsetY { get; set; } = new RangeNode<int>(0, -2000, 2000);

    [Menu("ExpeditionMode Tooltip Background Opacity")]
    public RangeNode<int> ExpeditionModeTooltipBackgroundOpacity { get; set; } = new RangeNode<int>(210, 0, 255);

    [Menu("ExpeditionMode Header Color")]
    public ColorNode ExpeditionModeHeaderColor { get; set; } = new ColorNode(Color.White);

    [Menu("ExpeditionMode Tooltip Fallback List", "If encounter screen position is not drawable, show tooltip entries in a fixed list on the screen.")]
    public ToggleNode ExpeditionModeTooltipFallbackList { get; set; } = new ToggleNode(true);

    [Menu("ExpeditionMode Tooltip Fallback X")]
    public RangeNode<int> ExpeditionModeTooltipFallbackX { get; set; } = new RangeNode<int>(40, 0, 3000);

    [Menu("ExpeditionMode Tooltip Fallback Y")]
    public RangeNode<int> ExpeditionModeTooltipFallbackY { get; set; } = new RangeNode<int>(260, 0, 3000);


    [Menu("Display Prices In Exalted Orbs", "ON = values are shown in Exalted Orb units.")]
    public ToggleNode DisplayPricesInExaltedOrbs { get; set; } = new ToggleNode(true);

    [Menu("Display Prices In Divine Orbs", "ON = values are shown in Divine Orb units. Takes priority over Exalted mode.")]
    public ToggleNode DisplayPricesInDivineOrbs { get; set; } = new ToggleNode(false);

    [Menu("Highlight Most Valuable Reward")]
    public ToggleNode HighlightMostValuableReward { get; set; } = new ToggleNode(true);

    [Menu("Highlight Rewards Above Value")]
    public ToggleNode HighlightRewardsAboveValue { get; set; } = new ToggleNode(false);

    [Menu("Minimum Value To Highlight", "Uses the currently selected price display unit: Exalted or Divine. Example: set 5 to highlight rewards worth at least 5 ex/div.")]
    public RangeNode<int> MinimumValueToHighlight { get; set; } = new RangeNode<int>(10, 0, 1000);

    [Menu("Top Pick Frame Thickness")]
    public RangeNode<int> TopPickFrameThickness { get; set; } = new RangeNode<int>(6, 1, 15);

    [Menu("Top Pick Color")]
    public ColorNode TopPickColor { get; set; } = new ColorNode(Color.FromArgb(255, 200, 0, 255));

    [Menu("Second Pick Color")]
    public ColorNode SecondPickColor { get; set; } = new ColorNode(Color.FromArgb(255, 25, 203, 232));

    [Menu("Highlight Only Top 2 Picks", "When ON, only the most valuable and second most valuable rewards are highlighted.")]
    public ToggleNode HighlightOnlyTopTwoPicks { get; set; } = new ToggleNode(false);

    [Menu("Highlight Only Rewards Above Value", "When ON, rewards below Minimum Value To Highlight are ignored. The value uses the selected unit: Exalted or Divine.")]
    public ToggleNode HighlightOnlyRewardsAboveValue { get; set; } = new ToggleNode(false);

    [Menu("Enable Pre-Open Preview", "Shows exact priced Expedition2 reward preview near encounter labels before opening the reward UI.")]
    public ToggleNode EnablePreOpenPreview { get; set; } = new ToggleNode(true);

    [Menu("Preview Best Reward Only", "Shows only the single most valuable possible reward in the pre-open preview.")]
    public ToggleNode PreviewBestRewardOnly { get; set; } = new ToggleNode(true);

    [Menu("Preview Top 2 Picks Only", "Shows only the best two possible rewards. Ignored when Preview Best Reward Only is ON.")]
    public ToggleNode PreviewTopTwoOnly { get; set; } = new ToggleNode(false);

    [Menu("Preview Use Minimum Value Filter", "Uses Minimum Value To Highlight for pre-open preview. Example: 5 in Exalted mode means show only rewards worth at least 5 ex.")]
    public ToggleNode PreviewUseMinimumValueFilter { get; set; } = new ToggleNode(false);

    [Menu("Preview Max Lines")]
    public RangeNode<int> PreviewMaxLines { get; set; } = new RangeNode<int>(8, 1, 20);

    [Menu("Preview Minimum Value", "Uses selected price unit: Exalted or Divine.")]
    public RangeNode<float> PreviewMinimumValue { get; set; } = new RangeNode<float>(0f, 0f, 100f);

    [Menu("Preview Offset X")]
    public RangeNode<int> PreviewOffsetX { get; set; } = new RangeNode<int>(35, -400, 600);

    [Menu("Preview Offset Y")]
    public RangeNode<int> PreviewOffsetY { get; set; } = new RangeNode<int>(-35, -400, 400);

    [Menu("Pre-Open Background Opacity")]
    public RangeNode<int> PreviewBackgroundOpacity { get; set; } = new RangeNode<int>(180, 0, 255);

    [Menu("Reward Selection", "Checked rewards will be highlighted. Unchecked rewards will be ignored.")]
    public RewardItemSettings Rewards { get; set; } = new RewardItemSettings();
}

public class RewardItemSettings
{
    [Menu("10x Divine Orb")]
    public ToggleNode Reward__10x_Divine_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Arcanist's Etcher")]
    public ToggleNode Reward__2x_Arcanist_s_Etcher { get; set; } = new ToggleNode(true);

    [Menu("2x Armourer's Scrap")]
    public ToggleNode Reward__2x_Armourer_s_Scrap { get; set; } = new ToggleNode(true);

    [Menu("2x Artificer's Orb")]
    public ToggleNode Reward__2x_Artificer_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Blacksmith's Whetstone")]
    public ToggleNode Reward__2x_Blacksmith_s_Whetstone { get; set; } = new ToggleNode(true);

    [Menu("2x Chaos Orb")]
    public ToggleNode Reward__2x_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Divine Orb")]
    public ToggleNode Reward__2x_Divine_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Exalted Orb")]
    public ToggleNode Reward__2x_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Gemcutter's Prism")]
    public ToggleNode Reward__2x_Gemcutter_s_Prism { get; set; } = new ToggleNode(true);

    [Menu("2x Glassblower's Bauble")]
    public ToggleNode Reward__2x_Glassblower_s_Bauble { get; set; } = new ToggleNode(true);

    [Menu("2x Orb of Annulment")]
    public ToggleNode Reward__2x_Orb_of_Annulment { get; set; } = new ToggleNode(true);

    [Menu("2x Orb of Augmentation")]
    public ToggleNode Reward__2x_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("2x Orb of Chance")]
    public ToggleNode Reward__2x_Orb_of_Chance { get; set; } = new ToggleNode(true);

    [Menu("2x Orb of Transmutation")]
    public ToggleNode Reward__2x_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("2x Regal Orb")]
    public ToggleNode Reward__2x_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("2x Runic Alloy")]
    public ToggleNode Reward__2x_Runic_Alloy { get; set; } = new ToggleNode(true);

    [Menu("3x Artificer's Orb")]
    public ToggleNode Reward__3x_Artificer_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Chaos Orb")]
    public ToggleNode Reward__3x_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Divine Orb")]
    public ToggleNode Reward__3x_Divine_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Exalted Orb")]
    public ToggleNode Reward__3x_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Gemcutter's Prism")]
    public ToggleNode Reward__3x_Gemcutter_s_Prism { get; set; } = new ToggleNode(true);

    [Menu("3x Glassblower's Bauble")]
    public ToggleNode Reward__3x_Glassblower_s_Bauble { get; set; } = new ToggleNode(true);

    [Menu("3x Greater Chaos Orb")]
    public ToggleNode Reward__3x_Greater_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Greater Exalted Orb")]
    public ToggleNode Reward__3x_Greater_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Greater Orb of Augmentation")]
    public ToggleNode Reward__3x_Greater_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("3x Greater Orb of Transmutation")]
    public ToggleNode Reward__3x_Greater_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("3x Greater Regal Orb")]
    public ToggleNode Reward__3x_Greater_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Orb of Alchemy")]
    public ToggleNode Reward__3x_Orb_of_Alchemy { get; set; } = new ToggleNode(true);

    [Menu("3x Orb of Annulment")]
    public ToggleNode Reward__3x_Orb_of_Annulment { get; set; } = new ToggleNode(true);

    [Menu("3x Orb of Chance")]
    public ToggleNode Reward__3x_Orb_of_Chance { get; set; } = new ToggleNode(true);

    [Menu("3x Perfect Chaos Orb")]
    public ToggleNode Reward__3x_Perfect_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Perfect Exalted Orb")]
    public ToggleNode Reward__3x_Perfect_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Perfect Orb of Augmentation")]
    public ToggleNode Reward__3x_Perfect_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("3x Perfect Orb of Transmutation")]
    public ToggleNode Reward__3x_Perfect_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("3x Perfect Regal Orb")]
    public ToggleNode Reward__3x_Perfect_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("3x Regal Orb")]
    public ToggleNode Reward__3x_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("4x Arcanist's Etcher")]
    public ToggleNode Reward__4x_Arcanist_s_Etcher { get; set; } = new ToggleNode(true);

    [Menu("4x Armourer's Scrap")]
    public ToggleNode Reward__4x_Armourer_s_Scrap { get; set; } = new ToggleNode(true);

    [Menu("4x Blacksmith's Whetstone")]
    public ToggleNode Reward__4x_Blacksmith_s_Whetstone { get; set; } = new ToggleNode(true);

    [Menu("5x Random Currency")]
    public ToggleNode Reward__5x_Random_Currency { get; set; } = new ToggleNode(true);

    [Menu("6x Arcanist's Etcher")]
    public ToggleNode Reward__6x_Arcanist_s_Etcher { get; set; } = new ToggleNode(true);

    [Menu("6x Armourer's Scrap")]
    public ToggleNode Reward__6x_Armourer_s_Scrap { get; set; } = new ToggleNode(true);

    [Menu("6x Blacksmith's Whetstone")]
    public ToggleNode Reward__6x_Blacksmith_s_Whetstone { get; set; } = new ToggleNode(true);

    [Menu("1x Adaptive Alloy")]
    public ToggleNode Reward__1x_Adaptive_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Adept Rune")]
    public ToggleNode Reward__1x_Adept_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Aldur's Legacy")]
    public ToggleNode Reward__1x_Aldur_s_Legacy { get; set; } = new ToggleNode(true);

    [Menu("1x Aldur's Saga")]
    public ToggleNode Reward__1x_Aldur_s_Saga { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Animosity")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Animosity { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Control")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Control { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Decay")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Decay { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Detonation")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Detonation { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Discovery")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Discovery { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Dueling")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Dueling { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Prowess")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Prowess { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Retaliation")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Retaliation { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Shattering")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Shattering { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Splinters")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Splinters { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of the Horde")]
    public ToggleNode Reward__1x_Ancient_Rune_of_the_Horde { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of the Titan")]
    public ToggleNode Reward__1x_Ancient_Rune_of_the_Titan { get; set; } = new ToggleNode(true);

    [Menu("1x Ancient Rune of Witchcraft")]
    public ToggleNode Reward__1x_Ancient_Rune_of_Witchcraft { get; set; } = new ToggleNode(true);

    [Menu("Skill: Animus Exchange")]
    public ToggleNode Reward__Skill_Animus_Exchange { get; set; } = new ToggleNode(true);

    [Menu("Skill: Animus Splinters")]
    public ToggleNode Reward__Skill_Animus_Splinters { get; set; } = new ToggleNode(true);

    [Menu("1x Artificer's Orb")]
    public ToggleNode Reward__1x_Artificer_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Astrid's Creativity")]
    public ToggleNode Reward__1x_Astrid_s_Creativity { get; set; } = new ToggleNode(true);

    [Menu("1x Betrayal of Aldur")]
    public ToggleNode Reward__1x_Betrayal_of_Aldur { get; set; } = new ToggleNode(true);

    [Menu("Skill: Bitter Dead")]
    public ToggleNode Reward__Skill_Bitter_Dead { get; set; } = new ToggleNode(true);

    [Menu("1x Blazing Flux")]
    public ToggleNode Reward__1x_Blazing_Flux { get; set; } = new ToggleNode(true);

    [Menu("1x Body Rune")]
    public ToggleNode Reward__1x_Body_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Breath of Aldur")]
    public ToggleNode Reward__1x_Breath_of_Aldur { get; set; } = new ToggleNode(true);

    [Menu("1x Cadigan's Epiphany")]
    public ToggleNode Reward__1x_Cadigan_s_Epiphany { get; set; } = new ToggleNode(true);

    [Menu("1x Celestial Alloy")]
    public ToggleNode Reward__1x_Celestial_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Chaos Orb")]
    public ToggleNode Reward__1x_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Charging Rune")]
    public ToggleNode Reward__1x_Charging_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Chilling Flux")]
    public ToggleNode Reward__1x_Chilling_Flux { get; set; } = new ToggleNode(true);

    [Menu("Support: Concussive Runes")]
    public ToggleNode Reward__Support_Concussive_Runes { get; set; } = new ToggleNode(true);

    [Menu("Skill: Conductive Runes")]
    public ToggleNode Reward__Skill_Conductive_Runes { get; set; } = new ToggleNode(true);

    [Menu("1x Countess Seske's Rune of Archery")]
    public ToggleNode Reward__1x_Countess_Seske_s_Rune_of_Archery { get; set; } = new ToggleNode(true);

    [Menu("1x Courtesan Mannan's Rune of Cruelty")]
    public ToggleNode Reward__1x_Courtesan_Mannan_s_Rune_of_Cruelty { get; set; } = new ToggleNode(true);

    [Menu("1x Crackling Flux")]
    public ToggleNode Reward__1x_Crackling_Flux { get; set; } = new ToggleNode(true);

    [Menu("1x Craiceann's Rune of Recovery")]
    public ToggleNode Reward__1x_Craiceann_s_Rune_of_Recovery { get; set; } = new ToggleNode(true);

    [Menu("1x Craiceann's Rune of Warding")]
    public ToggleNode Reward__1x_Craiceann_s_Rune_of_Warding { get; set; } = new ToggleNode(true);

    [Menu("1x Cyclonic Alloy")]
    public ToggleNode Reward__1x_Cyclonic_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Desert Rune")]
    public ToggleNode Reward__1x_Desert_Rune { get; set; } = new ToggleNode(true);

    [Menu("Skill: Detonate Living")]
    public ToggleNode Reward__Skill_Detonate_Living { get; set; } = new ToggleNode(true);

    [Menu("1x Divine Orb")]
    public ToggleNode Reward__1x_Divine_Orb { get; set; } = new ToggleNode(true);

    [Menu("Skill: Eternal March")]
    public ToggleNode Reward__Skill_Eternal_March { get; set; } = new ToggleNode(true);

    [Menu("1x Exalted Orb")]
    public ToggleNode Reward__1x_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Expansive Alloy")]
    public ToggleNode Reward__1x_Expansive_Alloy { get; set; } = new ToggleNode(true);

    [Menu("Skill: Explosive Transmutation")]
    public ToggleNode Reward__Skill_Explosive_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("1x Farrul's Rune of Grace")]
    public ToggleNode Reward__1x_Farrul_s_Rune_of_Grace { get; set; } = new ToggleNode(true);

    [Menu("1x Farrul's Rune of the Chase")]
    public ToggleNode Reward__1x_Farrul_s_Rune_of_the_Chase { get; set; } = new ToggleNode(true);

    [Menu("1x Farrul's Rune of the Hunt")]
    public ToggleNode Reward__1x_Farrul_s_Rune_of_the_Hunt { get; set; } = new ToggleNode(true);

    [Menu("1x Fenumus' Rune of Agony")]
    public ToggleNode Reward__1x_Fenumus_Rune_of_Agony { get; set; } = new ToggleNode(true);

    [Menu("1x Fenumus' Rune of Draining")]
    public ToggleNode Reward__1x_Fenumus_Rune_of_Draining { get; set; } = new ToggleNode(true);

    [Menu("1x Fenumus' Rune of Spinning")]
    public ToggleNode Reward__1x_Fenumus_Rune_of_Spinning { get; set; } = new ToggleNode(true);

    [Menu("Support: Fist Of Kalguur")]
    public ToggleNode Reward__Support_Fist_Of_Kalguur { get; set; } = new ToggleNode(true);

    [Menu("Skill: Fragments Of The Past")]
    public ToggleNode Reward__Skill_Fragments_Of_The_Past { get; set; } = new ToggleNode(true);

    [Menu("Skill: Frostflame Nova")]
    public ToggleNode Reward__Skill_Frostflame_Nova { get; set; } = new ToggleNode(true);

    [Menu("1x Gemcutter's Prism")]
    public ToggleNode Reward__1x_Gemcutter_s_Prism { get; set; } = new ToggleNode(true);

    [Menu("1x Glacial Rune")]
    public ToggleNode Reward__1x_Glacial_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Glassblower's Bauble")]
    public ToggleNode Reward__1x_Glassblower_s_Bauble { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Adept Rune")]
    public ToggleNode Reward__1x_Greater_Adept_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Body Rune")]
    public ToggleNode Reward__1x_Greater_Body_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Chaos Orb")]
    public ToggleNode Reward__1x_Greater_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Charging Rune")]
    public ToggleNode Reward__1x_Greater_Charging_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Desert Rune")]
    public ToggleNode Reward__1x_Greater_Desert_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Exalted Orb")]
    public ToggleNode Reward__1x_Greater_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Glacial Rune")]
    public ToggleNode Reward__1x_Greater_Glacial_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Inspiration Rune")]
    public ToggleNode Reward__1x_Greater_Inspiration_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Iron Rune")]
    public ToggleNode Reward__1x_Greater_Iron_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Jeweller's Orb")]
    public ToggleNode Reward__1x_Greater_Jeweller_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Mind Rune")]
    public ToggleNode Reward__1x_Greater_Mind_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Orb of Augmentation")]
    public ToggleNode Reward__1x_Greater_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Orb of Transmutation")]
    public ToggleNode Reward__1x_Greater_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Rebirth Rune")]
    public ToggleNode Reward__1x_Greater_Rebirth_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Regal Orb")]
    public ToggleNode Reward__1x_Greater_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Resolve Rune")]
    public ToggleNode Reward__1x_Greater_Resolve_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Robust Rune")]
    public ToggleNode Reward__1x_Greater_Robust_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Stone Rune")]
    public ToggleNode Reward__1x_Greater_Stone_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Storm Rune")]
    public ToggleNode Reward__1x_Greater_Storm_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Vision Rune")]
    public ToggleNode Reward__1x_Greater_Vision_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Greater Ward Rune")]
    public ToggleNode Reward__1x_Greater_Ward_Rune { get; set; } = new ToggleNode(true);

    [Menu("Skill: Grim Pillars")]
    public ToggleNode Reward__Skill_Grim_Pillars { get; set; } = new ToggleNode(true);

    [Menu("Support: Healing Runes")]
    public ToggleNode Reward__Support_Healing_Runes { get; set; } = new ToggleNode(true);

    [Menu("1x Hedgewitch Assandra's Rune of Wisdom")]
    public ToggleNode Reward__1x_Hedgewitch_Assandra_s_Rune_of_Wisdom { get; set; } = new ToggleNode(true);

    [Menu("1x Hinekora's Lock")]
    public ToggleNode Reward__1x_Hinekora_s_Lock { get; set; } = new ToggleNode(true);

    [Menu("Skill: Hollow Shell")]
    public ToggleNode Reward__Skill_Hollow_Shell { get; set; } = new ToggleNode(true);

    [Menu("1x Inspiration Rune")]
    public ToggleNode Reward__1x_Inspiration_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Ire of Aldur")]
    public ToggleNode Reward__1x_Ire_of_Aldur { get; set; } = new ToggleNode(true);

    [Menu("1x Iron Rune")]
    public ToggleNode Reward__1x_Iron_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Katla's Gloom")]
    public ToggleNode Reward__1x_Katla_s_Gloom { get; set; } = new ToggleNode(true);

    [Menu("1x Kolr's Hunt")]
    public ToggleNode Reward__1x_Kolr_s_Hunt { get; set; } = new ToggleNode(true);

    [Menu("Krillson's Bay Key")]
    public ToggleNode Reward__Krillson_s_Bay_Key { get; set; } = new ToggleNode(true);

    [Menu("1x Lady Hestra's Rune of Winter")]
    public ToggleNode Reward__1x_Lady_Hestra_s_Rune_of_Winter { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Adept Rune")]
    public ToggleNode Reward__1x_Lesser_Adept_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Body Rune")]
    public ToggleNode Reward__1x_Lesser_Body_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Charging Rune")]
    public ToggleNode Reward__1x_Lesser_Charging_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Desert Rune")]
    public ToggleNode Reward__1x_Lesser_Desert_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Glacial Rune")]
    public ToggleNode Reward__1x_Lesser_Glacial_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Inspiration Rune")]
    public ToggleNode Reward__1x_Lesser_Inspiration_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Iron Rune")]
    public ToggleNode Reward__1x_Lesser_Iron_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Jeweller's Orb")]
    public ToggleNode Reward__1x_Lesser_Jeweller_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Mind Rune")]
    public ToggleNode Reward__1x_Lesser_Mind_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Rebirth Rune")]
    public ToggleNode Reward__1x_Lesser_Rebirth_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Resolve Rune")]
    public ToggleNode Reward__1x_Lesser_Resolve_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Robust Rune")]
    public ToggleNode Reward__1x_Lesser_Robust_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Stone Rune")]
    public ToggleNode Reward__1x_Lesser_Stone_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Storm Rune")]
    public ToggleNode Reward__1x_Lesser_Storm_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Vision Rune")]
    public ToggleNode Reward__1x_Lesser_Vision_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Lesser Ward Rune")]
    public ToggleNode Reward__1x_Lesser_Ward_Rune { get; set; } = new ToggleNode(true);

    [Menu("Skill: Leylines")]
    public ToggleNode Reward__Skill_Leylines { get; set; } = new ToggleNode(true);

    [Menu("1x Masterwork Rune")]
    public ToggleNode Reward__1x_Masterwork_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Medved's Boon")]
    public ToggleNode Reward__1x_Medved_s_Boon { get; set; } = new ToggleNode(true);

    [Menu("1x Medved's Saga")]
    public ToggleNode Reward__1x_Medved_s_Saga { get; set; } = new ToggleNode(true);

    [Menu("1x Medved's Tending")]
    public ToggleNode Reward__1x_Medved_s_Tending { get; set; } = new ToggleNode(true);

    [Menu("1x Mind Rune")]
    public ToggleNode Reward__1x_Mind_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Mirror of Kalandra")]
    public ToggleNode Reward__1x_Mirror_of_Kalandra { get; set; } = new ToggleNode(true);

    [Menu("1x Mystic Alloy")]
    public ToggleNode Reward__1x_Mystic_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Olroth's Boon")]
    public ToggleNode Reward__1x_Olroth_s_Boon { get; set; } = new ToggleNode(true);

    [Menu("1x Olroth's Saga")]
    public ToggleNode Reward__1x_Olroth_s_Saga { get; set; } = new ToggleNode(true);

    [Menu("1x Orb of Alchemy")]
    public ToggleNode Reward__1x_Orb_of_Alchemy { get; set; } = new ToggleNode(true);

    [Menu("1x Orb of Annulment")]
    public ToggleNode Reward__1x_Orb_of_Annulment { get; set; } = new ToggleNode(true);

    [Menu("1x Orb of Augmentation")]
    public ToggleNode Reward__1x_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("1x Orb of Chance")]
    public ToggleNode Reward__1x_Orb_of_Chance { get; set; } = new ToggleNode(true);

    [Menu("1x Orb of Transmutation")]
    public ToggleNode Reward__1x_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("1x Passion of Aldur")]
    public ToggleNode Reward__1x_Passion_of_Aldur { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Chaos Orb")]
    public ToggleNode Reward__1x_Perfect_Chaos_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Exalted Orb")]
    public ToggleNode Reward__1x_Perfect_Exalted_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Flux")]
    public ToggleNode Reward__1x_Perfect_Flux { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Jeweller's Orb")]
    public ToggleNode Reward__1x_Perfect_Jeweller_s_Orb { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Orb of Augmentation")]
    public ToggleNode Reward__1x_Perfect_Orb_of_Augmentation { get; set; } = new ToggleNode(true);

    [Menu("1x Perfect Orb of Transmutation")]
    public ToggleNode Reward__1x_Perfect_Orb_of_Transmutation { get; set; } = new ToggleNode(true);

    [Menu("Skill: Powered by Verisium")]
    public ToggleNode Reward__Skill_Powered_by_Verisium { get; set; } = new ToggleNode(true);

    [Menu("1x Prismatic Alloy")]
    public ToggleNode Reward__1x_Prismatic_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Protective Alloy")]
    public ToggleNode Reward__1x_Protective_Alloy { get; set; } = new ToggleNode(true);

    [Menu("Rare Unique Item")]
    public ToggleNode Reward__Rare_Unique_Item { get; set; } = new ToggleNode(true);

    [Menu("1x Rebirth Rune")]
    public ToggleNode Reward__1x_Rebirth_Rune { get; set; } = new ToggleNode(true);

    [Menu("Skill: Refutation")]
    public ToggleNode Reward__Skill_Refutation { get; set; } = new ToggleNode(true);

    [Menu("1x Regal Orb")]
    public ToggleNode Reward__1x_Regal_Orb { get; set; } = new ToggleNode(true);

    [Menu("Skill: Remnants of Kalguur")]
    public ToggleNode Reward__Skill_Remnants_of_Kalguur { get; set; } = new ToggleNode(true);

    [Menu("Skill: Repulsion")]
    public ToggleNode Reward__Skill_Repulsion { get; set; } = new ToggleNode(true);

    [Menu("1x Resolve Rune")]
    public ToggleNode Reward__1x_Resolve_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Robust Rune")]
    public ToggleNode Reward__1x_Robust_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Accumulation")]
    public ToggleNode Reward__1x_Rune_of_Accumulation { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Acrobatics")]
    public ToggleNode Reward__1x_Rune_of_Acrobatics { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Confrontation")]
    public ToggleNode Reward__1x_Rune_of_Confrontation { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Consistency")]
    public ToggleNode Reward__1x_Rune_of_Consistency { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Culmination")]
    public ToggleNode Reward__1x_Rune_of_Culmination { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Foundations")]
    public ToggleNode Reward__1x_Rune_of_Foundations { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Reach")]
    public ToggleNode Reward__1x_Rune_of_Reach { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Renown")]
    public ToggleNode Reward__1x_Rune_of_Renown { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of the Blossom")]
    public ToggleNode Reward__1x_Rune_of_the_Blossom { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of the Hunt")]
    public ToggleNode Reward__1x_Rune_of_the_Hunt { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of the Prism")]
    public ToggleNode Reward__1x_Rune_of_the_Prism { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Vital Flame")]
    public ToggleNode Reward__1x_Rune_of_Vital_Flame { get; set; } = new ToggleNode(true);

    [Menu("1x Rune of Vitality")]
    public ToggleNode Reward__1x_Rune_of_Vitality { get; set; } = new ToggleNode(true);

    [Menu("Support: Runeforged Blades")]
    public ToggleNode Reward__Support_Runeforged_Blades { get; set; } = new ToggleNode(true);

    [Menu("1x Runic Alloy")]
    public ToggleNode Reward__1x_Runic_Alloy { get; set; } = new ToggleNode(true);

    [Menu("Support: Runic Extraction")]
    public ToggleNode Reward__Support_Runic_Extraction { get; set; } = new ToggleNode(true);

    [Menu("Support: Runic Infusion")]
    public ToggleNode Reward__Support_Runic_Infusion { get; set; } = new ToggleNode(true);

    [Menu("Skill: Runic Reprieve")]
    public ToggleNode Reward__Skill_Runic_Reprieve { get; set; } = new ToggleNode(true);

    [Menu("1x Saqawal's Rune of Erosion")]
    public ToggleNode Reward__1x_Saqawal_s_Rune_of_Erosion { get; set; } = new ToggleNode(true);

    [Menu("1x Saqawal's Rune of Memory")]
    public ToggleNode Reward__1x_Saqawal_s_Rune_of_Memory { get; set; } = new ToggleNode(true);

    [Menu("1x Saqawal's Rune of the Sky")]
    public ToggleNode Reward__1x_Saqawal_s_Rune_of_the_Sky { get; set; } = new ToggleNode(true);

    [Menu("Support: Scouring Flame")]
    public ToggleNode Reward__Support_Scouring_Flame { get; set; } = new ToggleNode(true);

    [Menu("1x Serle's Triumph")]
    public ToggleNode Reward__1x_Serle_s_Triumph { get; set; } = new ToggleNode(true);

    [Menu("Skill: Skyfall")]
    public ToggleNode Reward__Skill_Skyfall { get; set; } = new ToggleNode(true);

    [Menu("1x Sovereign Alloy")]
    public ToggleNode Reward__1x_Sovereign_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Stone Rune")]
    public ToggleNode Reward__1x_Stone_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Storm Rune")]
    public ToggleNode Reward__1x_Storm_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Thane Girt's Rune of Wildness")]
    public ToggleNode Reward__1x_Thane_Girt_s_Rune_of_Wildness { get; set; } = new ToggleNode(true);

    [Menu("1x Thane Grannell's Rune of Mastery")]
    public ToggleNode Reward__1x_Thane_Grannell_s_Rune_of_Mastery { get; set; } = new ToggleNode(true);

    [Menu("1x Thane Leld's Rune of Spring")]
    public ToggleNode Reward__1x_Thane_Leld_s_Rune_of_Spring { get; set; } = new ToggleNode(true);

    [Menu("1x Thane Myrk's Rune of Summer")]
    public ToggleNode Reward__1x_Thane_Myrk_s_Rune_of_Summer { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 10)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_10 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 11)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_11 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 12)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_12 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 13)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_13 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 14)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_14 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 15)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_15 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 16)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_16 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 17)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_17 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 18)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_18 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 19)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_19 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 20)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_20 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 5)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_5 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 6)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_6 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 7)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_7 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 8)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_8 { get; set; } = new ToggleNode(true);

    [Menu("1x Thaumaturgic Flux (Level 9)")]
    public ToggleNode Reward__1x_Thaumaturgic_Flux_Level_9 { get; set; } = new ToggleNode(true);

    [Menu("1x The Greatwolf's Rune of Claws")]
    public ToggleNode Reward__1x_The_Greatwolf_s_Rune_of_Claws { get; set; } = new ToggleNode(true);

    [Menu("1x The Greatwolf's Rune of Willpower")]
    public ToggleNode Reward__1x_The_Greatwolf_s_Rune_of_Willpower { get; set; } = new ToggleNode(true);

    [Menu("1x The Runebinder's Alloy")]
    public ToggleNode Reward__1x_The_Runebinder_s_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x The Runefather's Alloy")]
    public ToggleNode Reward__1x_The_Runefather_s_Alloy { get; set; } = new ToggleNode(true);

    [Menu("1x Thrud's Might")]
    public ToggleNode Reward__1x_Thrud_s_Might { get; set; } = new ToggleNode(true);

    [Menu("1x Transcendent Alloy")]
    public ToggleNode Reward__1x_Transcendent_Alloy { get; set; } = new ToggleNode(true);

    [Menu("Skill: Triskelion Cascade")]
    public ToggleNode Reward__Skill_Triskelion_Cascade { get; set; } = new ToggleNode(true);

    [Menu("1x Uhtred's Boon")]
    public ToggleNode Reward__1x_Uhtred_s_Boon { get; set; } = new ToggleNode(true);

    [Menu("1x Uhtred's Saga")]
    public ToggleNode Reward__1x_Uhtred_s_Saga { get; set; } = new ToggleNode(true);

    [Menu("1x Uhtred's Sidereus")]
    public ToggleNode Reward__1x_Uhtred_s_Sidereus { get; set; } = new ToggleNode(true);

    [Menu("Uncut Skill Gem")]
    public ToggleNode Reward__Uncut_Skill_Gem { get; set; } = new ToggleNode(true);

    [Menu("1x Uncut Skill Gem (Level 19)")]
    public ToggleNode Reward__1x_Uncut_Skill_Gem_Level_19 { get; set; } = new ToggleNode(true);

    [Menu("1x Uncut Skill Gem (Level 20)")]
    public ToggleNode Reward__1x_Uncut_Skill_Gem_Level_20 { get; set; } = new ToggleNode(true);

    [Menu("Uncut Spirit Gem")]
    public ToggleNode Reward__Uncut_Spirit_Gem { get; set; } = new ToggleNode(true);

    [Menu("1x Uncut Spirit Gem (Level 19)")]
    public ToggleNode Reward__1x_Uncut_Spirit_Gem_Level_19 { get; set; } = new ToggleNode(true);

    [Menu("1x Uncut Spirit Gem (Level 20)")]
    public ToggleNode Reward__1x_Uncut_Spirit_Gem_Level_20 { get; set; } = new ToggleNode(true);

    [Menu("Uncut Support Gem")]
    public ToggleNode Reward__Uncut_Support_Gem { get; set; } = new ToggleNode(true);

    [Menu("Skill: Verisium Manifestations")]
    public ToggleNode Reward__Skill_Verisium_Manifestations { get; set; } = new ToggleNode(true);

    [Menu("Verisium Pile")]
    public ToggleNode Reward__Verisium_Pile { get; set; } = new ToggleNode(true);

    [Menu("Very Rare Unique item")]
    public ToggleNode Reward__Very_Rare_Unique_item { get; set; } = new ToggleNode(true);

    [Menu("1x Vision Rune")]
    public ToggleNode Reward__1x_Vision_Rune { get; set; } = new ToggleNode(true);

    [Menu("1x Void Flux")]
    public ToggleNode Reward__1x_Void_Flux { get; set; } = new ToggleNode(true);

    [Menu("Skill: Voltaic Barrier")]
    public ToggleNode Reward__Skill_Voltaic_Barrier { get; set; } = new ToggleNode(true);

    [Menu("1x Vorana's Boon")]
    public ToggleNode Reward__1x_Vorana_s_Boon { get; set; } = new ToggleNode(true);

    [Menu("1x Vorana's Carnage")]
    public ToggleNode Reward__1x_Vorana_s_Carnage { get; set; } = new ToggleNode(true);

    [Menu("1x Vorana's Saga")]
    public ToggleNode Reward__1x_Vorana_s_Saga { get; set; } = new ToggleNode(true);

    [Menu("1x Ward Rune")]
    public ToggleNode Reward__1x_Ward_Rune { get; set; } = new ToggleNode(true);

    [Menu("Skill: Wardbound Minions")]
    public ToggleNode Reward__Skill_Wardbound_Minions { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Annihilation")]
    public ToggleNode Reward__1x_Warding_Rune_of_Annihilation { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Armature")]
    public ToggleNode Reward__1x_Warding_Rune_of_Armature { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Bodyguards")]
    public ToggleNode Reward__1x_Warding_Rune_of_Bodyguards { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Courage")]
    public ToggleNode Reward__1x_Warding_Rune_of_Courage { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Desperation")]
    public ToggleNode Reward__1x_Warding_Rune_of_Desperation { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Disintegration")]
    public ToggleNode Reward__1x_Warding_Rune_of_Disintegration { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Equinox")]
    public ToggleNode Reward__1x_Warding_Rune_of_Equinox { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Glancing")]
    public ToggleNode Reward__1x_Warding_Rune_of_Glancing { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Heart")]
    public ToggleNode Reward__1x_Warding_Rune_of_Heart { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Hollowing")]
    public ToggleNode Reward__1x_Warding_Rune_of_Hollowing { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Obsession")]
    public ToggleNode Reward__1x_Warding_Rune_of_Obsession { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Protection")]
    public ToggleNode Reward__1x_Warding_Rune_of_Protection { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Reinforcement")]
    public ToggleNode Reward__1x_Warding_Rune_of_Reinforcement { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Salvaging")]
    public ToggleNode Reward__1x_Warding_Rune_of_Salvaging { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Stability")]
    public ToggleNode Reward__1x_Warding_Rune_of_Stability { get; set; } = new ToggleNode(true);

    [Menu("1x Warding Rune of Symbiosis")]
    public ToggleNode Reward__1x_Warding_Rune_of_Symbiosis { get; set; } = new ToggleNode(true);

    [Menu("Unique Amulet")]
    public ToggleNode Reward__Unique_Amulet { get; set; } = new ToggleNode(true);

    [Menu("Unique Belt")]
    public ToggleNode Reward__Unique_Belt { get; set; } = new ToggleNode(true);

    [Menu("Unique Body Armour")]
    public ToggleNode Reward__Unique_Body_Armour { get; set; } = new ToggleNode(true);

    [Menu("Unique Boots")]
    public ToggleNode Reward__Unique_Boots { get; set; } = new ToggleNode(true);

    [Menu("Unique Bow")]
    public ToggleNode Reward__Unique_Bow { get; set; } = new ToggleNode(true);

    [Menu("Unique Crossbow")]
    public ToggleNode Reward__Unique_Crossbow { get; set; } = new ToggleNode(true);

    [Menu("Unique Focus")]
    public ToggleNode Reward__Unique_Focus { get; set; } = new ToggleNode(true);

    [Menu("Unique Gloves")]
    public ToggleNode Reward__Unique_Gloves { get; set; } = new ToggleNode(true);

    [Menu("Unique Helmet")]
    public ToggleNode Reward__Unique_Helmet { get; set; } = new ToggleNode(true);

    [Menu("Unique Jewellery")]
    public ToggleNode Reward__Unique_Jewellery { get; set; } = new ToggleNode(true);

    [Menu("Unique One Hand Mace")]
    public ToggleNode Reward__Unique_One_Hand_Mace { get; set; } = new ToggleNode(true);

    [Menu("Unique Quarterstaff")]
    public ToggleNode Reward__Unique_Quarterstaff { get; set; } = new ToggleNode(true);

    [Menu("Unique Quiver")]
    public ToggleNode Reward__Unique_Quiver { get; set; } = new ToggleNode(true);

    [Menu("Unique Ring")]
    public ToggleNode Reward__Unique_Ring { get; set; } = new ToggleNode(true);

    [Menu("Unique Sceptre")]
    public ToggleNode Reward__Unique_Sceptre { get; set; } = new ToggleNode(true);

    [Menu("Unique Shield")]
    public ToggleNode Reward__Unique_Shield { get; set; } = new ToggleNode(true);

    [Menu("Unique Spear")]
    public ToggleNode Reward__Unique_Spear { get; set; } = new ToggleNode(true);

    [Menu("Unique Staff")]
    public ToggleNode Reward__Unique_Staff { get; set; } = new ToggleNode(true);

    [Menu("Unique Talisman")]
    public ToggleNode Reward__Unique_Talisman { get; set; } = new ToggleNode(true);

    [Menu("Unique Two Hand Mace")]
    public ToggleNode Reward__Unique_Two_Hand_Mace { get; set; } = new ToggleNode(true);

    [Menu("Unique Wand")]
    public ToggleNode Reward__Unique_Wand { get; set; } = new ToggleNode(true);

}
