namespace PhantomBladeMap.Core.I18n;

public sealed class LocaleMessageTable
{
    public required string FontAssetPath { get; init; }
    public required string FontFaceName { get; init; }
    public required IReadOnlyDictionary<string, string> Messages { get; init; }

    public string Get(string key) =>
        Messages.TryGetValue(key, out var value) ? value : key;

    public string Format(string key, params object[] args) =>
        Messages.TryGetValue(key, out var pattern)
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, args)
            : key;
}

public static class LocaleResources
{
    private static readonly LocaleMessageTable ZhCn = new()
    {
        FontAssetPath = "/Game/PBZContent/UI/Fonts/ZH_ZiKuTangKuangShanKaiShuJF_Font.ZH_ZiKuTangKuangShanKaiShuJF_Font",
        FontFaceName = "Default",
        Messages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["map.atlas"] = "行迹",
            ["map.pangzhen"] = "庞镇",
            ["map.houjie"] = "后街",
            ["map.lujiacun"] = "芦家村",
            ["map.meiguhu"] = "梅谷湖",
            ["map.meigu_yiji"] = "幻术遗迹",
            ["map.dengdingshandao"] = "枷笼山",
            ["map.ximeng"] = "西盟",
            ["map.duanhunya"] = "断魂崖",
            ["map.duanhunshan"] = "断魂山",
            ["survey.saveFailed"] = "测绘保存失败",
            ["region.failed"] = "区域数据不可用",
            ["region.navigation_pilot"] = "测绘样区",
            ["floor.survey_1"] = "下层测绘",
            ["floor.survey_2"] = "上层测绘",
            ["floor.namedPinned"] = "固定 · {0}",
            ["floor.native"] = "原生层 {0}/{1}",
            ["storage.failed"] = "标记保存失败",
            ["catalog.failed"] = "探测记录保存失败",
            ["map.fujizhilin"] = "赤林",
            ["map.xigu"] = "溪谷",
            ["map.zhengdaodaying"] = "正道大营",
            ["map.b03_pilot"] = "赤林",
            ["map.unknown"] = "",
            ["floor.current"] = "高度切片",
            ["floor.layer"] = "层 {0}/{1}",
            ["floor.pinned"] = "固定层 {0}/{1}",
            ["floor.offset"] = "高度 {0:+0;-0;0} 米",
            ["floor.overview"] = "全域概览",
            ["survey.uncovered"] = "此处未测绘",
            ["survey.regionMissing"] = "当前区域地形未收录",
            ["survey.loading"] = "测绘载入中",
            ["survey.partial"] = "局部测绘",
            ["survey.limited"] = "局部测绘 · 简化",
            ["scale.meters"] = "{0} 米",
            ["filter.item"] = "物品",
            ["filter.chest"] = "宝箱",
            ["filter.npc"] = "人物",
            ["filter.quest"] = "任务",
            ["filter.marker"] = "标记",
            ["filter.uncollected"] = "未获取",
            ["survey.structure.house"] = "屋舍",
            ["survey.structure.stair"] = "石阶",
            ["survey.structure.bridge"] = "木桥",
            ["survey.structure.archway"] = "牌坊",
            ["survey.structure.fence"] = "围栏",
        },
    };

    private static readonly LocaleMessageTable En = new()
    {
        FontAssetPath = "/Game/PBZContent/UI/Fonts/EN_AlverataCYR-Light_Font.EN_AlverataCYR-Light_Font",
        FontFaceName = "Default",
        Messages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["map.atlas"] = "Wayfarer",
            ["map.pangzhen"] = "Pangzhen",
            ["map.houjie"] = "Backstreet",
            ["map.lujiacun"] = "Lujia Village",
            ["map.meiguhu"] = "Meigu Lake",
            ["map.meigu_yiji"] = "Illusion Ruins",
            ["map.dengdingshandao"] = "Jialong Mountain",
            ["map.ximeng"] = "Ximeng",
            ["map.duanhunya"] = "Duanhun Cliff",
            ["map.duanhunshan"] = "Duanhun Mountain",
            ["survey.saveFailed"] = "Survey save failed",
            ["region.failed"] = "Region data unavailable",
            ["region.navigation_pilot"] = "Survey area",
            ["floor.survey_1"] = "Lower survey",
            ["floor.survey_2"] = "Upper survey",
            ["floor.namedPinned"] = "Pinned · {0}",
            ["floor.native"] = "Native layer {0}/{1}",
            ["storage.failed"] = "Marker save failed",
            ["catalog.failed"] = "Detection storage failed",
            ["map.fujizhilin"] = "Crimson Forest",
            ["map.xigu"] = "Valley",
            ["map.zhengdaodaying"] = "Grand Camp",
            ["map.b03_pilot"] = "Crimson Forest",
            ["map.unknown"] = "",
            ["floor.current"] = "Height slice",
            ["floor.layer"] = "Layer {0}/{1}",
            ["floor.pinned"] = "Pinned {0}/{1}",
            ["floor.offset"] = "Height {0:+0;-0;0} m",
            ["floor.overview"] = "Overview",
            ["survey.uncovered"] = "Unsurveyed area",
            ["survey.regionMissing"] = "Region terrain not recorded",
            ["survey.loading"] = "Loading survey",
            ["survey.partial"] = "Partial survey",
            ["survey.limited"] = "Partial · simplified",
            ["scale.meters"] = "{0} m",
            ["filter.item"] = "Items",
            ["filter.chest"] = "Chests",
            ["filter.npc"] = "NPCs",
            ["filter.quest"] = "Quests",
            ["filter.marker"] = "Marks",
            ["filter.uncollected"] = "Left",
            ["survey.structure.house"] = "House",
            ["survey.structure.stair"] = "Stairs",
            ["survey.structure.bridge"] = "Bridge",
            ["survey.structure.archway"] = "Archway",
            ["survey.structure.fence"] = "Fence",
        },
    };

    private static readonly IReadOnlyDictionary<string, LocaleMessageTable> Tables =
        new Dictionary<string, LocaleMessageTable>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = ZhCn,
            ["zh"] = ZhCn,
            ["en"] = En,
            ["en-US"] = En,
        };

    public static LocaleMessageTable Resolve(string? localeCode)
    {
        if (!string.IsNullOrWhiteSpace(localeCode) && Tables.TryGetValue(localeCode.Trim(), out var table))
        {
            return table;
        }
        return ZhCn;
    }
}
