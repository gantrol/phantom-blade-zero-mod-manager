/**
 * Menu selection only. A battle catalog contains verified encounter records:
 * { bosses: [{ id, defaultSceneId, sceneIds }], scenes: [{ id }] }.
 * The native codex/map inventory is deliberately a separate input: a map ID
 * does not identify an arena, a player start or a complete boss encounter.
 */
export class Selection {
    constructor(catalog) {
        this.bosses = new Map(catalog.bosses.map(boss => [boss.id, boss]));
        this.scenes = new Map(catalog.scenes.map(scene => [scene.id, scene]));
        this.draft = { bossId: null, sceneId: null, lockHealth: false };
        this.active = null;

        for (const boss of this.bosses.values()) {
            if (!boss.sceneIds.includes(boss.defaultSceneId)
                || boss.sceneIds.some(id => !this.scenes.has(id))) {
                throw new Error('INVALID_BATTLE_SCENES');
            }
        }
    }

    selectBoss(id) {
        const boss = this.requireBoss(id);
        this.draft = { ...this.draft, bossId: id, sceneId: boss.defaultSceneId };
        return this.snapshot();
    }

    selectScene(id) {
        const boss = this.requireBoss(this.draft.bossId);
        if (!boss.sceneIds.includes(id)) throw new Error('INCOMPATIBLE_BATTLE_SCENE');
        this.draft = { ...this.draft, sceneId: id };
        return this.snapshot();
    }

    restoreDefaultScene() {
        return this.selectBoss(this.draft.bossId);
    }

    setLockHealth(enabled) {
        this.draft = { ...this.draft, lockHealth: Boolean(enabled) };
        return this.snapshot();
    }

    startPlan() {
        this.requireBoss(this.draft.bossId);
        return this.plan(this.draft);
    }

    // Called only after the native battle has successfully initialized.
    // Preparing another battle does not replace the active reset target.
    acceptStarted(plan) {
        const boss = this.requireBoss(plan.bossId);
        if (!boss.sceneIds.includes(plan.sceneId)) throw new Error('INCOMPATIBLE_BATTLE_SCENE');
        this.active = Object.freeze({ bossId: plan.bossId, sceneId: plan.sceneId,
            lockHealth: Boolean(plan.lockHealth) });
    }

    // Called after the active battle has applied the health-lock change.
    acceptLockHealth(enabled) {
        if (!this.active) throw new Error('NO_ACTIVE_BATTLE');
        this.setLockHealth(enabled);
        this.active = Object.freeze({ ...this.active, lockHealth: Boolean(enabled) });
    }

    resetPlan() {
        if (!this.active) throw new Error('NO_ACTIVE_BATTLE');
        return this.plan(this.active);
    }

    acceptEnded() {
        this.active = null;
    }

    snapshot() {
        return Object.freeze({ ...this.draft });
    }

    requireBoss(id) {
        const boss = this.bosses.get(id);
        if (!boss) throw new Error('BATTLE_NOT_CONFIGURED');
        return boss;
    }

    plan(selection) {
        return Object.freeze({ ...selection, initialization: 'full-encounter',
            playerHealth: 'maximum', playerEnergy: 'maximum' });
    }
}
