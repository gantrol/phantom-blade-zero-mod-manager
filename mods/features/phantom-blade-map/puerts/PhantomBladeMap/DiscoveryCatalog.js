"use strict";
const Diagnostics = require("./Diagnostics");
const {source,savedState,classifyGroup} = require("./SourceState");
const Treasures = require("./Treasures");
const ItemContents = require("./ItemContents");
const {SceneSources} = require("./SceneSources");
const validPosition = p => [p.x,p.y,p.z].every(Number.isFinite) && (p.x !== 0 || p.y !== 0 || p.z !== 0);

// Page-owned work queue: native registry containers and actors never survive a
// step. Discovery records contain identity/position, not cached completion.
class DiscoveryCatalog {
    constructor(mapIDs, store, region) {
        this.allowed = new Set(mapIDs.map(id => id.toLowerCase()));
        this.store = store; this.region = region; this.records = new Map();
        this.ids = undefined; this.cursor = 0; this.points = []; this.ready = false;
        this.discovered = []; this.loaded = 0;
        this.scene = new SceneSources(mapIDs); this.offline = 0;
    }
    step(page, UE, helper, text, explored) {
        if (this.ready) return;
        const puerts = require("puerts");
        const manager = helper.getWorldSubsystem(page, UE.PBZLevelActorManager.StaticClass());
        const map = helper.getWorldSubsystem(page, UE.MapSubsystem.StaticClass());
        const bag = helper.getWorldSubsystem(page, UE.PBZBackPackSubsystem.StaticClass());
        if (!manager || !map || !bag) return;
        if (!this.ids) {
            for (const record of this.store?.discoveries() || []) {
                if (this.allowed.has(record.mapID.toLowerCase())) this.records.set(record.save, record);
            }
            const ids = new Set(this.records.keys()), registry = manager.LevelActorList;
            for(const record of this.scene.entries())if(record.save)ids.add(record.save);
            // One container pass, with no reflected calls while reading its keys.
            for (let i=0,n=registry.GetMaxIndex();i<n;i++) {
                if (!registry.IsValidIndex(i)) continue;
                const id = String(registry.GetKey(i));
                if (source(id)) ids.add(id);
            }
            this.ids = Array.from(ids);
            return;
        }
        let treasureContext;
        for (let budget=0;budget<8 && this.cursor<this.ids.length;budget++,this.cursor++) {
            const id=this.ids[this.cursor];
            const remembered=this.records.get(id);
            let record=remembered, state,availability;
            const baked=this.scene.find(id);
            let sourceClass=baked?.sourceClass || record?.sourceClass, kind=source(id,sourceClass);
            if(!kind)continue;
            if(record && baked) {
                const rewards=ItemContents.contents(baked.loot,baked.reward,baked.rewards);
                record={...record,sourceClass};
                if(rewards.length)record={...record,loot:baked.loot,rewards,reward:rewards[0].id};
            }
            if(!record) {
                if(baked) {
                    const [x,y,z]=baked.position,mapID=baked.mapID || String(map.GetMapIDByLevelAndLocation(baked.levelName,new UE.Vector(x,y,z)));
                    if(this.allowed.has(mapID.toLowerCase())) {
                        const rewards=ItemContents.contents(baked.loot,baked.reward,baked.rewards);
                        record={save:id,sourceClass,mapID,x,y,z,category:kind.category,loot:baked.loot,rewards,reward:rewards[0]?.id || ""};
                        this.offline++;
                    }
                }
            }
            const actor=manager.GetActorByID(id,UE.Actor.StaticClass());
            if (actor && UE.KismetSystemLibrary.IsValid(actor) && !actor.IsActorBeingDestroyed()) {
                sourceClass=String(UE.KismetSystemLibrary.GetPathName(actor.GetClass())).split(".").pop();
                kind=source(id,sourceClass) || kind;
                const p=actor.K2_GetActorLocation();
                const mapID=String(map.GetMapIDByLocation(p));
                const direct=kind.category==="supply"?String(kind.pickup?actor.ItemId:actor["Item ID"] || ""):"";
                const loot=kind.pickup?"":String(sourceClass==="BP_HangingGift_C"?actor.LootItemID:actor["Loot Item ID"] || "");
                const declared=!kind.pickup && sourceClass!=="BP_HangingGift_C"?ItemContents.pickupMap(actor,sourceClass):[];
                const dust=!kind.pickup && sourceClass!=="BP_HangingGift_C"?Number(actor["Get Dust"] || 0):0;
                if(dust>0)declared.push({kind:"dust",id:"currency:dust",min:dust,max:dust,amountBasis:"base"});
                const rewards=ItemContents.contents(loot,direct,declared);
                const candidate={save:id,sourceClass,mapID,x:p.X,y:p.Y,z:p.Z,category:kind.category,loot,rewards,reward:rewards[0]?.id || ""};
                if (this.allowed.has(mapID.toLowerCase()) && validPosition(candidate)) {
                    record=candidate;
                    const done=kind.live?actor[kind.live]:undefined;
                    state=typeof done==="boolean" ? (done?"collected":"remaining") : undefined;
                    if(kind.pickup && actor.bIsPickable && actor.IsValidPickup())state="remaining";
                    if(state!=="collected" && (actor.bHidden || actor["Not Interact"]===true))availability="sourceUnavailable";
                    record.observedRemaining=state==="remaining" && !availability;
                    if(id.includes("_UAID_")) {
                        if(!baked)this.discovered.push(record);
                        else if(record.observedRemaining)this.discovered.push({save:id,sourceClass,mapID,x:candidate.x,y:candidate.y,z:candidate.z,category:kind.category,observedRemaining:true});
                        else if(remembered)this.discovered.push({save:id,evict:true});
                    }
                    this.loaded++;
                }
            }
            if (!record) continue;
            const staticSource=!!baked;
            const saved=savedState(manager,id,UE,puerts,sourceClass,staticSource);
            if(saved==="collected" && baked && remembered)this.discovered.push({save:id,evict:true});
            const point={...record,id:"discovered:"+id,category:kind.category,layer:-1,label:text[kind.category],staticSource,availability,sourceUnavailable:!!availability,
                state:saved==="collected"?saved:state || (saved!=="unknown"?saved:record.observedRemaining?"remaining":"unknown")};
            if(point.reward && point.category==="chest")point.category="supply";
            ItemContents.describe(UE,bag,point,text);
            classifyGroup(point);
            if(point.category==="book") {
                const owned=bag.Inventories.GetRef(point.reward);
                if(owned && owned.CurrentAmount>0)point.state="collected";
            }
            if(Treasures.contains(point.reward)) {
                Treasures.classify(point);
                treasureContext ||= {
                    bag:helper.getWorldSubsystem(page,UE.PBZBackPackSubsystem.StaticClass()),
                    cycle:helper.getWorldSubsystem(page,UE.PBZGameSystem.StaticClass()).GetCycleCount(),
                    tags:require("./CollectionAvailability").snapshot(helper.getWorldSubsystem(page,UE.EventPipelineManager.StaticClass()))
                };
                const {bag,cycle,tags}=treasureContext;
                point.state=Treasures.collected(point.reward,bag,cycle,tags)?"collected":"remaining";
            }
            point.explored=explored(point); this.points.push(point);
        }
        if (this.cursor===this.ids.length) {
            this.store?.rememberDiscoveries(this.discovered);
            this.ready=true;
            Diagnostics.record("discovery-catalog",{region:this.region,registry:this.ids.length,loaded:this.loaded,
                remembered:this.records.size,points:this.points.length,remaining:this.points.filter(p=>p.state==="remaining").length,
                unknown:this.points.filter(p=>p.state==="unknown").length,offline:this.offline,coverage:"archive-sources-native-registry-and-save"});
            this.ids=[];this.records.clear();this.discovered=[];this.store=undefined;
            this.scene.clear();
        }
    }
}
module.exports={DiscoveryCatalog};
