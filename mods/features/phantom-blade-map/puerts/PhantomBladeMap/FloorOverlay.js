"use strict";
// Original-map annotations share the game's point projection. Raster floor
// plans belong to the independent planar view, not the painted native map.
class FloorOverlay {
    constructor(view,action) {
        this.view = view; this.lines = [];
        this.portals=[];this.portalIndices=[];this.action=action;
    }
    update(atlas, layer, route) {
        for(const node of this.lines)if(node)this.view.visible(node,false);
        for(const node of this.portals)this.view.visible(node,false);
        this.portalIndices=[];
        if (!atlas || !atlas.layers[layer]) { this.clear(); return; }
        const { page, UE } = this.view;
        const project = p => page.ConvertWorldLocationToScreen(new UE.Vector(p.x, p.y, p.z), atlas.mapID);
        const points=route?.mapPoints||route?.points||[];
        let drawn=0;
        for(let i=1;i<points.length && drawn<160;i++) {
            if(points[i-1].floor!==layer || points[i].floor!==layer)continue;
            if(!this.lines[drawn])this.lines[drawn]=this.view.makeLineWidget(page.Gadget);
            const p=project(points[i-1]),q=project(points[i]);
            this.lines[drawn].slot.SetZOrder(12);
            this.view.drawLine(this.lines[drawn++],p.X,p.Y,q.X,q.Y,2,.84,.64,.3,.8);
        }
        const transitions=route?.transitions||[];
        for(let i=0;i<transitions.length && this.portalIndices.length<8;i++) {
            const p=transitions[i];if(p.from!==layer && p.to!==layer)continue;
            const slot=this.portalIndices.length;this.portalIndices.push(i);
            if(!this.portals[slot]) {
                this.portals[slot]=this.view.makeButton(["Portal:"+slot,"route","viewConnection"],0,0,32,key=>{
                    const current=this.portalIndices[Number(key.slice(7))];
                    if(current!==undefined)this.action("Portal:"+current);
                },page.Gadget);
                this.portals[slot].slot.SetZOrder(36);
            }
            const position=project(p);
            this.view.place(this.portals[slot],position.X-16,position.Y-16);
            this.view.visible(this.portals[slot],true);
        }
    }
    clear(detach=true) {
        this.portalIndices=[];
        if(detach) {
            const errors=[];
            for(const node of this.lines)if(node) {
                try { this.view.visible(node,false); } catch(error) { errors.push(String(error)); }
            }
            for(const node of this.portals) {
                try { this.view.visible(node,false); } catch(error) { errors.push(String(error)); }
            }
            if(errors.length)throw new Error("FLOOR_RELEASE: "+errors.join("; "));
        }
    }
    dispose(detach=true) {
        try { this.clear(detach); }
        finally { this.lines=[]; this.portals=[]; this.view=undefined; this.action=undefined; }
    }
}
module.exports={FloorOverlay};
