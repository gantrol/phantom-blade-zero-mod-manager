"use strict";
const UE = require("ue");
const puerts = require("puerts");
const Policy = require("./Policy");

const orders = new WeakMap();
const minimumAlignment = Math.cos(Policy.acquisitionAngleDegrees * Math.PI / 180);

function valid(object) { return !!object && UE.KismetSystemLibrary.IsValid(object); }
function reset(handler) { orders.delete(handler); }
function dot(a, b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }

function cameraFrame(handler) {
    const camera = UE.GameplayStatics.GetPlayerCameraManager(handler.Avatar, 0);
    if (!valid(camera)) return null;
    const rotation = camera.GetCameraRotation();
    return {
        origin: camera.GetCameraLocation(),
        forward: UE.KismetMathLibrary.GetForwardVector(rotation),
        right: UE.KismetMathLibrary.GetRightVector(rotation),
        up: UE.KismetMathLibrary.GetUpVector(rotation)
    };
}

function pointedActor(handler, frame, all) {
    const range = Math.max(0, ...all.map(candidate => candidate.distance)) + 1;
    if (range <= 0) return null;
    const end = new UE.Vector();
    for (const axis of ["X", "Y", "Z"])
        end[axis] = frame.origin[axis] + frame.forward[axis] * range;
    const ignored = UE.NewArray(UE.Actor);
    ignored.Add(handler.Avatar);
    const hitRef = puerts.$ref(new UE.HitResult());
    // Visibility limits the ray at walls. Destructible/PhysicsActor profiles
    // ignore Visibility in this build, so also trace eligible actors' visible
    // components directly; otherwise a barrel could lose to an enemy behind it.
    const hit = UE.KismetSystemLibrary.LineTraceSingle(handler.Avatar, frame.origin, end,
        UE.ETraceTypeQuery.TraceTypeQuery1, true, ignored, UE.EDrawDebugTrace.None,
        hitRef, true, new UE.LinearColor(), new UE.LinearColor(), 0);
    const worldHit = puerts.$unref(hitRef);
    let closest = hit ? worldHit.Distance : range;
    let pointed = hit && valid(worldHit.Component) ? worldHit.Component.GetOwner() : null;
    let traced = 0;
    const locationRef = puerts.$ref(new UE.Vector()), normalRef = puerts.$ref(new UE.Vector());
    const boneRef = puerts.$ref("None");
    for (const actor of new Set(all.map(candidate => candidate.actor))) {
        const components = actor.GetComponentsByClass(UE.PrimitiveComponent.StaticClass());
        for (let i = 0; i < components.Num(); i++) {
            if (++traced > Policy.maxTraceComponents) return pointed;
            const component = components.Get(i);
            if (!valid(component) || !component.IsVisible() || component.bHiddenInGame) continue;
            if (component.K2_LineTraceComponent(frame.origin, end, true, false, false,
                locationRef, normalRef, boneRef, hitRef)) {
                const distance = puerts.$unref(hitRef).Distance;
                if (distance < closest) { closest = distance; pointed = actor; }
            }
        }
    }
    return pointed;
}

function candidates(targets, frame) {
    const result = [];
    for (let i = 0; i < targets.Num(); i++) {
        const source = targets.Get(i);
        if (!UE.ComponentSocketHelper.IsValid(source.Target)) continue;
        const actor = UE.ComponentSocketHelper.GetActor(source.Target);
        if (!valid(actor)) continue;
        const point = UE.ComponentSocketHelper.GetLocation(source.Target);
        const delta = { X: point.X - frame.origin.X, Y: point.Y - frame.origin.Y, Z: point.Z - frame.origin.Z };
        const distance = Math.hypot(delta.X, delta.Y, delta.Z);
        const depth = dot(delta, frame.forward);
        const target = new UE.TargetingRatedTarget();
        const socket = new UE.ComponentSocket();
        socket.Component = source.Target.Component;
        socket.SocketName = source.Target.SocketName;
        target.Target = socket;
        target.Score = source.Score;
        target.SocketScore = source.SocketScore;
        result.push({
            target, actor, distance,
            key: String(UE.KismetSystemLibrary.GetPathName(socket.Component)) + ":" + String(socket.SocketName),
            alignment: distance > 1 ? depth / distance : 1,
            x: depth > 0 ? dot(delta, frame.right) / depth : Infinity,
            y: depth > 0 ? -dot(delta, frame.up) / depth : Infinity
        });
    }
    return result;
}

function spatialOrder(a, b) {
    return a.x - b.x || a.y - b.y || a.distance - b.distance || (a.key < b.key ? -1 : a.key > b.key ? 1 : 0);
}

function arrange(handler, targets) {
    if (!targets.Num() || targets.Num() > Policy.maxCandidates) { reset(handler); return; }
    const frame = cameraFrame(handler);
    if (!frame) return;
    const all = candidates(targets, frame);
    const acquiring = !handler.TargetingComponent.HasLockTarget();
    let state = orders.get(handler);
    if (acquiring || !state) state = { ranks: new Map(), next: 0 };

    // Keep a bounded order for this lock session. Existing targets retain their
    // place as the camera follows them; newly eligible targets join at the end.
    const present = new Set(all.map(candidate => candidate.key));
    for (const key of state.ranks.keys()) if (!present.has(key)) state.ranks.delete(key);
    for (const candidate of [...all].sort(spatialOrder)) {
        if (!state.ranks.has(candidate.key)) state.ranks.set(candidate.key, state.next++);
        // The native previous/next changer sorts Score ascending and uses it
        // when reconciling its cache. Stable scores also keep overlapping
        // sockets reachable instead of re-sorting around the new camera angle.
        candidate.target.Score = state.ranks.get(candidate.key);
    }
    orders.set(handler, state);

    let selected = all;
    if (acquiring) {
        const pointed = pointedActor(handler, frame, all);
        selected = all.filter(candidate => candidate.actor === pointed || candidate.alignment >= minimumAlignment);
        selected.sort((a, b) => Number(b.actor === pointed) - Number(a.actor === pointed)
            || b.alignment - a.alignment || a.distance - b.distance || spatialOrder(a, b));
    } else selected.sort((a, b) => a.target.Score - b.target.Score);

    // Never select by faction or Pawn type. Both enemies and environmental
    // objects must already have passed this mode's native candidate query.
    targets.Empty();
    for (const candidate of selected) targets.Add(candidate.target);
}

module.exports = { arrange, reset };
