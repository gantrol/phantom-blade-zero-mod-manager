"use strict";
const { Catalog } = require("./Catalog");
const { View } = require("./View");
const Atlas = require("./OfflineAtlas");
const {MapPane}=require("./MapPane");
const {Exploration}=require("./Exploration");
const NativeTracking = require("./NativeTracking");
const { SideQuests } = require("./SideQuests");
const { located } = require("./QuestObjectives");
const HudTracking = require("./HudTracking");
const { dictionary } = require("./Locales");
const Settings = require("./Settings");
const Store = require("./Store");
const Diagnostics = require("./Diagnostics");
const {priority} = require("./SourceState");
const Fog = require("./Fog");
const {NativeFog}=require("./NativeFog");
const regions = require("./atlas/regions");
const Filter=require("./CollectibleFilter");
const {groups:filterGroups}=require("./Filters");
const {TraversalMarkers}=require("./TraversalMarkers");

function insidePolygon(x, y, poly) {
    let inside = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
        const a = poly[i], b = poly[j];
        if ((a[1] > y) !== (b[1] > y) && x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0]) inside = !inside;
    }
    return inside;
}

function elevationText(dzCm, text) {
    const m = Math.round(dzCm / 100);
    if (Math.abs(m) <= 1) return text.sameLevel;
    return (m > 0 ? text.above : text.below) + " " + Math.abs(m) + "m";
}

function shouldRevealUnexplored() {
    try {
        const UE = require("ue"), puerts = require("puerts");
        const values = Settings.open(UE, puerts).read().values;
        return !!(values.visibility && !values.showFog);
    } catch {
        return false;
    }
}

