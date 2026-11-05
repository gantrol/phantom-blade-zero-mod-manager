"use strict";

// Reveal the painted map without changing unlock IDs or the save. These are
// terrain fog widgets; opening/closing fades and input gates remain native.
class NativeFog {
    constructor(UE) {this.UE=UE;this.masks=new Map();this.page=undefined;this.maskCheck=undefined;}
    update(page,reveal) {
        if(this.page!==page) {
            this.page=page;
            this.maskCheck=page?.enableMaskCheck;
        }
        if(page) {
            page.__pbmRevealUnexplored=!!reveal;
            if(typeof this.maskCheck==="boolean")page.enableMaskCheck=reveal?false:this.maskCheck;
        }
        const current=new Set(reveal?[page.MaskWidgetInstance,page.mergedFogImage,page.lutFogWidget,page.W_MapMask_BigMap].filter(Boolean):[]);
        for(const [widget,opacity] of this.masks)if(!current.has(widget)) {
            if(this.UE.KismetSystemLibrary.IsValid(widget))widget.SetRenderOpacity(opacity);
            this.masks.delete(widget);
        }
        for(const widget of current) {
            if(!this.masks.has(widget))this.masks.set(widget,widget.GetRenderOpacity());
            if(widget.GetRenderOpacity()!==0)widget.SetRenderOpacity(0);
        }
    }
    dispose(restore=true) {
        if(restore) {
            if(this.page) {
                this.page.__pbmRevealUnexplored=false;
                if(typeof this.maskCheck==="boolean")this.page.enableMaskCheck=this.maskCheck;
            }
            for(const [widget,opacity] of this.masks)
                if(this.UE.KismetSystemLibrary.IsValid(widget))widget.SetRenderOpacity(opacity);
        }
        this.masks.clear();
        this.page=undefined;
        this.maskCheck=undefined;
    }
}
module.exports={NativeFog};
