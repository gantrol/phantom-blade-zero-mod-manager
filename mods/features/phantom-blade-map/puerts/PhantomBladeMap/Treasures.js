"use strict";
// DT_InventoryData: exact treasure IDs and AddTagWhenHas acquisition tags.
// These identify the objects found in the world, independently of Xiao Zhang's
// interaction points and exchange rewards. T_06003 belongs to MeiGu Lake.
const acquiredTags = {
    T_02001:"fjzl.小张收集物.1", T_02002:"fjzl.小张收集物.2", T_02003:"fjzl.小张收集物.3",
    T_05001:"ljc.小张收集物.1", T_05002:"ljc.小张收集物.2", T_05003:"ljc.小张收集物.3",
    T_06001:"pz.小张收集物.1", T_06002:"pz.小张收集物.2", T_06004:"pz.小张收集物.3", T_06005:"pz.小张收集物.4",
    T_08001:"mgxl.小张收集物.1", T_06003:"mgxl.小张收集物.2", T_08003:"mgxl.小张收集物.3",
    T_12001:"xsd.小张收集物.1", T_12002:"xsd.小张收集物.2", T_12003:"xsd.小张收集物.3"
};
const contains = id => Object.prototype.hasOwnProperty.call(acquiredTags,id);
function classify(point) {
    if(contains(point.reward)) { point.category="treasure";point.collectionKey="treasure"; }
    return point;
}
function collected(id,bag,cycle,tags) {
    const item=bag.Inventories.GetRef(id);
    // Handing a treasure over leaves an acquisition record with amount zero.
    // An earlier cycle's record must not complete the current cycle's treasure.
    if(item)return item.AcquiredCycle===cycle;
    return tags.has(acquiredTags[id]);
}
module.exports={contains,classify,collected};
