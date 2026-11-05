"use strict";
// Immutable numeric data only. No world objects, navigation calls or journals.
const aliases = { xuesesenlin: "fujizhilin", lujiacun: "zhengdaodaying", meiguhu: "pangzhen", meigu_yiji: "huanshuyiji2",
    huanshushi: "huanshuyiji2", duanhunshan: "duanhun", duanhunya: "duanhun" };
function ints(encoded) {
    const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    const output = new Int32Array(Math.floor(encoded.replace(/=+$/, "").length * 6 / 32));
    let bits = 0, value = 0, word = 0, byte = 0, index = 0;
    for (const c of encoded) {
        if (c === "=") break;
        value = (value << 6) | alphabet.indexOf(c); bits += 6;
        if (bits >= 8) { bits -= 8; word |= ((value >>> bits) & 255) << (byte++ * 8);
            if (byte === 4) { output[index++] = word; byte = 0; word = 0; } }
    }
    return output;
}
function open(mapID, geometry = true) {
    const key = String(mapID).toLowerCase(), world = aliases[key] || key;
    const index = require("./offline/index");
    if (!index.worlds.some(w => w.world === world)) return undefined;
    const raw = require("./offline/" + world);
    if (!geometry) return {world,items:raw.items,floor(){return -1;},dispose(){}};
    const lookup = ints(raw.lookup), columns = new Map();
    for (let i = 0; i < lookup.length; i += 5) {
        const cell = lookup[i] + ":" + lookup[i + 1];
        if (!columns.has(cell)) columns.set(cell, []);
        columns.get(cell).push([lookup[i + 2], lookup[i + 3], lookup[i + 4]]);
    }
    const nodes = ints(raw.nodes);
    const atlas = { world, mapID, items: raw.items,
        levels: index.levels, tilePixels: index.tilePixels,
        layers: raw.layers.map(layer => ({ ...layer, tiles: layer.tiles.map(ints) })),
        floor(point) {
            const cell = Math.floor(point.x / raw.lookupCell) + ":" + Math.floor(point.y / raw.lookupCell);
            let best = -1, distance = Infinity;
            for (const [layer, min, max] of columns.get(cell) || []) {
                const d = Math.max(min - point.z, point.z - max, 0);
                if (d < distance) { best = layer; distance = d; }
            }
            return distance <= (raw.floorSnapHeightCm ?? 250) ? best : -1;
        },
        floorsNear(point,radius=4800) {
            const found=new Set(),span=raw.lookupCell;
            for(let x=Math.floor((point.x-radius)/span);x<=Math.floor((point.x+radius)/span);x++)
                for(let y=Math.floor((point.y-radius)/span);y<=Math.floor((point.y+radius)/span);y++)
                    for(const row of columns.get(x+":"+y)||[])found.add(row[0]);
            const height=id=>{const l=atlas.layers[id];return l.kind==="under"?-l.depth:l.depth;};
            return Array.from(found).sort((a,b)=>height(b)-height(a));
        },
        heightAtLayer(x,y,layer,referenceZ) {
            const cell=Math.floor(x/raw.lookupCell)+":"+Math.floor(y/raw.lookupCell);
            const spans=(columns.get(cell)||[]).filter(p=>p[0]===layer);
            if(Number.isFinite(referenceZ))spans.sort((a,b)=>Math.max(a[1]-referenceZ,referenceZ-a[2],0)-Math.max(b[1]-referenceZ,referenceZ-b[2],0));
            const bounds=spans[0];
            if(bounds)return (bounds[1]+bounds[2])/2;
            let height,distance=Infinity;
            for(let i=0;i<nodes.length;i+=4)if(nodes[i+3]===layer) {
                const d=(nodes[i]-x)**2+(nodes[i+1]-y)**2;
                if(d<distance){distance=d;height=nodes[i+2];}
            }
            return height;
        },
        fogGrid(layer,span) {
            // Sample actual navigation surfaces, including their own height.
            // One pass per floor/resolution replaces a nearest-node search for
            // every fog cell; empty space has no invented projection.
            const grid=new Map();
            for(let i=0;i<nodes.length;i+=4)if(nodes[i+3]===layer) {
                const x=nodes[i],y=nodes[i+1],gx=Math.floor(x/span),gy=Math.floor(y/span),key=gx+":"+gy;
                const distance=(x-(gx+.5)*span)**2+(y-(gy+.5)*span)**2;
                if(!grid.has(key) || distance<grid.get(key).distance)
                    grid.set(key,{x,y,z:nodes[i+2],distance});
            }
            return grid;
        },
        dispose() { columns.clear(); }
    };
    return atlas;
}
module.exports = { open, aliases };
