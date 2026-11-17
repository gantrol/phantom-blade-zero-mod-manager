using System.Text.Json.Nodes;

namespace PhantomBladeQuestManager;

static class GuiTuRegret
{
    const string Accepted = "ljc.老根头.同意请求";
    const string Entered = "pz.后街.进入";
    const string Collected = "ljc.老根头.获得木梳子";
    const string Completed = "EBranchType::BranchCompleted";
    const string Active = "EBranchType::BranchActivating";
    const string Dormant = "EBranchType::None";
    static readonly string[] BranchIds = ["1", "2", "3-A", "4", "3-B"];
    static readonly string[] LaterTags = ["car.老根头.出现", "car.老根头.第一次对话之后", "car.老根头.死亡"];

    static bool Has(JsonArray tags, string tag) => tags.Any(value => value?.GetValue<string>() == tag);
    static JsonObject[] Quests(JsonObject slot, QuestRule rule) =>
        (slot["Narrative"]?["NarrativeSavedQuests"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(quest => quest["QuestClass"]?.GetValue<string>() == rule.Quest).ToArray();
    static bool Owns(JsonObject slot, string item) =>
        slot["Inventory"]?["InventoryInfos"]?[item] is JsonObject info
        && info["CurrentAmount"]?.GetValue<int>() > 0
        && info["AcquiredCycle"]?.GetValue<int>() == slot["PBZGameSystem"]?["CycleCount"]?.GetValue<int>();

    public static QuestState Inspect(JsonObject slot, QuestRule rule)
    {
        QuestState State(bool eligible, string status) => new(rule.Id, eligible, status);
        var matches = Quests(slot, rule);
        if (matches.Length == 0) return State(false, "notStarted");
        if (matches.Length != 1) return State(false, "schema");
        var quest = matches[0];
        string? completion = quest["QuestCompletion"]?.GetValue<string>();
        if (completion != "EQuestCompletion::QC_Failed")
            return State(false, completion == "EQuestCompletion::QC_Succeded" ? "completed"
                : completion == "EQuestCompletion::QC_Started" ? "inProgress" : "notStarted");
        if (slot["EventPipeline"] is not JsonObject pipeline || pipeline["GlobalTags"] is not JsonArray tags
            || pipeline["ComponentsOwnerActorInfo"] is not JsonObject || pipeline["ExecuteNodeGuid"] is not JsonObject
            || slot["Inventory"]?["InventoryInfos"] is not JsonObject
            || slot["PBZGameSystem"]?["CycleCount"] is not JsonValue
            || slot["PBZGameSystem"]?["DateTimeString"] is not JsonValue
            || quest["QuestBranches"] is not JsonArray branches || branches.Count != BranchIds.Length
            || quest["CompletedBranches"] is not JsonArray || quest["ReachedStateNames"] is not JsonArray)
            return State(false, "schema");
        if (!BranchIds.All(id => branches.OfType<JsonObject>().Count(branch => branch["BranchID"]?.GetValue<string>() == id) == 1)
            || branches.OfType<JsonObject>().Any(branch => branch["BranchType"]?.GetValue<string>() is not
                (Completed or Active or Dormant or "EBranchType::Failed" or "EBranchType::BranchLocked")))
            return State(false, "schema");

        // The backstreet entrance fails GuiTu only when Lao Gentou's request was declined or missed.
        // Do not reopen a successful hand-in or a later carriage scene.
        if (!Has(tags, Entered) || Has(tags, Accepted) || LaterTags.Any(tag => Has(tags, tag))
            || branches.OfType<JsonObject>().Any(branch => branch["BranchID"]?.GetValue<string>() == "4"
                && branch["BranchType"]?.GetValue<string>() == Completed))
            return State(false, "guiTuDifferentOutcome");
        bool collected = Has(tags, Collected);
        if (collected != Owns(slot, "I_06010")) return State(false, "guiTuItemMismatch");
        return State(true, collected ? "guiTuReturnReady" : Owns(slot, "K_05004") ? "guiTuSearchReady" : "guiTuRestoreKey");
    }

    public static void Apply(JsonObject slot, QuestRule rule,
        Action<JsonObject, string, JsonNode?, string> set,
        Action<JsonObject, string, string> remove)
    {
        var quest = Quests(slot, rule).Single();
        var pipeline = slot["EventPipeline"]!.AsObject();
        var tags = pipeline["GlobalTags"]!.DeepClone().AsArray();
        bool collected = Has(tags, Collected);
        tags.Add(Accepted);
        set(pipeline, "GlobalTags", tags, "EventPipeline");
        string path = "Narrative/NarrativeSavedQuests/" + rule.Quest;
        string time = slot["PBZGameSystem"]!["DateTimeString"]!.GetValue<string>();
        if (!collected && !Owns(slot, "K_05004"))
        {
            // The native accept choice grants this key before beginning 3-A. Accepting also
            // hides the corpse that otherwise grants it, so restore the missing key together.
            var inventory = slot["Inventory"]!["InventoryInfos"]!.AsObject();
            var key = inventory["K_05004"]?.DeepClone().AsObject() ?? new JsonObject();
            key["CurrentAmount"] = 1;
            key["MaxAmount"] = 1;
            key["FirstAddTime"] = time;
            key["DirtyTime"] = time;
            key["AcquiredCycle"] = slot["PBZGameSystem"]!["CycleCount"]!.DeepClone();
            set(inventory, "K_05004", key, "Inventory/InventoryInfos");
        }
        set(quest, "QuestCompletion", JsonValue.Create("EQuestCompletion::QC_Started"), path);
        var branches = quest["QuestBranches"]!.AsArray().OfType<JsonObject>().ToArray();
        foreach (var branch in branches)
        {
            string id = branch["BranchID"]!.GetValue<string>();
            string? before = branch["BranchType"]?.GetValue<string>();
            string after = id switch
            {
                "3-A" => collected ? Completed : Active,
                "4" => collected ? Active : Dormant,
                "3-B" => Dormant,
                _ => before == Completed ? Completed : Dormant,
            };
            if (before == after) continue;
            set(branch, "BranchType", JsonValue.Create(after), path + "/" + id);
            set(branch, "DirtyTime", JsonValue.Create(time), path + "/" + id);
        }
        set(quest, "CompletedBranches", new JsonArray(branches
            .Where(branch => branch["BranchType"]?.GetValue<string>() == Completed)
            .Select(branch => branch["BranchID"]!.DeepClone()).ToArray()), path);
        set(quest, "ReachedStateNames", new JsonArray(quest["ReachedStateNames"]!.AsArray()
            .Where(state => state?.GetValue<string>() != "GuiTu_PBZQuestState_4")
            .Select(state => state?.DeepClone()).ToArray()), path);

        // Replay only the return NPC's initial visibility/pose and accepted-request visibility.
        // Item grants, comb collection and hand-in execution records are never cleared.
        foreach (string id in rule.ExecutionNodes) remove(pipeline["ExecuteNodeGuid"]!.AsObject(), id, "EventPipeline/ExecuteNodeGuid");
        foreach (string id in rule.Owners) remove(pipeline["ComponentsOwnerActorInfo"]!.AsObject(), id, "EventPipeline/ComponentsOwnerActorInfo");
    }

    public static void VerifyInventory(JsonObject source, JsonObject result)
    {
        var expected = source["Inventory"]!.DeepClone().AsObject();
        var actual = result["Inventory"]!.AsObject();
        // Only the request key is allowed to differ; equipment, other items and the comb stay exact.
        expected["InventoryInfos"]!.AsObject().Remove("K_05004");
        var comparison = actual.DeepClone().AsObject();
        comparison["InventoryInfos"]!.AsObject().Remove("K_05004");
        SaveContainer.Require(JsonNode.DeepEquals(expected, comparison), "verification");
        SaveContainer.Require(Owns(result, "I_06010") || Owns(result, "K_05004"), "verification");
    }
}
