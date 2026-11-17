"use strict";
const UE = require("ue");
const protectedSequences = new Set(require("./ProtectedSequences").map(path => path.toLowerCase()));

function valid(object) { return !!object && UE.KismetSystemLibrary.IsValid(object); }
function path(object) { return String(UE.KismetSystemLibrary.GetPathName(object)); }
function active(object) { return valid(object) && object.IsActivated(); }

class Guard {
    constructor(classes) {
        this.classes = classes;
        this.cache = new WeakMap();
    }

    sequence(sequence, fresh = false) {
        if (!valid(sequence)) return "unloaded-sequence";
        if (!fresh && this.cache.has(sequence)) return this.cache.get(sequence);
        const visiting = new Set(), visited = new Set();
        let remaining = 16384;
        const each = (items, visit) => {
            if (!items || typeof items.Num !== "function") throw new Error("unreadable-tracks");
            const count = items.Num();
            if (count > remaining) throw new Error("inspection-limit");
            remaining -= count;
            for (let index = 0; index < count; index++) {
                const reason = visit(items.Get(index));
                if (reason) return reason;
            }
            return "";
        };
        const inspect = (current, depth) => {
            if (!valid(current)) return "unloaded-subsequence";
            if (protectedSequences.has(path(current).toLowerCase())) return "qte-sequence";
            if (visiting.has(current)) return "cyclic-subsequence";
            if (visited.has(current)) return "";
            if (depth > 32 || visited.size + visiting.size >= 128) return "inspection-limit";
            if (!valid(UE.MovieSceneSequenceExtensions.GetMovieScene(current))) return "unreadable-movie-scene";
            visiting.add(current);
            const inspectTrack = track => {
                if (!valid(track)) return "unloaded-track";
                // Disabled tracks are deliberately protected too: a flow may enable them later.
                if (track.IsA(UE.QTETrack.StaticClass())) return "qte-track";
                return each(UE.MovieSceneTrackExtensions.GetSections(track), section => {
                    if (!valid(section)) return "unloaded-section";
                    if (section.IsA(UE.QTESection.StaticClass())) return "qte-section";
                    if (section.IsA(UE.MovieSceneSubSection.StaticClass()))
                        return inspect(section.GetSequence(), depth + 1);
                    return "";
                });
            };
            const reason = each(UE.MovieSceneSequenceExtensions.GetTracks(current), inspectTrack)
                || each(UE.MovieSceneSequenceExtensions.GetBindings(current), binding =>
                    each(UE.MovieSceneBindingExtensions.GetTracks(binding), inspectTrack));
            visiting.delete(current);
            visited.add(current);
            return reason;
        };
        const reason = inspect(sequence, 0);
        // Missing assets may become available later. Only completed classifications are cached.
        if (!reason || reason.startsWith("qte-")) this.cache.set(sequence, reason);
        return reason;
    }

    context(context, controller) {
        if (!valid(controller) || !controller.IsLocalPlayerController()) return { reason: "no-controller" };
        if (UE.GameplayStatics.IsGamePaused(context)) return { reason: "paused" };
        const layout = UE.PBZBlueprintFunctionLibrary.GetUILayOut(context);
        if (valid(layout) && layout.HasModal()) return { reason: "modal" };
        const manager = UE.SubsystemBlueprintLibrary.GetWorldSubsystem(context, UE.PBZLevelSequenceManager.StaticClass());
        const flow = UE.SubsystemBlueprintLibrary.GetWorldSubsystem(context, UE.EventPipelineManager.StaticClass());
        if (!valid(manager) || !valid(flow)) return { reason: "no-story-manager" };
        // These are additional vetoes for QTEs started by scripts rather than a timeline track.
        if (valid(manager.QTE) || controller.QTEManager.Num() > 0) return { reason: "qte-input" };
        const node = flow.GetCurrentAffectFlowNode();
        if (valid(node) && node.GetActivationState() === 1 && node.IsA(this.classes.qte))
            return { reason: "qte-node" };
        const dialogue = flow.DialogueWidget;
        if (active(dialogue) && dialogue.IsVisible()) return { reason: "dialogue" };
        const sequence = manager.GetCurrentSequence();
        if (manager.IsInSequence()) {
            const reason = this.sequence(sequence);
            if (reason) return { reason };
        }
        return { reason: "", manager, flow, node, sequence,
            sessionId: String(manager.GetCurrentSequenceSessionId()),
            locked: manager.IsInSequence() || controller.bCinematicMode || controller.IsMoveInputIgnored() };
    }

    scene(scene, state, fresh = false) {
        if (state.reason) return state.reason;
        const { widget, player, sequence, kind, node } = scene;
        if (!active(widget) || !valid(player) || (!player.IsPlaying() && !player.IsPaused())) return "inactive-scene";
        if (!valid(sequence) || player.GetSequence() !== sequence) return "changed-sequence";
        if (valid(node) && node.IsA(this.classes.qte) && node.GetActivationState() === 1) return "qte-node";
        if (kind === "sequence") {
            if (!valid(node)) return "unknown-owner";
            if (node.GetActivationState() !== 1) return "inactive-scene";
            if (typeof widget.CanSkip !== "boolean") return "unknown-skip-interface";
            if (!node.IsA(this.classes.node) || node.SequencePlayer !== player || node.Loop.Value !== 0)
                return "unknown-sequence-owner";
            if (widget.InLevelSeq !== sequence || widget.InLevelSeqPlayer !== player
                || widget["In Level Sequence Node"] !== node) return "changed-widget";
        } else {
            // FlowVideo owns its player directly; CurrentAffectFlowNode may refer to an
            // earlier dialogue rather than this video, so it is only an additional veto.
            if (!widget.IsA(this.classes.video) || widget.LoadedLevelSequence !== sequence
                || widget.LevelSequencePlayer !== player) return "unknown-video-owner";
            if (valid(node) && node.IsA(this.classes.videoNode) && node.GetActivationState() === 1
                && node["As WBP Flow Video"] !== widget) return "overlapping-video-owner";
        }
        // Revalidate the current world sequence as well as the selected UI's sequence.
        if (state.manager.IsInSequence()) {
            const reason = this.sequence(state.sequence, fresh);
            if (reason) return reason;
        }
        return this.sequence(sequence, fresh);
    }
}

module.exports = { Guard, valid, path, active };
