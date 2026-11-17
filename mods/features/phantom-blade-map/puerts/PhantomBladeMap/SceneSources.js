"use strict";
const {aliases}=require("./OfflineAtlas");
const available=require("./scene/index");
const catalogs=new Map();

// Archive positions supplement the native registry. A placement alone never
// establishes that an actor has spawned or that its reward remains uncollected.
class SceneSources {
    constructor(mapIDs) {
        const worlds=new Set(mapIDs.map(id=>aliases[String(id).toLowerCase()] || String(id).toLowerCase()));
        const key=[...worlds].join(":");
        let records=catalogs.get(key);
        if(!records) {
            records=new Map();
            for(const world of worlds) {
                if(!available.includes(world))continue;
                const data=require("./scene/"+world);
                for(const record of data.records)records.set(record.name,{...record,world,levelName:data.levelName});
            }
        }
        catalogs.delete(key);catalogs.set(key,records);
        while(catalogs.size>4)catalogs.delete(catalogs.keys().next().value);
        this.records=records;
    }
    find(id) {
        const name=/(?:^|-)([A-Za-z0-9_]+_UAID_[A-Fa-f0-9]+_\d+)$/.exec(id)?.[1];
        return name ? this.records.get(name) : undefined;
    }
    entries(){return this.records.values();}
    clear(){this.records=new Map();}
}
module.exports={SceneSources};
