"use strict";
const itemCategories=require("./ItemCategories");
const KeyItems=require("./KeyItems"),Treasures=require("./Treasures"),Documents=require("./Documents");
const inventoryFields={weapon:"MainWeaponInfos",shadowWeapon:"ShaqiWeaponInfos",accessory:"Accessories",
    medicine:"MedicineInfos",flower:"Inventories",book:"Inventories",soulMaterial:"Inventories"};

function bind(point) {
    if(point.inventory || point.landmark || !point.reward || (point.rewards?.length || 1)>1)return;
    // Unique reward types retain acquisition records after use or assembly.
    // Ordinary materials and mixed containers still need their source's state.
    const field=inventoryFields[point.category] ||
        (point.collectionKey==="accessoryFragment" || point.category==="key" && itemCategories[point.reward]==="key" ? "Inventories" : undefined);
    if(field)point.inventory=field;
}
function supported(point) {
    return !point.landmark && !!point.reward &&
        (!!point.inventory || point.category==="key" || point.category==="book" || Treasures.contains(point.reward));
}
function collected(point,context) {
    const {bag,cycle,events}=context;
    if(!bag || !supported(point))return false;
    if(point.category==="key" || Treasures.contains(point.reward)) {
        context.tags ||= events?require("./CollectionAvailability").snapshot(events):new Set();
        // Region-specific quest items must never fall through to a shared ID.
        if(point.category==="key")return KeyItems.collected(point,bag,cycle,context.tags);
        return Treasures.collected(point.reward,bag,cycle,context.tags);
    }
    if(point.category==="book")return Documents.collected(point,bag,cycle,events);
    const item=bag[point.inventory]?.GetRef(point.reward);
    // Zero quantity is valid evidence; a previous cycle's record is not.
    return cycle!==undefined && !!item && item.AcquiredCycle===cycle;
}
module.exports={bind,supported,collected};
