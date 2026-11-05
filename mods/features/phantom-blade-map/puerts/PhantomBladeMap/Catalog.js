"use strict";
// USTRUCT wrappers live only inside materialize(). No UFUNCTION, callback or
// generator runs while walking a struct's child containers. Across frames the
// queue contains strings and numbers only.
const specs = [
    ["BellDatas", "bell", "BellID"], ["WeaponItemDatas", "weapon", "WeaponID", "MainWeaponInfos"],
    ["ShaChiWeaponItemDatas", "shadowWeapon", "WeaponID", "ShaqiWeaponInfos"],
    ["AccessoryItemDatas", "accessory", "AccessoryID", "Accessories"],
    ["MedicineItemDatas", "medicine", "MedicineID", "MedicineInfos"],
    ["RongShu", "treeHeart"], ["LongZhu", "challenge"], ["DaoCaoRen", "challenge"],
    ["HupoItemData", "amber"], ["HeiShiItemData", "blackstone"],
    ["MemoryFlowerData", "flower", "ItemID", "Inventories"],
    ["YaoShiItemData", "key", "ItemID", "Inventories"], ["PrimaryItemData", "supply", "ItemId"]
];
const regions = require("./atlas/regions");
const Diagnostics = require("./Diagnostics");
const {DiscoveryCatalog} = require("./DiscoveryCatalog");
const {source,savedState,describeItem,classifyGroup} = require("./SourceState");
const Availability = require("./CollectionAvailability");
const Treasures = require("./Treasures");
const name = value => value == null || String(value) === "None" ? "" : String(value);
function localizedName(UE, key) {
    if (!key) return "";
    // Materialize FText through Unreal; JS String() only describes its wrapper.
    const text = UE.PBZBlueprintFunctionLibrary.MakeTextByKey(key);
    return UE.KismetTextLibrary.Conv_TextToString(text);
}
function insidePolygon(x, y, poly) {
    let inside = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
        const a = poly[i], b = poly[j];
        if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
    }
    return inside;
}
function authoredLayer(mapID, x, y, z) {
    const world = (regions.nativeRegions || []).find(r => r.id.toLowerCase() === mapID.toLowerCase())?.world || mapID.toLowerCase();
    for (const area of regions.areas || []) {
        if (area.world === world && insidePolygon(x, y, area.footprint)) {
            const idx = (area.layers || []).findIndex(l => z >= l.min && z < l.max);
            if (idx >= 0) return idx;
        }
    }
    return -1;
}
function resolveCanonicalMapIDs(asset, mapIDs) {
    const wanted = new Set(mapIDs.map(id => String(id).toLowerCase()));
    const result = [];
    const datas = asset?.MapItemDatas;
    if (!datas) return mapIDs;
    const maxIdx = datas.GetMaxIndex();
    for (let i = 0; i < maxIdx; i++) {
        if (!datas.IsValidIndex(i)) continue;
        const key = String(datas.GetKey(i));
        if (wanted.has(key.toLowerCase())) {
            result.push(key);
            wanted.delete(key.toLowerCase());
        }
    }
    for (const rest of wanted) result.push(rest);
    return result;
}
function materialize(asset, mapIDs, category) {
    const spec = specs[category], points = [];
    for (const mapID of mapIDs) {
        const region = asset.MapItemDatas.GetRef(mapID);
        if (!region) continue;
        const rows = region[spec[0]];
        for (let i = 0, n = rows.Num(); i < n; i++) {
            const row = rows.GetRef(i), p = row.Location;
            const point = { id: mapID + ":" + spec[0] + ":" + i, mapID, category: spec[1],
                x: p.X, y: p.Y, z: p.Z, reward: spec[2] ? name(row[spec[2]]) : "", inventory: spec[3],
                nodes: [], save: "", domain: "", layer: -1, state: "unknown", label: "" };
            if (spec[1] !== "bell") {
                const guids = row.NodeGuids;
                for (let j = 0, n = guids.Num(); j < n; j++) point.nodes.push(name(guids.Get(j)));
                point.save = name(row.SaveGuidString); point.domain = name(row.MobDomainName);
                point.flow = name(row.FlowAssetName);
            }
            if(point.category==="supply" && !point.reward && source(point.save)?.category==="chest")point.category="chest";
            Treasures.classify(point);
            points.push(point);
            if (spec[1] !== "bell" && /^BPF_XiaoZhang/.test(point.flow)) {
                // The game's quest reward catalog supplies this interaction point;
                // it does not assert that a moving NPC is currently standing here.
                points.push({ ...point, id: mapID + ":npc:xiaozhang:" + point.x + ":" + point.y, category: "npc",
                    landmark: true, labelKey: "npcXiaoZhang", inventory: undefined, reward: "", nodes: [] });
            }
        }
    }
    return points;
}
function context(page, UE, helper) {
    const get = key => helper.getWorldSubsystem(page, UE[key].StaticClass());
    return { game: get("PBZGameSystem"), bag: get("PBZBackPackSubsystem"), bells: get("BellManager"),
        events: get("EventPipelineManager"), actors: get("PBZLevelActorManager"), map: get("MapSubsystem") };
}
function resolve(point, c, UE, page, language) {
    const { game, bag, bells, events, actors } = c;
    if (!game || !bag || !bells || !events || !actors) return;
    let done = false, known = true;
    if (point.landmark) known = true;
    else if (Treasures.contains(point.reward)) {
        c.availabilityTags ||= Availability.snapshot(events);
        done=Treasures.collected(point.reward,bag,game.GetCycleCount(),c.availabilityTags);
    }
    else if (point.category === "bell") done = bells.GetAllActivateBellIDs().Contains(point.reward);
    else if (point.category === "key") {
        done = bag.Inventories.GetRef(point.reward) !== undefined;
        if (!done && page.W_MapItemCompletionUI)
            done = !page.W_MapItemCompletionUI.hasSwitchLockObj(c.map, point.reward, point.mapID);
    } else if (point.inventory) {
        if (point.category === "flower") {
            const prop = UE.PBZBackPackSubsystem.GetPropData(point.reward);
            if (!prop) { point.excluded = true; return; }
            const isWhole = prop.ItemType === UE.EItemTypes.MemoryFlower;
            const key = name(prop.ItemName.Key);
            point.label = localizedName(UE, key);
            if (isWhole && bag.GetOrderedMemoryFlowerPetals(point.reward).Num() > 1) { point.excluded = true; return; }
        }
        const cycle = game.GetCycleCount();
        const item = bag[point.inventory].GetRef(point.reward);
        done = !!item && item.AcquiredCycle === cycle;
    } else if(point.category==="amber" || point.category==="blackstone") {
        // Match the game's completion panel for these two native categories.
        done=!!((point.save && actors.HasSaveDataByActorID(point.save)) ||
            (point.domain && game.CompleteAddItemMobDomainNames.Contains(point.domain)) ||
            point.nodes.some(id=>events.ExecuteNodeGuid.Get(id)!==undefined));
    } else {
        let saved=point.save?savedState(actors,point.save,UE,require("puerts")):"unknown";
        // Save-record existence can precede pickup. For known containers read
        // their completion bit; for other sources use explicit reward evidence.
        known = saved!=="unknown" || !!point.domain || point.nodes.length>0;
        done = !!(saved==="collected" ||
            (point.domain && game.CompleteAddItemMobDomainNames.Contains(point.domain)) ||
            point.nodes.some(id => events.ExecuteNodeGuid.Get(id) !== undefined));
        if(done)known=true;
    }
    point.state = known ? (done ? "collected" : "remaining") : "unknown";
    c.progress ||= Availability.capture(events,UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page),bag,UE);
    Availability.refresh(point,c.progress);
    if(point.flow && point.state!=="collected")Availability.refreshSource(point,actors,UE);
    if (!point.label && point.reward) {
        let key = "";
        if (point.category === "accessory") key = name(UE.PBZBackPackSubsystem.GetAccessoryData(point.reward)?.AccessoryName?.Key);
        else if (point.category === "medicine") key = name(UE.PBZBackPackSubsystem.GetMedicineData(point.reward)?.MedicineName?.Key);
        else if (point.category === "weapon") key = name(bag.GetMainWeaponTableData(point.reward)?.WeaponName?.Key);
        else if (point.category === "shadowWeapon") key = name(bag.GetShaqiWeaponTableData(point.reward)?.WeaponName?.Key);
        point.label = localizedName(UE, key);
        if(["supply","key","treasure","chest"].includes(point.category))describeItem(UE,point);
    }
    if (!point.label) point.label = language[point.labelKey || point.category];
    const semantic = authoredLayer(point.mapID, point.x, point.y, point.z);
    if (semantic >= 0) point.layer = semantic;
    else if (point.mapID.toLowerCase() === String(page.GetCurrentMapID()).toLowerCase())
        point.layer = c.map.GetLayerByLocation(new UE.Vector(point.x, point.y, point.z));
}
class Catalog {
    constructor(mapIDs,store,region) { this.mapIDs = mapIDs; this.canonicalReady = false; this.category = 0; this.cursor = 0; this.points = []; this.ready = false; this.chestsRead = false; this.discovery = new DiscoveryCatalog(mapIDs,store,region); }
    refresh(page,UE,helper,player) {
        if(!this.ready)return false;
        const ctx=context(page,UE,helper);
        if(!ctx.events || !ctx.bag || !ctx.actors)return false;
        const progress=Availability.capture(ctx.events,UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page),ctx.bag,UE);
        let changed=Availability.refreshAll(this.points,progress);
        // Revisit known sources as their cells load. Keep native calls bounded;
        // quest predicates above are evaluated together from one value snapshot.
        const now=Date.now(),nearby=this.points.filter(p=>(p.flow || p.save && p.sourceUnavailable) &&
            p.state!=="collected" && now-(p.sourceCheckedAt || 0)>=4000 &&
            player && String(p.mapID).toLowerCase()===String(player.mapID).toLowerCase())
            .sort((a,b)=>Math.hypot(a.x-player.x,a.y-player.y,a.z-player.z)-Math.hypot(b.x-player.x,b.y-player.y,b.z-player.z)).slice(0,2);
        const batch=new Set(nearby);
        for(let n=0;n<2 && this.points.length;n++) {
            this.refreshCursor=(this.refreshCursor || 0)%this.points.length;
            batch.add(this.points[this.refreshCursor++]);
        }
        for(const point of batch) {
            if(point.state==="collected")continue;
            point.sourceCheckedAt=now;
            if(Availability.refreshSource(point,ctx.actors,UE))changed=true;
        }
        return changed;
    }
    step(page, UE, helper, language, explored) {
        if (this.ready) return;
        if (!this.canonicalReady && page.MapItemData) {
            this.mapIDs = resolveCanonicalMapIDs(page.MapItemData, this.mapIDs);
            this.canonicalReady = true;
        }
        if (this.category < specs.length) {
            for (let k = 0; k < 3 && this.category < specs.length; k++) {
                this.points.push(...materialize(page.MapItemData, this.mapIDs, this.category++));
            }
            return;
        }
        const ctx = context(page, UE, helper);
        if(!ctx.game || !ctx.bag || !ctx.bells || !ctx.events || !ctx.actors || !ctx.map)return;
        const end = Math.min(this.cursor + 36, this.points.length);
        for (; this.cursor < end; this.cursor++) {
            const point = this.points[this.cursor];
            resolve(point, ctx, UE, page, language);
            if (!point.excluded) point.explored = explored(point);
        }
        if (this.cursor === this.points.length) {
            if (!this.chestsRead) {
                this.chestsRead = true;
                const Atlas = require("./OfflineAtlas");
                const worlds = new Set();
                for (const mapID of this.mapIDs) {
                    const atlas = Atlas.open(mapID, false);
                    if (!atlas || worlds.has(atlas.world)) { atlas?.dispose(); continue; }
                    worlds.add(atlas.world);
                    for (const [category, cls, x, y, z, save] of atlas.items || []) {
                        const point = {id:"offline:"+atlas.world+":"+(save||[cls,x,y,z].join(":")),mapID,category,x,y,z,
                            save,nodes:[],domain:"",layer:atlas.floor({x,y,z}),label:language[category],state:"unknown"};
                        resolve(point,ctx,UE,page,language);point.explored=explored(point);this.points.push(point);
                    }
                    Diagnostics.record("offline-catalog",{world:atlas.world,items:atlas.items.length,status:atlas.items.length?"save-ids-unverified":"requires-usmap"});
                    atlas.dispose();
                }
                this.cursor=this.points.length;
            }
            if (!this.discovery.ready) {
                this.discovery.step(page,UE,helper,language,explored);
                if (!this.discovery.ready) return;
                const discovered=new Map(this.discovery.points.map(p=>[p.save,p]));
                const represented=new Set();
                for(const point of this.points)if(point.save) {
                    represented.add(point.save);
                    const live=discovered.get(point.save);
                    if(live?.sourceClass)point.sourceClass=live.sourceClass;
                    if(live)point.staticSource=live.staticSource;
                    if(live?.reward && !point.reward && ["supply","chest","key"].includes(point.category)) {
                        point.reward=live.reward;point.category=live.category;point.collectionKey=live.collectionKey;
                        point.label=live.label;point.filterGroup=live.filterGroup;
                    }
                    if(live?.contents?.length) {
                        point.rewards=live.rewards;point.contents=live.contents;
                        classifyGroup(point);
                        point.filterGroups=Array.from(new Set([point.filterGroup,...(live.filterGroups || [])].filter(Boolean)));
                        point.documentGroup=live.documentGroup;point.documentPage=live.documentPage;
                    }
                    if(live && !point.inventory && !Treasures.contains(point.reward) && live.state!=="unknown" && point.state!=="collected")point.state=live.state;
                    if(live)point.sourceUnavailable=live.sourceUnavailable;
                }
                this.points.push(...this.discovery.points.filter(p=>!represented.has(p.save)));
            }
            const documents=require("./Documents").read(ctx,this.mapIDs,UE,language,explored);
            const locatedDocuments=new Set(documents.map(p=>p.reward));
            const sourcesBySave=new Map(this.points.filter(p=>p.save).map(p=>[p.save,p]));
            for(const point of documents) {
                const observed=point.save && sourcesBySave.get(point.save);
                if(observed) {
                    point.sourceUnavailable=observed.sourceUnavailable;
                    point.contents=observed.contents;
                }
            }
            // Documents have their own complete ID catalog. A container's other
            // reward or state must not hide a page missing from the inventory.
            const documentSaves=new Set(documents.map(p=>p.save).filter(Boolean));
            this.points=this.points.filter(p=>!locatedDocuments.has(p.reward) && !documentSaves.has(p.save));
            this.points.push(...documents);
            this.points.push(...require("./PendingDocuments").read(ctx.bag,UE,language,locatedDocuments,ctx.game.GetCycleCount()));
            const seen = new Set();
            const rewards=new Set(this.points.filter(p=>p.inventory && p.reward).map(p=>p.reward));
            this.points = this.points.filter(point => {
                if (point.excluded) return false;
                if(!point.inventory && ["supply","weapon","shadowWeapon","accessory","medicine"].includes(point.category) &&
                    point.reward && rewards.has(point.reward) && (point.rewards?.length || 1)===1)return false;
                const id = point.inventory ? point.category + ":" + point.reward : point.save?point.save+":"+(point.reward || "container"):point.id;
                if (seen.has(id)) return false;
                seen.add(id); return true;
            });
            // Unique reward identity may become known only after source merging.
            const cycle=ctx.game.GetCycleCount();
            for(const point of this.points) {
                classifyGroup(point);
                if(Treasures.contains(point.reward)) {
                    ctx.availabilityTags ||= Availability.snapshot(ctx.events);
                    point.state=Treasures.collected(point.reward,ctx.bag,cycle,ctx.availabilityTags)?"collected":"remaining";
                }
            }
            const progress=Availability.capture(ctx.events,UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page),ctx.bag,UE);
            Availability.refreshAll(this.points,progress);
            this.ready = true;
        }
    }
}
module.exports = { Catalog };
