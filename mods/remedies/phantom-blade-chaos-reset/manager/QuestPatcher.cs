using System.Text.Json.Nodes;

namespace PhantomBladeQuestManager;

sealed class QuestPatcher(QuestRule[] rules)
{
    const string Completed = "EBranchType::BranchCompleted";
    const string Active = "EBranchType::BranchActivating";
    const string Dormant = "EBranchType::None";
    const string QuestStarted = "EQuestCompletion::QC_Started";
    const string QuestSucceeded = "EQuestCompletion::QC_Succeded";
    const string SwordswomanDeparted = "zddy.红衣女剑客.荡绳离开";
    const string FormationComplete = "zddy.剑阵.完成任务";
    const string BracerClue = "xg.村庄.石斧护符情报";
    const string BracerCollected = "xg.村庄.石斧护符已获取";
    static JsonObject? Quest(JsonObject slot, string path) => (slot["Narrative"]?["NarrativeSavedQuests"] as JsonArray)?
        .OfType<JsonObject>().SingleOrDefault(q => q["QuestClass"]?.GetValue<string>() == path);
    static bool Has(JsonArray tags, string tag) => tags.Any(t => t?.GetValue<string>() == tag);
    static JsonObject? Branch(JsonObject? quest, string id) => (quest?["QuestBranches"] as JsonArray)?
        .OfType<JsonObject>().SingleOrDefault(branch => branch["BranchID"]?.GetValue<string>() == id);
    static bool CultStageMissing(JsonObject? quest, JsonArray tags) => Has(tags, FormationComplete)
        && quest?["QuestCompletion"]?.GetValue<string>() == QuestStarted
        && Branch(quest, "1")?["BranchType"]?.GetValue<string>() == Dormant;

    public QuestState[] Inspect(JsonObject slot) => rules.Select(rule => Inspect(slot, rule)).ToArray();

    public (JsonObject Slot, Change[] Changes) CompleteCultDaughter(JsonObject source)
    {
        string questPath = rules.Single(rule => rule.Id == "cultDaughter").Quest;
        var originalQuest = Quest(source, questPath);
        var tags = source["EventPipeline"]?["GlobalTags"] as JsonArray ?? [];
        SaveContainer.Require(originalQuest?["QuestCompletion"]?.GetValue<string>() == QuestStarted
            && Branch(originalQuest, "1")?["BranchType"]?.GetValue<string>() is Dormant or Active
            && new[] { FormationComplete, "zddy.击杀.剑阵1", "zddy.击杀.剑阵2" }.All(tag => Has(tags, tag)), "notEligible");
        var slot = source.DeepClone().AsObject();
        var quest = Quest(slot, questPath)!;
        var stage = Branch(quest, "1")!;
        string path = "Narrative/NarrativeSavedQuests/" + questPath;
        List<Change> changes = [];
        void Set(JsonObject obj, string key, JsonNode value, string parent)
        {
            if (JsonNode.DeepEquals(obj[key], value)) return;
            changes.Add(new(parent + "/" + key, obj[key]?.ToJsonString(JsonOptions.Compact), value.ToJsonString(JsonOptions.Compact)));
            obj[key] = value;
        }
        // The native FinishQuest takes branch 1, then saves; rewards were issued earlier.
        Set(stage, "BranchType", JsonValue.Create(Completed)!, path + "/1");
        Set(stage, "DirtyTime", source["PBZGameSystem"]!["DateTimeString"]!.DeepClone(), path + "/1");
        Set(quest, "QuestCompletion", JsonValue.Create(QuestSucceeded)!, path);
        var completed = quest["CompletedBranches"]?.DeepClone().AsArray() ?? new JsonArray();
        if (!Has(completed, "1")) completed.Add("1");
        Set(quest, "CompletedBranches", completed, path);
        return (slot, changes.ToArray());
    }