function open(page) {
    HudTracking.mapOpened();
    const UE = require("ue"), puerts = require("puerts"), cpp = require("cpp");
    const helper = require("../Library/TsHelper").default;
    const text = {...dictionary(UE)}, settings = Settings.open(UE, puerts);
    if (settings.read().values.view === "survey") settings.apply({ view: "native" });
    const nativeFog=new NativeFog(UE);
    let inputControls, inputValues;
    try {
        const controls = require("../PhantomBladeRuntime/Controls");
        inputValues = controls.read();
        inputControls = controls;
        const caption = id => String(inputValues[id]).toLowerCase() === "none" ? "—" : inputValues[id];
        text.cancelTrackingShortcut = text.clearTracking + " · " + caption("map.clear");
        text.controls = text.controls.replace(/F6|F7|F10/g, key => caption({F6:"map.track",F7:"map.clear",F10:"map.visibility"}[key]));
    } catch (error) {
        Diagnostics.record("input-defaults", {error: String(error)});
    }
    let store;
    try { store = Store.open(UE, puerts, page); } catch (error) { Diagnostics.record("storage-error", { error: String(error) }); }
    let view, catalog, region = "", viewKey = "", nextUpdate = 0, selected = "", targetIndex = -1, pageOffset = 0;
    let atlas, pane, coverage, facilities, configOpen = false, pendingFocus, publishedKey="";
    let quests = [], nativeQuests = [], sideQuests, nativeTrackedID = "", trackedID = HudTracking.currentID(), trackingDirty = true, listMode = "collectibles";
    let closed = false, points = [], navigable = [], visibleRows = [], player, fog, fogReady = false, lastFog = 0, viewLayer = 0, lastCollectionRefresh = 0;
    const world = String(UE.KismetSystemLibrary.GetPathName(page.GetWorld()));
    const pageId = Diagnostics.record("page-open", { backend: "puerts", world });

    function readPlayer() {
        const current = require("./PlayerContext").read(page, UE, helper);
        if (!current) return undefined;
        const {x,y,z,yaw,aboard}=current;
        return {x,y,z,yaw,aboard,mapID:String(page.GetCurrentMapID() || region)};
    }

    function projectUV(point, pure = false) {
        const cached=coverage?.project(point);
        if(cached)return cached;
        if (pure || !catalog?.mapIDs.some(id=>id.toLowerCase()===String(point.mapID || region).toLowerCase())) return undefined;
        const position = page.ConvertWorldLocationToScreen(new UE.Vector(point.x, point.y, point.z), point.mapID || region), size = page.CurrentMapSize();
        if(!(size.X>0 && size.Y>0))return;
        return coverage.remember({...point,mapID:point.mapID || region},[position.X/size.X,position.Y/size.Y]);
    }

    function knownVisible(point, pure = false) {
        // Unlocated document/quest entries belong in the list, not a projection.
        if (!located(point)) return false;
        if(coverage?.visible(point))return true;
        if (!fogReady || !fog || !fog.known) return false;
        const uv = projectUV(point, pure);
        return !!uv && Fog.visible(fog, uv[0], uv[1]);
    }

    function focusMapOnPoint(point) {
        if(point?.pointKind==="mapMarker") { page.FocusToPoint(new UE.Vector2D(point.u,point.v));return; }
        if (!located(point)) return;
        if(!catalog.mapIDs.some(id=>id.toLowerCase()===point.mapID.toLowerCase())) {
            const destination=page.mapIDToBigMapID(point.mapID);
            if(destination) {
                pendingFocus={...point};
                page.toOtherMapByID(destination);
            }
            return;
        }
        const id=String(point.mapID || region), worldKey=Atlas.aliases[id.toLowerCase()] || id.toLowerCase();
        if(atlas?.world!==worldKey) {
            atlas?.dispose();atlas=Atlas.open(id);
        }
        if(atlas)atlas.mapID=id;
        if (sameAtlas(point)) {
            const floor = atlas.floor(point);
            viewLayer = floor >= 0 ? floor : atlas.layers[viewLayer] ? viewLayer : 0;
        }
        const map = helper.getWorldSubsystem(page, UE.MapSubsystem.StaticClass());
        const uv = map.ProjectWorldLocationToMap(new UE.Vector(point.x, point.y, point.z), point.mapID || region);
        if (!atlas && point.layer >= 0) viewLayer = point.layer;
        page.FocusToPoint(uv);
        pane?.focus(point);
    }

    function layerLabel(index) {
        const layer = atlas?.layers[index];
        return layer ? text[layer.kind] + (layer.depth ? " " + layer.depth : "") : text.floorUnknown;
    }
    function sameAtlas(point) {
        const id=String(point?.mapID || region).toLowerCase();
        return !!atlas && (Atlas.aliases[id] || id)===atlas.world;
    }

    function selectPoint(point) {
        selected = point.id;
        targetIndex = navigable.indexOf(point);
        focusMapOnPoint(point);
    }
    function trackPoint(point) {
        if(point.pointKind==="mapMarker")return;
        if(settings.read().values.autoCollect)settings.apply({autoCollect:false});
        if(point.id===trackedID){clearTracking();return;}
        function customTarget() {
            nativeTrackedID="";
            store?.trackQuest(undefined);
            HudTracking.select(page,point,text);
        }
        if (point.category === "quest") {
            if (NativeTracking.track(page,UE,point)) {
                store?.trackQuest(undefined); nativeTrackedID=point.nativeID || point.id;HudTracking.select(page,point,text,true);
            } else customTarget();
        } else customTarget();
        trackedID=point.id;
        Diagnostics.record("tracking-selected",{id:point.id,kind:point.pointKind,native:HudTracking.current()?.native});
    }
    function clearTracking() {
        if(settings.read().values.autoCollect)settings.apply({autoCollect:false});
        HudTracking.stop();store?.trackQuest(undefined);
        UE.PBZBlueprintFunctionLibrary.GetPBZNarrativeComp(page)?.ClearTrackedPoint();
        page.refreshTrackedHighlight();nativeTrackedID="";trackedID="";selected="";
        Diagnostics.record("tracking-cleared");
    }
    function mergeQuests() {
        const details=new Map((sideQuests?.points || []).map(p=>[p.nativeID,p]));
        const nativeIDs=new Set(nativeQuests.map(p=>p.id));
        const supplement=(sideQuests?.points || []).filter(p=>!nativeIDs.has(p.nativeID));
        const all=nativeQuests.map(p=>details.has(p.id)?{...p,...details.get(p.id),nativeVisible:true}:p).concat(supplement);
        const renamed=all.find(p=>p.nativeID===selected);if(renamed)selected=renamed.id;
        let choice=HudTracking.saved(store?.profile) || store?.trackedQuest();
        if(choice?.category && choice.category!=="quest")choice=undefined;
        if (choice && sideQuests?.ready) {
            // Keep tracking the same quest as its active branch advances. Old
            // branch points are never resurrected from the plugin journal.
            const current=all.find(p=>p.id===choice.id) || all.find(p=>p.questKey===choice.questKey);
            if(current) choice=current;
            else if(!sideQuests.active.has(choice.questKey)) { HudTracking.stop();store?.trackQuest(undefined);choice=undefined; }
        }
        const currentNative=all.find(p=>p.id===nativeTrackedID || p.nativeID===nativeTrackedID);
        trackedID=HudTracking.currentID() || choice?.id || currentNative?.id || nativeTrackedID;
        quests=all;
        if(choice && sideQuests?.ready) {
            const point=quests.find(p=>p.id===choice.id);
            const current=HudTracking.current();
            if(point && (!current || ["id","x","y","z"].some(k=>current[k]!==point[k]))) {
                HudTracking.select(page,point,text,NativeTracking.track(page,UE,point));store?.trackQuest(undefined);trackedID=point.id;
            }
        }
        if(listMode==="quest" && !selected && quests.some(p=>p.id===trackedID))selected=trackedID;
    }
    function performAction(key) {
        try { action(key); }
        catch(error) { Diagnostics.record("error",{error:String(error),source:"toolbar"}); }
    }
    function publishHUD() {
        if(!coverage)return;
        const config=settings.read(),marks=(store?.marks(region)||[]).filter(m=>Number.isFinite(m.x)).map(m=>({...m,id:"pin:"+m.u+":"+m.v,category:"marker",state:"remaining",label:text.marker,explored:true}));
        const key=[region,config.revision,catalog?.ready,sideQuests?.ready,facilities?.ready,quests.map(q=>q.id).join("|"),fogReady,fog?.revision,marks.map(m=>m.id).join("|")].join(":");
        if(key===publishedKey)return;publishedKey=key;
        HudTracking.publish(page,{profile:store?.profile,config:config.values,coverage:coverage.snapshot(),points:(catalog?.ready?catalog.points:[]).concat(quests,marks),facilities:facilities?.points || [],catalogReady:!!catalog?.ready,region},text);
        HudTracking.syncAutomatic(page,player);
    }

    function refresh() {
        const config = settings.read().values;
        publishHUD();
        if (config.view !== "survey") pane?.clear();
        view.setToggles(config);
        view.setListMode(listMode);
        if (!catalog?.ready && listMode === "collectibles") {
            view.setPanel({ rows: [], emptyText: text.scanning, pageText: "" }, config.visibility && config.showList, text.scanning, "");
            return;
        }
        const mapID = String(page.GetCurrentMapID() || "");
        const canSample = sameAtlas({mapID});
        const marks=(store?.marks(region)||[]).filter(m=>config.view==="native" || Number.isFinite(m.x)).map(m=>({...m,id:"pin:"+m.u+":"+m.v,mapID:m.mapID || region,pointKind:Number.isFinite(m.x)?"marker":"mapMarker",category:"marker",state:"remaining",label:text.marker,explored:true}));
        const sourcePoints = listMode === "quest" ? quests : (catalog?.points || []).concat(marks);
        const documentsOnly=config.filterDocuments && filterGroups.filter(g=>g.key!=="documents").every(g=>!config[g.setting]);
        points = sourcePoints.filter(p => (!p.globalDocument || documentsOnly) && Filter.visible(p,config) && (!config.remainingOnly || p.state !== "collected") && (p.pointKind==="pending" || p.pointKind==="mapMarker" || (located(p) && (p.x !== 0 || p.y !== 0 || p.z !== 0))));
        for (const point of points) {
            // These quest points are already visible in the native game map.
            // Exploration filters markers; terrain stays visible before unlocks.
            point.knownVisible = point.globalDocument || point.nativeVisible || point.knownQuestPoint || !config.exploredOnly || point.explored || knownVisible(point,true);
            if (located(point) && sameAtlas(point)) point.layer = atlas.floor(point);
            else point.layer=-1;
            point.order = 0;
        }
        points = points.filter(point => point.knownVisible);
        navigable = points.slice().sort((a, b) => {
            if ((a.id === trackedID) !== (b.id === trackedID)) return a.id === trackedID ? -1 : 1;
            if((a.state==="unknown")!==(b.state==="unknown"))return a.state==="unknown"?1:-1;
            if (!!a.availability!==!!b.availability)return a.availability?1:-1;
            if(located(a)!==located(b))return located(a)?-1:1;
            if(!located(a))return a.id.localeCompare(b.id);
            const importance=priority(a)-priority(b);
            if (listMode === "collectibles" && importance) return importance;
            const localA = a.mapID.toLowerCase() === mapID.toLowerCase(), localB = b.mapID.toLowerCase() === mapID.toLowerCase();
            if (localA !== localB) return localA ? -1 : 1;
            if (localA && player) {
                return Math.hypot(a.x - player.x, a.y - player.y) - Math.hypot(b.x - player.x, b.y - player.y);
            }
            return a.mapID.localeCompare(b.mapID) || a.id.localeCompare(b.id);
        });
        for (let i = 0; i < navigable.length; i++) {
            navigable[i].order = i + 1;
        }
        const facilityPoints=config.showTraversal?(facilities?.points || []).filter(p=>Filter.visible(p,config) && (!config.exploredOnly || p.explored || knownVisible(p,true))):[];
        for(const p of facilityPoints)p.layer=sameAtlas(p)?atlas.floor(p):-1;
        const selectedFacility = (facilities?.points || []).find(p => p.id === selected) || (facilities?.points || []).find(p => p.id === trackedID);
        if (selected && !selectedFacility && !navigable.some(point => point.id === selected) && (listMode!=="quest" || sideQuests?.ready)) selected = "";
        if(config.autoCollect)HudTracking.syncAutomatic(page,player);
        trackedID=HudTracking.currentID() || (config.autoCollect?"":trackedID);
        const target = navigable.find(point => point.id === selected) || selectedFacility || navigable.find(point=>point.id===trackedID) || HudTracking.current();
        if (selected && target && navigable.some(p=>p.id===target.id)) {
            targetIndex = navigable.findIndex(p=>p.id===target.id);
            pageOffset = Math.floor(targetIndex / 6) * 6;
        } else {
            const maxOffset = Math.max(0, Math.floor((navigable.length - 1) / 6) * 6);
            if (pageOffset > maxOffset) pageOffset = maxOffset;
        }
        // Supplementary quest points have no native map icon.
        view.setPins(navigable.concat(facilityPoints).filter(p=>(located(p) || p.pointKind==="mapMarker") && !p.nativeVisible && catalog.mapIDs.some(id=>id.toLowerCase()===p.mapID.toLowerCase())), config, selected || trackedID, region);
        visibleRows = navigable.slice(pageOffset, pageOffset + 6);
        function locationText(point, includeHeight = false) {
            if (point.documentSource==="initial") return "";
            if (point.pointKind==="mapMarker") return text["region_"+point.mapID.toLowerCase()] || point.mapID;
            if (!located(point)) return text.objectivePending;
            const subName=text["region_"+point.mapID.toLowerCase()] || point.mapID;
            if (!player || point.mapID.toLowerCase()!==mapID.toLowerCase()) return subName;
            const distance=Math.round(Math.hypot(point.x-player.x,point.y-player.y)/100)+" m";
            return [subName,distance,includeHeight?elevationText(point.z-player.z,text):""].filter(Boolean).join(" · ");
        }
        const rowModels = visibleRows.map(point => {
            const isSel = point.id === selected;
            const kind=point.collectionKey?text[point.collectionKey]:point.category==="chest" && !point.reward?text.contentsUnknown:"";
            const detail=[point.documentSource && point.stage?point.stage:kind,locationText(point,true)].filter(Boolean).join(" · ");
            return {
                selected: isSel,tracked:point.id===trackedID,
                canTrack:!point.globalDocument && point.pointKind!=="mapMarker",
                category:point.category, state:point.state,
                order: point.order,
                title: [point.label || text[point.category],text["objective_"+point.objectiveID]].filter(Boolean).join(" · "),
                detail: point.availability?text[point.availability]:(point.state!=="remaining"?text[point.state]+" · ":"")+detail,
                tooltip: [point.contents?.join("\n") || point.label,point.stage,text[point.availability],detail].filter(Boolean).join("\n")
            };
        });
        const totalPages = Math.max(1, Math.ceil(navigable.length / 6));
        const currentPage = Math.min(totalPages, Math.floor(pageOffset / 6) + 1);
        const pageText = navigable.length > 0 ? currentPage + " / " + totalPages : "0 / 0";
        const hidden = sourcePoints.filter(p => !p.nativeVisible && !p.knownQuestPoint && Filter.visible(p,config) && (!config.remainingOnly || p.state !== "collected") && !p.knownVisible).length;
        const count=config.remainingOnly && listMode!=="quest"?navigable.filter(p=>p.state==="remaining").length:navigable.length;
        const unknown=navigable.filter(p=>p.state==="unknown").length;
        const heading = text[listMode === "quest" ? "quest" : config.remainingOnly?"remaining":"collectibles"] + " " + count+
            (unknown && config.remainingOnly && listMode!=="quest"?" · "+text.unknown+" "+unknown:"");
        if(config.view==="survey") {
            if(!pane)pane=new MapPane(view,performAction);
            const floorCount = atlas?.layers?.length || 1;
            const layerTag = floorCount > 1 ? layerLabel(viewLayer) + " (" + (viewLayer + 1) + "/" + floorCount + ")" : layerLabel(viewLayer);
            // Bells are landmarks on the floor plan, independent of the item list
            // and its uncollected/category filters. Keep their native positions.
            const bells=(catalog?.points || []).filter(p=>p.category==="bell" && located(p) && sameAtlas(p) &&
                (p.x!==0 || p.y!==0 || p.z!==0)).map(p=>({...p,layer:atlas.floor(p)}));
            const mapPoints=points.filter(p=>p.category!=="bell").concat(facilityPoints,bells).filter(located);
            pane.update(atlas,viewLayer,canSample && player?{...player,layer:atlas.floor(player)}:undefined,mapPoints,located(target) && sameAtlas(target)?target:undefined,coverage,config,layerTag);
        }
        publishHUD();
        view.showSettings(configOpen, config, performAction);
        const statusParts = [
            store?.storageError ? text.storageUnavailable : "",
            target?.availability && !target.globalDocument ? text.questReward : "",
            hidden > 0 && config.exploredOnly ? text.hidden : ""
        ].filter(Boolean);
        if(listMode==="quest" && !sideQuests?.ready)statusParts.push(text.questScanning);
        let traversalHint = "";
        if (target && !target.facility && canSample && player && located(target) && sameAtlas(target)) {
            const pFloor = atlas.floor(player), tFloor = atlas.floor(target);
            if (pFloor >= 0 && tFloor >= 0 && pFloor !== tFloor && Math.abs(target.z - player.z) > 250 && facilityPoints.length) {
                const sameFloor = facilityPoints.filter(f => f.layer === pFloor);
                const pool = sameFloor.length ? sameFloor : facilityPoints;
                const nearest = pool.slice().sort((a, b) => Math.hypot(a.x - player.x, a.y - player.y) - Math.hypot(b.x - player.x, b.y - player.y))[0];
                if (nearest) traversalHint = nearest.label + " · " + Math.round(Math.hypot(nearest.x - player.x, nearest.y - player.y) / 100) + " m";
            }
        }
        view.setPanel({
            rows: rowModels,
            regionLabel: text["region_" + region.toLowerCase()] || text.journal,
            hasPreviousPage: pageOffset > 0,
            hasNextPage: pageOffset + 6 < navigable.length,
            emptyText: text[listMode === "quest" ? "noQuests" : "noRemaining"],
            pageText, listMode, autoCollect: HudTracking.autoStatus(),
            navigation: target ? {
                title: text[target.id===trackedID ? "tracking":"preview"]+" · "+(target.label || text[target.category]),
                detail: [target.contents?.length>1?target.contents.join(" · "):"",target.stage,target.availability?text[target.availability]:locationText(target,true),traversalHint].filter(Boolean).join("\n"),
                playerLocal:canSample,targetLocated:located(target) || target.pointKind==="mapMarker",tracked:target.id===trackedID,canTrack:target.state!=="collected" && !target.globalDocument && target.pointKind!=="mapMarker"
            } : undefined
        }, config.visibility && config.showList, heading, statusParts.join(" · "));
    }

    function stop(reason = "finish") {
        if (closed) return;
        closed = true;
        const detach = reason !== "slate-release" && reason !== "world-changed";
        HudTracking.mapClosed(reason);
        const before = { view: view?.resources() };
        let textureCache;
        if (detach) try {
            const cache = require("../Gameplay/MapTextureCacheManager").getMapTextureCache();
            textureCache = { current: cache?.currentBigMapID || "", requestedPaths: cache?.lastPushedSignature?.split("|").filter(Boolean).length || 0,
                gpuBudgetSource: "external-diagnostics" };
        } catch (error) { textureCache = {error:String(error)}; }
        Diagnostics.record("page-closing", { pageId, world, region, reason, detach, before, textureCache });
        const errors = []; let viewCleanup;
        // Every owner gets its cleanup even if a preceding native call fails.
        for (const [owner, cleanup] of [
            ["native-fog",()=>nativeFog.dispose(detach)],
            ["atlas", () => atlas?.dispose()], ["facilities",()=>facilities?.dispose()], ["pane",()=>pane?.dispose(detach)],
            ["view", () => { viewCleanup = view?.dispose(detach); }], ["store", () => store?.dispose()]
        ]) {
            try { cleanup(); } catch (error) { errors.push({ owner, error: String(error) }); }
        }
        atlas = store = view = catalog = undefined;
        points = []; navigable = []; visibleRows = []; quests = []; nativeQuests = []; sideQuests=undefined; player = fog = undefined;
        pane=undefined;coverage=undefined;facilities=undefined;
        page = undefined;
        Diagnostics.record("page-closed", { pageId, world, region, reason, viewCleanup, errors });
    }

    function nextTarget(direction) {
        if (!navigable.length) return;
        if (targetIndex < 0) targetIndex = direction > 0 ? 0 : navigable.length - 1;
        else targetIndex = (targetIndex + direction + navigable.length) % navigable.length;
        const point = navigable[targetIndex];
        selectPoint(point);
        pageOffset = Math.floor(targetIndex / 6) * 6;
        nextUpdate = 0;
    }

    return {
        tick() {
            if (closed) return;
            if (String(UE.KismetSystemLibrary.GetPathName(page.GetWorld())) !== world) { stop("world-changed"); return; }
            if (page.systemMenuExitPending) { stop("menu-exit"); return; }
            if (page.GetVisibility() === UE.ESlateVisibility.Collapsed || page.GetVisibility() === UE.ESlateVisibility.Hidden) { stop("hidden"); return; }
            const config = settings.read();
            nativeFog.update(page, !!(config.values.visibility && !config.values.showFog));
            if (page.BigMapMode || page.IsPlayingDialogue || page.isOpenCompletion() || page.isModalOpen()) {
                pane?.clear(); view?.hide(true); nextUpdate = 0; return;
            }
            if (page.pendingInitSlice) return;
            const nextRegion = String(page.CurrentDisplayMapID || "");
            if (!nextRegion || !page.MapItemData) return;
            if (region !== nextRegion) {
                pane?.dispose();pane=undefined;
                atlas?.dispose(); atlas = undefined; view?.dispose(); view = undefined;
                region = nextRegion; selected = ""; targetIndex = -1; pageOffset = 0; points = []; navigable = []; visibleRows = []; trackingDirty=true;
                viewLayer = page.GetPlayerLayer();
                // Map-page membership comes from DA_MapIDAsset and the native
                // merge groups. Sharing an Unreal world is only relevant to
                // offline geometry, not to the painted map's UV coordinate space.
                const allowed = Array.from(page.getallowedMapID(),String);
                catalog = new Catalog(allowed,store,region);sideQuests=new SideQuests();nativeQuests=[];quests=[];
                facilities?.dispose();facilities=new TraversalMarkers(allowed);
                Diagnostics.record("region-open", { region, current: String(page.GetCurrentMapID()), profile: !!store });
                const records=Fog.records(page,UE);lastFog=Date.now();
                fog=records.find(r=>r.mapID===region.toLowerCase())?.fog;
                fogReady=!!fog?.known;
                player = readPlayer();
                const currentMap=String(page.GetCurrentMapID());
                const atlasMap=allowed.some(id=>id.toLowerCase()===currentMap.toLowerCase())?currentMap:region;
                atlas = Atlas.open(atlasMap);
                if (player && sameAtlas(player)) viewLayer = Math.max(0,atlas.floor(player));
                else if(atlas)viewLayer=Math.max(0,atlas.layers.findIndex(layer=>layer.kind==="ground"));
                coverage=new Exploration(records);
                Diagnostics.record("map-coverage",{region,known:fogReady,masks:fog?.rectangles.length,regions:records.length,source:"native-fog-layout-and-save",projection:"native-per-point"});
                publishHUD();
                view = new View(page, UE, cpp, text, performAction);
                if(pendingFocus) {
                    const point=pendingFocus;pendingFocus=undefined;selected=point.id;focusMapOnPoint(point);
                }
                nextUpdate = 0; viewKey = "";
            } else if (Date.now()-lastFog>=1000) {
                lastFog=Date.now();
                const records=Fog.records(page,UE),nextFog=records.find(r=>r.mapID===region.toLowerCase())?.fog;
                const changed=nextFog?.revision!==fog?.revision || nextFog?.known!==fog?.known;
                coverage.refresh(records);fog=nextFog;fogReady=!!fog?.known;
                if(changed) {
                    for(const pt of catalog?.points || [])pt.explored=knownVisible(pt);
                    for(const pt of sideQuests?.points || [])pt.explored=located(pt) && knownVisible(pt);
                    nextUpdate=0;
                }
            }
            view.showControls(true);
            view.resize();
            if (!config.values.visibility) {
                publishHUD();
                view.setToggles(config.values);
                pane?.clear(); view.hide(); nextUpdate = 0; return;
            }
            const restoreQuest=HudTracking.saved(store?.profile)?.category==="quest" || !!store?.trackedQuest();
            if (trackingDirty && (listMode==="quest" || restoreQuest)) {
                const snapshot = NativeTracking.snapshot(page,UE);
                nativeQuests = snapshot.points; nativeTrackedID = snapshot.trackedID; trackingDirty = false;
                for (const point of nativeQuests) point.explored = knownVisible(point);
                mergeQuests();
                Diagnostics.record("native-tracking",{region,points:nativeQuests.length,trackedID});
                nextUpdate=0;
            }
            if((listMode==="quest" || restoreQuest) && !sideQuests.ready) {
                sideQuests.step(page,UE,helper,p=>catalog.mapIDs.some(id=>id.toLowerCase()===p.mapID.toLowerCase()) && knownVisible(p));mergeQuests();nextUpdate=0;
            }
            if (!catalog.ready && (listMode==="collectibles" || config.values.autoCollect)) {
                catalog.step(page, UE, helper, text, knownVisible); nextUpdate = 0;
                if (catalog.ready) Diagnostics.record("catalog-ready", { region, points: catalog.points.length, remaining: catalog.points.filter(p => p.state === "remaining").length, collected: catalog.points.filter(p => p.state === "collected").length, unknown: catalog.points.filter(p => p.state === "unknown").length,
                    documents: catalog.points.filter(p=>p.category==="book" && p.state!=="collected").map(p=>({reward:p.reward,state:p.state,mapID:p.mapID,source:p.documentSource,explored:p.explored})),
                    treasures: catalog.points.filter(p=>p.category==="treasure").map(p=>({id:p.id,reward:p.reward,state:p.state,explored:p.explored,layer:p.layer})) });
            }
            if(catalog.ready && Date.now()-lastCollectionRefresh>=1000) {
                lastCollectionRefresh=Date.now();
                player=readPlayer();
                if(catalog.refresh(page,UE,helper,player)) {
                    publishedKey="";nextUpdate=0;
                }
            }
            if(config.values.showTraversal && !facilities.ready) {
                facilities.step(page,UE,helper,text,knownVisible);nextUpdate=0;
            }
            const size = page.CurrentMapSize(), viewport = page.getMapPageLocalSize();
            if(config.values.view==="survey" && pane?.syncInput())nextUpdate=0;
            const key = [region, viewLayer, listMode, config.revision, size.X, size.Y, viewport.X, viewport.Y, view.panelScale, selected, pageOffset, !!catalog.ready].join(":");
            if (key !== viewKey || nextUpdate === 0) {
                viewKey = key; nextUpdate = 1;
                refresh();
            }
            view.stepPins();
        },
        key(event) {
            if (closed || page.BigMapMode || page.isOpenCompletion() || page.isModalOpen() || page.IsPlayingDialogue) return false;
            const input = UE.KismetInputLibrary, key = String(input.GetKey(event).KeyName);
            if(key==="Gamepad_FaceButton_Top")return false;
            if(configOpen) {
                if(input.InputEvent_IsRepeat(event) && !["Up","Down","Gamepad_DPad_Up","Gamepad_DPad_Down"].includes(key))return true;
                if(view.filterKey(key))return true;
            }
            if(input.InputEvent_IsControlDown(event) || input.InputEvent_IsShiftDown(event) || input.InputEvent_IsAltDown(event))return false;
            const values=settings.read().values, binding=inputControls?.action("map", event, inputValues);
            const repeat=input.InputEvent_IsRepeat(event);
            if(values.visibility && values.view==="survey" && (key==="Escape" || key==="Gamepad_FaceButton_Right")) {
                return repeat || action("MapMode");
            }
            if(key==="F4")return repeat || action("Settings");
            if(key==="F5" || key==="Gamepad_LeftThumbstick")return repeat || action("AutoCollect");
            if(binding?.id==="map.visibility")return repeat || action(binding.command);
            if(!inputControls && key==="F10")return repeat || action(key);
            if(!values.visibility)return false;
            if(binding && (!binding.survey || values.view==="survey"))return repeat && !binding.command.startsWith("Plan") || action(binding.command);
            if(key==="Gamepad_FaceButton_Left" || !inputControls && key==="F6")return repeat || action("TrackTarget");
            if(!inputControls && key==="F7")return repeat || action("ClearTracking");
            if(!inputControls && key==="F8")return repeat || action("F8");
            if(!inputControls && ["F9","PageUp","PageDown"].includes(key))return repeat || action(key);
            if(key==="Gamepad_RightThumbstick")return repeat || action("F9");
            if(values.view!=="survey")return false;
            const controls={Left:"PlanLeft",Right:"PlanRight",Up:"PlanUp",Down:"PlanDown",Add:"PlanZoomIn",Subtract:"PlanZoomOut",Equals:"PlanZoomIn",Hyphen:"PlanZoomOut",
                Gamepad_DPad_Left:"PlanLeft",Gamepad_DPad_Right:"PlanRight",Gamepad_DPad_Up:"PlanUp",Gamepad_DPad_Down:"PlanDown",
                Gamepad_LeftShoulder:"PageUp",Gamepad_RightShoulder:"PageDown",Gamepad_FaceButton_Left:"TrackTarget",Gamepad_FaceButton_Bottom:"SelectCenter",F9:"F9",PageUp:"PageUp",PageDown:"PageDown"};
            if(inputControls && !key.startsWith("Gamepad_"))return false;
            const command=controls[key];
            return command ? repeat && !command.startsWith("Plan") || action(command) : false;
        },
        dispose: stop,
        confirmMapPoint() {
            const config=settings.read().values;
            if(closed || !view || !config.visibility || configOpen || page.BigMapMode || page.IsPlayingDialogue || page.isOpenCompletion() || page.isModalOpen() || !page.CanUIInput())return false;
            if(config.view==="survey") { action("SelectCenter");return true; }
            const position=page.getCurrentInputType()===UE.ECommonInputType.Gamepad
                ? page.FocusPoint.op_Multiply(page.CurrentMapSize())
                : page.conv_MousePositionToCanvasPosition();
            const pin=view.pinAt(position);
            return pin?action((pin.isFacility?"Facility:":"SelectID:")+pin.pointID):false;
        },
        revealUnexplored() { const c=settings.read().values; return !closed && !!(c.visibility && !c.showFog); },
        surveyVisible() {const c=settings.read().values;return !closed && c.visibility && c.view==="survey" && !page.BigMapMode && !page.isOpenCompletion() && !page.isModalOpen();},
        invalidateTracking() { trackingDirty=true; },
        chromeWidgets() { return (view?.chromeWidgets() || []).concat(pane?.controls.map(p=>p.widget) || [],pane?.map.chromeWidgets() || []); }
    };

    function action(key) {
        if (closed) return false;
        const values = settings.read().values;
        if (key === "F10") settings.apply({ visibility: !values.visibility });
        else if(key==="MapMode")settings.apply({view:values.view==="native"?"survey":"native"});
        else if(key==="Minimap")settings.apply({minimap:!values.minimap});
        else if(key==="SelectCenter" && pane) pane.map.selectCenter();
        else if(key.startsWith("Plan"))pane?.action(key,player,atlas,viewLayer,navigable.find(p=>p.id===selected));
        else if (key === "QuestList" || key === "CollectibleList") {
            listMode=key==="QuestList"?"quest":"collectibles";
            selected=listMode==="quest"?trackedID:""; pageOffset=0; targetIndex=-1;
        }
        else if(key==="ClearTracking") {
            clearTracking();
        }
        else if(key==="TrackTarget") {
            const facility=(facilities?.points || []).find(p=>p.id===(selected || trackedID));
            const point=navigable.find(p=>p.id===(selected || trackedID)) || facility || (!selected?HudTracking.current():undefined);
            if(point && point.state!=="collected" && !point.globalDocument)trackPoint(point);
        }
        else if(key.startsWith("TrackRow:")) {
            const point=visibleRows[Number(key.slice(9))];if(point && !point.globalDocument){trackPoint(point);selectPoint(point);}
        }
        else if (key === "PlayerFloor" && player && sameAtlas(player)) focusMapOnPoint(player);
        else if (key === "TargetFloor") {
            const facility=(facilities?.points || []).find(p=>p.id===selected);
            focusMapOnPoint(navigable.find(p=>p.id===selected) || facility || HudTracking.current());
        }
        else if (key === "Settings") configOpen = !configOpen;
        else if (key === "AutoCollect") {
            const active=!values.autoCollect;
            settings.apply({autoCollect:active});
            if(active){listMode="collectibles";selected="";}
            if(active){HudTracking.stop();trackedID="";}
            publishedKey="";publishHUD();
        }
        else if (key === "FilterAll" || key === "FilterReset") {
            const enabled=key==="FilterReset" || !filterGroups.every(g=>values[g.setting]);
            settings.apply(Object.fromEntries(filterGroups.map(g=>[g.setting,enabled])));
        }
        else if (key.startsWith("Bell:")) {
            const point=catalog?.points.find(p=>p.category==="bell" && p.id===key.slice(5));
            if(point)focusMapOnPoint(point);
        }
        else if (key.startsWith("Facility:")) {
            const point=facilities?.points.find(p=>p.id===key.slice(9));
            if(point) {
                if(selected===point.id)trackPoint(point);
                else { selected=point.id; focusMapOnPoint(point); }
            }
        }
        else if (key.startsWith("Setting:")) { const field = key.slice(8); settings.apply({[field]:!values[field]}); }
        
        else if (key === "F8") settings.apply({ showList: !values.showList, visibility: true });
        else if (["NpcToggle", "TreeHeartToggle"].includes(key)) {
            const category = { NpcToggle: "npc", TreeHeartToggle: "treeHeart" }[key];
            settings.apply({ [category]: !values[category] });
        }
        else if (key === "PagePrevious" || key === "PageNext") {
            const last = Math.max(0, Math.floor((navigable.length - 1) / 6) * 6);
            pageOffset = Math.max(0, Math.min(last, pageOffset + (key === "PageNext" ? 6 : -6)));
            selected = ""; targetIndex = pageOffset - 1;
        }
        else if (key === "RemainingOnly") settings.apply({ remainingOnly: !values.remainingOnly });
        else if (key === "ExploredOnly") settings.apply({ exploredOnly: !values.exploredOnly });
        else if (key === "RightBracket" || key === "Gamepad_DPad_Right") nextTarget(1);
        else if (key === "LeftBracket" || key === "Gamepad_DPad_Left") nextTarget(-1);
        else if (key.startsWith("SelectRow:") || key.startsWith("SelectID:")) {
            const isPin = key.startsWith("SelectID:");
            const point = isPin ? navigable.find(p => p.id === key.slice(9)) : visibleRows[parseInt(key.split(":")[1], 10)];
            if (!point) return false;
            if (isPin && selected === point.id && point.state !== "collected" && !point.globalDocument) trackPoint(point);
            else selectPoint(point);
        } else if (["PageUp", "PageDown", "Gamepad_DPad_Up", "Gamepad_DPad_Down"].includes(key)) {
            const delta = key === "PageUp" || key === "Gamepad_DPad_Up" ? -1 : 1;
            if (atlas) {
                const anchor=values.view==="survey" && pane?pane.camera:player;
                let floors=anchor?atlas.floorsNear(anchor):atlas.layers.map((_,i)=>i);
                if(!floors.includes(viewLayer))floors.push(viewLayer);
                const height=id=>{const l=atlas.layers[id];return l.kind==="under"?-l.depth:l.depth;};
                floors.sort((a,b)=>height(b)-height(a));
                const index=floors.indexOf(viewLayer);
                viewLayer=floors[Math.max(0,Math.min(floors.length-1,index+delta))];nextUpdate=0;return true;
            }
            const nativeInfo = (regions.nativeRegions || []).find(r => r.id.toLowerCase() === region.toLowerCase());
            const maxLayers = Math.max(nativeInfo?.layers || 1, ...(regions.areas || []).filter(a => a.world === nativeInfo?.world).map(a => a.layers?.length || 1));
            const baseLayers = Array.from({ length: maxLayers }, (_, i) => i);
            const layers = [...new Set(baseLayers.concat((catalog?.points || []).map(p => p.layer).filter(layer => layer >= 0), viewLayer))].sort((a, b) => a - b);
            const index = layers.indexOf(viewLayer);
            viewLayer = layers[Math.max(0, Math.min(layers.length - 1, index + delta))];
        } else if (key === "F9" || key === "Gamepad_RightThumbstick") {
            if(store && values.view==="survey" && pane) {
                const position={x:pane.camera.x,y:pane.camera.y,z:pane.camera.z,mapID:atlas?.mapID||region};const uv=projectUV(position);
                if(uv)store.toggle(region,viewLayer,uv[0],uv[1],position);
            } else if (store && page.WB_Cursor) {
                const anchor = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(page.WB_Cursor).GetAnchors();
                const u = anchor.Minimum.X, v = anchor.Minimum.Y;
                if (u >= 0 && u <= 1 && v >= 0 && v <= 1) store.toggle(region, viewLayer, u, v);
            }
        } else return false;
        nextUpdate = 0; return true;
    }
}
module.exports = { open, shouldRevealUnexplored };
