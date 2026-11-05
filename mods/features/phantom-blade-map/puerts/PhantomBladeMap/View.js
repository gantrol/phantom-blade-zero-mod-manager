"use strict";
const { paths, collectibles } = require("./Icons");
const { layout, rgba } = require("./Appearance");
const {CanvasDrawing}=require("./CanvasDrawing");
const {FilterPanel}=require("./FilterPanel");
class View extends CanvasDrawing {
    constructor(page, UE, cpp, text, action) {
        super(page,UE,cpp,text);
        this.action = action;
        this.widgets = []; this.owned = new Set(); this.roots = []; this.pins = []; this.buttons = []; this.listeners = []; this.pending = []; this.cursor = 0;
        this.pinByID=new Map();this.pinWork=new Map();this.queuedPins=new Set();this.pinPositions=new Map();this.freePins=[];
        this.rows = []; this.toggleBars = {}; this.modeTabs = {};
        try {
            this.chrome = this.create(UE.Border, page.WidgetTree.RootWidget);
            this.chrome.SetPadding(new UE.Margin(0, 0, 0, 0));
            this.chrome.SetBrushColor(new UE.LinearColor(...rgba("ink", .97)));
            this.chrome.SetVisibility(UE.ESlateVisibility.Visible);
            this.chrome.SetCursor(UE.EMouseCursor.Default);
            this.chrome.SetRenderTransformPivot(new UE.Vector2D(0,0));
            this.chromeSlot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.chrome);
            this.chromeSlot.SetAnchors(new UE.Anchors(new UE.Vector2D(0, 0), new UE.Vector2D(0, 0)));
            this.chromeSlot.SetAlignment(new UE.Vector2D(0, 0));
            this.resize();
            this.chromeSlot.SetSize(new UE.Vector2D(layout.width, layout.compactHeight));
            this.chromeSlot.SetZOrder(40);
            this.canvas = this.create(UE.CanvasPanel, this.chrome);
            this.heading = this.makeText(this.canvas, 18, 20, 16, 202, 28, rgba("paper"));
            this.textValue(this.heading, text.journal);
            this.makeButton(["Settings", "filter", "filters"], 232, 12, 32, action);
            this.makeButton(["F8", "list", "showList"], 268, 12, 32, action);
            this.makeButton(["F10", "eye", "visibility"], 304, 12, 32, action);

            this.makeButton(["CollectibleList", "chest", "collectibles"], 20, 50, 32, action);
            this.makeButton(["QuestList", "quest", "quest"], 60, 50, 32, action);
            this.listBars={collectibles:this.makeRect(this.canvas,28,83,16,2,...rgba("accent")),quest:this.makeRect(this.canvas,68,83,16,2,...rgba("accent"))};
            this.makeRect(this.canvas, 104, 57, 1, 18, ...rgba("line", .55));
            this.mapModeButton=this.makeButton(["MapMode", "map", "view"], 116, 50, 32, action);
            this.makeButton(["Minimap", "compass", "minimap"], 156, 50, 32, action);
            this.makeButton(["PlayerFloor", "center", "locatePlayer"], 196, 50, 32, action);
            this.makeRect(this.canvas, 240, 57, 1, 18, ...rgba("line", .55));
            this.makeButton(["RemainingOnly", "remaining", "remainingOnly"], 252, 50, 32, action);
            this.makeButton(["F9", "pin", "marker"], 296, 50, 32, action);
            this.toggleBars.RemainingOnly = this.makeRect(this.canvas,264,83,8,2,...rgba("accent"));
            this.toggleBars.MapMode=this.makeRect(this.canvas,128,83,8,2,...rgba("accent"));
            this.toggleBars.Minimap=this.makeRect(this.canvas,168,83,8,2,...rgba("accent"));

            this.summary = this.makeText(this.canvas, 12, 20, 101, 312, 22, rgba("muted"));
            this.bodyRule = this.makeRect(this.canvas, 20, 128, 312, 1, ...rgba("line", .8));

            // One typographic list; selection is the only filled row.
            for (let i = 0; i < 6; i++) {
                const y = layout.rowsY + i * layout.rowHeight;
                const bg = this.makeRect(this.canvas, 12, y, 328, 50, ...rgba("selected",.7));
                const bar = this.makeRect(this.canvas, 12, y + 10, 2, 30, ...rgba("accent"));
                const icon = this.makePinWidget(this.canvas);this.place(icon,36,y+24);
                const title = this.makeText(this.canvas, 14, 62, y + 5, 224, 24, rgba("paper"));
                const detail = this.makeText(this.canvas, 10, 62, y + 30, 224, 18, rgba("muted"));
                const rule = this.makeRect(this.canvas, 62, y + 51, 270, 1, ...rgba("line", .3));
                const btn = this.makeRowButton(12, y, 276, 50, "SelectRow:" + i, action);
                const track=this.makeButton(["TrackRow:"+i,"quest","trackingTarget"],296,y+8,32,action);
                const trackingBar=this.makeRect(this.canvas,304,y+42,16,2,...rgba("accent"));
                icon.slot.SetZOrder(1);
                this.rows.push({ bg, bar, icon, title, detail, rule, btn,track,trackingBar });
            }
            this.emptyText = this.makeText(this.canvas, 12, 20, layout.rowsY + 8, 280, 42, rgba("muted"));
            this.emptyText.widget.SetAutoWrapText(true);
            this.previousPage = this.makeButton(["PagePrevious", "previous", "previousPage"], 12, 0, 32, action);
            this.nextPage = this.makeButton(["PageNext", "next", "nextPage"], 304, 0, 32, action);
            this.pageInfo = this.makeText(this.canvas, 11, 144, 0, 64, 22, rgba("muted"));
            this.pageInfo.widget.SetJustification(UE.ETextJustify.Center);
            this.status = this.makeText(this.canvas, 10, 20, 0, 312, 20, rgba("muted"));
            this.navigationRule=this.makeRect(this.canvas,20,0,312,1,...rgba("line",.8));
            this.navigationTitle=this.makeText(this.canvas,13,20,0,312,24,rgba("paper"));
            this.navigationDetail=this.makeText(this.canvas,11,20,0,312,84,rgba("muted"));
            this.navigationDetail.widget.SetAutoWrapText(true);
            this.navigationButtons=[
                this.makeButton(["PlayerFloor","center","locatePlayer"],20,0,32,action),
                this.makeButton(["TargetFloor","pin","locateTarget"],60,0,32,action),
                this.makeButton(["TrackTarget","quest","trackingTarget"],180,0,32,action)
            ];
            // Cancellation needs an explicit label: the previous minus icon
            // was indistinguishable from zoom/collapse in the map toolbar.
            this.cancelTracking=this.makeRowButton(220,0,112,32,"ClearTracking",action);
            this.cancelTracking.widget.SetToolTipText(cpp.FText.FromString(text.cancelTrackingShortcut));
            const captionCanvas=this.create(UE.CanvasPanel,this.cancelTracking.widget);
            captionCanvas.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
            const caption=this.makeText(captionCanvas,13,0,4,112,24,rgba("paper"));
            caption.widget.SetJustification(UE.ETextJustify.Center);this.textValue(caption,text.clearTracking);

            const autoCanvas=this.create(UE.CanvasPanel,this.canvas);
            this.autoStrip={widget:autoCanvas,slot:UE.WidgetLayoutLibrary.SlotAsCanvasSlot(autoCanvas),interactive:true};
            this.autoStrip.slot.SetSize(new UE.Vector2D(layout.width,62));
            this.makeRect(autoCanvas,20,0,312,1,...rgba("line",.8));
            this.autoTitle=this.makeText(autoCanvas,12,20,10,220,24,rgba("paper"));this.textValue(this.autoTitle,text.autoCollect);
            this.autoStatus=this.makeText(autoCanvas,10,20,34,220,22,rgba("muted"));
            this.autoPlay=this.makeButton(["AutoCollect","play","autoCollectStart"],256,15,32,action,autoCanvas);
            this.autoPause=this.makeButton(["AutoCollect","pause","autoCollectPause"],256,15,32,action,autoCanvas);
            this.autoStop=this.makeButton(["ClearTracking","stop","clearTracking"],300,15,32,action,autoCanvas);

            this.setPanel({ rows: [] }, false);
        } catch (error) {
            try { this.dispose(); } catch (cleanup) { console.warn("[PhantomBladeMap] " + String(cleanup)); }
            throw error;
        }
    }
    makeRowButton(x, y, width, height, key, action, parent = this.canvas) {
        const UE = this.UE, button = this.create(UE.Button, parent);
        button.IsFocusable = false;
        const style = button.WidgetStyle;
        style.NormalPadding = new UE.Margin(0, 0, 0, 0); style.PressedPadding = new UE.Margin(0, 0, 0, 0);
        for (const [name, alpha] of [["Normal", 0], ["Hovered", .10], ["Pressed", .18], ["Disabled", 0]]) {
            const brush = style[name]; brush.DrawAs = UE.ESlateBrushDrawType.Image;
            brush.TintColor = this.color(...rgba("paper", alpha)); style[name] = brush;
        }
        button.SetStyle(style); button.SetBackgroundColor(new UE.LinearColor(1, 1, 1, 1));
        button.SetCursor(UE.EMouseCursor.Default);
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(button);
        slot.SetPosition(new UE.Vector2D(x, y)); slot.SetSize(new UE.Vector2D(width, height));
        const handler = () => action(key); button.OnClicked.Add(handler);
        this.buttons.push({ button, handler, key });
        return { widget: button, slot, interactive: true };
    }
    makeButton([key, icon, label], x, y, size, action, parent = this.canvas) {
        const UE = this.UE, node = this.makeRowButton(x, y, size, size, key, action, parent);
        const button = node.widget;
        button.SetToolTipText(this.cpp.FText.FromString(this.text[label]));
        const box = this.create(UE.SizeBox, button); box.SetWidthOverride(size); box.SetHeightOverride(size);
        const canvas = this.create(UE.CanvasPanel, box); canvas.SetVisibility(UE.ESlateVisibility.HitTestInvisible);
        for (const path of paths[icon]) for (let i = 1; i < path.length; i++) {
            const a = path[i - 1], b = path[i], inset = (size - 20) / 2, scale = 20 / 24;
            const node = this.makeLineWidget(canvas);
            this.drawLine(node, a[0] * scale + inset, a[1] * scale + inset,
                b[0] * scale + inset, b[1] * scale + inset, 1.2, ...rgba("paper"));
        }
        return node;
    }
    textValue(node, value) {
        if (node.value !== value) { node.widget.SetText(this.cpp.FText.FromString(value)); node.value = value; }
    }
    stylePin(node, point, selected, opacity=1) {
        super.stylePin(node,point,selected,opacity);
        const unknown=point.state==="unknown";
        const label=[point.label || this.text[point.category] || this.text.marker,unknown?this.text.unknown:""].filter(Boolean).join(" · ");
        const tip=[label,this.text[point.availability]].filter(Boolean).join(" · ");
        if(node.button?.widget)node.button.widget.SetToolTipText(this.cpp.FText.FromString(tip));
        if(selected || point.category==="treasure") {
            this.textValue(node.text,label);
            node.text.widget.SetToolTipText(this.cpp.FText.FromString(tip));
        }
    }
    visible(node, value) {
        if (!node?.widget) return;
        const vis = value ? (node.interactive ? this.UE.ESlateVisibility.Visible : this.UE.ESlateVisibility.HitTestInvisible) : this.UE.ESlateVisibility.Collapsed;
        if (node.vis !== vis) { node.widget.SetVisibility(vis); node.vis = vis; }
    }
    showSettings(open, config, action) {
        this.settingsOpen=open;
        if(open && !this.filterPanel)this.filterPanel=new FilterPanel(this,action);
        this.filterPanel?.show(open,config);
        this.showControls(true);
    }
    filterKey(key) {return !!(this.settingsOpen && this.filterPanel?.key(key));}
    setToggles(config) {
        this.config = config;
        this.mapModeButton.widget.SetToolTipText(this.cpp.FText.FromString(this.text[config.view==="survey"?"native":"survey"]));
        for (const [mode, tab] of Object.entries(this.modeTabs)) {
            const active = config.view === mode && config.visibility;
            this.visible(tab.underline, active);
            tab.label.widget.SetColorAndOpacity(this.color(...rgba(active ? "paper" : "muted")));
        }
        this.visible(this.toggleBars.ExploredOnly, config.exploredOnly);
        this.visible(this.toggleBars.RemainingOnly, config.remainingOnly);
        this.visible(this.toggleBars.MapMode,config.view==="survey");
        this.visible(this.toggleBars.Minimap,config.minimap);
    }
    setListMode(mode) {
        for(const [key,bar] of Object.entries(this.listBars))this.visible(bar,key===mode);
    }
    place(node, x, y) { node.slot.SetPosition(new this.UE.Vector2D(x, y)); }
    resize() {
        const size=this.page.getMapPageLocalSize(),scale=Math.min(layout.panelScale,(size.Y-148)/(this.panelHeight || layout.compactHeight),(size.X-56)/layout.width);
        if(size.X!==this.viewportWidth || scale!==this.panelScale) {
            this.viewportWidth=size.X;this.panelScale=scale;
            this.chrome.SetRenderScale(new this.UE.Vector2D(scale,scale));
            this.chromeSlot.SetPosition(new this.UE.Vector2D(size.X-layout.width*scale-28,100));
        }
        if(this.settingsOpen)this.filterPanel?.resize();
    }
    setPanel(model, visible, heading = this.text.remaining, status = "") {
        this.textValue(this.heading, model?.regionLabel || this.text.journal);
        this.textValue(this.summary, heading);
        for (const node of [this.summary,this.bodyRule])this.visible(node,visible);
        const rows = model?.rows || [];
        for (let i = 0; i < this.rows.length; i++) {
            const row = this.rows[i], item = rows[i], show = visible && !!item;
            for (const name of ["icon", "title", "detail", "rule", "btn"]) this.visible(row[name], show);
            this.visible(row.bg, show && item.selected);
            this.visible(row.bar, show && item.selected);
            this.visible(row.track,show);this.visible(row.trackingBar,show && item.tracked);
            if (show) {
                row.track.widget.SetIsEnabled(item.canTrack!==false);
                this.stylePin(row.icon,item,item.selected);this.visible(row.icon.text,false);
                this.textValue(row.title, item.title);
                this.textValue(row.detail, item.detail);
                row.btn.widget.SetToolTipText(this.cpp.FText.FromString(item.tooltip || item.title));
                row.track.widget.SetToolTipText(this.cpp.FText.FromString(item.tracked?this.text.clearTracking:this.text.trackingTarget));
            }
        }
        this.textValue(this.emptyText, model?.emptyText || this.text.noRemaining);
        this.visible(this.emptyText, visible && !rows.length);
        const hasPaging = !!(model?.hasPreviousPage || model?.hasNextPage);
        const rowSlots = hasPaging ? 6 : Math.max(1, rows.length);
        const footer = layout.rowsY + rowSlots * layout.rowHeight + 8;
        this.place(this.previousPage,12,footer);this.place(this.nextPage,304,footer);
        this.place(this.pageInfo,144,footer+7);
        this.textValue(this.pageInfo, model?.pageText || "");
        this.previousPage.widget.SetIsEnabled(!!model?.hasPreviousPage);
        this.nextPage.widget.SetIsEnabled(!!model?.hasNextPage);
        for (const node of [this.previousPage, this.nextPage, this.pageInfo]) this.visible(node,visible && hasPaging);
        const statusY = footer + (hasPaging ? 46 : 8);
        this.place(this.status, 20, statusY);
        this.textValue(this.status, status);
        this.status.widget.SetToolTipText(this.cpp.FText.FromString(status));
        this.visible(this.status, visible && !!status);
        const navigation=model?.navigation, showNavigation=visible&&!!navigation, navigationY=statusY+(status?26:0);
        this.visible(this.navigationRule,showNavigation);this.visible(this.navigationTitle,showNavigation);this.visible(this.navigationDetail,showNavigation);
        for(let i=0;i<this.navigationButtons.length;i++)this.visible(this.navigationButtons[i],showNavigation && (i<2 || !navigation.tracked));
        this.visible(this.cancelTracking,showNavigation && navigation.tracked);
        if(showNavigation) {
            this.place(this.navigationRule,20,navigationY-6);
            this.place(this.navigationTitle,20,navigationY);this.textValue(this.navigationTitle,navigation.title);
            const detail=navigation.detail || "";
            const lines=detail.split("\n").reduce((sum,line)=>sum+Math.max(1,Math.ceil(Array.from(line).length/26)),0);
            this.navigationDetailHeight=Math.min(84,Math.max(22,lines*20));
            this.place(this.navigationDetail,20,navigationY+28);this.textValue(this.navigationDetail,detail);
            this.navigationDetail.slot.SetSize(new this.UE.Vector2D(312,this.navigationDetailHeight));
            this.navigationButtons.forEach((button,i)=>this.place(button,i<2?20+i*40:304,navigationY+36+this.navigationDetailHeight));
            this.place(this.cancelTracking,220,navigationY+36+this.navigationDetailHeight);
            this.navigationButtons[0].widget.SetIsEnabled(navigation.playerLocal);
            this.navigationButtons[1].widget.SetIsEnabled(navigation.targetLocated);
            this.navigationButtons[2].widget.SetIsEnabled(navigation.canTrack);
            this.navigationDetail.widget.SetToolTipText(this.cpp.FText.FromString(navigation.detail));
        }
        const bodyHeight=showNavigation?navigationY+80+this.navigationDetailHeight:statusY+(status?30:8);
        const automatic=model?.autoCollect || {active:!!this.config?.autoCollect};
        const showAuto=visible && (model?.listMode!=="quest" || automatic.active);
        this.visible(this.autoStrip,showAuto);
        this.place(this.autoStrip,0,bodyHeight+8);
        this.textValue(this.autoStatus,automatic.status || (automatic.active?this.text.autoCollectRunning:this.text.autoCollectStart));
        this.autoTitle.widget.SetColorAndOpacity(this.color(...rgba(automatic.active?"accent":"paper")));
        this.visible(this.autoPlay,!automatic.active);this.visible(this.autoPause,automatic.active);
        this.visible(this.autoStop,automatic.active || automatic.tracked);
        const height = visible ? bodyHeight+(showAuto?78:0) : layout.compactHeight;
        if (this.panelHeight !== height) {
            this.chromeSlot.SetSize(new this.UE.Vector2D(layout.width, height)); this.panelHeight = height;
            this.resize();
        }
    }
    setPins(points, settings, selected, region) {
        if (settings.view !== "native") { this.resetPins();return; }
        const allowed=new Set(Array.from(this.page.getallowedMapID(),id=>String(id).toLowerCase()));
        const pending = points.filter(p => allowed.has(String(p.mapID).toLowerCase()) && (!settings.remainingOnly || p.state !== "collected"))
            .sort((a,b)=>a.id<b.id?-1:a.id>b.id?1:0);
        const size=this.page.CurrentMapSize();
        const layoutKey=[region,size.X,size.Y].join(":");
        if(layoutKey!==this.pinLayoutKey){this.pinLayoutKey=layoutKey;this.pinPositions.clear();}
        const active=new Set(pending.map(p=>p.id));
        for(const [id,node] of this.pinByID)if(!active.has(id)) {
            this.visible(node,false);this.pinByID.delete(id);this.pinPositions.delete(id);this.freePins.push(node);
        }
        for(const id of this.pinWork.keys())if(!active.has(id))this.pinWork.delete(id);
        this.drawState={...settings,selected};
        // Loading can append or reorder sources every refresh. Queue each ID
        // once, keep its newest data, and leave already drawn IDs in place.
        for(const point of pending) {
            const id=point.id,key=JSON.stringify([layoutKey,settings.visibility,id===selected,point.mapID,point.x,point.y,point.z,point.u,point.v,point.category,point.icon,point.state,point.availability,point.label]);
            if((this.pinWork.get(id)?.key || this.pinByID.get(id)?.renderKey)===key)continue;
            this.pinWork.set(id,{point,key});
            if(!this.queuedPins.has(id)){this.queuedPins.add(id);this.pending.push(id);}
        }
    }
    stepPins() {
        if (this.cursor >= this.pending.length) return;
        const UE = this.UE, page = this.page, size = page.CurrentMapSize(), settings = this.drawState, started = Date.now();
        for (let count = 0; this.cursor < this.pending.length && count < 4 && Date.now() - started < 2; count++, this.cursor++) {
            const id=this.pending[this.cursor],work=this.pinWork.get(id);
            this.queuedPins.delete(id);this.pinWork.delete(id);
            if(!work)continue;
            const {point,key}=work;
            let node=this.pinByID.get(id);
            if(!node) {
                node=this.freePins.pop();
                if(!node){
                    node=this.makePinWidget();
                    node.interactive=true;
                    node.button=this.makeRowButton(0,0,32,32,"",()=>{
                        if(!node.pointID)return;
                        if(node.isFacility)this.action("Facility:"+node.pointID);
                        else if(this.drawState?.selected===node.pointID)this.action("TrackTarget");
                        else this.action("SelectID:"+node.pointID);
                    },node.widget);
                    this.pins.push(node);
                }
                this.pinByID.set(id,node);
            }
            const rawPos = point.u !== undefined
                ? new UE.Vector2D(point.u * size.X, point.v * size.Y)
                : page.ConvertWorldLocationToScreen(new UE.Vector(point.x, point.y, point.z), point.mapID);

            // An appended endpoint must not move existing coincident icons.
            let position=this.pinPositions.get(id);
            if(!position || position.x!==rawPos.X || position.y!==rawPos.Y) {
                const slots=new Set();
                for(const [other,p] of this.pinPositions)if(other!==id && Math.hypot(p.x-rawPos.X,p.y-rawPos.Y)<28)slots.add(p.overlap);
                let overlap=0;while(slots.has(overlap))overlap++;
                const angle=overlap*2.094395-Math.PI/2,radius=overlap?28+Math.floor((overlap-1)/3)*20:0;
                position={x:rawPos.X,y:rawPos.Y,overlap,px:rawPos.X+Math.cos(angle)*radius,py:rawPos.Y+Math.sin(angle)*radius};
                this.pinPositions.set(id,position);
            }

            const isSelected = point.id === settings.selected;
            const isMarker = point.category === "marker";
            const isFacility = !!point.facility || point.pointKind === "facility";
            const isCollected = point.state === "collected";

            node.slot.SetPosition(new UE.Vector2D(position.px, position.py));
            node.zOrder = isSelected ? 32 : isMarker ? 24 : isCollected ? 8 : isFacility ? 11 : 18;
            node.slot.SetZOrder(node.zOrder);

            this.stylePin(node, point, isSelected);
            node.pointID = point.id;node.isFacility = isFacility;node.renderKey=key;
            // The painted map combines several worlds. Inferred floor IDs
            // belong to each world's planar atlas, not this shared canvas.
            this.visible(node, settings.visibility && settings.view === "native");
        }
        if(this.cursor===this.pending.length){this.pending=[];this.cursor=0;}
    }
    pinAt(position) {
        let nearest, distance = Infinity;
        for (const node of this.pinByID.values()) {
            if (node.vis !== this.UE.ESlateVisibility.Visible) continue;
            const p = node.slot.GetPosition(), dx = position.X - p.X, dy = position.Y - p.Y;
            if (Math.abs(dx) > 16 || Math.abs(dy) > 16) continue;
            const d = dx * dx + dy * dy;
            if (!nearest || node.zOrder > nearest.zOrder || node.zOrder === nearest.zOrder && d < distance) {
                nearest = node; distance = d;
            }
        }
        return nearest;
    }
    resetPins() {
        this.pending=[];this.cursor=0;this.pinLayoutKey=undefined;
        this.pinByID.clear();this.pinWork.clear();this.queuedPins.clear();this.pinPositions.clear();
        for(const node of this.pins)this.visible(node,false);
        this.freePins=this.pins.slice();
    }
    hide(includeControls = false) {
        this.resetPins();this.setPanel({ rows: [] }, false);
        this.settingsOpen=false;this.filterPanel?.show(false,this.config);
        this.showControls(!includeControls);
    }
    showControls(value) {
        value=value && !this.settingsOpen;
        if (this.controlsVisible !== value) {
            this.controlsVisible = value;
            this.chrome.SetVisibility(value ? this.UE.ESlateVisibility.Visible : this.UE.ESlateVisibility.Collapsed);
        }
    }
    chromeWidgets() { return [this.chrome,this.filterPanel?.root].filter(Boolean); }
    resources() { return { widgets: this.widgets.length, roots: this.roots.length, delegates: this.listeners.length + this.buttons.length, pendingPins: this.pinWork.size }; }
    dispose(detach = true) {
        const report = { before: this.resources(), detachedRoots: 0, removedDelegates: 0, errors: [] };
        const remove = operation => { try { operation(); return true; } catch (error) { report.errors.push(String(error)); return false; } };
        for (const { widget, name, handler } of this.listeners) if (remove(() => widget[name].Remove(handler))) report.removedDelegates++;
        for (const { button, handler } of this.buttons) if (remove(() => button.OnClicked.Remove(handler))) report.removedDelegates++;
        // On Finish, detach only our roots. During NativeDestruct the host owns
        // Slate teardown: removing children here can re-enter that teardown.
        if (detach) for (const widget of this.roots) if (remove(() => widget.RemoveFromParent())) report.detachedRoots++;
        this.filterPanel?.release();this.filterPanel=undefined;this.settingsOpen=false;
        this.buttons = []; this.listeners = []; this.widgets = []; this.pins = []; this.pending = []; this.rows = [];
        this.pinByID.clear();this.pinWork.clear();this.queuedPins.clear();this.pinPositions.clear();this.freePins=[];this.pinLayoutKey=undefined;
        this.roots = []; this.owned.clear(); this.cursor = 0;
        this.categoryButtons = []; this.toggleBars = {}; this.modeTabs = {}; this.config = this.drawState = undefined;
        this.listBars={};this.navigationButtons=[];this.navigationTitle=this.navigationDetail=this.navigationRule=undefined;
        this.cancelTracking=undefined;
        this.autoStrip=this.autoTitle=this.autoStatus=this.autoPlay=this.autoPause=this.autoStop=undefined;
        this.heading = this.pageInfo = this.status = this.emptyText = undefined;
        this.summary = this.bodyRule = undefined;
        this.previousPage = this.nextPage = this.previewButton = undefined;
        this.chrome = this.chromeSlot = this.canvas = this.page = this.action = undefined;
        report.remainingReferences = this.resources();
        if (report.errors.length) require("./Diagnostics").record("widget-cleanup-error", report);
        return report;
    }
}
module.exports = { View };
