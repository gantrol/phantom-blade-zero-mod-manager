"use strict";

// Copy into the business mod's private module namespace. No framework import.
const SLOT = "__PBZ_MOD_CONFIG_V1__";
const ID = /^[A-Za-z][A-Za-z0-9_.-]{0,95}$/;
function getHub(root = globalThis, report = event => console.warn("[PbzModConfig] " + JSON.stringify(event))) {
    if (root[SLOT] !== undefined) {
        const hub = root[SLOT];
        if (!hub || hub.protocol !== 1 || !(hub.providers instanceof Map) ||
            !["publish", "subscribeProviders", "snapshotProviders"].every(k => typeof hub[k] === "function")) {
            throw new Error("PROTOCOL_CONFLICT: " + SLOT);
        }
        return hub;
    }
    const providers = new Map(), listeners = new Set();
    const diagnostic = (code, detail) => { try { report({ code, detail: String(detail) }); } catch (_) { /* logger must not break business */ } };
    const notify = event => {
        for (const listener of Array.from(listeners)) {
            try { listener(event); } catch (e) { diagnostic("DIRECTORY_LISTENER_FAILED", e); }
        }
    };
    const hub = Object.freeze({
        protocol: 1, providers,
        publish(descriptor) {
            if (!descriptor || descriptor.apiVersion !== 1 || !ID.test(descriptor.modId) ||
                !["read", "apply", "subscribe", "reset", "flush"].every(k => typeof descriptor[k] === "function")) {
                diagnostic("INVALID_PROVIDER", descriptor && descriptor.modId);
                throw new Error("INVALID_PROVIDER");
            }
            const modId = descriptor.modId, ownerToken = Object.freeze({});
            const previous = providers.get(modId);
            const record = Object.freeze({ modId, ownerToken, descriptor });
            providers.set(modId, record);
            if (previous) diagnostic("PROVIDER_REPLACED", modId);
            notify({ type: "published", record, previous });
            let disposed = false;
            return Object.freeze({ ownerToken, dispose() {
                if (disposed) return false;
                disposed = true;
                if (providers.get(modId)?.ownerToken !== ownerToken) {
                    diagnostic("STALE_UNREGISTER", modId);
                    return false;
                }
                providers.delete(modId);
                notify({ type: "removed", record });
                return true;
            } });
        },
        subscribeProviders(listener) {
            if (typeof listener !== "function") throw new TypeError("INVALID_LISTENER");
            listeners.add(listener);
            return () => listeners.delete(listener);
        },
        snapshotProviders() { return Array.from(providers.values()); }
    });
    root[SLOT] = hub;
    return hub;
}
function publishOptional(descriptor, root = globalThis, report) {
    try { return getHub(root, report).publish(descriptor); }
    catch (e) {
        const event = { code: "REGISTRATION_FAILED", modId: descriptor?.modId, detail: String(e) };
        try { (report || (x => console.warn("[PbzModConfig] " + JSON.stringify(x))))(event); } catch (_) {}
        return Object.freeze({ ownerToken: null, error: event, dispose: () => false });
    }
}
module.exports = { SLOT, ID, getHub, publishOptional };
