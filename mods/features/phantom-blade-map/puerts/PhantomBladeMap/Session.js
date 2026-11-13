"use strict";
const { Catalog } = require("./Catalog");
const { View } = require("./View");
const Atlas = require("./OfflineAtlas");
const { MapPane } = require("./MapPane");
const { Exploration } = require("./Exploration");
const NativeTracking = require("./NativeTracking");
const NativeLocks = require("./NativeLocks");
const { SideQuests } = require("./SideQuests");
const QuestChains = require("./QuestChains");
const { located } = require("./QuestObjectives");
const HudTracking = require("./HudTracking");
const { dictionary } = require("./Locales");
const Settings = require("./Settings");
const Store = require("./Store");
const Diagnostics = require("./Diagnostics");
const { priority } = require("./SourceState");
const Fog = require("./Fog");
const { NativeFog } = require("./NativeFog");
const regions = require("./atlas/regions");
const Filter = require("./CollectibleFilter");
const { groups: filterGroups, traversalGroups } = require("./Filters");
const { TraversalMarkers } = require("./TraversalMarkers");
const { BellTravel } = require("./BellTravel");
const Conditions = require("./Conditions");
const { PointHint, resolveLockSource } = require("./PointHint");
const Sound = require("./Sound");

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
    const text = { ...dictionary(UE) }, settings = Settings.open(UE, puerts);
    const nativeFog = new NativeFog(UE);
    const bellTravel = new BellTravel(page, UE, cpp, helper, text);
    let inputControls, inputValues;
    try {
        const controls = require("../PhantomBladeRuntime/Controls");
        inputValues = controls.read();
        inputControls = controls;
        const caption = id => String(inputValues[id]).toLowerCase() === "none" ? "—" : inputValues[id];
        text.cancelTrackingShortcut = text.clearTracking + " · " + caption("map.clear");
        text.controls = text.controls.replace(/F6|F7|F10/g, key => caption({ F6: "map.track", F7: "map.clear", F10: "map.visibility" }[key]));
        if (text.controlsKbm) text.controlsKbm = text.controlsKbm.replace(/F6/g, caption("map.track"));
        if (text.trackHintKbm) text.trackHintKbm = text.trackHintKbm.replace(/F6/g, caption("map.track"));
        if (text.untrackHintKbm) text.untrackHintKbm = text.untrackHintKbm.replace(/F7/g, caption("map.clear"));
    } catch (error) {
        Diagnostics.record("input-defaults", { error: String(error) });
    }

    let store;
    try { store = Store.open(UE, puerts, page); } catch (error) { Diagnostics.record("storage-error", { error: String(error) }); }
    let view, catalog, region = "", viewKey = "", nextUpdate = 0, selected = "", targetIndex = -1, pageOffset = 0, scrollToSelected = false;
    let atlas, pane, coverage, facilities, configOpen = false, pendingFocus, publishedKey = "";
    let quests = [], nativeQuests = [], sideQuests, nativeTrackedID = "", trackedID = HudTracking.currentID(), trackingDirty = true, listMode = "collectibles";
    let nativeLocks = [], lastLocks = 0, lockSignature = "";
    let listEntries = [], questChainKey = "";
    let questFocus, questChoices, questReturn, focusPending = false, expandedCompleted = false;
    let closed = false, points = [], navigable = [], visibleRows = [], player, fog, fogReady = false, lastFog = 0, viewLayer = 0, lastCollectionRefresh = 0, lastProfileCheck = 0;
    let floorFocus, pointHint;
    let hoveredPinID = "", overlapPeers = [], overlapChoiceID = "";
    let lastDeltaTime = 0.016, surveyAxisIdle = 999, internalMapZoom = false;
    let sceneKey = "", lastGameTime, lastCycle;
    const world = String(UE.KismetSystemLibrary.GetPathName(page.GetWorld()));
    const pageId = Diagnostics.record("page-open", { backend: "puerts", world });

    function isGamepadNow() {
        try { return page.getCurrentInputType() === UE.ECommonInputType.Gamepad; }
        catch { return false; }
    }

    function readPlayer() {
        const current = require("./PlayerContext").read(page, UE, helper);
        if (!current) return undefined;
        const { x, y, z, groundZ, yaw, aboard } = current;
        return { x, y, z, groundZ, yaw, aboard, mapID: String(page.GetCurrentMapID() || region) };
    }

    function projectUV(point, pure = false) {
        const cached = coverage?.project(point);
        if (cached) return cached;
        if (pure || !catalog?.mapIDs.some(id => id.toLowerCase() === String(point.mapID || region).toLowerCase())) return undefined;
        const position = page.ConvertWorldLocationToScreen(new UE.Vector(point.x, point.y, point.z), point.mapID || region), size = page.CurrentMapSize();
        if (!(size.X > 0 && size.Y > 0)) return;
        return coverage.remember({ ...point, mapID: point.mapID || region }, [position.X / size.X, position.Y / size.Y]);
    }

    function knownVisible(point, pure = false) {
        if (!located(point)) return false;
        if (coverage?.visible(point)) return true;
        if (!fogReady || !fog || !fog.known) return false;
        const uv = projectUV(point, pure);
        return !!uv && Fog.visible(fog, uv[0], uv[1]);
    }

    function focusMapOnPoint(point) {
        if (point?.pointKind === "mapMarker") {
            page.FocusToPoint(new UE.Vector2D(point.u, point.v));
            return;
        }
        if (!located(point)) return;
        if (!catalog.mapIDs.some(id => id.toLowerCase() === point.mapID.toLowerCase())) {
            const destination = page.mapIDToBigMapID(point.mapID);
            if (destination) {
                pendingFocus = { ...point };
                internalMapZoom = true;
                try { page.toOtherMapByID(destination); } finally { internalMapZoom = false; }
            }
            return;
        }
        const id = String(point.mapID || region), worldKey = Atlas.aliases[id.toLowerCase()] || id.toLowerCase();
        if (atlas?.world !== worldKey) {
            atlas?.dispose();
            atlas = Atlas.open(id, true, catalog?.mapIDs || []);
        }
        if (atlas) atlas.mapID = id;
        if (sameAtlas(point)) {
            const floor = atlas.floor(point);
            floorFocus = floor < 0 && atlas.pending ? point : undefined;
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
        const id = String(point?.mapID || region).toLowerCase();
        return !!atlas && (Atlas.aliases[id] || id) === atlas.world;
    }

    function selectPoint(point, shouldFocus = true) {
        selected = point.id;
        scrollToSelected = true;
        targetIndex = navigable.indexOf(point);
        if (shouldFocus) focusMapOnPoint(point);
    }

    function resolveAnyPointByID(id) {
        if (!id) return undefined;
        return (
            navigable.find(p => p.id === id) ||
            listEntries.find(row => row.id === id || row.point?.id === id)?.point ||
            (catalog?.points || []).find(p => p.id === id) ||
            (facilities?.points || []).find(p => p.id === id) ||
            quests.find(p => p.id === id || p.nativeID === id) ||
            view?.pinByID.get(id)?.point ||
            pane?.map.pinByID.get(id)?.point
        );
    }

    function targetByID(id) {
        return navigable.find(p => p.id === id) || listEntries.find(row => row.id === id)?.point || resolveAnyPointByID(id);
    }

    function selectEntry(entry) {
        if (entry.chainKey) {
            questChainKey = entry.chainKey;
            selected = "";
            pageOffset = 0;
            targetIndex = -1;
            questFocus = questChoices?.find(link => link.questKey === entry.chainKey);
            focusPending = !!questFocus;
            expandedCompleted = false;
            if (entry.point) selectPoint(entry.point);
        } else if (entry.toggleCompleted) {
            expandedCompleted = !expandedCompleted;
            selected = entry.id;
            nextUpdate = 0;
        } else if (entry.point) {
            selectPoint(entry.point);
        } else if (listMode === "quest") {
            selected = entry.id;
        } else {
            selectPoint(entry);
        }
    }

    function openPrerequisite(links, source) {
        if (!links.length) return;
        const point = source || targetByID(selected) || catalog?.points.find(p => p.id === selected);
        questReturn = {
            listMode, questChainKey, selected: point?.id || selected, pageOffset, point,
            previous: questReturn, choices: questChoices, focus: questFocus, expanded: expandedCompleted
        };
        questChoices = links;
        questFocus = links.length === 1 ? links[0] : undefined;
        questChainKey = questFocus?.questKey || "";
        focusPending = !!questFocus;
        expandedCompleted = false;
        listMode = "quest";
        selected = "";
        pageOffset = 0;
        targetIndex = -1;
        if (!settings.read().values.showList) settings.apply({ showList: true });
        sideQuests?.invalidate();
        trackingDirty = true;
        nextUpdate = 0;
    }

    function prerequisiteLinks(point, entry) {
        if (entry?.step?.missing?.length) {
            return [{
                questKey: questChainKey,
                step: entry.step.missing.length === 1 ? entry.step.missing[0] : undefined,
                label: sideQuests?.chains.find(c => c.questKey === questChainKey)?.label || text.questPrerequisite
            }];
        }
        return point ? Conditions.links(point, sideQuests?.chains || [], text, UE, page) : [];
    }

    function triggerBell(point) {
        try {
            return bellTravel.request(point);
        } catch (error) {
            Diagnostics.record("error", { error: String(error), source: "bell-teleport" });
            return false;
        }
    }

    function trackPoint(point) {
        if (!point || point.pointKind === "mapMarker") return;
        if (point.category === "quest" && settings.read().values.questGuidance === "explore") return;
        if (point.category === "quest" && !located(point)) return;
        if (point.id === trackedID) {
            clearTracking(true);
            return;
        }
        if (point.availability) {
            selectPoint(point, false);
            nextUpdate = 0;
            return;
        }
        if (settings.read().values.autoCollect) settings.apply({ autoCollect: false });
        function customTarget() {
            nativeTrackedID = "";
            store?.trackQuest(undefined);
            HudTracking.select(page, point, text);
        }
        if (point.category === "quest") {
            if (NativeTracking.track(page, UE, point)) {
                store?.trackQuest(undefined);
                nativeTrackedID = point.nativeID || point.id;
                HudTracking.select(page, point, text, true);
            } else {
                customTarget();
            }
        } else {
            customTarget();
        }
        trackedID = point.id;
        Diagnostics.record("tracking-selected", { id: point.id, kind: point.pointKind, native: HudTracking.current()?.native });
    }

    function clearTracking(keepSelection = false) {
        const automatic = settings.read().values.autoCollect;
        if (automatic) settings.apply({ autoCollect: false });
        else NativeTracking.clear(page, UE, HudTracking.current());
        HudTracking.stop();
        store?.trackQuest(undefined);
        nativeTrackedID = "";
        trackedID = "";
        if (!keepSelection) selected = "";
        trackingDirty = true;
        Diagnostics.record("tracking-cleared");
    }

    // Unified 1st-click inspect, 2nd-click toggle track / Bell travel semantics
    function activatePoint(point, fromMap = true) {
        if (!point) return false;
        Sound.clickPin(page);
        const values = settings.read().values;

        if (selected !== point.id) {
            const crossFloorInSurvey = fromMap && values.view === "survey" && sameAtlas(point) && atlas.floor(point) >= 0 && atlas.floor(point) !== viewLayer;
            selectPoint(point, !fromMap || crossFloorInSurvey);
            nextUpdate = 0;
            return true;
        }

        // Already selected -> 2nd click/confirm triggers primary action
        if (point.category === "bell" && point.state === "collected") {
            triggerBell(point);
        } else if (
            !point.globalDocument &&
            point.pointKind !== "mapMarker" &&
            (point.id === trackedID || (!point.availability && point.state !== "collected" && (point.category !== "quest" || located(point))))
        ) {
            trackPoint(point);
        } else if (point.availability) {
            const links = prerequisiteLinks(point);
            if (links.length) openPrerequisite(links, point);
        }
        nextUpdate = 0;
        return true;
    }

    function mergeQuests() {
        const details = new Map((sideQuests?.points || []).map(p => [p.nativeID, p]));
        const allowedNative = nativeQuests.filter(p => !sideQuests?.ready || sideQuests.chains.some(c => c.questKey === p.questKey && c.status === "active"));
        const nativeIDs = new Set(allowedNative.map(p => p.id));
        const supplement = (sideQuests?.points || []).filter(p => !nativeIDs.has(p.nativeID));
        const all = allowedNative.map(p => (details.has(p.id) ? { ...p, ...details.get(p.id), nativeVisible: true } : p)).concat(supplement);
        const renamed = all.find(p => p.nativeID === selected);
        if (renamed) selected = renamed.id;
        let choice = HudTracking.saved(store?.profile) || store?.trackedQuest();
        const exploring = settings.read().values.questGuidance === "explore";
        if (choice?.category && choice.category !== "quest") choice = undefined;
        if (exploring) choice = undefined;
        if (choice && sideQuests?.ready) {
            const current = QuestChains.next(all, choice);
            if (current) choice = current;
            else {
                const chain = sideQuests.chains.find(c => c.questKey === choice.questKey);
                if (chain && ["completed", "failed"].includes(chain.status)) {
                    HudTracking.stop();
                    store?.trackQuest(undefined);
                    choice = undefined;
                } else {
                    choice = QuestChains.pending(choice, all.filter(p => p.questKey === choice.questKey).length > 1, chain ? undefined : "questState_unknown");
                }
            }
        }
        const currentNative = all.find(p => p.id === nativeTrackedID || p.nativeID === nativeTrackedID);
        trackedID = HudTracking.currentID() || (exploring ? "" : choice?.id || currentNative?.id || nativeTrackedID);
        quests = all;
        if (choice && sideQuests?.ready) {
            const point = QuestChains.next(quests, choice) || choice;
            const current = HudTracking.current();
            if (point && (!current || ["id", "x", "y", "z", "availability", "pointKind"].some(k => current[k] !== point[k]))) {
                HudTracking.select(page, point, text, NativeTracking.track(page, UE, point));
                store?.trackQuest(undefined);
                trackedID = point.id;
            }
        }
    }

    function performAction(key) {
        try { action(key); }
        catch (error) { Diagnostics.record("error", { error: String(error), source: "toolbar" }); }
    }

    function publishHUD() {
        if (!coverage) return;
        const config = settings.read();
        const marks = (store?.marks(region) || [])
            .filter(m => Number.isFinite(m.x))
            .map(m => ({ ...m, id: "pin:" + m.u + ":" + m.v, category: "marker", state: "remaining", label: text.marker, explored: true }));
        const key = [
            region, config.revision, catalog?.ready, sideQuests?.ready, facilities?.ready,
            quests.map(q => q.id).join("|"), fogReady, fog?.revision, marks.map(m => m.id).join("|")
        ].join(":");
        if (key === publishedKey) return;
        publishedKey = key;
        HudTracking.publish(page, {
            profile: store?.profile,
            config: config.values,
            coverage: coverage.snapshot(),
            points: (catalog?.ready ? catalog.points : []).concat(QuestChains.visiblePoints(quests, config.values.questGuidance), marks),
            facilities: facilities?.points || [],
            locks: nativeLocks,
            catalogReady: !!catalog?.ready,
            region
        }, text);
        HudTracking.syncAutomatic(page, player);
    }

    function computeFilterCounts(config, marks) {
        const counts = {};
        const catPoints = (catalog?.points || []).filter(p => !p.globalDocument && Filter.hasContent(p) && (!config.exploredOnly || p.explored || knownVisible(p, true)));
        for (const g of filterGroups) {
            const matching = catPoints.filter(p => {
                if (["npc", "treeHeart", "marker", "quest"].includes(p.category)) return false;
                const matches = p.filterGroups?.length ? p.filterGroups : [Filter.group(p)];
                return matches.includes(g.key);
            });
            const rem = matching.filter(p => p.state !== "collected").length;
            counts[g.setting] = [rem, matching.length];
        }
        for (const kind of ["npc", "treeHeart"]) {
            const list = catPoints.filter(p => p.category === kind);
            counts[kind] = [list.filter(p => p.state !== "collected").length, list.length];
        }
        counts.marker = [marks.length, marks.length];
        const facPoints = (facilities?.points || []).filter(p => !config.exploredOnly || p.explored || knownVisible(p, true));
        for (const tg of traversalGroups) {
            const c = facPoints.filter(p => tg.kinds.includes(p.facilityKind || p.category)).length;
            counts[tg.setting] = [c, c];
        }
        return counts;
    }

    function hasCustomFilterActive(config) {
        return (
            filterGroups.some(g => config[g.setting] === false) ||
            (config.showTraversal !== false && traversalGroups.some(g => config[g.setting] === false)) ||
            config.npc === false ||
            config.treeHeart === false ||
            config.marker === false
        );
    }

    function refresh() {
        const config = settings.read().values;
        const isPad = isGamepadNow();
        publishHUD();
        if (config.view !== "survey") pane?.clear();
        const customFilter = hasCustomFilterActive(config);
        view.setToggles(config, customFilter);
        view.setListMode(listMode);

        if (!catalog?.ready && listMode === "collectibles") {
            view.setPanel({ rows: [], emptyText: text.scanning, pageText: "" }, config.visibility && config.showList, text.scanning, "", isPad);
            return;
        }
        const mapID = String(page.GetCurrentMapID() || "");
        const canSample = sameAtlas({ mapID });
        const marks = (store?.marks(region) || [])
            .filter(m => config.view === "native" || Number.isFinite(m.x))
            .map(m => ({
                ...m,
                id: "pin:" + m.u + ":" + m.v,
                mapID: m.mapID || region,
                pointKind: Number.isFinite(m.x) ? "marker" : "mapMarker",
                category: "marker",
                state: "remaining",
                label: text.marker,
                explored: true
            }));

        const documentsOnly = config.filterDocuments && filterGroups.filter(g => g.key !== "documents").every(g => !config[g.setting]);
        const allCollectibles = (catalog?.points || []).concat(marks).filter(p =>
            (!p.globalDocument || documentsOnly) &&
            Filter.visible(p, config) &&
            (!config.remainingOnly || p.state !== "collected") &&
            (p.pointKind === "pending" || p.pointKind === "mapMarker" || (located(p) && (p.x !== 0 || p.y !== 0 || p.z !== 0)))
        );
        for (const p of allCollectibles) {
            p.knownVisible = p.globalDocument || p.nativeVisible || p.knownQuestPoint || !config.exploredOnly || p.explored || knownVisible(p, true);
        }
        const visibleCollectibles = allCollectibles.filter(p => p.knownVisible);

        const questSource = QuestChains.visiblePoints(quests, config.questGuidance);
        const sourcePoints = listMode === "quest" ? questSource : (catalog?.points || []).concat(marks);
        points = listMode === "quest"
            ? sourcePoints.filter(p =>
                Filter.visible(p, config) &&
                (!config.remainingOnly || p.state !== "collected") &&
                (p.pointKind === "pending" || p.pointKind === "mapMarker" || (located(p) && (p.x !== 0 || p.y !== 0 || p.z !== 0))))
            : visibleCollectibles.slice();

        for (const point of points) {
            point.knownVisible = point.globalDocument || point.nativeVisible || point.knownQuestPoint || !config.exploredOnly || point.explored || knownVisible(point, true);
            if (located(point) && sameAtlas(point)) point.layer = atlas.floor(point);
            else point.layer = -1;
            point.order = 0;
        }
        points = points.filter(point => point.knownVisible);

        navigable = points.slice().sort((a, b) => {
            if ((a.id === trackedID) !== (b.id === trackedID)) return a.id === trackedID ? -1 : 1;
            if ((a.state === "unknown") !== (b.state === "unknown")) return a.state === "unknown" ? 1 : -1;
            if (!!a.availability !== !!b.availability) return a.availability ? 1 : -1;
            if (located(a) !== located(b)) return located(a) ? -1 : 1;
            if (!located(a)) return a.id.localeCompare(b.id);
            const importance = priority(a) - priority(b);
            if (listMode === "collectibles" && importance) return importance;
            const localA = a.mapID.toLowerCase() === mapID.toLowerCase(), localB = b.mapID.toLowerCase() === mapID.toLowerCase();
            if (localA !== localB) return localA ? -1 : 1;
            if (localA && player) {
                return Math.hypot(a.x - player.x, a.y - player.y) - Math.hypot(b.x - player.x, b.y - player.y);
            }
            return a.mapID.localeCompare(b.mapID) || a.id.localeCompare(b.id);
        });
        for (let i = 0; i < navigable.length; i++) navigable[i].order = i + 1;

        const questPanel = QuestChains.entries(sideQuests?.chains || [], quests, text, {
            key: listMode === "quest" ? questChainKey : "",
            mode: config.questGuidance,
            remainingOnly: config.remainingOnly,
            tracked: HudTracking.current(),
            collections: catalog?.points || [],
            focus: listMode === "quest" ? questFocus : undefined,
            choices: listMode === "quest" ? questChoices : undefined,
            expanded: listMode === "quest" ? expandedCompleted : false
        });

        listEntries = listMode === "quest" ? questPanel.rows : navigable;
        if (focusPending && listMode === "quest") {
            const focused = listEntries.find(row => row.focused);
            if (focused) {
                selected = focused.id;
                scrollToSelected = true;
                if (focused.point) focusMapOnPoint(focused.point);
                focusPending = false;
            }
        }

        const facilityPoints = config.showTraversal
            ? (facilities?.points || []).filter(p => Filter.visible(p, config) && (!config.exploredOnly || p.explored || knownVisible(p, true)))
            : [];
        for (const p of facilityPoints) p.layer = sameAtlas(p) ? atlas.floor(p) : -1;

        const selectedFacility = (facilities?.points || []).find(p => p.id === selected) || (!selected ? (facilities?.points || []).find(p => p.id === trackedID) : undefined);
        const selectedBell = (catalog?.points || []).find(p => p.category === "bell" && p.id === selected);
        if (selectedBell && located(selectedBell) && sameAtlas(selectedBell)) selectedBell.layer = atlas.floor(selectedBell);
        const selectedQuestPoint = quests.find(p => p.id === selected || p.nativeID === selected);
        const selectedCatalogPoint = (catalog?.points || []).find(p => p.id === selected);

        const waitingForQuestRows = listMode === "quest" && (!sideQuests?.ready || (questChainKey && !catalog.ready));
        if (
            selected &&
            !selectedFacility &&
            !selectedBell &&
            !selectedQuestPoint &&
            !selectedCatalogPoint &&
            !marks.some(m => m.id === selected) &&
            !listEntries.some(point => point.id === selected) &&
            !navigable.some(point => point.id === selected) &&
            !waitingForQuestRows
        ) {
            selected = "";
        }

        if (config.autoCollect) HudTracking.syncAutomatic(page, player);
        trackedID = HudTracking.currentID() || (config.autoCollect ? "" : trackedID);

        const selectedEntry = listEntries.find(row => row.id === selected);
        const trackedTarget = navigable.find(point => point.id === trackedID) || HudTracking.current();
        const fallbackTarget = (!questPanel?.current || listMode !== "quest" || trackedTarget?.questKey === questChainKey) ? trackedTarget : undefined;
        const target =
            navigable.find(p => p.id === selected) ||
            selectedEntry?.point ||
            selectedBell ||
            selectedFacility ||
            selectedQuestPoint ||
            selectedCatalogPoint ||
            marks.find(m => m.id === selected) ||
            (!selected ? fallbackTarget : undefined);

        if (scrollToSelected && selected && listEntries.some(p => p.id === selected)) {
            targetIndex = listEntries.findIndex(p => p.id === selected);
            pageOffset = Math.floor(targetIndex / 6) * 6;
            scrollToSelected = false;
        } else {
            scrollToSelected = false;
            const maxOffset = Math.max(0, Math.floor((listEntries.length - 1) / 6) * 6);
            if (pageOffset > maxOffset) pageOffset = maxOffset;
        }

        const displayedChain = listMode === "quest" ? (questChainKey || HudTracking.current()?.questKey) : "";
        const mapPoints = listMode === "quest"
            ? navigable.map(p => ({ ...p, dimmed: !!displayedChain && p.questKey !== displayedChain }))
            : visibleCollectibles;
        if (listMode === "quest" && target && target.category !== "quest" && located(target) && !mapPoints.some(p => p.id === target.id)) {
            mapPoints.push(target);
        }

        view.setPins(
            mapPoints.concat(facilityPoints).filter(p => (located(p) || p.pointKind === "mapMarker") && !p.nativeVisible && catalog.mapIDs.some(id => id.toLowerCase() === p.mapID.toLowerCase())),
            config,
            selected,
            trackedID,
            region
        );

        visibleRows = listEntries.slice(pageOffset, pageOffset + 6);

        function locationText(point, includeHeight = false) {
            if (point.documentSource === "initial") return "";
            if (point.pointKind === "mapMarker") return text["region_" + point.mapID.toLowerCase()] || point.mapID;
            if (!located(point)) return text.objectivePending;
            const subName = text["region_" + point.mapID.toLowerCase()] || point.mapID;
            if (!player || point.mapID.toLowerCase() !== mapID.toLowerCase()) return subName;
            const distance = Math.round(Math.hypot(point.x - player.x, point.y - player.y) / 100) + " m";
            return [subName, distance, includeHeight ? elevationText(point.z - player.z, text) : ""].filter(Boolean).join(" · ");
        }

        const rowModels = visibleRows.map(point => {
            const isSel = point.id === selected;
            if (listMode === "quest") return { ...point, selected: isSel, order: 0 };
            const kind = point.category === "bell"
                ? text[point.state === "collected" ? "bellActivated" : "bellUnactivated"]
                : point.collectionKey ? text[point.collectionKey]
                : point.category === "chest" && !point.reward ? text.contentsUnknown : "";
            const detail = [point.documentSource && point.stage ? point.stage : kind, locationText(point, true)].filter(Boolean).join(" · ");
            return {
                selected: isSel,
                tracked: point.id === trackedID,
                canTrack: point.id === trackedID || (!point.availability && !point.globalDocument && point.pointKind !== "mapMarker" && (point.state !== "collected" || point.category === "bell")),
                category: point.category,
                state: point.state,
                order: point.order,
                title: [point.label || text[point.category], text["objective_" + point.objectiveID]].filter(Boolean).join(" · "),
                detail: point.availability ? Conditions.describe(point, text, UE, page).replace(/\n/g, " · ") : (point.state !== "remaining" && point.category !== "bell" ? text[point.state] + " · " : "") + detail
            };
        });

        const totalPages = Math.max(1, Math.ceil(listEntries.length / 6));
        const currentPage = Math.min(totalPages, Math.floor(pageOffset / 6) + 1);
        const pageText = listEntries.length > 0 ? currentPage + " / " + totalPages : "0 / 0";
        const hidden = sourcePoints.filter(p => !p.nativeVisible && !p.knownQuestPoint && Filter.visible(p, config) && (!config.remainingOnly || p.state !== "collected") && !p.knownVisible).length;
        const count = config.remainingOnly && listMode !== "quest" ? navigable.filter(p => p.state === "remaining").length : navigable.length;
        const unknown = navigable.filter(p => p.state === "unknown").length;
        const heading = listMode === "quest"
            ? (questPanel.current
                ? questPanel.current.label + " · " + (text["questState_" + questPanel.current.status] || text.unknown)
                : text.questChains + " " + listEntries.length)
            : text[config.remainingOnly ? "remaining" : "collectibles"] + " " + count + (unknown && config.remainingOnly ? " · " + text.unknown + " " + unknown : "");

        if (config.view === "survey") {
            if (!pane) pane = new MapPane(view, performAction);
            const floorCount = atlas?.layers?.length || 1;
            const layerTag = floorCount > 1 ? layerLabel(viewLayer) + " (" + (viewLayer + 1) + "/" + floorCount + ")" : layerLabel(viewLayer);
            const bells = (catalog?.points || [])
                .filter(p => p.category === "bell" && located(p) && (p.x !== 0 || p.y !== 0 || p.z !== 0))
                .map(p => ({ ...p, layer: sameAtlas(p) ? atlas.floor(p) : -1 }));
            const locks = nativeLocks.map(p => ({ ...p, layer: sameAtlas(p) ? atlas.floor(p) : -1, explored: knownVisible(p) }));
            const panePoints = mapPoints.filter(p => p.category !== "bell").concat(facilityPoints, bells, locks).filter(located);
            const inRegion = point => point && located(point) && (sameAtlas(point) || atlas?.regionWorlds?.has(Atlas.aliases[String(point.mapID || "").toLowerCase()] || String(point.mapID || "").toLowerCase()));
            pane.update(atlas, viewLayer, canSample && player ? { ...player, layer: atlas.floor(player) } : undefined, panePoints, inRegion(target) ? target : undefined, coverage, config, layerTag, trackedID, hoveredPinID);
        }

        publishHUD();
        const filterCounts = configOpen ? computeFilterCounts(config, marks) : undefined;
        view.showSettings(configOpen, config, performAction, filterCounts, isPad);

        const statusParts = [
            store?.storageError ? text.storageUnavailable : "",
            target?.availability && !target.globalDocument ? text[target.availability] : "",
            hidden > 0 && config.exploredOnly ? text.hidden : ""
        ].filter(Boolean);
        if (listMode === "quest" && !sideQuests?.ready) statusParts.push(text.questScanning);

        const isActivatedBell = target?.category === "bell" && target?.state === "collected";
        const peerIndex = target && overlapPeers.length > 1 ? overlapPeers.indexOf(target.id) : -1;
        const rootQuestRows = listMode === "quest" && !questChainKey ? listEntries.length : QuestChains.entries(sideQuests?.chains || [], quests, text, {
            key: "", mode: config.questGuidance, remainingOnly: config.remainingOnly, tracked: HudTracking.current(), collections: catalog?.points || []
        }).rows.length;

        view.setPanel({
            rows: rowModels,
            totalEntries: listEntries.length,
            pageOffset,
            playerLocal: canSample,
            collectibleCountText: catalog?.ready ? String(visibleCollectibles.length) : "",
            questCountText: sideQuests?.ready || quests.length ? String(rootQuestRows) : "",
            regionLabel: text["region_" + region.toLowerCase()] || text.journal,
            hasPreviousPage: pageOffset > 0,
            hasNextPage: pageOffset + 6 < listEntries.length,
            emptyText: text[listMode === "quest" ? "noQuests" : "noRemaining"],
            pageText,
            listMode,
            autoCollect: HudTracking.autoStatus(),
            questChain: listMode === "quest" ? questPanel?.current : undefined,
            questOverview: listMode === "quest" ? questPanel?.overview : undefined,
            questReturn: !!questReturn,
            navigation: target ? {
                title: (isActivatedBell ? text.bellActivated : text[target.id === trackedID ? "tracking" : "preview"]) + " · " + (target.label || text[target.category]),
                detail: [
                    target.contents?.length > 1 ? target.contents.join(" · ") : "",
                    target.stage,
                    target.availability ? Conditions.describe(target, text, UE, page) : locationText(target, true)
                ].filter(Boolean).join("\n"),
                playerLocal: canSample,
                targetLocated: located(target) || target.pointKind === "mapMarker",
                tracked: target.id === trackedID,
                prerequisites: prerequisiteLinks(target, selectedEntry).length > 0,
                canOpenQuestChain: !!(target.questKey && !(listMode === "quest" && questChainKey === target.questKey)),
                canTeleport: isActivatedBell,
                canTrack: target.id === trackedID || (!target.availability && target.state !== "collected" && !target.globalDocument && target.pointKind !== "mapMarker" && (target.category !== "quest" || located(target))),
                overlapIndex: peerIndex >= 0 ? peerIndex + 1 : 1,
                overlapTotal: peerIndex >= 0 ? overlapPeers.length : 1
            } : selectedEntry && listMode === "quest" ? {
                title: selectedEntry.title,
                detail: selectedEntry.tooltip || selectedEntry.detail,
                playerLocal: canSample,
                prerequisites: prerequisiteLinks(undefined, selectedEntry).length > 0,
                targetLocated: false,
                canTrack: false,
                tracked: false
            } : undefined
        }, config.visibility && config.showList, heading, statusParts.join(" · "), isPad);
    }

    function stop(reason = "finish") {
        if (closed) return;
        closed = true;
        const detach = reason !== "slate-release" && reason !== "world-changed";
        HudTracking.mapClosed(reason);
        const before = { view: view?.resources() };
        let textureCache;
        if (detach) {
            try {
                const cache = require("../Gameplay/MapTextureCacheManager").getMapTextureCache();
                textureCache = {
                    current: cache?.currentBigMapID || "",
                    requestedPaths: cache?.lastPushedSignature?.split("|").filter(Boolean).length || 0,
                    gpuBudgetSource: "external-diagnostics"
                };
            } catch (error) { textureCache = { error: String(error) }; }
        }
        Diagnostics.record("page-closing", { pageId, world, region, reason, detach, before, textureCache });
        const errors = [];
        let viewCleanup;
        for (const [owner, cleanup] of [
            ["bell-travel", () => bellTravel.dispose(detach)],
            ["native-fog", () => nativeFog.dispose(detach)],
            ["point-hint", () => pointHint?.dispose(detach)],
            ["atlas", () => atlas?.dispose()],
            ["facilities", () => facilities?.dispose()],
            ["pane", () => pane?.dispose(detach)],
            ["view", () => { viewCleanup = view?.dispose(detach); }],
            ["store", () => store?.dispose()]
        ]) {
            try { cleanup(); } catch (error) { errors.push({ owner, error: String(error) }); }
        }
        atlas = store = view = catalog = undefined;
        points = []; navigable = []; visibleRows = []; listEntries = []; quests = []; nativeQuests = []; sideQuests = undefined; player = fog = undefined;
        pane = undefined; coverage = undefined; facilities = undefined; pointHint = undefined;
        page = undefined;
        Diagnostics.record("page-closed", { pageId, world, region, reason, viewCleanup, errors });
    }

    function nextTarget(direction) {
        if (!listEntries.length) return;
        if (targetIndex < 0) targetIndex = direction > 0 ? 0 : listEntries.length - 1;
        else targetIndex = (targetIndex + direction + listEntries.length) % listEntries.length;
        const point = listEntries[targetIndex];
        Sound.focusPin(page, point?.category);
        if (listMode === "quest") {
            selected = point.id;
            scrollToSelected = true;
            if (questChainKey && point.point) focusMapOnPoint(point.point);
        } else {
            selectPoint(point);
        }
        pageOffset = Math.floor(targetIndex / 6) * 6;
        nextUpdate = 0;
    }

    function cycleOverlapStack() {
        if (overlapPeers.length < 2) return false;
        const curID = overlapChoiceID && overlapPeers.includes(overlapChoiceID)
            ? overlapChoiceID
            : (selected && overlapPeers.includes(selected) ? selected : overlapPeers[0]);
        const idx = (overlapPeers.indexOf(curID) + 1) % overlapPeers.length;
        const nextID = overlapPeers[idx];
        overlapChoiceID = nextID;
        hoveredPinID = nextID;
        const pt = resolveAnyPointByID(nextID);
        if (pt) {
            Sound.focusPin(page, pt.category);
            selectPoint(pt, false);
            nextUpdate = 0;
            return true;
        }
        return false;
    }

    function updateHoverAndHint(config, dt) {
        if (configOpen || !config.visibility) {
            hoveredPinID = "";
            overlapPeers = [];
            pointHint?.hide();
            if (config.view === "survey") pane?.syncCursor(isGamepadNow(), false);
            else view?.stepPins(dt, "", trackedID);
            return;
        }

        const isPad = isGamepadNow();
        const occluded = !isPad && !!page.wantSystemCursorNow?.();
        const sidebarLeft = view?.sidebarLeft() || page.getMapPageLocalSize().X;

        if (config.view === "survey" && pane) {
            let activeNode, peers = [];
            if (!occluded) {
                const hit = pane.pinAtPagePos(page.getMouseLocalPosition(), isPad, hoveredPinID, selected || trackedID);
                activeNode = hit.node;
                peers = hit.peers;
                if (peers.length > 1 && overlapChoiceID) {
                    const chosen = peers.find(n => n.point?.id === overlapChoiceID);
                    if (chosen) activeNode = chosen;
                    else overlapChoiceID = "";
                } else if (peers.length <= 1) {
                    overlapChoiceID = "";
                }
            }
            const nextHover = activeNode?.point?.id || "";
            if (nextHover !== hoveredPinID) {
                hoveredPinID = nextHover;
                if (nextHover) Sound.focusPin(page, activeNode.point?.category);
                nextUpdate = 0;
            }
            if (peers.length > 1) overlapPeers = peers.map(n => n.point.id);
            else if (!selected || !overlapPeers.includes(selected)) overlapPeers = [];

            // Gamepad gentle center snap on Floor Plan after stick release
            if (isPad && activeNode?.point && Number.isFinite(activeNode.point.x) && surveyAxisIdle >= 0.10 && surveyAxisIdle < 0.55) {
                const dx = activeNode.point.x - pane.camera.x, dy = activeNode.point.y - pane.camera.y;
                if (Math.hypot(dx, dy) > pane.camera.cm * 0.4) {
                    const blend = 1 - Math.exp(-dt / 0.055);
                    pane.camera.x += dx * blend;
                    pane.camera.y += dy * blend;
                    pane.panRevision++;
                    nextUpdate = 0;
                }
            }

            pane.syncCursor(isPad, !!hoveredPinID);

            if (pointHint?.hovered()) return;
            const hintNode = activeNode || pane.map.pinByID.get(selected);
            if (!hintNode?.shown || !hintNode.point || !hintNode.screen) {
                pointHint?.leave();
                return;
            }
            const pIdx = overlapPeers.indexOf(hintNode.point.id);
            pointHint?.update(
                hintNode.point,
                pane.map.root.widget,
                new UE.Vector2D(hintNode.screen.x, hintNode.screen.y),
                {
                    player, chains: sideQuests?.chains, selected, trackedID, isGamepad: isPad,
                    sidebarLeft, occluded,
                    overlapIndex: pIdx >= 0 ? pIdx + 1 : 1,
                    overlapTotal: pIdx >= 0 ? overlapPeers.length : 1
                }
            );
            return;
        }

        // Native Map branch
        let activeNode, peers = [];
        if (!occluded) {
            const position = isPad
                ? page.FocusPoint.op_Multiply(page.CurrentMapSize())
                : page.conv_MousePositionToCanvasPosition();
            const hit = view.arbitratePin(position, hoveredPinID, selected || trackedID);
            activeNode = hit.node;
            peers = hit.peers;
            if (peers.length > 1 && overlapChoiceID) {
                const chosen = peers.find(n => n.pointID === overlapChoiceID);
                if (chosen) activeNode = chosen;
                else overlapChoiceID = "";
            } else if (peers.length <= 1) {
                overlapChoiceID = "";
            }
        }

        const nextHover = activeNode?.pointID || "";
        if (nextHover !== hoveredPinID) {
            hoveredPinID = nextHover;
            if (nextHover) Sound.focusPin(page, activeNode.point?.category);
        }
        if (peers.length > 1) overlapPeers = peers.map(n => n.pointID);
        else if (!selected || !overlapPeers.includes(selected)) overlapPeers = [];

        // Clear native SlotWidgetGroup selection when a MOD pin wins hover so two ink bars never overlap
        if (activeNode) {
            try {
                const curSlot = page.SlotWidgetGroup?.GetSelectedSlotID?.();
                if (curSlot && String(curSlot) !== "" && String(curSlot) !== "None") {
                    page.SlotWidgetGroup.ClearSelection();
                }
            } catch {}

            // Gamepad gentle magnetic settle onto MOD pin after stick release
            if (isPad) {
                const idle = page.timeSinceAxisInput ?? 999;
                const pos = view.pinPositions.get(activeNode.pointID);
                const mapSize = page.CurrentMapSize();
                if (pos && mapSize.X > 0 && mapSize.Y > 0 && idle >= 0.10 && idle < 0.55) {
                    const targetU = pos.px / mapSize.X, targetV = pos.py / mapSize.Y;
                    const du = targetU - page.FocusPoint.X, dv = targetV - page.FocusPoint.Y;
                    if (Math.hypot(du * mapSize.X, dv * mapSize.Y) > 0.5) {
                        const blend = 1 - Math.exp(-dt / 0.05);
                        page.FocusToPoint(new UE.Vector2D(page.FocusPoint.X + du * blend, page.FocusPoint.Y + dv * blend));
                        page.UpdateGadgetPositions?.();
                    }
                }
            }
        }

        view.stepPins(dt, hoveredPinID, trackedID);

        if (pointHint?.hovered()) return;
        const parent = page.Gadget;
        const slot = String(page.SlotWidgetGroup?.GetSelectedSlotID?.() || "").toLowerCase();
        const focusData = slot ? page.focusItemMap?.get(slot) : undefined;

        if (!activeNode && focusData?.itemType === 2 && focusData.widget) {
            const lockSource = resolveLockSource(focusData, catalog?.points, UE);
            const lockSlot = lockSource && UE.WidgetLayoutLibrary.SlotAsCanvasSlot(focusData.widget);
            if (lockSlot) {
                pointHint?.update(
                    { ...lockSource, pointKind: "lockSource" },
                    parent,
                    lockSlot.GetPosition(),
                    { player, chains: sideQuests?.chains, selected, trackedID, isGamepad: isPad, sidebarLeft, occluded }
                );
                return;
            }
        }

        const hintNode = activeNode || (!focusData ? view.pinByID.get(selected) : undefined);
        if (!hintNode?.shown || !hintNode.point) {
            pointHint?.leave();
            return;
        }
        const pIdx = overlapPeers.indexOf(hintNode.pointID);
        pointHint?.update(
            hintNode.point,
            parent,
            hintNode.slot.GetPosition(),
            {
                player, chains: sideQuests?.chains, selected, trackedID, isGamepad: isPad,
                sidebarLeft, occluded,
                overlapIndex: pIdx >= 0 ? pIdx + 1 : 1,
                overlapTotal: pIdx >= 0 ? overlapPeers.length : 1
            }
        );
    }

    return {
        tick(deltaTime) {
            if (closed) return;
            const dt = deltaTime > 0 && deltaTime < 0.25 ? deltaTime : 0.016;
            lastDeltaTime = dt;
            surveyAxisIdle += dt;

            if (String(UE.KismetSystemLibrary.GetPathName(page.GetWorld())) !== world) { stop("world-changed"); return; }
            if (page.systemMenuExitPending) { stop("menu-exit"); return; }
            if (page.GetVisibility() === UE.ESlateVisibility.Collapsed || page.GetVisibility() === UE.ESlateVisibility.Hidden) {
                pointHint?.hide();
                pane?.clear();
                view?.hide(true);
                nextUpdate = 0;
                return;
            }
            if (Date.now() - lastProfileCheck >= 1000) {
                lastProfileCheck = Date.now();
                const game = helper.getWorldSubsystem(page, UE.PBZGameSystem.StaticClass());
                const gameTime = Number(game?.GetGameTime()), cycle = game?.GetCycleCount();
                const reload = (Number.isFinite(lastGameTime) && gameTime < lastGameTime) || (lastCycle !== undefined && cycle !== lastCycle);
                lastGameTime = gameTime;
                lastCycle = cycle;
                if (Store.profile(UE, page) !== (store?.profile || "") || reload) {
                    store?.dispose();
                    store = Store.open(UE, puerts, page);
                    HudTracking.stop(false);
                    trackedID = nativeTrackedID = "";
                    pendingFocus = undefined;
                    questChainKey = "";
                    listEntries = [];
                    questFocus = questChoices = questReturn = undefined;
                    focusPending = expandedCompleted = false;
                    region = "";
                    publishedKey = "";
                }
                const nextScene = JSON.stringify([
                    String(page.GetCurrentMapID()),
                    Array.from(page.getallowedMapID(), id => String(id).toLowerCase()).sort()
                ]);
                if (sceneKey && sceneKey !== nextScene) { region = ""; publishedKey = ""; }
                sceneKey = nextScene;
            }

            const config = settings.read();
            nativeFog.update(page, !!(config.values.visibility && !config.values.showFog));
            if (page.BigMapMode || page.IsPlayingDialogue || page.isOpenCompletion()) {
                hoveredPinID = "";
                pointHint?.hide();
                pane?.clear();
                view?.hide(true);
                nextUpdate = 0;
                return;
            }
            if (page.isModalOpen() || page.pendingInitSlice) {
                pointHint?.hide();
                if (config.values.view === "survey") pane?.syncCursor(isGamepadNow(), false);
                return;
            }

            const nextRegion = String(page.CurrentDisplayMapID || "");
            if (!nextRegion || !page.MapItemData) return;
            if (region !== nextRegion) {
                pointHint?.dispose(); pointHint = undefined;
                pane?.dispose(); pane = undefined;
                atlas?.dispose(); atlas = undefined;
                view?.dispose(); view = undefined;
                region = nextRegion;
                selected = ""; targetIndex = -1; pageOffset = 0; scrollToSelected = false;
                hoveredPinID = ""; overlapPeers = []; overlapChoiceID = "";
                points = []; navigable = []; visibleRows = []; trackingDirty = true;
                viewLayer = page.GetPlayerLayer();
                const allowed = Array.from(page.getallowedMapID(), String);
                catalog = new Catalog(allowed, store, region);
                sideQuests = new SideQuests();
                nativeQuests = [];
                quests = [];
                nativeLocks = []; lastLocks = 0; lockSignature = "";
                facilities?.dispose(); facilities = new TraversalMarkers(allowed);
                Diagnostics.record("region-open", { region, current: String(page.GetCurrentMapID()), profile: !!store });
                const records = Fog.records(page, UE);
                lastFog = Date.now();
                fog = records.find(r => r.mapID === region.toLowerCase())?.fog;
                fogReady = !!fog?.known;
                player = readPlayer();
                const currentMap = String(page.GetCurrentMapID());
                const atlasMap = allowed.some(id => id.toLowerCase() === currentMap.toLowerCase()) ? currentMap : region;
                atlas = Atlas.open(atlasMap, true, allowed);
                floorFocus = undefined;
                if (player && sameAtlas(player)) {
                    const floor = atlas.floor(player);
                    viewLayer = Math.max(0, floor);
                    if (floor < 0 && atlas.pending) floorFocus = player;
                } else if (atlas) {
                    viewLayer = Math.max(0, atlas.layers.findIndex(layer => layer.kind === "ground"));
                }
                coverage = new Exploration(records);
                Diagnostics.record("map-coverage", { region, known: fogReady, masks: fog?.rectangles.length, regions: records.length, source: "native-fog-layout-and-save", projection: "native-per-point" });
                publishHUD();
                view = new View(page, UE, cpp, text, performAction);
                pointHint = new PointHint(view, openPrerequisite);
                if (pendingFocus) {
                    const point = pendingFocus;
                    pendingFocus = undefined;
                    selected = point.id;
                    scrollToSelected = true;
                    focusMapOnPoint(point);
                }
                nextUpdate = 0;
                viewKey = "";
            } else if (Date.now() - lastFog >= 1000) {
                lastFog = Date.now();
                const records = Fog.records(page, UE), nextFog = records.find(r => r.mapID === region.toLowerCase())?.fog;
                const changed = nextFog?.revision !== fog?.revision || nextFog?.known !== fog?.known;
                coverage.refresh(records);
                fog = nextFog;
                fogReady = !!fog?.known;
                if (changed) {
                    for (const pt of catalog?.points || []) pt.explored = knownVisible(pt);
                    for (const pt of sideQuests?.points || []) pt.explored = located(pt) && knownVisible(pt);
                    nextUpdate = 0;
                }
            }

            view.showControls(true);
            view.resize();

            if (atlas?.step()) {
                nextUpdate = 0;
                if (floorFocus) {
                    const floor = atlas.floor(floorFocus);
                    if (floor >= 0) { viewLayer = floor; floorFocus = undefined; }
                    else if (!atlas.pending) floorFocus = undefined;
                }
            }
            if (!config.values.visibility) {
                publishHUD();
                view.setToggles(config.values);
                hoveredPinID = "";
                pointHint?.hide();
                pane?.clear();
                view.hide(true);
                nextUpdate = 0;
                return;
            }
            if (Date.now() - lastLocks >= 1000) {
                lastLocks = Date.now();
                const locks = NativeLocks.read(page, catalog.mapIDs, UE, text), signature = JSON.stringify(locks);
                if (signature !== lockSignature) {
                    nativeLocks = locks;
                    lockSignature = signature;
                    publishedKey = "";
                    nextUpdate = 0;
                }
            }
            if (trackingDirty) {
                const snapshot = NativeTracking.snapshot(page, UE);
                nativeQuests = snapshot.points;
                nativeTrackedID = snapshot.trackedID;
                trackingDirty = false;
                for (const point of nativeQuests) point.explored = knownVisible(point);
                mergeQuests();
                Diagnostics.record("native-tracking", { region, points: nativeQuests.length, trackedID });
                nextUpdate = 0;
            }
            if (sideQuests.step(page, UE, helper, p => catalog.mapIDs.some(id => id.toLowerCase() === p.mapID.toLowerCase()) && knownVisible(p))) {
                const snapshot = NativeTracking.snapshot(page, UE);
                nativeQuests = snapshot.points;
                nativeTrackedID = snapshot.trackedID;
                mergeQuests();
                publishedKey = "";
                nextUpdate = 0;
            }
            if (!catalog.ready && (listMode === "collectibles" || questChainKey || config.values.view === "survey" || config.values.autoCollect)) {
                catalog.step(page, UE, helper, text, knownVisible);
                nextUpdate = 0;
                if (catalog.ready) {
                    Diagnostics.record("catalog-ready", {
                        region,
                        points: catalog.points.length,
                        remaining: catalog.points.filter(p => p.state === "remaining").length,
                        collected: catalog.points.filter(p => p.state === "collected").length,
                        unknown: catalog.points.filter(p => p.state === "unknown").length
                    });
                }
            }
            if (catalog.ready && Date.now() - lastCollectionRefresh >= 1000) {
                lastCollectionRefresh = Date.now();
                player = readPlayer();
                nextUpdate = 0;
                if (catalog.refresh(page, UE, helper, player)) {
                    publishedKey = "";
                    nextUpdate = 0;
                }
            }
            if (config.values.showTraversal && !facilities.ready) {
                facilities.step(page, UE, helper, text, knownVisible);
                nextUpdate = 0;
            }

            if (config.values.view === "survey" && pane?.tick(dt)) nextUpdate = 0;

            const size = page.CurrentMapSize(), viewport = page.getMapPageLocalSize();
            const surveyState = config.values.view === "survey" ? [pane?.panRevision || 0, hoveredPinID].join(":") : "";
            const key = [
                region, viewLayer, listMode, config.revision, size.X, size.Y,
                viewport.X, viewport.Y, view.panelScale, selected, trackedID,
                pageOffset, configOpen, isGamepadNow(), !!catalog.ready, surveyState
            ].join(":");
            if (key !== viewKey || nextUpdate === 0) {
                viewKey = key;
                nextUpdate = 1;
                refresh();
            }

            updateHoverAndHint(config.values, dt);
        },

        key(event) {
            if (closed || page.BigMapMode || page.isOpenCompletion() || page.isModalOpen() || page.IsPlayingDialogue) return false;
            const input = UE.KismetInputLibrary, key = String(input.GetKey(event).KeyName);
            const repeat = input.InputEvent_IsRepeat(event);

            if (key === "Tab" && !configOpen && settings.read().values.visibility && overlapPeers.length > 1) {
                return repeat || cycleOverlapStack();
            }

            // Native BellTeleport owns X / Square and confirms the current cursor through readyToTeleport
            if (key === "Gamepad_FaceButton_Top" || key === "Gamepad_FaceButton_Left") return false;

            if (configOpen) {
                if (repeat && !["Up", "Down", "Gamepad_DPad_Up", "Gamepad_DPad_Down"].includes(key)) return true;
                if (view?.filterKey(key)) return true;
            }
            if (input.InputEvent_IsControlDown(event) || input.InputEvent_IsShiftDown(event) || input.InputEvent_IsAltDown(event)) return false;

            const values = settings.read().values, binding = inputControls?.action("map", event, inputValues);
            if (key === "Escape" || key === "Gamepad_FaceButton_Right") {
                if (!repeat && this.handleBack()) return true;
                return false;
            }
            if (key === "F4") return repeat || action("Settings");
            if (key === "F5" || key === "Gamepad_LeftThumbstick") return repeat || action("AutoCollect");
            if (binding?.id === "map.visibility") return repeat || action(binding.command);
            if (!inputControls && key === "F10") return repeat || action(key);
            if (!values.visibility) return false;

            if ((key === "Enter" || key === "Gamepad_FaceButton_Bottom") && pointHint?.shown && pointHint.links.length) {
                return repeat || pointHint.open();
            }
            if (listMode === "quest" && selected && (key === "Enter" || key === "Gamepad_FaceButton_Bottom")) {
                return repeat || action("QuestConfirm");
            }
            if (binding && (!binding.survey || values.view === "survey")) {
                return (repeat && !binding.command.startsWith("Plan")) || action(binding.command);
            }
            if (!inputControls && key === "F6") return repeat || action("TrackTarget");
            if (!inputControls && key === "F7") return repeat || action("ClearTracking");
            if (!inputControls && key === "F8") return repeat || action("F8");
            if (!inputControls && ["F9", "PageUp", "PageDown"].includes(key)) return repeat || action(key);
            if (key === "Gamepad_RightThumbstick") return repeat || action("F9");

            if (values.view !== "survey") return false;
            const controls = {
                Left: "PlanLeft", Right: "PlanRight", Up: "PlanUp", Down: "PlanDown",
                Add: "PlanZoomIn", Subtract: "PlanZoomOut", Equals: "PlanZoomIn", Hyphen: "PlanZoomOut",
                Gamepad_DPad_Left: "PlanLeft", Gamepad_DPad_Right: "PlanRight", Gamepad_DPad_Up: "PlanUp", Gamepad_DPad_Down: "PlanDown",
                Gamepad_LeftShoulder: "PageUp", Gamepad_RightShoulder: "PageDown",
                Gamepad_FaceButton_Bottom: "SelectCenter", F9: "F9", PageUp: "PageUp", PageDown: "PageDown"
            };
            if (inputControls && !key.startsWith("Gamepad_")) return false;
            const command = controls[key];
            return command ? (repeat && !command.startsWith("Plan")) || action(command) : false;
        },

        handleBack() {
            if (closed || !view) return false;
            if (configOpen) return action("Settings");
            const values = settings.read().values;
            if (!values.visibility) return false;
            if (listMode === "quest" && (questChainKey || questReturn)) return action("QuestBack");
            if (values.view === "survey") return action("ViewNative");
            return false;
        },

        onMouseWheelZoom(newScale, currentScale, scaleRate) {
            if (closed || !view || page.BigMapMode || page.isOpenCompletion() || page.isModalOpen()) return false;
            const values = settings.read().values;
            const delta = (newScale - currentScale) / Math.max(0.0001, scaleRate || 0.1);
            if (!delta) return false;

            if (values.visibility && view.isMouseInSidebar()) {
                if (configOpen) return view.wheelFilter(delta);
                if (values.showList) {
                    const maxOffset = Math.max(0, Math.floor((listEntries.length - 1) / 6) * 6);
                    const nextOffset = Math.max(0, Math.min(maxOffset, pageOffset + (delta > 0 ? -6 : 6)));
                    if (nextOffset !== pageOffset) {
                        pageOffset = nextOffset;
                        Sound.hoverUI(page);
                        nextUpdate = 0;
                    }
                    return true;
                }
            }

            if (this.surveyVisible() && pane) {
                if (page.wantSystemCursorNow?.()) return true;
                pane.zoomAtPoint(delta, page.getMouseLocalPosition(), atlas, viewLayer);
                nextUpdate = 0;
                return true;
            }

            if (page.wantSystemCursorNow?.()) return true;
            return false;
        },

        onGamepadZoom(newScale, currentScale, scaleRate) {
            if (internalMapZoom || !this.surveyVisible() || !pane || !isGamepadNow()) return false;
            const delta = (newScale - currentScale) / Math.max(0.0001, scaleRate || 0.1);
            if (!delta) return false;
            pane.zoomAtPoint(delta, undefined, atlas, viewLayer);
            nextUpdate = 0;
            return true;
        },

        panSurveyByPixels(dx, dy) {
            if (!this.surveyVisible() || !pane) return false;
            if (pane.panByPixels(dx, dy, atlas, viewLayer)) {
                nextUpdate = 0;
                return true;
            }
            return false;
        },

        moveSurveyAxis(axisX, axisY) {
            if (!this.surveyVisible() || !pane) return false;
            surveyAxisIdle = 0;
            if (pane.moveByAxis(axisX, axisY, lastDeltaTime, atlas, viewLayer)) {
                nextUpdate = 0;
                return true;
            }
            return false;
        },

        onSurveySurfaceClick() {
            if (!this.surveyVisible() || !pane) return false;
            const hit = pane.pinAtPagePos(page.getMouseLocalPosition(), false, hoveredPinID, selected || trackedID);
            let node = hit.node;
            if (hit.peers.length > 1 && overlapChoiceID) {
                node = hit.peers.find(n => n.point?.id === overlapChoiceID) || node;
            }
            if (node?.point) {
                return pane.handlePinConfirm(node.point);
            }
            return true;
        },

        resetSurveyView() {
            if (!this.surveyVisible() || !pane) return false;
            if (player && sameAtlas(player)) {
                focusMapOnPoint(player);
            } else {
                pane.fit(atlas?.bounds(viewLayer));
            }
            nextUpdate = 0;
            return true;
        },

        isCursorOverModPin() {
            const values = settings.read().values;
            if (closed || !values.visibility || configOpen || values.view !== "native") return false;
            return !!hoveredPinID;
        },

        dispose: stop,

        confirmMapPoint() {
            const config = settings.read().values;
            if (closed || !view || !config.visibility || configOpen || page.BigMapMode || page.IsPlayingDialogue || page.isOpenCompletion() || page.isModalOpen() || !page.CanUIInput()) {
                return false;
            }
            const isPad = isGamepadNow();
            if (isPad && pointHint?.open()) return true;

            if (config.view === "survey") {
                if (pane) {
                    const hit = pane.pinAtPagePos(page.getMouseLocalPosition(), isPad, hoveredPinID, selected || trackedID);
                    const node = (hit.peers.length > 1 && overlapChoiceID && hit.peers.find(n => n.point?.id === overlapChoiceID)) || hit.node;
                    if (node?.point) pane.handlePinConfirm(node.point);
                }
                return true;
            }

            const position = isPad
                ? page.FocusPoint.op_Multiply(page.CurrentMapSize())
                : page.conv_MousePositionToCanvasPosition();
            const hit = view.arbitratePin(position, hoveredPinID, selected || trackedID);
            const pin = (hit.peers.length > 1 && overlapChoiceID && hit.peers.find(n => n.pointID === overlapChoiceID)) || hit.node;
            if (pin?.point) {
                return activatePoint(pin.point, true);
            }

            if (config.questGuidance === "explore") return false;
            const slotID = String(page.SlotWidgetGroup.GetSelectedSlotID() || "").toLowerCase();
            const data = page.focusItemMap?.get(slotID);
            if (!data?.questObjName || String(data.taskID).startsWith("Accept_")) return false;
            const nativeID = "quest:" + data.questObjName + ":" + data.taskID;
            const point = quests.find(p => p.nativeID === nativeID || p.id === nativeID) || NativeTracking.snapshot(page, UE).points.find(p => p.id === nativeID);
            if (!point?.questKey) return false;
            const result = activatePoint(point, true);
            trackingDirty = true;
            return result;
        },

        revealUnexplored() {
            const c = settings.read().values;
            return !closed && !!(c.visibility && !c.showFog);
        },

        surveyVisible() {
            const c = settings.read().values;
            return !closed && !!c.visibility && c.view === "survey" && !page.BigMapMode && !page.isOpenCompletion() && !page.isModalOpen();
        },

        invalidateTracking() {
            trackingDirty = true;
            sideQuests?.invalidate();
        },

        chromeWidgets() {
            return (view?.chromeWidgets() || []).concat(pane?.chromeWidgets() || [], pointHint?.chromeWidgets() || []);
        }
    };

    function action(key) {
        if (closed || bellTravel.pending || page.isModalOpen()) return false;
        const values = settings.read().values;

        if (key === "F10") {
            settings.apply({ visibility: !values.visibility });
        } else if (key === "ViewNative") {
            if (values.view !== "native") {
                Sound.switchView(page);
                settings.apply({ view: "native" });
            }
        } else if (key === "ViewSurvey") {
            if (values.view !== "survey") {
                Sound.switchView(page);
                settings.apply({ view: "survey" });
            }
        } else if (key === "MapMode") {
            Sound.switchView(page);
            settings.apply({ view: values.view === "native" ? "survey" : "native" });
        } else if (key === "Minimap") {
            settings.apply({ minimap: !values.minimap });
        } else if (key === "OverlapCycle") {
            return cycleOverlapStack();
        } else if (key === "SelectCenter" && pane) {
            pane.map.selectCenter();
        } else if (key.startsWith("Plan")) {
            pane?.action(key, player, atlas, viewLayer, resolveAnyPointByID(selected));
        } else if (key === "QuestList" || key === "CollectibleList") {
            listMode = key === "QuestList" ? "quest" : "collectibles";
            questChainKey = "";
            selected = "";
            pageOffset = 0;
            targetIndex = -1;
            questFocus = questChoices = questReturn = undefined;
            focusPending = expandedCompleted = false;
        } else if (key === "QuestBack") {
            if (questChainKey && questChoices?.length > 1) {
                questChainKey = "";
            } else if (questReturn) {
                const previous = questReturn;
                ({ listMode, questChainKey, selected, pageOffset } = previous);
                questReturn = previous.previous;
                questChoices = previous.choices;
                questFocus = previous.focus;
                expandedCompleted = previous.expanded;
                focusPending = false;
                targetIndex = -1;
                if (previous.point) focusMapOnPoint(previous.point);
                nextUpdate = 0;
                return true;
            } else {
                questChainKey = "";
            }
            questFocus = undefined;
            focusPending = expandedCompleted = false;
            selected = "";
            pageOffset = 0;
            targetIndex = -1;
        } else if (key.startsWith("QuestGuidance:")) {
            const mode = key.slice(14);
            if (!QuestChains.guidanceModes.includes(mode)) return false;
            settings.apply({ questGuidance: mode });
            if (mode === "explore" && HudTracking.current()?.category === "quest") HudTracking.stop();
            selected = "";
            pageOffset = 0;
            targetIndex = -1;
            trackingDirty = true;
            publishedKey = "";
            publishHUD();
        } else if (key === "QuestConfirm") {
            const entry = listEntries.find(row => row.id === selected);
            if (entry?.chainKey || entry?.toggleCompleted) selectEntry(entry);
            else if (entry?.canTrack && entry.point) { Sound.clickPin(page); trackPoint(entry.point); }
        } else if (key === "Prerequisite" || key === "SecondaryNavAction") {
            const entry = listEntries.find(row => row.id === selected);
            const point = resolveAnyPointByID(selected) || (!selected ? HudTracking.current() : undefined);
            const links = prerequisiteLinks(point, entry);
            if (links.length) {
                openPrerequisite(links, point);
            } else if (point?.questKey) {
                listMode = "quest";
                questChainKey = point.questKey;
                pageOffset = 0;
                if (!values.showList) settings.apply({ showList: true });
            }
        } else if (key === "ClearTracking") {
            Sound.clickPin(page);
            clearTracking(false);
        } else if (key === "PrimaryNavAction") {
            Sound.clickPin(page);
            const bell = (catalog?.points || []).find(p => p.category === "bell" && p.id === selected);
            if (bell && bell.state === "collected") {
                triggerBell(bell);
            } else if ((selected || trackedID) === trackedID && trackedID) {
                clearTracking(true);
            } else {
                const point = resolveAnyPointByID(selected || trackedID) || (!selected ? HudTracking.current() : undefined);
                if (point && point.state !== "collected" && !point.globalDocument) trackPoint(point);
            }
        } else if (key === "TrackTarget") {
            Sound.clickPin(page);
            const slot = values.view === "native" && !hoveredPinID ? String(page.SlotWidgetGroup?.GetSelectedSlotID?.() || "").toLowerCase() : "";
            const focusData = slot ? page.focusItemMap?.get(slot) : undefined;
            const lockSource = focusData?.itemType === 2 ? resolveLockSource(focusData, catalog?.points, UE) : undefined;
            if (lockSource) {
                listMode = "collectibles";
                questChainKey = "";
                selectPoint(lockSource, true);
                if (lockSource.state !== "collected" && !lockSource.availability) trackPoint(lockSource);
            } else {
                const candidateID = selected || hoveredPinID || trackedID;
                const bell = (catalog?.points || []).find(p => p.category === "bell" && p.id === candidateID);
                if (bell && bell.state === "collected") {
                    selectPoint(bell, false);
                    triggerBell(bell);
                } else {
                    const point = resolveAnyPointByID(candidateID) || (!selected ? HudTracking.current() : undefined);
                    if (point && !point.globalDocument && (point.id === trackedID || point.state !== "collected")) {
                        if (!selected) selectPoint(point, false);
                        trackPoint(point);
                    }
                }
            }
        } else if (key.startsWith("TrackRow:")) {
            Sound.clickPin(page);
            const row = visibleRows[Number(key.slice(9))];
            if (listMode === "quest" && row?.tracked) {
                clearTracking(true);
                nextUpdate = 0;
                return true;
            }
            const point = listMode === "quest" ? (row?.canTrack ? row.point : undefined) : row;
            if (point && point.category === "bell" && point.state === "collected") {
                selectPoint(point, values.view !== "survey");
                triggerBell(point);
            } else if (point && !point.globalDocument) {
                trackPoint(point);
                if (listMode === "quest" && row.chainKey) {
                    questChainKey = row.chainKey;
                    pageOffset = 0;
                }
                selectPoint(point, values.view !== "survey");
            }
        } else if (key === "PlayerFloor" && player && sameAtlas(player)) {
            focusMapOnPoint(player);
        } else if (key === "TargetFloor") {
            focusMapOnPoint(resolveAnyPointByID(selected) || (!selected ? HudTracking.current() : undefined));
        } else if (key === "StatusFloorJump") {
            const t = resolveAnyPointByID(selected) || (!selected ? (resolveAnyPointByID(trackedID) || HudTracking.current()) : undefined);
            if (t && sameAtlas(t) && atlas.floor(t) >= 0 && atlas.floor(t) !== viewLayer) focusMapOnPoint(t);
            else if (player && sameAtlas(player) && atlas.floor(player) >= 0 && atlas.floor(player) !== viewLayer) focusMapOnPoint(player);
        } else if (key === "Settings") {
            configOpen = !configOpen;
            Sound.panelToggle(page, configOpen);
        } else if (key === "AutoCollect") {
            Sound.clickPin(page);
            const active = !values.autoCollect;
            settings.apply({ autoCollect: active });
            if (active) {
                listMode = "collectibles";
                selected = "";
                HudTracking.stop();
                trackedID = "";
            }
            publishedKey = "";
            publishHUD();
        } else if (key === "FilterAll" || key === "FilterReset") {
            const enabled = key === "FilterReset" || !filterGroups.every(g => values[g.setting]);
            settings.apply(Object.fromEntries(filterGroups.map(g => [g.setting, enabled])));
        } else if (key.startsWith("Bell:")) {
            const point = catalog?.points.find(p => p.category === "bell" && p.id === key.slice(5));
            if (point) return activatePoint(point, true);
        } else if (key.startsWith("Facility:")) {
            const point = facilities?.points.find(p => p.id === key.slice(9));
            if (point) return activatePoint(point, true);
        } else if (key.startsWith("Setting:")) {
            const field = key.slice(8);
            settings.apply({ [field]: !values[field] });
        } else if (key === "F8") {
            const nextShow = !values.showList;
            Sound.panelToggle(page, nextShow);
            settings.apply({ showList: nextShow, visibility: true });
        } else if (["NpcToggle", "TreeHeartToggle"].includes(key)) {
            const category = { NpcToggle: "npc", TreeHeartToggle: "treeHeart" }[key];
            settings.apply({ [category]: !values[category] });
        } else if (key === "PagePrevious" || key === "PageNext") {
            const last = Math.max(0, Math.floor((listEntries.length - 1) / 6) * 6);
            pageOffset = Math.max(0, Math.min(last, pageOffset + (key === "PageNext" ? 6 : -6)));
            targetIndex = pageOffset - 1;
        } else if (key === "RemainingOnly") {
            settings.apply({ remainingOnly: !values.remainingOnly });
        } else if (key === "ExploredOnly") {
            settings.apply({ exploredOnly: !values.exploredOnly });
        } else if (key === "RightBracket" || key === "Gamepad_DPad_Right") {
            nextTarget(1);
        } else if (key === "LeftBracket" || key === "Gamepad_DPad_Left") {
            nextTarget(-1);
        } else if (key.startsWith("SelectRow:") || key.startsWith("SelectID:")) {
            const isPin = key.startsWith("SelectID:");
            const entry = isPin ? undefined : visibleRows[parseInt(key.split(":")[1], 10)];
            if (listMode === "quest" && (entry?.chainKey || entry?.toggleCompleted)) {
                Sound.clickPin(page);
                selectEntry(entry);
                nextUpdate = 0;
                return true;
            }
            if (listMode === "quest" && entry && !entry.point) {
                Sound.clickPin(page);
                selected = entry.id;
                nextUpdate = 0;
                return true;
            }
            const point = isPin ? resolveAnyPointByID(key.slice(9)) : (listMode === "quest" ? entry?.point : entry);
            if (!point) return false;
            return activatePoint(point, isPin);
        } else if (["PageUp", "PageDown", "Gamepad_DPad_Up", "Gamepad_DPad_Down"].includes(key)) {
            const delta = key === "PageUp" || key === "Gamepad_DPad_Up" ? -1 : 1;
            if (atlas) {
                const floors = atlas.layers.map((_, i) => i);
                const height = id => { const l = atlas.layers[id]; return l.kind === "under" ? -l.depth : l.depth; };
                floors.sort((a, b) => height(b) - height(a));
                const index = floors.indexOf(viewLayer);
                floorFocus = undefined;
                viewLayer = floors[Math.max(0, Math.min(floors.length - 1, index + delta))];
                Sound.hoverUI(page);
                nextUpdate = 0;
                return true;
            }
            const nativeInfo = (regions.nativeRegions || []).find(r => r.id.toLowerCase() === region.toLowerCase());
            const maxLayers = Math.max(nativeInfo?.layers || 1, ...(regions.areas || []).filter(a => a.world === nativeInfo?.world).map(a => a.layers?.length || 1));
            const baseLayers = Array.from({ length: maxLayers }, (_, i) => i);
            const layers = [...new Set(baseLayers.concat((catalog?.points || []).map(p => p.layer).filter(layer => layer >= 0), viewLayer))].sort((a, b) => a - b);
            const index = layers.indexOf(viewLayer);
            floorFocus = undefined;
            viewLayer = layers[Math.max(0, Math.min(layers.length - 1, index + delta))];
            Sound.hoverUI(page);
        } else if (key === "F9" || key === "Gamepad_RightThumbstick") {
            Sound.clickPin(page);
            if (store && values.view === "survey" && pane) {
                const position = { x: pane.camera.x, y: pane.camera.y, z: pane.camera.z, mapID: atlas?.mapID || region };
                const uv = projectUV(position);
                if (uv) store.toggle(region, viewLayer, uv[0], uv[1], position);
            } else if (store && page.WB_Cursor) {
                const anchor = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(page.WB_Cursor).GetAnchors();
                const u = anchor.Minimum.X, v = anchor.Minimum.Y;
                if (u >= 0 && u <= 1 && v >= 0 && v <= 1) store.toggle(region, viewLayer, u, v);
            }
        } else {
            return false;
        }
        nextUpdate = 0;
        return true;
    }
}

module.exports = { open, shouldRevealUnexplored };
