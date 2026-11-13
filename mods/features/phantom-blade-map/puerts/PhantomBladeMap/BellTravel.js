"use strict";
const Diagnostics=require("./Diagnostics");

// The native charm flow owns its cost dialog and consumption. Bell mode uses
// the same modal layer, then hands the confirmed destination to BellTeleport.
class BellTravel {
    constructor(page,UE,cpp,helper,text) {Object.assign(this,{page,UE,cpp,helper,text});}
    activated(id) {
        const manager=this.helper.getWorldSubsystem(this.page,this.UE.BellManager.StaticClass());
        return !!manager?.GetAllActivateBellIDs().Contains(id);
    }
    allowed(id,recheck=false) {
        const page=this.page,data=this.UE.BellManager.GetBellData(id);
        const mapID=String(data?.MapID || "").toLowerCase();
        if(!mapID || mapID==="none" || page.isTeleportBlockedByStory() || !page.isTeleportAllowed(mapID)) {
            page.showTeleportTip("UI_Map_31");return false;
        }
        // EPBZFlowTeleportSource.Bell = 1 (Charm = 0).
        if(page.isTeleportBlockedByFlowCriticalSection(1,recheck)) {
            page.showTeleportTip("UI_Map_39");return false;
        }
        return true;
    }
    request(point) {
        const page=this.page,id=String(point?.reward || "");
        if(!page || this.pending || page.isModalOpen() || !page.CanUIInput() || point?.category!=="bell" || point.state!=="collected" || !id || !this.activated(id))return false;
        page.playMapSfxList(page.MapConfig.BellConfirmSounds);
        if(!page.IsOpenByBell()) {
            page.tryTeleportByCharm(id);
            Diagnostics.record("bell-travel-request",{id,mode:"charm"});
            return true;
        }
        if(!this.allowed(id))return false;
        const {UE,cpp,text}=this;
        const title=cpp.FText.FromString(text.bellTeleport);
        const body=cpp.FText.Format(cpp.FText.FromString(text.bellTeleportConfirm),cpp.FText.FromString(point.label || text.bell));
        const dialog=UE.AsyncAction_ShowConfirmation.ShowConfirmationYesNo(page,title,body);
        const handler=result=>{
            this.pending=undefined;
            if(!this.page)return;
            try {
                if(result===UE.ECommonMessagingResult.Confirmed && page.IsOpenByBell() && this.activated(id) && this.allowed(id,true)) {
                    page.BellTeleport.Broadcast(id);
                    Diagnostics.record("bell-travel-confirmed",{id,mode:"bell"});
                } else page.SetFocus();
            } catch(error) {Diagnostics.record("error",{source:"bell-travel",error:String(error)});}
        };
        this.pending={dialog,handler};
        dialog.OnResult.Add(handler);
        try {dialog.Activate();}
        catch(error) {this.pending=undefined;dialog.OnResult.Remove(handler);throw error;}
        return true;
    }
    dispose(detach=true) {
        const pending=this.pending;this.pending=undefined;this.page=undefined;
        if(detach && pending && this.UE.KismetSystemLibrary.IsValid(pending.dialog))pending.dialog.OnResult.Remove(pending.handler);
    }
}
module.exports={BellTravel};
