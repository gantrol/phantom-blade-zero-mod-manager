"use strict";

const groups = [
    { id: "amp", attribute: "Amplification", side: "player", mode: "add" },
    { id: "dealtUp", attribute: "DealtVulnerabilityMultiplier", side: "player" },
    { id: "dealtDown", attribute: "DealtReductionMultiplier", side: "player" },
    { id: "resist", attribute: "Resistance", side: "target", mode: "resist" },
    { id: "takenAdd", attribute: "TakenAdditiveMultiplier", side: "target", mode: "add" },
    { id: "takenUp", attribute: "TakenVulnerabilityMultiplier", side: "target" },
    { id: "takenDown", attribute: "TakenReductionMultiplier", side: "target" }
];
const order = ["attack", "correction", "amp", "dealtUp", "dealtDown", "resist", "takenAdd", "takenUp", "takenDown", "preTake"];
const finite = value => typeof value === "number" && Number.isFinite(value);
const product = values => values.every(finite) ? values.reduce((a, b) => a * b, 1) : undefined;
const ratio = (value, base) => finite(value) && finite(base) && base > 0 ? value / base : undefined;
function number(value) {
    if (!finite(value)) return "—";
    if (Math.abs(value) >= 1e6) return value.toExponential(2);
    return value.toFixed(2).replace(/\.00$/, "").replace(/(\.\d)0$/, "$1");
}
function lane(attackTag) {
    const name = attackTag.replace("Damage.Attack.Target.", "");
    if (name === "PhysicalDamage") return { id: "Physical", families: ["Physical"] };
    if (name === "PhysicalDamage.Sharp" || name === "PhysicalDamage.Blunt")
        return { id: "Physical", families: ["Physical", "Physical" + name.split(".")[1]] };
    if (name === "TrueDamage") return { id: "True", families: [] };
    const match = /^(Fire|Lightning|Toxin|Ice|Aqua|Blood|Chaos)Damage$/.exec(name);
    if (match) return { id: match[1], families: ["Elemental", match[1]] };
}
function factors(read, families) {
    const values = { attack: read("player", "Attack") }, attributes = {};
    for (const group of groups) {
        attributes[group.id] = families.map(family => {
            const name = group.attribute + "_" + family;
            const raw = read(group.side, name);
            let value;
            if (finite(raw)) {
                if (group.mode === "resist") {
                    // The special resistance-floor branch is not guessed. The
                    // observed settlement still remains available in this case.
                    const floorEnabled = read(group.side, "ResistanceMin_Valid");
                    if (floorEnabled === 0 && raw >= 0) value = Math.max(1 - raw, 0);
                } else value = Math.max(group.mode === "add" ? 1 + raw : raw, 0);
            }
            return { name, raw, value, side: group.side };
        });
        values[group.id] = product(attributes[group.id].map(item => item.value));
    }
    return { values, attributes };
}
function healthDamage(map) {
    if (!map) return;
    return Object.entries(map).filter(([key]) => key === "Damage.Damage.Target.TrueDamage"
        || key.startsWith("Damage.Damage.Target.GeneralDamage.")
        || key.startsWith("Damage.Damage.Target.StatusDamage.")).reduce((sum, [, value]) => sum + value, 0);
}
const api = { groups, order, finite, product, ratio, number, lane, factors, healthDamage };
if (typeof module !== "undefined" && module.exports) module.exports = api;
else window.PhantomBladeDamageModel = api;
