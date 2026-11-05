"use strict";
const {aliases}=require("./OfflineAtlas");
const available=require("./scene/index");

// Archive positions supplement the native registry. A placement alone never
// establishes that an actor has spawned or that its reward remains uncollected.
class SceneSources {
    constructor(mapIDs) {
        this.records=new Map();
        const worlds=new Set(mapIDs.map(id=>aliases[String(id).toLowerCase()] || String(id).toLowerCase()));
        for(const world of worlds) {
            if(!available.includes(world))continue;
            const data=require("./scene/"+world);
            for(const record of data.records)this.records.set(record.name,{...record,world,levelName:data.levelName});
        }
    }
    find(id) {
        const name=/(?:^|-)([A-Za-z0-9_]+_UAID_[A-Fa-f0-9]+_\d+)$/.exec(id)?.[1];
        return name ? this.records.get(name) : undefined;
    }
    entries(){return this.records.values();}
    clear(){this.records.clear();}
}
module.exports={SceneSources};
