/* Illustrative snapshots only; never imported by the game runtime. */
(() => {
  "use strict";
  const model = window.PhantomBladeDamageModel;
  const base = { attack: 120, correction: 1.15, amp: 1.1, dealtUp: 1.2, dealtDown: 1,
    resist: 0.8, takenAdd: 1, takenUp: 1, takenDown: 0.8, preTake: 1.3 };
  const scenarios = [
    { id: "baseline", movement: null },
    { id: "movement", movement: { level: 2, distance: 10, cap: 0.6, multiplier: 1.3 } },
    { id: "reset", movement: { level: 2, distance: 0, cap: 0.6, multiplier: 1 } }
  ];
  function evaluate(scenario) {
    const values = { ...base };
    if (scenario.movement) values.correction *= scenario.movement.multiplier;
    return { values, movement: scenario.movement, total: model.product(model.order.map(id => values[id])) };
  }
  window.PhantomBladeDamageExamples = { scenarios, evaluate };
})();
