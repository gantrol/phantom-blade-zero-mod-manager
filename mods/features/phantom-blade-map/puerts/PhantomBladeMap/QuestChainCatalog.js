"use strict";
// Array order is presentation only. Explicit requirements and satisfaction
// evidence never replace native branch progress. Positions live in QuestObjectives.
const path=id=>"/Game/PBZstory/PBZQuest/Simple/"+id+"."+id+"_C";
module.exports={
    // DaHuang branch 1 -> BPF_HuangJinQuan dialogue -> FinishQuest(1).
    [path("DaHuang")]:{steps:[
        {id:"goldenDog",branch:"1",objective:"goldenDog"}
    ]},
    // The fisherman can also grant R_06033 and begin 1 without finishing S2.
    [path("FangKuai")]:{steps:[
        {id:"cubeEnemy",branch:"S1",objective:"cubeEnemy",doneTag:"pz.渔夫.获得方块1",parallel:true},
        {id:"cubeChest",branch:"S1",objective:"cubeChest",doneTag:"pz.渔夫.获得方块2",parallel:true},
        {id:"fisherman",branch:"S2",objective:"fisherman",requiresAll:["cubeEnemy","cubeChest"],satisfiedByBranch:"1"},
        {id:"phantom",branch:"1",objective:"phantom"}
    ],rewards:[{id:"R_06033",step:"fisherman",category:"book",inventory:"Inventories"},
        {id:"AC_ShachiWeapon",step:"phantom",category:"accessory",inventory:"Accessories"}]},
    // BPF_HongYiNvJianKe_Leave sets departed then begins branch 1.
    // BPF_JianZhenDeath grants K_03004 before FinishQuest(1). The memory
    // flower is a separate interaction in BPF_JianZhen_Memory_0.
    [path("MoJiaoZhiNv")]:{steps:[
        {id:"swordswoman",branch:"S1",objective:"swordswoman",doneTag:"zddy.红衣女剑客.荡绳离开",
            missedTag:"zddy.击杀.红衣女剑客",reason:"questSwordswomanMissed"},
        {id:"swordFormation",branch:"1",objective:"swordFormation",
            unsettledTag:"zddy.剑阵.完成任务"}
    ],rewards:[{id:"JianKeZhaoJiLing",step:"swordFormation",category:"shadowWeapon",inventory:"ShaqiWeaponInfos"},
        {id:"K_03004",step:"swordFormation",category:"key",inventory:"Inventories"},
        {id:"F_03008",step:"swordFormation",category:"flower",inventory:"Inventories",optional:true,
            availableTag:"zddy.剑阵.显示记忆之花",excludedTag:"zddy.击杀.红衣女剑客"}]}
};
