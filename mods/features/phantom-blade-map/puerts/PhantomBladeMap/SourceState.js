"use strict";
const nativeItemCategories=require("./ItemCategories");
function source(id, sourceClass) {
    const value=sourceClass?sourceClass+"_":id;
    if (/(?:^|-)BP_Chest_C_/.test(value)) return {category:"chest",field:"SwitchState",live:"CurSwitchState"};
    if (/(?:^|-)BP_Rubbish(?:_A|_b|_C)?_C_/.test(value)) return {category:"supply",field:"Destroy",live:"bDestroyed"};
    if (/(?:^|-)BP_HangingGift_C_/.test(value)) return {category:"supply",field:"Broken",live:"bBroken"};
    if (/(?:^|-)PBZPickupActor_/.test(value)) return {category:"supply",pickup:true};
    return undefined;
}
function savedState(manager,id,UE,puerts,sourceClass,staticSource=false) {
    const field=source(id,sourceClass)?.field;
    if (!field || !manager) return "unknown";
    if(!manager.HasSaveDataByActorID(id)) {
        // These authored sources create their persistent record on completion.
        // The engine query also handles legacy save keys; a raw map lookup does not.
        const persistent=/^BP_(?:Chest|Rubbish(?:_A|_b|_C)?)_C$/.test(sourceClass || "");
        return staticSource && persistent && /_UAID_[A-Fa-f0-9]+_\d+$/.test(id)?"remaining":"unknown";
    }
    const data=puerts.$ref(new UE.JsonObjectWrapper());
    manager.GetSaveData(id,data);
    const found=puerts.$ref(false),done=puerts.$ref(false);
    UE.JSONUtility.TryGetBoolField(puerts.$unref(data),field,found,done);
    return puerts.$unref(found) ? (puerts.$unref(done)?"collected":"remaining") : "unknown";
}
function propName(UE,id) {
    if (!id || id==="None") return "";
    if(id==="currency:dust")return require("./Locales").dictionary(UE).dust;
    return itemName(UE,UE.PBZBackPackSubsystem.GetPropData(id));
}
function itemName(UE,prop) {
    const key=[prop?.ItemName?.Key,prop?.Subhead?.Key].map(v=>String(v || "")).find(v=>v && v!=="None");
    return key && key!=="None" ? UE.KismetTextLibrary.Conv_TextToString(UE.PBZBlueprintFunctionLibrary.MakeTextByKey(key)) : "";
}
function describeItem(UE,point) {
    if (!point.reward || point.reward==="None") return;
    const catalogCategory=nativeItemCategories[point.reward];
    if(catalogCategory) {point.category=catalogCategory;point.collectionKey=catalogCategory;}
    const prop=UE.PBZBackPackSubsystem.GetPropData(point.reward);
    if (!prop) return;
    const kind=prop.MaterialType,detail=prop.ItemDetailType,itemType=prop.ItemType;
    point.label=itemName(UE,prop) || point.label;
    if(catalogCategory==="book" || itemType===UE.EItemTypes.Book || itemType===UE.EItemTypes.Letter) {
        point.category="book";point.collectionKey="book";
        const group=String(prop.IntegrationItemID || "");
        if(group && group!=="None")point.documentGroup=group;
        point.documentPage=Number(prop.SequentialIndex || 0);return;
    }
    if(catalogCategory)return;
    // Classify by the game's declared use, independently of container colour.
    if (!["supply","chest"].includes(point.category)) return;
    const types=UE.EItemMaterialType;
    if(detail===UE.EItemDetailType.NarrativeItem)point.category="key";
    else if(detail===UE.EItemDetailType.Treasures)point.category="treasure";
    else if(detail===UE.EItemDetailType.AccessoryFragment) {
        point.filterGroup="materials";point.collectionKey="accessoryFragment";return;
    }
    else if(detail===UE.EItemDetailType.AddAttribute)point.category="growthItem";
    else if(detail!==UE.EItemDetailType.Material)return;
    else if (kind===types.WeaponSnapSoulMaterial || kind===types.ShaChiWeaponSnapSoulMaterial) point.category="soulMaterial";
    else if ([types.WeaponUpgradeMaterial,types.ShaChiWeaponUpgradeMaterial,types.AccessoryUpgradeMaterial,types.MedicineUpgradeMaterial].includes(kind)) point.category="upgradeMaterial";
    else if (kind===types.CoreProgressionMaterial) point.category="growthItem";
    else return;
    point.collectionKey=point.category;
}
const groupByCategory={bell:"collection",weapon:"collection",shadowWeapon:"collection",accessory:"collection",medicine:"collection",
    challenge:"collection",treeHeart:"collection",amber:"collection",blackstone:"collection",flower:"collection",book:"documents",soulMaterial:"materials",
    upgradeMaterial:"materials",dust:"money",treasure:"keyItems",key:"keyItems",growthItem:"supplies",supply:"supplies",chest:"supplies"};
function classifyGroup(point) {point.filterGroup=point.filterGroup || groupByCategory[point.category];return point;}
const groupPriority={collection:0,documents:1,keyItems:2,materials:3,supplies:4,money:5};
const priorities={soulMaterial:0,upgradeMaterial:1,growthItem:0,supply:1,chest:2};
const priority=point=>(groupPriority[point.filterGroup || groupByCategory[point.category]] ?? 4)*10+(priorities[point.category] || 0);
module.exports={source,savedState,propName,describeItem,priority,classifyGroup};
