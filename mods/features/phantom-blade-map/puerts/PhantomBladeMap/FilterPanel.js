"use strict";
const {groups,traversalGroups}=require("./Filters");
const {rgba,layout}=require("./Appearance");
const pages=["items","map","traversal"];

class FilterPanel {
    constructor(view,action) {
        this.view=view;this.action=action;this.page="items";this.height=558;
        const UE=view.UE;
        this.root=view.create(UE.Border,view.page.WidgetTree.RootWidget);
        this.root.SetPadding(new UE.Margin(0,0,0,0));
        this.root.SetRenderTransformPivot(new UE.Vector2D(0,0));
        this.root.SetBrushColor(new UE.LinearColor(...rgba("ink",.99)));
        this.slot=UE.WidgetLayoutLibrary.SlotAsCanvasSlot(this.root);
        this.slot.SetSize(new UE.Vector2D(layout.width,this.height));this.slot.SetZOrder(50);
        this.canvas=view.create(UE.CanvasPanel,this.root);
        view.textValue(view.makeText(this.canvas,18,20,16,250,28,rgba("paper")),view.text.filters);
        view.makeButton(["Settings","close","close"],304,12,32,action,this.canvas);
        this.tabs={};
        for(const [key,label,x] of [["items","filterItems",20],["map","filterMap",124],["traversal","filterTraversal",228]]) {
            const text=view.makeText(this.canvas,13,x,61,104,28,rgba("paper"));
            text.widget.SetJustification(UE.ETextJustify.Center);view.textValue(text,view.text[label]);
            const bar=view.makeRect(this.canvas,x,92,104,2,...rgba("accent"));
            view.makeRowButton(x,52,104,42,key,key=>{this.page=key;this.focus=undefined;this.update(this.config);},this.canvas);
            this.tabs[key]={text,bar};
        }
        view.makeRect(this.canvas,20,146,312,1,...rgba("line",.8));
        this.common=[this.row("remainingOnly","remainingOnly",20,102,152),this.row("exploredOnly","exploredOnly",180,102,152)];
        this.traversalMaster=this.row("showTraversal","showTraversal",20,102,152);
        this.items=groups.map((g,i)=>this.row(g.setting,g.label,20,164+i*48,312,g.icon));
        this.map=["showFloors","showFog","minimap","npc","treeHeart","marker"].map((field,i)=>this.row(field,field,20,164+i*48,312));
        this.traversal=traversalGroups.map((g,i)=>this.row(g.setting,g.label,20,164+i*48,312,g.icon));
        view.makeRect(this.canvas,20,484,312,1,...rgba("line",.8));
        this.all=this.labelButton("FilterAll","remaining","filterSelectAll",20,492,148);
        this.reset=this.labelButton("FilterReset","reset","filterReset",184,492,148);
        view.textValue(view.makeText(this.canvas,9,20,534,312,18,rgba("muted")),view.text.filterControls);
    }
    labelButton(key,icon,label,x,y,width) {
        const v=this.view,bg=v.makeRect(this.canvas,x,y,width,38,...rgba("selected",.8));
        const button=v.makeRowButton(x,y,width,38,key,this.action,this.canvas);
        const iconButton=v.makeButton([key,icon,label],x,y,32,this.action,this.canvas);
        const text=v.makeText(this.canvas,12,x+38,y+8,width-42,24,rgba("paper"));v.textValue(text,v.text[label]);
        return {key,button,text,icon:iconButton,bg};
    }
    row(field,label,x,y,width,icon) {
        const v=this.view,bg=v.makeRect(this.canvas,x-4,y,width+8,42,...rgba("selected",.8));
        const button=v.makeRowButton(x,y,width,42,"Setting:"+field,this.action,this.canvas);
        button.widget.SetToolTipText(v.cpp.FText.FromString(v.text[label]));
        const text=v.makeText(this.canvas,12,x+(icon?38:0),y+10,width-(icon?70:28),24,rgba("paper"));v.textValue(text,v.text[label]);
        let pin;
        if(icon){pin=v.makePinWidget(this.canvas);v.place(pin,x+12,y+22);v.stylePin(pin,{category:icon,state:"remaining"},false);v.visible(pin.text,false);}
        const cx=x+width-20,cy=y+15;
        const border=[[[0,0],[14,0]],[[14,0],[14,14]],[[14,14],[0,14]],[[0,14],[0,0]]].map(([a,b])=>{
            const line=v.makeLineWidget(this.canvas);v.drawLine(line,cx+a[0],cy+a[1],cx+b[0],cy+b[1],1,...rgba("muted"));return line;
        });
        const check=[[[2,6],[6,10]],[[6,10],[15,0]]].map(([a,b])=>{
            const line=v.makeLineWidget(this.canvas);v.drawLine(line,cx+a[0],cy+a[1],cx+b[0],cy+b[1],1.6,...rgba("accent"));return line;
        });
        return {field,button,text,pin,border,check,bg};
    }
    update(config) {
        this.config=config;
        const v=this.view;
        const allLabel=v.text[groups.every(g=>config[g.setting]!==false)?"filterSelectNone":"filterSelectAll"];
        v.textValue(this.all.text,allLabel);this.all.icon.widget.SetToolTipText(v.cpp.FText.FromString(allLabel));
        for(const [key,tab] of Object.entries(this.tabs)) {
            v.visible(tab.bar,key===this.page);
            tab.text.widget.SetColorAndOpacity(v.color(...rgba(key===this.page?"paper":"muted")));
        }
        const traversalPage=this.page==="traversal",traversalEnabled=config.showTraversal!==false;
        if(traversalPage && !traversalEnabled && this.traversal.includes(this.focus))this.focus=this.traversalMaster;
        for(const [rows,visible] of [[[this.common[0]],!traversalPage],[[this.common[1]],true],[[this.traversalMaster],traversalPage],
            [this.items,this.page==="items"],[this.map,this.page==="map"],[this.traversal,traversalPage]])for(const row of rows) {
            const enabled=!this.traversal.includes(row) || traversalEnabled;
            for(const node of [row.button,row.text,row.pin,...row.border])v.visible(node,visible);
            row.button.widget.SetIsEnabled(enabled);
            if(row.pin)row.pin.widget.SetRenderOpacity(enabled?1:.35);
            v.visible(row.bg,visible && enabled && this.focus===row);
            for(const node of row.check)v.visible(node,visible && config[row.field]!==false);
            row.text.widget.SetColorAndOpacity(v.color(...rgba(enabled && config[row.field]!==false?"paper":"muted")));
        }
        for(const node of [this.all.button,this.all.text,this.all.icon,this.reset.button,this.reset.text,this.reset.icon])v.visible(node,this.page==="items");
        v.visible(this.all.bg,this.page==="items" && this.focus===this.all);
        v.visible(this.reset.bg,this.page==="items" && this.focus===this.reset);
    }
    key(key) {
        if(["Escape","Gamepad_FaceButton_Right"].includes(key)){this.action("Settings");return true;}
        if(["Left","Right","Gamepad_DPad_Left","Gamepad_DPad_Right","Gamepad_LeftShoulder","Gamepad_RightShoulder"].includes(key)) {
            const direction=["Left","Gamepad_DPad_Left","Gamepad_LeftShoulder"].includes(key)?-1:1;
            this.page=pages[(pages.indexOf(this.page)+direction+pages.length)%pages.length];this.focus=undefined;this.update(this.config);return true;
        }
        const rows=this.page==="traversal"?[this.traversalMaster,this.common[1]].concat(this.config.showTraversal!==false?this.traversal:[]):
            this.common.concat(this.page==="items"?this.items.concat(this.all,this.reset):this.map);
        if(["Up","Down","Gamepad_DPad_Up","Gamepad_DPad_Down"].includes(key)) {
            const direction=key==="Up" || key==="Gamepad_DPad_Up"?-1:1;
            const index=rows.indexOf(this.focus);
            this.focus=rows[index<0?0:(index+direction+rows.length)%rows.length];this.update(this.config);return true;
        }
        if(["Enter","SpaceBar","Gamepad_FaceButton_Bottom"].includes(key)) {
            const row=rows.includes(this.focus)?this.focus:rows[0];this.focus=row;this.action(row.key || "Setting:"+row.field);return true;
        }
        return false;
    }
    resize() {
        const v=this.view,size=v.page.getMapPageLocalSize();
        const scale=Math.min(layout.panelScale,(size.Y-148)/this.height,(size.X-56)/layout.width);
        this.root.SetRenderScale(new v.UE.Vector2D(scale,scale));
        this.slot.SetPosition(new v.UE.Vector2D(size.X-layout.width*scale-28,100));
    }
    show(open,config) {
        this.root.SetVisibility(open?this.view.UE.ESlateVisibility.Visible:this.view.UE.ESlateVisibility.Collapsed);
        if(open){this.update(config);this.resize();}
    }
    release(){this.items=this.map=this.common=this.traversal=[];this.tabs={};this.all=this.reset=this.focus=this.traversalMaster=undefined;this.config=this.action=this.canvas=this.root=this.slot=this.view=undefined;}
}
module.exports={FilterPanel};