    QuestState Inspect(JsonObject slot, QuestRule rule)
    {
        if (rule.Id == "cultDaughter")
        {
            var quest = Quest(slot, rule.Quest);
            var globalTags = slot["EventPipeline"]?["GlobalTags"] as JsonArray ?? [];
            bool battleReady = Has(globalTags, SwordswomanDeparted) && !Has(globalTags, FormationComplete)
                && Branch(quest, "1")?["BranchType"]?.GetValue<string>() == Active;
            return new(rule.Id, false, quest?["QuestCompletion"]?.GetValue<string>() switch
            {
                "EQuestCompletion::QC_Succeded" => "completed",
                "EQuestCompletion::QC_Failed" => "cultFailed",
                "EQuestCompletion::QC_Started" => CultStageMissing(quest, globalTags) ? "cultStageMissing" : battleReady ? "cultBattleReady" : "cultProgress",
                _ => "notStarted"
            });
        }
        if (slot["PBZGameSystem"]?["NightmareSlotIndex"]?.GetValue<int>() >= 0)
            return new(rule.Id, false, "nightmare");
        if (slot["EventPipeline"] is not JsonObject pipeline || pipeline["GlobalTags"] is not JsonArray tags)
            return new(rule.Id, false, "schema");
        if (rule.Id == "stoneBracer")
        {
            if (slot["Inventory"]?["AccessoryInfos"] is not JsonObject accessories)
                return new(rule.Id, false, "schema");
            if (Has(tags, BracerCollected) || accessories.ContainsKey("AC_ResistNonBreak"))
                return new(rule.Id, false, "bracerCollected");
            if (Has(tags, BracerClue)) return new(rule.Id, false, "bracerReady");
            return Has(tags, "xg.击杀.斧老大")
                ? new(rule.Id, true, "bracerMissingClue") : new(rule.Id, false, "bracerPrerequisite");
        }
        if (pipeline["ComponentInfoJson"] is not JsonObject || pipeline["ComponentsOwnerActorInfo"] is not JsonObject
            || pipeline["ExecuteNodeGuid"] is not JsonObject || slot["AISystem"]?["SpawnerSaveData"] is not JsonObject spawners)
            return new(rule.Id, false, "schema");
        if (rule.Id == "chaos")
        {
            var quest = Quest(slot, rule.Quest);
            if (quest == null) return new(rule.Id, false, "notStarted");
            string? completion = quest["QuestCompletion"]?.GetValue<string>();
            if (completion != "EQuestCompletion::QC_Failed")
                return new(rule.Id, false, completion == "EQuestCompletion::QC_Succeded" ? "completed" : "inProgress");
            if (quest["QuestBranches"] is not JsonArray branches || quest["CompletedBranches"] is not JsonArray)
                return new(rule.Id, false, "schema");
            bool Ready(string id) => branches.OfType<JsonObject>().Any(b => b["BranchID"]?.GetValue<string>() == id && b["BranchType"]?.GetValue<string>() == Completed);
            if (!new[] { "S1", "S2", "S3", "S4", "S5" }.All(Ready)) return new(rule.Id, false, "prerequisites");
            if (!new[] { "S6", "1" }.All(id => branches.OfType<JsonObject>().Any(b => b["BranchID"]?.GetValue<string>() == id)))
                return new(rule.Id, false, "schema");
            if (!Has(tags, "fjzl.三头魔偶.死亡") || Has(tags, "fjzl.三头魔偶.p3")) return new(rule.Id, false, "differentEnding");
            return spawners.ContainsKey(rule.Spawners[0]) ? new(rule.Id, true, "failedEligible") : new(rule.Id, false, "spawnMissing");
        }
        // Flower collection and the swordswoman's narrative stage are separate records.
        if (slot["Narrative"]?["NarrativeSavedQuests"] is not JsonArray) return new(rule.Id, false, "schema");
        var cult = Quest(slot, rule.Quest);
        if (cult != null && cult["QuestCompletion"]?.GetValue<string>() != QuestSucceeded)
        {
            if (cult["QuestBranches"] is not JsonArray branches || branches.Count != 2
                || Branch(cult, "S1") == null || Branch(cult, "1") == null)
                return new(rule.Id, false, "schema");
        }
        if (CultStageMissing(cult, tags)) return new(rule.Id, true, "cultResetEligible");
        if (Has(tags, SwordswomanDeparted)) return new(rule.Id, false, "survived");
        if (!Has(tags, "zddy.击杀.红衣女剑客")) return new(rule.Id, false, "noEarlyKill");
        return spawners.ContainsKey(rule.Spawners[0]) ? new(rule.Id, true, "earlyKillEligible") : new(rule.Id, false, "spawnMissing");
    }

