"use strict";

function plain(value) {
    return String(value || "").replace(/<[^>]*>/g, " ").replace(/&nbsp;/gi, " ")
        .replace(/&amp;/gi, "&").replace(/&lt;/gi, "<").replace(/&gt;/gi, ">");
}
function normalize(value) { return plain(value).normalize("NFKC").trim().toLowerCase(); }
function index(item) {
    const names = [item.name, ...(item.aliases || [])].map(normalize).filter(Boolean);
    return { item, names, text: normalize([item.id, ...names, item.description, item.category].join(" ")) };
}
function query(entries, value) {
    const text = normalize(value), words = text.split(/\s+/).filter(Boolean);
    if (!words.length) return entries.map(entry => entry.item);
    return entries.map((entry, order) => {
        if (!words.every(word => entry.text.includes(word))) return null;
        const score = entry.names.includes(text) ? 0 : entry.names.some(name => name.startsWith(text)) ? 1
            : entry.names.some(name => name.includes(text)) ? 2 : 3;
        return { item: entry.item, score, order };
    }).filter(Boolean).sort((a, b) => a.score - b.score || a.order - b.order).map(entry => entry.item);
}
module.exports = { plain, normalize, index, query };
