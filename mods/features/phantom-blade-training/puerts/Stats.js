"use strict";
const E = require("./Engine"), { UE, cpp, valid, t, text } = E;
let frameClass;
class Stats {
    constructor(battle) {
        this.battle = battle; this.player = E.player(); this.pc = UE.GameplayStatics.GetPlayerController(this.player, 0);
        if (!frameClass) {
            class PhantomBladeArenaStats extends UE.UserWidget {}
            Object.defineProperty(PhantomBladeArenaStats, "hasOwnProperty", { value: Object.prototype.hasOwnProperty });
            frameClass = cpp.makeUClass(PhantomBladeArenaStats);
        }
        this.frame = UE.WidgetBlueprintLibrary.Create(this.player, frameClass, this.pc);
        this.root = UE.NewObject(UE.CanvasPanel.StaticClass(), this.frame.WidgetTree); this.frame.WidgetTree.RootWidget = this.root;
        this.frame.SetVisibility(UE.ESlateVisibility.HitTestInvisible); this.frame.AddToViewport(2);
        this.summary = this.label(340, 150, 15); this.summary.slot.SetPosition(new UE.Vector2D(24, 160));
        this.labels = Array.from({ length: 20 }, () => this.label(250, 30, 14));
    }
    label(width, height, size) {
        const widget = UE.NewObject(UE.TextBlock.StaticClass(), this.frame.WidgetTree); this.root.AddChild(widget);
        const font = widget.Font; font.Size = size; widget.SetFont(font);
        const color = new UE.SlateColor(); color.SpecifiedColor = new UE.LinearColor(.93, .87, .69, 1); widget.SetColorAndOpacity(color);
        widget.SetShadowOffset(new UE.Vector2D(1, 1)); widget.SetShadowColorAndOpacity(new UE.LinearColor(0, 0, 0, 1));
        const slot = UE.WidgetLayoutLibrary.SlotAsCanvasSlot(widget); slot.SetSize(new UE.Vector2D(width, height)); return { widget, slot };
    }
    update() {
        if (!valid(this.frame)) return;
        const hidden = UE.GameplayStatics.IsGamePaused(this.player);
        this.frame.SetVisibility(hidden ? UE.ESlateVisibility.Collapsed : UE.ESlateVisibility.HitTestInvisible); if (hidden) return;
        const practice = this.battle.practice, s = practice.sample, targeting = this.player.GetComponentByClass(UE.TargetingComponent.StaticClass());
        const locked = valid(targeting) && targeting.HasLockTarget() ? UE.ComponentSocketHelper.GetActor(targeting.LockTarget) : null;
        const selected = practice.targets.find(target => target.actor === locked), prop = practice.props.find(p => p.actor === locked);
        const lock = selected ? "#" + selected.config.id : prop ? t("propExplosiveBarrel") + " B" + prop.config.id : t(locked ? "targetLockOther" : "targetLockNone");
        this.summary.widget.SetText(text(`${t("currentLock")} ${lock}\n${t("totalDamage")} ${s.damage.toFixed(1)} · DPS ${(s.elapsed ? s.damage / s.elapsed : 0).toFixed(1)}\n${t("lastMax")} ${s.last.toFixed(1)} / ${s.maximum.toFixed(1)}\n${t("allyDamage")} ${s.ally.toFixed(1)} · ${t("conversions")} ${s.conversions}`));
        const members = practice.targets.map(target => ({ actor: target.actor, label: "#" + target.config.id + " · " + t(target.state), height: 150 }))
            .concat(practice.props.map(prop => ({ actor: prop.actor, label: t("propExplosiveBarrel") + " B" + prop.config.id, height: 130 })));
        this.labels.forEach((label, i) => {
            const item = members[i], position = cpp.$ref(new UE.Vector2D());
            if (!valid(item?.actor)) { label.widget.SetVisibility(UE.ESlateVisibility.Collapsed); return; }
            const p = item.actor.K2_GetActorLocation(); p.Z += item.height;
            const visible = UE.WidgetLayoutLibrary.ProjectWorldLocationToWidgetPosition(this.pc, p, position, false);
            label.widget.SetVisibility(visible ? UE.ESlateVisibility.HitTestInvisible : UE.ESlateVisibility.Collapsed);
            if (visible) { const point = cpp.$unref(position); label.slot.SetPosition(new UE.Vector2D(point.X - 125, point.Y - 30)); label.widget.SetText(text((item.actor === locked ? "◆ " : "") + item.label)); }
        });
    }
    dispose() { if (valid(this.frame)) this.frame.RemoveFromParent(); }
}
module.exports = Stats;