    public (JsonObject Slot, Change[] Changes) Prepare(JsonObject source, string[] ids)
    {
        SaveContainer.Require(ids.Length > 0 && ids.Distinct().Count() == ids.Length, "noSelection");
        var chosen = ids.Select(id => rules.Single(r => r.Id == id)).ToArray();
        foreach (var rule in chosen) SaveContainer.Require(Inspect(source, rule).Eligible, "notEligible");
        var slot = source.DeepClone().AsObject();
        List<Change> changes = [];
        void Set(JsonObject obj, string key, JsonNode? value, string path)
        {
            if (JsonNode.DeepEquals(obj[key], value)) return;
            changes.Add(new(path + "/" + key, obj[key]?.ToJsonString(JsonOptions.Compact), value?.ToJsonString(JsonOptions.Compact)));
            obj[key] = value;
        }
        void Remove(JsonObject obj, string key, string path)
        {
            if (!obj.ContainsKey(key)) return;
            changes.Add(new(path + "/" + key, obj[key]?.ToJsonString(JsonOptions.Compact), null));
            obj.Remove(key);
        }
        var pipeline = slot["EventPipeline"]!.AsObject();
        void ResumeSwordswomanQuest(QuestRule rule)
        {
            // BPF_HongYiNvJianKe_Leave records this tag and begins MoJiaoZhiNv at branch 1.
            var tags = pipeline["GlobalTags"]!.DeepClone().AsArray();
            if (!Has(tags, SwordswomanDeparted)) tags.Add(SwordswomanDeparted);
            Set(pipeline, "GlobalTags", tags, "EventPipeline");
            var quest = Quest(slot, rule.Quest);
            if (quest?["QuestCompletion"]?.GetValue<string>() == QuestSucceeded) return;
            string time = slot["PBZGameSystem"]?["DateTimeString"]?.GetValue<string>()
                ?? DateTime.UtcNow.ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture);
            string questPath = "Narrative/NarrativeSavedQuests/" + rule.Quest;
            if (quest == null)
            {
                // The verified quest template contains only S1 and 1, with no task counters.
                // Beginning at 1 does not invent completion of the optional S1 rumour.
                quest = new JsonObject
                {
                    ["QuestClass"] = rule.Quest,
                    ["QuestBranches"] = new JsonArray(
                        new JsonObject { ["BranchID"] = "S1", ["BranchType"] = Dormant, ["DirtyTime"] = "0001.01.01-00.00.00" },
                        new JsonObject { ["BranchID"] = "1", ["BranchType"] = Active, ["DirtyTime"] = time }),
                    ["QuestCompletion"] = QuestStarted,
                    ["CompletedBranches"] = new JsonArray(),
                    ["ReachedStateNames"] = new JsonArray()
                };
                changes.Add(new(questPath, null, quest.ToJsonString(JsonOptions.Compact)));
                slot["Narrative"]!["NarrativeSavedQuests"]!.AsArray().Add(quest);
                return;
            }
            Set(quest, "QuestCompletion", JsonValue.Create(QuestStarted), questPath);
            var stage = Branch(quest, "1")!;
            Set(stage, "BranchType", JsonValue.Create(Active), questPath + "/1");
            Set(stage, "DirtyTime", JsonValue.Create(time), questPath + "/1");
            Set(quest, "CompletedBranches", new JsonArray(quest["QuestBranches"]!.AsArray().OfType<JsonObject>()
                .Where(branch => branch["BranchType"]?.GetValue<string>() == Completed)
                .Select(branch => branch["BranchID"]!.DeepClone()).ToArray()), questPath);
        }
        foreach (var rule in chosen)
        {
            if (rule.Id == "stoneBracer")
            {
                // The grave checks this clue before offering its normal reward interaction.
                // Leave the collected flag and inventory to the game's reward flow.
                var tags = pipeline["GlobalTags"]!.DeepClone().AsArray();
                tags.Add(BracerClue);
                Set(pipeline, "GlobalTags", tags, "EventPipeline");
                continue;
            }
            if (rule.Id == "chaos")
            {
                var quest = Quest(slot, rule.Quest)!;
                string path = "Narrative/NarrativeSavedQuests/" + rule.Quest;
                Set(quest, "QuestCompletion", JsonValue.Create("EQuestCompletion::QC_Started"), path);
                foreach (var branch in quest["QuestBranches"]!.AsArray().OfType<JsonObject>())
                    if (branch["BranchID"]?.GetValue<string>() is "S6" or "1")
                        Set(branch, "BranchType", JsonValue.Create(Active), path + "/" + branch["BranchID"]!.GetValue<string>());
                Set(quest, "CompletedBranches", new JsonArray(quest["CompletedBranches"]!.AsArray()
                    .Where(n => n?.GetValue<string>() is not ("S6" or "1")).Select(n => n?.DeepClone()).ToArray()), path);
            }
            HashSet<string> removed = [.. rule.Tags];
            void ClearTags(JsonObject obj, string key, string path)
            {
                if (obj[key] is JsonArray array)
                    Set(obj, key, new JsonArray(array.Where(n => !removed.Contains(n!.GetValue<string>())).Select(n => n!.DeepClone()).ToArray()), path);
            }
            ClearTags(pipeline, "GlobalTags", "EventPipeline");
            foreach (var (key, info) in pipeline["ComponentInfoJson"]!.AsObject())
                if (info is JsonObject obj) ClearTags(obj, "EventTag", "EventPipeline/ComponentInfoJson/" + key);
            foreach (string id in rule.ExecutionNodes) Remove(pipeline["ExecuteNodeGuid"]!.AsObject(), id, "EventPipeline/ExecuteNodeGuid");
            foreach (string id in rule.Spawners) Remove(slot["AISystem"]!["SpawnerSaveData"]!.AsObject(), id, "AISystem/SpawnerSaveData");
            foreach (string id in rule.Owners) Remove(pipeline["ComponentsOwnerActorInfo"]!.AsObject(), id, "EventPipeline/ComponentsOwnerActorInfo");
            foreach (string section in new[] { "LoadedDataLayer", "UnLoadedDataLayer" })
                if (pipeline[section] is JsonObject worlds)
                    foreach (string world in worlds.Select(p => p.Key).ToArray())
                        if (worlds[world] is JsonArray layers)
                            Set(worlds, world, new JsonArray(layers.Where(n => !rule.DataLayers.Contains(n!.GetValue<string>()))
                                .Select(n => n!.DeepClone()).ToArray()), "EventPipeline/" + section);
            if (rule.Id == "sevenStar") ResumeSwordswomanQuest(rule);
        }
        SaveContainer.Require(changes.Count != 0 && chosen.All(rule => !Inspect(slot, rule).Eligible), "verification");
        // Everything outside the explicit quest and encounter sections remains identical.
        foreach (var pair in source)
            if (pair.Key is not ("Narrative" or "EventPipeline" or "AISystem"))
                SaveContainer.Require(JsonNode.DeepEquals(pair.Value, slot[pair.Key]), "verification");
        var editedQuestPaths = chosen.Where(rule => rule.Id is "chaos" or "sevenStar").Select(rule => rule.Quest).ToHashSet();
        if (source["Narrative"] is JsonObject narrative)
        {
            foreach (var pair in narrative)
                if (pair.Key != "NarrativeSavedQuests") SaveContainer.Require(JsonNode.DeepEquals(pair.Value, slot["Narrative"]?[pair.Key]), "verification");
            foreach (var quest in (narrative["NarrativeSavedQuests"] as JsonArray ?? []).OfType<JsonObject>())
                if (quest["QuestClass"]?.GetValue<string>() is string path && !editedQuestPaths.Contains(path))
                    SaveContainer.Require(JsonNode.DeepEquals(quest, Quest(slot, path)), "verification");
        }
        return (slot, changes.ToArray());
    }
}
