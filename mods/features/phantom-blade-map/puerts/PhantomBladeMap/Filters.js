"use strict";
const groups = [
    {key:"collection",setting:"filterCollection",label:"filterCollection",icon:"flower"},
    {key:"documents",setting:"filterDocuments",label:"filterDocuments",icon:"book"},
    {key:"materials",setting:"filterMaterials",label:"filterMaterials",icon:"blackstone"},
    {key:"money",setting:"filterMoney",label:"filterMoney",icon:"dust"},
    {key:"keyItems",setting:"filterKeyItems",label:"filterKeyItems",icon:"key"},
    {key:"supplies",setting:"filterSupplies",label:"filterSupplies",icon:"supply"}
];
const traversalGroups = [
    {key:"vertical",setting:"filterTraversalVertical",label:"filterTraversalVertical",icon:"stairs",kinds:["stairs","ladder"]},
    {key:"slides",setting:"filterTraversalSlides",label:"filterTraversalSlides",icon:"zipline",kinds:["zipline","slide"]},
    {key:"ropes",setting:"filterTraversalRopes",label:"filterTraversalRopes",icon:"grapple",kinds:["grapple","rappel"]},
    {key:"climbing",setting:"filterTraversalClimbing",label:"filterTraversalClimbing",icon:"wallClimb",kinds:["wallRun","wallClimb"]}
];
const traversalSettings=Object.fromEntries(traversalGroups.flatMap(group=>group.kinds.map(kind=>[kind,group.setting])));
function traversalVisible(point,config) {
    const setting=traversalSettings[point.facilityKind || point.category];
    return config.showTraversal!==false && !!setting && config[setting]!==false;
}
module.exports = {groups,traversalGroups,traversalVisible};
