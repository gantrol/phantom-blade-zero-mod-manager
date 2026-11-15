"use strict";
const {aliases}=require("./OfflineAtlas");
const worlds=require("./traversal/index");
const {StoryLinks}=require("./StoryLinks");

function endpoints(record) {
    const anchors=record.anchors.filter((point,index,all)=>!all.slice(0,index).some(other=>
        Math.hypot(...point.position.map((value,i)=>value-other.position[i]))<25));
    if(anchors.length<2)return [{...(anchors[0] || {name:"placement",position:record.position}),role:"site"}];
    const dz=anchors[0].position[2]-anchors[1].position[2];
    return anchors.map((point,index)=>({...point,role:Math.abs(dz)>=100?
        ((index===0?dz:-dz)>0?"upper":"lower"):(index===0?"a":"b")}));
}
const endpointStyles={upper:{label:"endpointUpper",icon:"Upper"},lower:{label:"endpointLower",icon:"Lower"},
    a:{label:"endpointA",icon:"EndpointA"},b:{label:"endpointB",icon:"EndpointB"}};

// Resolve each authored location in its source level. Map browsing can show a
// different world from the loaded pawn; bare XYZ coordinates are not global.
// This runs only with an open map; the HUD receives numeric points.
class TraversalMarkers {
    constructor(mapIDs) {
        this.allowed=new Set(mapIDs.map(id=>String(id).toLowerCase()));
        this.pending=[];this.points=[];this.cursor=0;this.ready=false;
        this.story=new StoryLinks(mapIDs);
        const requested=new Set([...this.allowed].map(id=>aliases[id] || id));
        for(const world of requested) {
            if(!worlds.includes(world))continue;
            const data=require("./traversal/"+world);
            const levelName=data.root.split("/").pop().replace(/\.umap$/i,"");
            for(const record of data.records) {
                const anchors=endpoints(record);
                for(const anchor of anchors)this.pending.push({world,levelName,record,anchor,pairedEndpoint:anchors.length===2});
            }
        }
    }
    step(page,UE,helper,text,explored) {
        if(this.ready)return;
        const map=helper.getWorldSubsystem(page,UE.MapSubsystem.StaticClass());
        if(!map)return;
        for(let budget=0;budget<8 && this.cursor<this.pending.length;budget++,this.cursor++) {
            const {world,levelName,record,anchor,pairedEndpoint}=this.pending[this.cursor], [x,y,z]=anchor.position;
            const mapID=String(map.GetMapIDByLevelAndLocation(levelName,new UE.Vector(x,y,z))),key=mapID.toLowerCase();
            if(!this.allowed.has(key) || (aliases[key] || key)!==world)continue;
            const endpoint=endpointStyles[anchor.role];
            const point={id:"facility:"+world+":"+record.id+":"+anchor.name,
                facility:true,facilityID:record.id,facilityKind:record.kind,endpoint:anchor.name,endpointRole:anchor.role,pairedEndpoint,
                category:record.kind,icon:record.kind+(endpoint?.icon || ""),
                label:[text[record.kind],endpoint?text[endpoint.label]:undefined].filter(Boolean).join(" · "),mapID,x,y,z,layer:-1,
                state:"remaining",pointKind:"facility",conditionKnown:false};
            point.explored=explored(point);this.points.push(point);
        }
        if(!this.story.ready) {
            const count=this.story.points.length;
            this.story.step(map,UE,text,explored);
            this.points.push(...this.story.points.slice(count));
        }
        if(this.cursor===this.pending.length && this.story.ready){this.pending=[];this.ready=true;}
    }
    dispose(){this.pending=[];this.points=[];this.story.dispose();this.ready=true;}
}
module.exports={TraversalMarkers};
