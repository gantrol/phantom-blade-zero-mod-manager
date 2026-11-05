"use strict";
// Explanations for known prerequisite failures. Live reward-branch availability
// is authoritative in CollectionFlow; this table supplies more specific reasons.
// Native identifiers stay here; all player-facing reasons live in Locales.
const tag=(id,reason="collectionQuestRequired")=>({tag:id,reason});
const absent=id=>({absent:id,reason:"collectionStageUnavailable"});
const branch=(quest,id,state,invert=false)=>({quest,branch:id,state,invert,reason:"collectionQuestRequired"});
const items=ids=>({items:ids,reason:"collectionItemsRequired"});
const flows={
    BPF_FuLaoDa_FenMu:[tag("xg.村庄.石斧护符情报","stoneBracerClueRequired"),absent("xg.村庄.石斧护符已获取")],
    BPF_BaiXueEr_MemoryFlower:[branch("BaiXue","2",2)],
    BPF_BaiXueEr_MemoryFlower2:[branch("BaiXue","2",2),tag("pz.老槐树.解锁记忆之花"),absent("pz.老槐树.可以挖掘")],
    BPF_CiMuXin_MemoryFlower_Monster:[tag("pz.击杀.慈母心怪物","collectionCombatRequired"),branch("CiMuXin","1",0,true)],
    BPF_DaJiaGuiXiu_XiongZhang_MemoryFlower:[tag("pz.击杀.再会兄长怪物","collectionCombatRequired"),branch("ZaiHuiXiongZhang","1",0,true)],
    BPF_XiangJianHeJi_MemoryFlower2:[tag("pz.击杀.相煎何急怪物","collectionCombatRequired"),branch("XiangJianHeJi","1",0,true)],
    BPF_DaoJiaoZhangMen_MemoryFlower:[tag("ljc.击杀.道教掌门","collectionCombatRequired"),tag("bsmw.掌门本体.已解脱2","flowerMasterRequired")],
    BPF_FoJiaoZhangMen_MemoryFlower:[tag("ljc.击杀.佛教掌门","collectionCombatRequired"),tag("bsmw.掌门本体.已解脱1","flowerMasterRequired")],
    BPF_ShenJiZhangMen_MemoryFlower:[tag("bsmw.掌门本体.已解脱3","flowerMasterRequired")],
    BPF_JianZhen_Memory_0:[tag("zddy.剑阵.显示记忆之花","flowerFormationRequired"),absent("zddy.击杀.红衣女剑客")],
    BPF_JianZhen_Memory_1:[tag("zddy.剑阵.显示记忆之花","flowerFormationRequired")],
    BPF_LJC_MemoryFlower_2:[tag("ljc.击杀.河道女性人偶","collectionCombatRequired")],
    BPF_LaoGenTou_Memory:[tag("car.老根头.死亡")],
    BPF_LuJiaoLingDang_MemoryFlower:[tag("xkhx.虚空幻象.走吧")],
    BPF_NpcYuerMemory:[tag("qsd.Boss战.玉儿对话")],
    BPF_XMSZ_MemoryFlower_YuCong_3:[tag("qsd.Boss战.获得虎炮")],
    BPF_XMSZ_MemoryFlower_ZhuangZaiHuangXinZhiQu:[tag("qsd.Boss战.结束","collectionCombatRequired")],
    BPF_Mirror_Memory:[absent("jyzh.冷荼至高者镜子和遗迹.开始")],
    BPF_XMSZ_LaoQiGai:[tag("XMSZ.老乞丐.第一次对话之后"),items(["AC_MagicWeapon_1","AC_MagicWeapon_2","AC_MagicWeapon_3"])],
    BPF_ShenZhaiYaoGuan:[tag("pz.庞镇.到达"),items(["AC_DrinkCost_1","AC_DrinkCost_2","AC_DrinkCost_3","AC_DrinkCost_4"])],
    BPF_ShiWuFangFuHuFu:[items(["AC_ResistPoison_1","AC_ResistPoison_2"])]
};
for(let i=1;i<=6;i++) {
    flows["BPF_BuMieXiaDan_Memory_"+i]=[
        tag("pz.不灭侠胆.帮助正道解脱"+i,"flowerRescueRequired"),
        tag("pz.狼统领.完成任务","flowerQuestRequired")
    ];
    if(i>1)flows["BPF_BuMieXiaDan_Memory_"+i].push(absent("pz.不灭侠胆.计入任务进度"+i));
}
// Some native map rows omit FlowAssetName. Only use unambiguous reward IDs as
// fallbacks; a declared flow always takes precedence over the fallback.
const rewards={
    AC_ResistNonBreak:"BPF_FuLaoDa_FenMu",
    F_06004:"BPF_BaiXueEr_MemoryFlower",F_06022:"BPF_BaiXueEr_MemoryFlower2",
    F_06005:"BPF_CiMuXin_MemoryFlower_Monster",F_13001:"BPF_DaJiaGuiXiu_XiongZhang_MemoryFlower",
    F_11001:"BPF_XiangJianHeJi_MemoryFlower2",F_05051:"BPF_DaoJiaoZhangMen_MemoryFlower",
    F_05052:"BPF_FoJiaoZhangMen_MemoryFlower",F_05053:"BPF_ShenJiZhangMen_MemoryFlower",
    F_03008:"BPF_JianZhen_Memory_0",F_03001:"BPF_JianZhen_Memory_1",
    F_05031:"BPF_LJC_MemoryFlower_2",F_13020:"BPF_LaoGenTou_Memory",
    F_02001:"BPF_LuJiaoLingDang_MemoryFlower",F_12001:"BPF_Mirror_Memory",
    FS_14001:"BPF_NpcYuerMemory",F_08043:"BPF_XMSZ_MemoryFlower_YuCong_3",
    FS_12004:"BPF_XMSZ_MemoryFlower_ZhuangZaiHuangXinZhiQu",
    AC_MagicWeapon:"BPF_XMSZ_LaoQiGai",AC_DrinkCost:"BPF_ShenZhaiYaoGuan",
    AC_ResistPoison:"BPF_ShiWuFangFuHuFu"
};
for(let i=1;i<=6;i++)rewards["F_0604"+i]="BPF_BuMieXiaDan_Memory_"+i;
const checks=Object.values(flows).flat();
module.exports={flows,rewards,quests:new Set(checks.map(c=>c.quest).filter(Boolean)),items:new Set(checks.flatMap(c=>c.items || []))};
