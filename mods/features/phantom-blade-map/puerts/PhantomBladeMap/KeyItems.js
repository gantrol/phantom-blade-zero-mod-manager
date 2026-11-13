"use strict";
// Native acquisition/hand-in events for quest rewards whose scene sources can
// differ from the authored map point. These are gameplay IDs, not UI text.
const stoneTags={
    xigu:["xg.鬼差.获得浑源石","xg.鬼差.给鬼差浑源石"],
    pangzhen:["jmkd.鬼差.获得浑源石","jmkd.鬼差.给鬼差浑源石"],
    fujizhilin:["xssl.鬼差.获得浑源石","xssl.鬼差.给鬼差浑源石"]
};
function collected(point,bag,cycle,tags) {
    if(point.category!=="key" || !point.reward)return false;
    const item=bag?.Inventories.GetRef(point.reward);
    if(point.reward==="I_01002") {
        // All three stones share an inventory ID; acquisition in another region
        // cannot complete this source. Its own quest events identify the pickup.
        if(item && item.AcquiredCycle!==cycle)return false;
        const world=/^PBZ_(.+)_WP-/i.exec(point.save || "")?.[1] || point.mapID || "";
        return !!tags && (stoneTags[world.toLowerCase()] || []).some(tag=>tags.has(tag));
    }
    // Native key rows declare inventory identity. Generic quest-material scene
    // drops may share an item ID and continue to use their own actor state.
    if(point.inventory!=="Inventories" && point.reward!=="K_04902")return false;
    // Consumption leaves the current cycle's acquisition record at amount zero.
    if(item)return item.AcquiredCycle===cycle;
    return point.reward==="K_04902" && !!tags &&
        ["rozs.人偶中枢.人偶舌头","hfz.哑巴人偶.装上舌头"].some(tag=>tags.has(tag));
}
module.exports={collected};
