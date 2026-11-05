"use strict";
const {rgba}=require("./Appearance");
const {FloorContext}=require("./FloorContext");
const {TextureCache}=require("./TextureCache");
const {FogOverlay}=require("./FogOverlay");
class PlanarMap {
    constructor(view,parent,limit=64,onSelect) {
        this.view=view;this.limit=limit;this.onSelect=onSelect;this.images=[];this.pins=[];this.pinByID=new Map();
        this.floors=new FloorContext(limit);
        this.textures=new TextureCache(view,2*(limit+Math.min(32,Math.ceil(limit/2))),()=>this.applyTextures());
        const UE=view.UE,widget=view.create(UE.CanvasPanel,parent),slot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget);
        widget.SetClipping(UE.EWidgetClipping.ClipToBounds);slot.SetZOrder(20);
        this.root={widget,slot,interactive:!!onSelect};this.background=view.makeRect(widget,0,0,1,1,...rgba("ink",.98));
        this.fog=new FogOverlay(view,widget);
        this.player=view.makePinWidget(widget);
        this.scale=view.makeText(widget,10,12,0,140,20,rgba("muted"));
        this.scaleLine=view.makeLineWidget(widget);
    }
    bounds(x,y,width,height) {
        const UE=this.view.UE;this.width=width;this.height=height;
        this.root.slot.SetPosition(new UE.Vector2D(x,y));this.root.slot.SetSize(new UE.Vector2D(width,height));
        this.background.slot.SetSize(new UE.Vector2D(width,height));
    }
    project(p) {return {x:this.width/2+(p.x-this.camera.x)/this.camera.cm,y:this.height/2+(p.y-this.camera.y)/this.camera.cm};}
    applyTextures() {
        for(const node of this.images)if(node.path && !node.loaded) {
            const texture=this.textures.get(node.path);if(!texture)continue;
            node.image.widget.SetBrushFromTexture(texture,false);
            node.image.widget.SetRenderOpacity(1);node.loaded=true;
        }
    }
    world(x,y) {return {x:this.camera.x+(x-this.width/2)*this.camera.cm,y:this.camera.y+(y-this.height/2)*this.camera.cm,z:this.camera.z,mapID:this.mapID};}
    selectCenter() {
        let nearest, distance=32;
        for(const node of this.pinByID.values()) {
            const screen=this.project(node.point),d=Math.hypot(screen.x-this.width/2,screen.y-this.height/2);
            if(d<distance){nearest=node.point;distance=d;}
        }
        if(nearest)this.onSelect(nearest);
    }
    chromeWidgets() {return Array.from(this.pinByID.values(),node=>node.button.widget);}
    update(atlas,layer,camera,player,points,target,coverage,exploredOnly,showFloors=true,showFog=true) {
        if(this.atlasWorld!==atlas?.world){this.clear();this.atlasWorld=atlas?.world;}
        this.camera=camera;this.mapID=atlas?.mapID;const v=this.view,UE=v.UE;
        v.visible(this.root,true);
        const patches=showFloors?this.floors.patches(atlas,layer,camera,this.width,this.height):[];
        const active=new Set(patches.map(p=>p.key)),available=this.images.filter(p=>!active.has(p.key));
        for(const patch of patches) {
            let node=this.images.find(p=>p.key===patch.key);
            if(!node && available.length) {
                const sameTexture=available.findIndex(p=>p.path===patch.path);
                node=available.splice(sameTexture<0?available.length-1:sameTexture,1)[0];
            }
            if(!node) {
                const widget=v.create(UE.CanvasPanel,this.root.widget);widget.SetClipping(UE.EWidgetClipping.ClipToBounds);
                node={widget,slot:UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget),image:v.makeRect(widget,0,0,1,1,1,1,1,1)};this.images.push(node);
            }
            node.key=patch.key;const p=this.project(patch),s=patch.size/camera.cm;
            node.slot.SetPosition(new UE.Vector2D(p.x,p.y));node.slot.SetSize(new UE.Vector2D(s,s));node.slot.SetZOrder(patch.ghost?1:2);
            if(node.opacity!==patch.opacity){node.widget.SetRenderOpacity(patch.opacity);node.opacity=patch.opacity;}
            node.image.slot.SetPosition(new UE.Vector2D((patch.tile.x-patch.x)/camera.cm,(patch.tile.y-patch.y)/camera.cm));
            node.image.slot.SetSize(new UE.Vector2D(patch.span/camera.cm,patch.span/camera.cm));
            if(node.path!==patch.path) {
                node.path=patch.path;node.loaded=false;
                const texture=this.textures.get(patch.path);
                node.image.widget.SetBrushFromTexture(texture);
                node.image.widget.SetRenderOpacity(texture?1:0);node.loaded=!!texture;
            }
            v.visible(node,true);
        }
        // Image widgets and path-keyed textures have separate bounded pools.
        for(const node of this.images)if(!active.has(node.key))v.visible(node,false);
        this.textures.update(patches.map(p=>p.path));this.applyTextures();
        const aliases=require("./OfflineAtlas").aliases;
        const candidates=(points||[]).filter(p=>{
            const id=String(p.mapID).toLowerCase(),s=this.project(p);
            return (aliases[id]||id)===atlas?.world && (layer<0 || p.layer===layer || p.id===target?.id || p.facility) && s.x>=-16 && s.x<=this.width+16 && s.y>=-16 && s.y<=this.height+16 &&
                (!exploredOnly || p.explored || coverage.visible(p) || p.knownQuestPoint || p.category==="marker" || p.category==="bell" && p.state==="collected");
        }).map(point=>({point,screen:this.project(point),
            priority:point.id===target?.id?0:point.category==="bell"?1:point.facility?(layer<0 || point.layer===layer?3:4):(layer<0 || point.layer===layer?2:3),
            distance:Math.hypot(point.x-camera.x,point.y-camera.y)/camera.cm-(this.pinByID.has(point.id)?32:0)}));
        // Retained points get a small screen-space margin at the pool boundary;
        // equal/near-equal distances no longer swap widgets on every footstep.
        candidates.sort((a,b)=>a.priority-b.priority || a.distance-b.distance || (a.point.id<b.point.id?-1:a.point.id>b.point.id?1:0));
        const visible=candidates.slice(0,64),activePins=new Set(visible.map(p=>p.point.id));
        const freePins=[];
        for(const node of this.pins)if(!activePins.has(node.point?.id)) {
            if(node.point)this.pinByID.delete(node.point.id);
            node.point=undefined;v.visible(node,false);freePins.push(node);
        }
        for(const {point:p,screen} of visible) {
            let node=this.pinByID.get(p.id);
            if(!node) {
                node=freePins.pop();
                if(!node) {
                    node=v.makePinWidget(this.root.widget);this.pins.push(node);
                    if(this.onSelect){node.interactive=true;node.button=v.makeRowButton(0,0,32,32,"",()=>this.onSelect(node.point),node.widget);}
                }
                this.pinByID.set(p.id,node);
            }
            node.point=p;
            if(node.button)node.button.widget.SetToolTipText(v.cpp.FText.FromString(p.label || ""));
            const selected=p.id===target?.id,ghost=p.facility && p.layer>=0 && p.layer!==layer && !selected;
            node.slot.SetZOrder(selected?9:ghost?5:p.facility?7:8);v.place(node,screen.x,screen.y);
            v.stylePin(node,p,selected,ghost ? .18 : 1);
            v.visible(node.text,false);v.visible(node,true);
        }
        if(player) {
            const p=this.project(player);v.stylePin(this.player,{category:"player",state:player.layer===layer?"remaining":"collected"},false);v.place(this.player,p.x,p.y);
            this.player.widget.SetRenderTransformAngle(player.yaw || 0);this.player.slot.SetZOrder(10);
        }
        v.visible(this.player,!!player);
        const meters=Math.pow(10,Math.floor(Math.log10(Math.max(1,this.width*camera.cm/500))));
        v.place(this.scale,12,this.height-36);v.textValue(this.scale,meters+" m");
        this.scale.slot.SetZOrder(12);this.scaleLine.slot.SetZOrder(12);
        v.drawLine(this.scaleLine,12,this.height-14,12+meters*100/camera.cm,this.height-14,1,...rgba("paper"));
        this.fog.update(atlas,layer,camera,this.width,this.height,coverage,showFog);
    }
    clear(detach=true) {
        this.textures.clear();this.fog.clear(detach);
        if(detach) {
            for(const node of this.images) {
                if(node.path!==undefined)node.image.widget.SetBrushFromTexture(undefined);
                node.path=node.key=undefined;node.loaded=false;this.view.visible(node,false);
            }
            for(const node of this.pins)this.view.visible(node,false);
            this.view.visible(this.root,false);
        }
        this.pinByID.clear();for(const node of this.pins)node.point=undefined;
        this.atlasWorld=undefined;
        this.floors.clear();
    }
    dispose(detach=true){this.clear(detach);this.textures.dispose();this.fog.dispose(false);this.images=[];this.pins=[];this.view=undefined;}
}
module.exports={PlanarMap};
