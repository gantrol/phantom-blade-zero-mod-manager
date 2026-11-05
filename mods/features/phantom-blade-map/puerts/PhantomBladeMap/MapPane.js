"use strict";
const {PlanarMap}=require("./PlanarMap"),{rgba,layout}=require("./Appearance"),{aliases}=require("./OfflineAtlas"),Diagnostics=require("./Diagnostics");
const sameWorld=(atlas,point)=>{
    const id=String(point?.mapID || "").toLowerCase();
    return !!atlas && !!id && (aliases[id] || id)===atlas.world;
};
class MapPane {
    constructor(view,action) {
        this.view=view;this.camera={x:0,y:0,z:0,cm:50};this.panRevision=0;this.initialized=false;
        const root=view.page.WidgetTree.RootWidget;
        this.background=view.makeRect(root,0,0,1,1,...rgba("ink",.58));this.background.slot.SetZOrder(18);
        this.frameBorder=view.makeRect(root,0,0,1,1,...rgba("line",.85));this.frameBorder.slot.SetZOrder(19);
        this.map=new PlanarMap(view,root,96,p=>action((p.category==="bell"?"Bell:":p.facility?"Facility:":"SelectID:")+p.id));
        this.toolbar=view.create(view.UE.CanvasPanel,this.map.root.widget);
        const toolbarSlot=view.UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.toolbar);
        toolbarSlot.SetSize(new view.UE.Vector2D(560,84));toolbarSlot.SetZOrder(30);
        this.toolbar.SetRenderTransformPivot(new view.UE.Vector2D(0,0));
        this.headerBg=view.makeRect(this.toolbar,0,0,560,46,...rgba("panel",.94));this.headerBg.slot.SetZOrder(28);
        this.headerRule=view.makeRect(this.toolbar,0,46,560,1,...rgba("line",.8));this.headerRule.slot.SetZOrder(29);
        this.floorLabel=view.makeText(this.toolbar,14,16,11,180,24,rgba("paper"));this.floorLabel.slot.SetZOrder(30);
        const pageUp=view.makeButton(['PageUp','up','previousLayer'],200,7,32,action,this.toolbar);
        const pageDown=view.makeButton(['PageDown','down','nextLayer'],236,7,32,action,this.toolbar);
        const divider=view.makeRect(this.toolbar,276,14,1,18,...rgba("line",.55));divider.slot.SetZOrder(29);
        const zoomIn=view.makeButton(['PlanZoomIn','plus','zoomIn'],286,7,32,action,this.toolbar);
        const zoomOut=view.makeButton(['PlanZoomOut','minus','zoomOut'],322,7,32,action,this.toolbar);
        this.centerButton=view.makeButton(['PlanCenter','center','centerPlayer'],358,7,32,action,this.toolbar);
        this.fitButton=view.makeButton(['PlanFit','fit','fitMap'],394,7,32,action,this.toolbar);
        const exitBg=view.makeRect(this.toolbar,438,7,108,32,...rgba("selected",.92));exitBg.slot.SetZOrder(29);
        const exitBar=view.makeRect(this.toolbar,438,7,3,32,...rgba("accent",1));exitBar.slot.SetZOrder(30);
        const exitIcon=view.makeButton(['MapMode','close','native'],442,7,32,action,this.toolbar);
        const exitText=view.makeText(this.toolbar,12,474,12,68,22,rgba("paper"));exitText.slot.SetZOrder(30);
        view.textValue(exitText,view.text.native);
        const exitButton=view.makeRowButton(438,7,108,32,'MapMode',action,this.toolbar);
        exitButton.widget.SetToolTipText(view.cpp.FText.FromString(view.text.native+" · Esc"));
        this.controls=[pageUp,pageDown,zoomIn,zoomOut,this.centerButton,this.fitButton,exitIcon,exitButton];
        for(const node of this.controls)node.slot.SetZOrder(30);
        this.targetStatus=view.makeText(this.toolbar,12,16,52,528,24,rgba("gold"));this.targetStatus.slot.SetZOrder(30);
        const font=this.map.scale.widget.Font;font.Size=13;this.map.scale.widget.SetFont(font);
        this.crosshair=Array.from({length:4},()=>view.makeLineWidget(this.map.root.widget));
        for(const node of this.crosshair)node.slot.SetZOrder(26);
        this.syncInput(true);
    }
    focus(p) {if(p){this.pendingFocus={...p};this.panRevision++;this.syncInput(true);}}
    center(p) {this.camera.x=p.x;this.camera.y=p.y;this.camera.z=p.z;this.fitToBounds=false;}
    fit(bounds) {
        if(!bounds)return;
        this.fitToBounds=true;
        this.camera.x=(bounds[0]+bounds[2])/2;this.camera.y=(bounds[1]+bounds[3])/2;
        this.camera.cm=Math.max(8,(bounds[2]-bounds[0])/Math.max(1,this.map.width-120),(bounds[3]-bounds[1])/Math.max(1,this.map.height-180));
    }
    syncInput(reset=false) {
        const page=this.view.page,p=page.FocusPoint,scale=page.MapSizeScale;
        if(!p || !(scale>0))return false;
        const previous=this.nativeInput;this.nativeInput={x:p.X,y:p.Y,scale};
        if(reset || !previous)return false;
        const dx=p.X-previous.x,dy=p.Y-previous.y,zoom=previous.scale/scale;
        if(dx===0 && dy===0 && zoom===1)return false;
        this.fitToBounds=false;
        const size=page.CurrentMapSize();
        this.camera.x+=dx*size.X*this.camera.cm;this.camera.y+=dy*size.Y*this.camera.cm;
        this.camera.cm=Math.max(8,Math.min(2400,this.camera.cm*zoom));this.panRevision++;
        return true;
    }
    action(key,player,atlas,layer,target) {
        if(!sameWorld(atlas,player))player=undefined;
        if(!sameWorld(atlas,target))target=undefined;
        this.fitToBounds=false;
        const distance=this.camera.cm*Math.min(this.map.width||500,this.map.height||500)*.18;
        const move={PlanLeft:[-1,0],PlanRight:[1,0],PlanUp:[0,-1],PlanDown:[0,1]}[key];
        if(move){this.camera.x+=move[0]*distance;this.camera.y+=move[1]*distance;}
        else if(key==='PlanZoomIn')this.camera.cm=Math.max(8,this.camera.cm/1.4);
        else if(key==='PlanZoomOut')this.camera.cm=Math.min(2400,this.camera.cm*1.4);
        else if(key==='PlanCenter')this.focus(player);
        else if(key==='PlanFit') {
            this.fit(atlas?.layers[layer]?.bounds);
        } else return false;
        this.panRevision++;return true;
    }
    update(atlas,layer,player,points,target,coverage,config,label) {
        const v=this.view,size=v.page.getMapPageLocalSize(),scale=v.panelScale||layout.panelScale;
        const left=28,top=100,mapWidth=Math.max(240,size.X-layout.width*scale-left-56),mapHeight=Math.max(200,size.Y-148);
        this.background.slot.SetSize(new v.UE.Vector2D(size.X,size.Y));v.visible(this.background,true);
        this.frameBorder.slot.SetPosition(new v.UE.Vector2D(left-1,top-1));
        this.frameBorder.slot.SetSize(new v.UE.Vector2D(mapWidth+2,mapHeight+2));v.visible(this.frameBorder,true);
        const oldWidth=this.map.width,oldHeight=this.map.height;
        this.map.bounds(left,top,mapWidth,mapHeight);
        this.toolbar.SetRenderScale(new v.UE.Vector2D(scale,scale));
        if(!sameWorld(atlas,player))player=undefined;
        if(!sameWorld(atlas,target))target=undefined;
        const worldChanged=this.world!==atlas?.world,layerChanged=this.layer!==layer,b=atlas?.layers[layer]?.bounds;
        const focus=sameWorld(atlas,this.pendingFocus)?this.pendingFocus:undefined;
        const outside=b && (this.camera.x<b[0] || this.camera.x>b[2] || this.camera.y<b[1] || this.camera.y>b[3]);
        if(focus || worldChanged || !this.initialized || (layerChanged && outside)) {
            const anchor=focus || (player?.layer===layer?player:undefined) || (target?.layer===layer?target:undefined);
            if(anchor){this.center(anchor);if(worldChanged)this.camera.cm=50;}
            else this.fit(b);
            this.syncInput(true);
            Diagnostics.record("map-pane-frame",{world:atlas?.world,layer,focus:focus?"selection":anchor && anchor===player?"player":anchor && anchor===target?"target":"bounds",camera:{...this.camera}});
        }
        else if(this.fitToBounds && (oldWidth!==this.map.width || oldHeight!==this.map.height))this.fit(b);
        this.pendingFocus=undefined;this.world=atlas?.world;this.layer=layer;this.initialized=true;
        this.centerButton.widget.SetIsEnabled(!!player);this.fitButton.widget.SetIsEnabled(!!b);
        // A browsed floor has its own height; it must not inherit the player's
        // ground height when evaluating the explored footprint underground.
        const heightKey=[layer,this.camera.x,this.camera.y].join(":");
        if(heightKey!==this.heightKey) {
            this.heightKey=heightKey;
            this.camera.z=atlas?.heightAtLayer(this.camera.x,this.camera.y,layer,this.camera.z) ?? this.camera.z;
        }
        coverage.sample(v.page,v.UE,()=>this.map.update(atlas,layer,this.camera,player,points,target,coverage,config.exploredOnly,config.showFloors,config.showFog));
        const x=this.map.width/2,y=this.map.height/2;
        [[-10,0,-4,0],[4,0,10,0],[0,-10,0,-4],[0,4,0,10]].forEach(([a,b,c,d],i)=>v.drawLine(this.crosshair[i],x+a,y+b,x+c,y+d,1,...rgba("muted",.7)));
        v.textValue(this.floorLabel,atlas?[v.text["region_"+String(atlas.mapID).toLowerCase()],label].filter(Boolean).join(" · "):v.text.offlineMissing);
        const targetFloor=target && atlas?.layers[atlas.floor(target)];
        const status=[];
        if(targetFloor && atlas.floor(target)!==layer)status.push(v.text.targetFloor+" "+v.text[targetFloor.kind]+(targetFloor.depth?" "+targetFloor.depth:""));
        v.textValue(this.targetStatus,status.filter(Boolean).join(" · "));
    }
    clear(detach=true){this.nativeInput=undefined;this.map.clear(detach);if(detach){this.view.visible(this.background,false);this.view.visible(this.frameBorder,false);}}
    dispose(detach=true){this.clear(detach);this.map.dispose(false);this.view=undefined;}
}
module.exports={MapPane};
