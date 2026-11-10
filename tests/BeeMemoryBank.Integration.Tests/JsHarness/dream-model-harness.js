// Runs wwwroot/js/pages/dream-model.js (what the Dream page says: kinds, bands, outcomes, the numbers of a run, the state card, a card of advice and its buttons,
// what the Apply dialog promises, the live feed, the start form) in a bare context, and prints one JSON line {"ok": true|false, "detail": "..."} for the scenario
// named on the command line. The scenarios are the ones of DreamPageModelTests.cs.
//
//   node dream-model-harness.js <path of dream-model.js> <scenario>
'use strict';
const fs = require('fs');
const vm = require('vm');

const scriptPath = process.argv[2];
const scenario = process.argv[3];

const sandbox = { window: {} };
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(scriptPath, 'utf8'), sandbox, { filename: scriptPath });
const M = sandbox.window.bmbDreamModel;

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function has(text, part, what) { if (String(text).indexOf(part) < 0) throw new Error(what + ': "' + part + '" is not in "' + text + '"'); }

const NOW = Date.parse('2026-11-10T12:00:00Z');
const ago = (seconds) => new Date(NOW - seconds * 1000).toISOString();

const ALL_KINDS = ['duplicate_exact', 'near_duplicate', 'duplicate', 'superseded', 'overlap', 'contradiction', 'move_article', 'split_folder',
    'rename_folder', 'merge_folders', 'structure_note', 'merge_tags', 'delete_unused_tags', 'delete_empty_folder', 'test_leftovers'];
const ALL_OUTCOMES = ['completed', 'advice_limit', 'time_limit', 'budget_limit', 'stopped', 'locked', 'heavy_operation', 'provider_error', 'interrupted', 'failed', 'skipped'];

const A1 = '11111111-1111-1111-1111-111111111111';
const A2 = '22222222-2222-2222-2222-222222222222';
const A3 = '33333333-3333-3333-3333-333333333333';

function advice(extra) {
    return Object.assign({
        id: 'adv-1', runId: 'run-1', seq: 1, kind: 'duplicate_exact', source: 'rule', score: 95, band: 'high', status: 'new', applyMode: 'code', trial: false,
        createdAt: ago(60), title: 'Two notes say the same', summary: 'They are identical.',
        evidence: [{ type: 'similarity', text: 'The texts match', value: 0.99 }],
        objects: [
            { role: 'subject', type: 'article', id: A1, title: 'Backup notes', treePath: '/Ops', lamportTs: 5 },
            { role: 'other', type: 'article', id: A2, title: 'Backup notes (copy)', treePath: '/Ops/Old', lamportTs: 6 }
        ],
        action: { type: 'trash_article', articleId: A2, keepId: A1 },
        can: { apply: true, chat: true, prompt: true, dismiss: true, markDone: true }
    }, extra || {});
}

function status(extra) {
    return Object.assign({
        available: true, activeRun: null, openAdvice: { total: 3, high: 1, medium: 1, low: 1 },
        settings: { scheduleEnabled: false, useModel: false, maxAdviceScheduled: 10, maxAdviceManual: 50 },
        modelReady: true, modelLabel: 'Some model', chatAvailable: true, embeddings: { total: 100, pending: 0 }, nextScheduledAt: null
    }, extra || {});
}

const scenarios = {
    // ---- kinds, bands, statuses ----
    every_kind_has_a_label_and_an_icon() {
        ALL_KINDS.forEach((k) => {
            const info = M.kindInfo(k);
            assert(info.label && info.icon && info.noun, k + ' has a label, an icon and a noun');
            assert(info.label.indexOf('_') < 0, k + ' is told in words');
        });
        eq(M.kindOptions().map((o) => o.value), ALL_KINDS, 'the filter lists every kind, in the order of the plan');
        const unknown = M.kindInfo('some_new_kind');
        eq([unknown.label, unknown.icon], ['Some new kind', 'lightbulb'], 'a kind that is not known is told from its name');
    },
    bands_follow_the_score() {
        eq([100, 80, 79, 60, 59, 0].map(M.bandOf), ['high', 'high', 'medium', 'medium', 'low', 'low'], 'the bands');
        eq([undefined, NaN, 'x'].map(M.bandOf), ['low', 'low', 'low'], 'no score is a weak one');
        eq(M.bandLabel('high'), 'High', 'a label');
    },
    statuses_are_told_and_only_open_ones_are_open() {
        eq(['new', 'applying', 'in_chat'].map((s) => M.statusInfo(s).open), [true, true, true], 'open');
        eq(['applied', 'dismissed', 'expired', 'strange'].map((s) => M.statusInfo(s).open), [false, false, false, false], 'closed');
        eq(M.statusInfo('in_chat').label, 'In chat', 'the word');
    },

    // ---- runs ----
    every_outcome_has_words_and_a_colour() {
        ALL_OUTCOMES.forEach((o) => {
            const info = M.outcomeInfo({ state: 'finished', outcome: o });
            assert(info.label && info.variant && info.text, o + ' has a label, a variant and a sentence');
        });
        eq(M.outcomeInfo({ state: 'finished', outcome: 'completed' }).variant, 'success', 'completed is green');
        eq(M.outcomeInfo({ state: 'finished', outcome: 'failed' }).variant, 'danger', 'failed is red');
        eq(M.outcomeInfo({ state: 'running' }).label, 'Running', 'a run that goes on');
        eq(M.outcomeInfo({ state: 'stopping' }).variant, 'warning', 'stopping');
        eq(M.outcomeInfo({ state: 'finished' }).label, 'Finished', 'no outcome yet');
    },
    a_skipped_night_run_says_why() {
        has(M.outcomeInfo({ state: 'finished', outcome: 'skipped', errorCode: 'open_list_full' }).text, 'too much open advice', 'the reason');
        has(M.outcomeInfo({ state: 'finished', outcome: 'skipped', errorCode: 'locked' }).text, 'locked', 'the reason');
        has(M.outcomeInfo({ state: 'finished', outcome: 'skipped', errorCode: 'no_embeddings' }).text, 'vectors', 'the reason');
        has(M.outcomeInfo({ state: 'finished', outcome: 'skipped', errorCode: 'busy' }).text, 'another run', 'the reason');
    },
    the_kind_of_a_run_is_night_manual_or_trial() {
        eq(M.triggerInfo({ trigger: 'schedule', mode: 'normal' }).label, 'Night', 'night');
        eq(M.triggerInfo({ trigger: 'manual', mode: 'normal' }).label, 'Manual', 'manual');
        eq(M.triggerInfo({ trigger: 'manual', mode: 'trial' }).label, 'Trial', 'trial');
        eq(M.triggerInfo({ trigger: 'schedule', mode: 'trial' }).label, 'Trial', 'a trial is a trial whoever started it');
        eq(M.triggerInfo(null).label, 'Manual', 'nothing known');
    },
    phases_are_told_in_words() {
        ['embeddings', 'detectors', 'pairs', 'folders', 'tree'].forEach((p) => assert(M.phaseLabel(p).length > 8 && M.phaseLabel(p).indexOf('_') < 0, p));
        eq(M.phaseLabel(null), '', 'no phase');
        eq(M.phaseLabel('new_phase'), 'New phase', 'a phase that is not known');
    },

    // ---- numbers and times ----
    numbers_are_told_short() {
        eq([850, 12300, 1500000, 1000000, 999, 1000].map(M.formatTokens), ['850', '12.3k', '1.5M', '1M', '999', '1k'], 'tokens');
        eq([0, 12, 12345, 1234567].map(M.formatCount), ['0', '12', '12,345', '1,234,567'], 'counts');
        eq([undefined, null, NaN].map(M.formatCount), ['-', '-', '-'], 'no number');
    },
    durations_are_told_as_a_person_says_them() {
        eq([200, 42000, 185000, 3720000, 7200000, 60000].map(M.formatDuration), ['< 1 s', '42 s', '3 min 5 s', '1 h 2 min', '2 h', '1 min'], 'durations');
        eq([null, -5, NaN].map(M.formatDuration), ['-', '-', '-'], 'no duration');
    },
    the_duration_of_a_run_runs_to_its_end_or_to_now() {
        eq(M.runDurationMs({ startedAt: ago(100), finishedAt: ago(40) }, NOW), 60000, 'finished');
        eq(M.runDurationMs({ startedAt: ago(100) }, NOW), 100000, 'still going');
        eq(M.runDurationMs({ createdAt: ago(10) }, NOW), 10000, 'not started: from its creation');
        eq(M.runDurationMs({}, NOW), null, 'no time at all');
        eq(M.runDurationMs({ startedAt: ago(-10) }, NOW), 0, 'a clock a little ahead is no negative time');
    },
    times_are_told_relative_to_now() {
        eq([ago(10), ago(5 * 60), ago(3 * 3600), ago(30 * 3600), ago(12 * 86400)].map((t) => M.relativeWhen(t, NOW)),
            ['just now', '5 min ago', '3 h ago', 'yesterday', '12 days ago'], 'the past');
        eq(M.relativeWhen(new Date(NOW + 10 * 60000).toISOString(), NOW), 'in 10 min', 'the future');
        eq(M.relativeWhen('nonsense', NOW), '-', 'a bad time');
        eq(M.relativeWhen(null, NOW), '-', 'no time');
    },
    the_counters_are_one_line() {
        const line = M.counterLine({ articlesInScope: 142, candidatesExamined: 37, adviceCreated: 5, modelCalls: 12, promptTokens: 40000, completionTokens: 1200, errors: 1 });
        eq(line, '142 notes in scope · 37 candidates looked at · 5 advice · 12 model calls · 41.2k tokens · 1 error', 'the line');
        eq(M.counterLine({ articlesInScope: 1, candidatesExamined: 1, adviceCreated: 0 }), '1 note in scope · 1 candidate looked at · 0 advice', 'singular, and nothing the run did not do');
        eq(M.counterLine(null), '', 'no counters');
    },
    the_limits_are_told_with_none_as_no_limit() {
        eq(M.limitsList({ maxAdvice: 50, maxMinutes: 30, maxModelCalls: 200, maxTokens: 1500000 }), ['Advice: 50', 'Time: 30 min', 'Model calls: 200', 'Tokens: 1.5M'], 'limits');
        eq(M.limitsList({ maxAdvice: 0, maxMinutes: 0, maxModelCalls: 0, maxTokens: 0 }), ['Advice: no limit', 'Time: no limit', 'Model calls: 0', 'Tokens: 0'], 'none');
    },
    a_history_row_has_the_big_numbers() {
        const run = { state: 'finished', useModel: true, modelLabel: 'M1', startedAt: ago(200), finishedAt: ago(80), counters: { articlesInScope: 1200, articlesSent: 300, adviceCreated: 14, promptTokens: 900000, completionTokens: 10000 } };
        const stats = M.runStats(run, { 'new': 5, inChat: 1, applied: 6, dismissed: 2, expired: 0 }, NOW);
        eq(stats.map((s) => s.key), ['viewed', 'sent', 'advice', 'tokens', 'took'], 'the five numbers');
        eq(stats.map((s) => s.value), ['1,200', '300', '14', '910k', '2 min'], 'the values');
        eq(stats[2].sub, '6 open · 6 applied · 2 dismissed', 'open, applied and dismissed');
        eq(stats[3].sub, 'M1', 'the model under the tokens');
        const noModel = M.runStats({ state: 'finished', useModel: false, counters: { articlesInScope: 10, adviceCreated: 0 } }, null, NOW);
        eq([noModel[1].value, noModel[1].sub, noModel[3].value, noModel[2].sub], ['-', 'no model', '-', ''], 'a run without a model, and counts not asked for yet');
    },
    pages_are_counted_and_clamped() {
        eq(M.pageInfo(0, 1, 10, 'run').pages, 1, 'no runs is one page');
        const p = M.pageInfo(63, 2, 10, 'run');
        eq([p.page, p.pages, p.hasPrev, p.hasNext, p.text], [2, 7, true, true, 'Page 2 of 7 · 63 runs'], 'a middle page');
        eq(M.pageInfo(63, 99, 10, 'run').page, 7, 'beyond the last page');
        eq(M.pageInfo(1, 1, 10, 'run').text, 'Page 1 of 1 · 1 run', 'singular');
        eq(M.pageInfo(undefined, undefined, undefined).pages, 1, 'nothing');
    },

    // ---- the state card ----
    the_idle_card_says_the_last_run_and_the_next() {
        const lastRun = { state: 'finished', outcome: 'completed', finishedAt: ago(3 * 3600) };
        const v = M.statusView(status({ settings: { scheduleEnabled: true }, nextScheduledAt: new Date(NOW + 3 * 3600000).toISOString() }), lastRun, NOW);
        eq([v.tone, v.title, v.canStart, v.canStop], ['idle', 'Idle', true, false], 'idle');
        has(v.lines[0], 'Last run 3 h ago: completed.', 'the last run');
        has(v.lines[1], 'Next night run in 3 h', 'the next night run');
        const off = M.statusView(status(), null, NOW);
        eq(off.lines, ['Dream has not run yet.', 'The night run is off.'], 'never ran, schedule off');
    },
    a_running_card_says_the_phase_and_the_counters_and_can_stop() {
        const run = { id: 'r', state: 'running', mode: 'normal', trigger: 'manual', phase: 'pairs', startedAt: ago(75), counters: { articlesInScope: 10, candidatesExamined: 4, adviceCreated: 2 } };
        const v = M.statusView(status({ activeRun: run }), null, NOW);
        eq([v.tone, v.title, v.canStart, v.canStop, v.elapsedMs], ['running', 'Run is going', false, true, 75000], 'running');
        eq(v.lines[0], 'Comparing pairs of notes...', 'the phase');
        has(v.lines[1], '4 candidates looked at', 'the counters');
        eq(M.statusView(status({ activeRun: Object.assign({}, run, { mode: 'trial' }) }), null, NOW).title, 'Trial run is going', 'a trial');
        eq(M.statusView(status({ activeRun: Object.assign({}, run, { trigger: 'schedule' }) }), null, NOW).title, 'Night run is going', 'a night run');
        const stopping = M.statusView(status({ activeRun: Object.assign({}, run, { state: 'stopping' }) }), null, NOW);
        eq([stopping.tone, stopping.canStop, stopping.canStart], ['stopping', false, false], 'stopping');
        eq(M.statusView(status({ activeRun: { id: 'r', state: 'queued' } }), null, NOW).lines[0], 'Waiting to start.', 'queued');
        eq(M.statusView(status({ activeRun: { id: 'r', state: 'finished' } }), null, NOW).tone, 'idle', 'a finished run is not active');
    },
    a_card_that_cannot_run_says_why() {
        const locked = M.statusView(status({ available: false, reason: 'locked' }), null, NOW);
        eq([locked.tone, locked.canStart], ['unavailable', false], 'locked');
        has(locked.lines[0], 'vault is locked', 'the reason in words');
        eq(M.statusView(status({ available: false, reason: 'something else' }), null, NOW).lines[0], 'something else', 'a reason that is not known is shown as it is');
        eq(M.statusView(status({ available: false }), null, NOW).lines[0], 'The node did not say why.', 'no reason');
        const unknown = M.statusView(null, null, NOW);
        eq([unknown.tone, unknown.canStart, unknown.canStop], ['error', false, false], 'no answer at all');
    },
    warnings_name_pending_vectors_and_leave_the_model_to_the_start_form() {
        const w = M.warnings(status({ modelReady: false, modelProblem: 'no_key', embeddings: { total: 100, pending: 40 } }));
        eq(w.length, 1, 'one notice: a model that is not set up is said at its own checkbox');
        has(w[0].text, '40 of 100', 'the vectors');
        eq(M.warnings(status()).length, 0, 'nothing to say');
        eq(M.warnings(status({ available: false })).length, 0, 'a card that cannot run has its own words');
        eq(M.warnings(null).length, 0, 'no state');
    },
    the_start_form_becomes_a_request() {
        eq(M.buildStartRequest({ withModel: true, modelReady: true }).body, { mode: 'normal', useRules: true, useModel: true }, 'with a model');
        eq(M.buildStartRequest({ withModel: true, modelReady: false }).body, { mode: 'normal', useRules: true, useModel: false }, 'a model that is not ready is not asked for');
        eq(M.buildStartRequest({ trial: true, maxAdvice: '25', maxMinutes: ' 10 ' }).body, { mode: 'trial', useRules: true, useModel: false, maxAdvice: 25, maxMinutes: 10 }, 'a trial with limits');
        eq(M.buildStartRequest({ maxAdvice: '0' }).body.maxAdvice, 0, 'zero is no limit, and is sent');
        eq(M.buildStartRequest({ maxAdvice: '', maxMinutes: '' }).body, { mode: 'normal', useRules: true, useModel: false }, 'blank fields are left to the settings');
        eq(M.buildStartRequest({ useRules: false, withModel: true, modelReady: true }).body, { mode: 'normal', useRules: false, useModel: true }, 'the model alone');
        const nothing = M.buildStartRequest({ useRules: false, withModel: true, modelReady: false });
        eq(nothing.ok, false, 'neither the rules nor a model that can run: refused here, before the node says 400');
        has(nothing.error, 'at least one', 'in words');
        assert(!M.buildStartRequest({ maxAdvice: 'abc' }).ok, 'letters are refused');
        assert(!M.buildStartRequest({ maxMinutes: '-5' }).ok, 'a negative number is refused');
        assert(!M.buildStartRequest({ maxAdvice: '2.5' }).ok, 'a fraction is refused');
        assert(!M.buildStartRequest({ maxAdvice: '1001' }).ok, 'more than 1000 advice is refused');
        assert(!M.buildStartRequest({ maxMinutes: '1441' }).ok, 'more than a day is refused');
        has(M.buildStartRequest({ maxAdvice: 'abc' }).error, 'whole number', 'in words');
    },

    the_start_form_has_two_switches_and_says_what_leaves_the_node() {
        const both = M.startForm(status(), { useRules: true, useModel: true });
        eq([both.rules, both.useModel, both.canStart, both.model.disabled, both.model.label], [true, true, true, false, 'Some model'], 'rules and the model');
        has(both.help, 'sent to OpenRouter', 'what leaves the node');
        has(both.help, 'Some model', 'to which model');
        has(both.help, 'built-in rules run here', 'and that the rules stay here');
        const rules = M.startForm(status(), { useRules: true, useModel: false });
        has(rules.help, 'Nothing leaves this node', 'the rules alone');
        const model = M.startForm(status(), { useRules: false, useModel: true });
        eq(model.canStart, true, 'the model alone can run');
        has(model.help, 'Only the AI model runs', 'the model alone');
        has(model.help, 'sent to OpenRouter', 'and what it sends');
        const none = M.startForm(status(), { useRules: false, useModel: false });
        eq([none.canStart, none.startReason], [false, 'Choose at least one: the built-in rules or the AI model.'], 'neither: Start is off, with the reason');
        has(M.startForm(status(), { useRules: true, trial: true }).help, 'trial run does not count', 'a trial says so');
        eq(M.startForm(status(), {}).rules, true, 'the rules are on unless they are turned off');
    },
    a_model_that_is_not_ready_is_a_disabled_switch_with_the_reason_and_the_links() {
        const noModel = M.startForm(status({ modelReady: false, modelLabel: null, modelProblem: 'no_model' }), { useRules: true, useModel: true });
        eq([noModel.model.disabled, noModel.useModel, noModel.model.label], [true, false, 'not set up'], 'off, and not used even when it was ticked');
        has(noModel.model.reason, 'No text model', 'why');
        eq(noModel.model.links.map((l) => l.href), ['/Admin/AiChat', '/Admin/Dream'], 'where models and keys are, and where Dream chooses one');
        has(M.startForm(status({ modelReady: false, modelProblem: 'no_key' }), {}).model.reason, 'No usable OpenRouter key', 'no key');
        has(M.startForm(status({ modelReady: false }), {}).model.reason, 'not ready', 'an older node that does not say why');
        const nothing = M.startForm(status({ modelReady: false, modelProblem: 'no_model' }), { useRules: false, useModel: true });
        eq(nothing.canStart, false, 'the rules off and no model: nothing to run');
        eq(M.startForm(status(), { useModel: true }).model.links, [], 'a ready model needs no links');
    },
    a_run_says_what_ran() {
        eq(M.runWhat({ useRules: true, useModel: true, modelLabel: 'M' }).label, 'Rules + AI', 'both');
        has(M.runWhat({ useRules: true, useModel: true, modelLabel: 'M' }).text, '(M)', 'with the model');
        eq(M.runWhat({ useRules: false, useModel: true, modelLabel: 'M' }).label, 'AI only', 'the model alone');
        has(M.runWhat({ useRules: false, useModel: true }).text, 'rules were off', 'in words');
        eq(M.runWhat({ useRules: true, useModel: false }).label, 'Rules only', 'the rules alone');
        eq(M.runWhat({ useModel: false }).label, 'Rules only', 'a run from before the switch ran the rules');
        eq(M.runWhat({ useRules: false, useModel: false }).key, 'none', 'a run of phases without either');
        eq(M.runWhat(null).key, 'rules', 'nothing known');
    },

    // ---- a card of advice ----
    a_card_says_who_found_it_and_what_each_object_is() {
        eq(M.adviceView(advice(), {}).foundBy, 'Found by the built-in rules', 'the rules');
        eq(M.adviceView(advice({ source: 'model', model: { label: 'Some model', promptVersion: 'pair-1' } }), { runModelLabel: 'Other' }).foundBy, 'Found by the AI model: Some model', 'its own model first');
        eq(M.adviceView(advice({ source: 'model', model: null }), { runModelLabel: 'Run model' }).foundBy, 'Found by the AI model: Run model', 'else the model of its run');
        eq(M.adviceView(advice({ source: 'model' }), {}).foundBy, 'Found by the AI model', 'or no name at all');
        eq(M.adviceView(advice({ source: 'model', model: { label: 'X', promptVersion: 'pair-1' } }), {}).promptVersion, 'pair-1', 'the prompt version stays for the fold');
        const types = M.objectLinks([{ type: 'article', id: A1, title: 'N' }, { type: 'folder', id: A2, path: '/F' }, { type: 'folder', path: '/New' }, { type: 'tag', name: 't' }]).map((o) => o.typeLabel);
        eq(types, ['Note', 'Folder', 'Folder', 'Tag'], 'the type in words');
    },
    no_closed_card_offers_apply_or_the_chat_and_the_prompt_stays_for_reference() {
        const all = { apply: true, chat: true, prompt: true, dismiss: true, markDone: true };
        // whatever an older or confused node says it can, a closed card never offers Apply or the chat
        ['applied', 'dismissed', 'expired', 'applying'].forEach((s) => {
            const v = M.adviceView(advice({ status: s, can: all }), { chatAvailable: true });
            eq([v.buttons.apply, v.buttons.chat, v.buttons.markDone], [false, false, false], s);
        });
        ['applied', 'dismissed'].forEach((s) => {
            const v = M.adviceView(advice({ status: s, can: { prompt: true } }), {});
            eq(v.buttons.prompt, true, s + ': Show prompt stays when the node gives one');
            has(v.promptNote, 'for reference only', s + ': and the dialog says it is for reference');
        });
        eq(M.adviceView(advice({ status: 'expired', can: { prompt: false } }), {}).buttons.prompt, false, 'an expired advice has no prompt');
        eq(M.adviceView(advice({ status: 'new' }), {}).promptNote, '', 'an open advice: no note');
        eq(M.adviceView(advice({ status: 'in_chat', can: all }), {}).buttons.chat, true, 'in chat it can go to the chat again');
    },
    an_advice_closed_elsewhere_is_told_with_its_status_and_time() {
        const at = '2026-11-09T18:30:00Z';
        const err = (body) => { const e = new Error(body.error || 'x'); e.status = 409; e.body = body; return e; };
        const when = M.formatWhen(at);
        eq(M.errorText(err({ error: 'server words', code: 'not_applicable', status: 'applied', statusAt: at })), 'This advice was already applied on ' + when + '.', 'applied, in local time');
        eq(M.errorText(err({ code: 'not_applicable', status: 'applied', statusAt: at, statusNote: 'marked_done' })), 'This advice was already marked done on ' + when + '.', 'marked done');
        eq(M.errorText(err({ code: 'not_applicable', status: 'dismissed', statusAt: at })), 'This advice was already dismissed on ' + when + '.', 'dismissed');
        eq(M.errorText(err({ code: 'expired', status: 'expired', statusAt: at, statusNote: 'The note was changed after this advice was made.' })),
            'This advice expired on ' + when + ': the note was changed after this advice was made.', 'expired, with the reason');
        has(M.errorText(err({ code: 'expired', status: 'expired', statusAt: at, statusNote: 'superseded' })), 'a newer verdict', 'superseded');
        eq(M.errorText(err({ code: 'expired', status: 'applied', statusAt: at, statusNote: 'changed_after_chat' })),
            'A note it is about changed after it went to the chat, so it counts as done (' + when + ').', 'done after the chat');
        eq(M.conflictText({ status: 'applied' }), 'This advice was already applied.', 'no time');
        eq(M.isClosedConflict(err({ code: 'not_applicable', error: 'The folder is read-only for you.' })), false, 'a 409 with no status is not a closed advice');
        eq(M.isClosedConflict(err({ status: 'new' })), false, 'nor one that is still open');
        const e404 = new Error('gone'); e404.status = 404; e404.body = { status: 'applied' };
        eq(M.isClosedConflict(e404), false, 'only a 409');
    },
    the_rule_book_reads_the_node_and_labels_every_kind() {
        const book = M.ruleBookView({
            intro: ['Intro.'], similarity: ['Similarity.'], skips: ['Locked notes.'],
            rules: [{ id: 'TagVariants', name: 'Tag variants', kinds: ['tag_variants'], kindDetails: [{ kind: 'tag_variants', action: 'merge_tags', applyModes: ['code', 'chat', 'prompt'] }],
                finds: 'Tags that differ by case.', example: 'Docker and docker.', how: ['Same letters.'], thresholds: [{ name: 'Max distance', value: '1' }], score: '60 + 5 per note.',
                apply: 'Merges.', never: 'Deletes notes.', skips: ['Locked notes.'] }, null],
            modelTasks: [{ id: 'pair_verdict', name: 'Pair verdict', promptVersion: 'pair-1', kinds: ['overlap', 'strange'], candidates: ['Pairs.'], asked: 'Which?', answers: 'One of.', checks: ['Ids.'], limits: [{ name: 'Pairs', value: '40' }] }],
            limits: [{ name: 'Advice', value: '50' }], bands: [{ band: 'high', from: 70, to: 100, meaning: 'Do it.' }]
        });
        eq(book.rules.length, 1, 'a null rule is skipped');
        eq(book.rules[0].kinds[0].label, M.kindInfo('tag_variants').label, 'the label of the kind filter');
        eq(book.rules[0].kinds[0].modes, 'Apply or Open in AI chat or Show prompt', 'the ways to carry it out');
        eq(book.rules[0].thresholds, [{ name: 'Max distance', value: '1' }], 'the numbers as the node says them');
        eq(book.tasks[0].kinds.map((k) => k.label), [M.kindInfo('overlap').label, 'Strange'], 'an unknown kind still has words');
        eq(book.bands[0].label, 'High', 'the band');
        eq(book.empty, false, 'not empty');
        eq(M.ruleBookView(null).empty, true, 'a stub is empty, and does not throw');
        eq(M.ruleBookView({ rules: [{ id: 'X', described: false }] }).rules[0].described, false, 'a rule with no words says so');
    },
    a_list_redraws_only_when_it_changed_and_the_link_names_one_advice() {
        const a = advice();
        eq(M.adviceSignature([a]), M.adviceSignature([advice()]), 'the same advice, the same signature');
        assert(M.adviceSignature([a]) !== M.adviceSignature([advice({ status: 'applied' })]), 'a status changes it');
        assert(M.adviceSignature([a]) !== M.adviceSignature([advice({ can: { apply: false, chat: true, prompt: true, dismiss: true, markDone: true } })]), 'what it can changes it');
        assert(M.runSignature([{ id: 'r', state: 'running' }]) !== M.runSignature([{ id: 'r', state: 'finished' }]), 'a run that finished');
        eq(M.adviceSignature(null), '', 'nothing');
        eq(M.adviceIdFromQuery('?advice=' + A1.toUpperCase()), A1, 'a guid, in lower case');
        eq(M.adviceIdFromQuery('?x=1&advice=' + A1 + '#top'), A1, 'among other parameters');
        eq(M.adviceIdFromQuery('?advice=nope'), '', 'not a guid');
        eq(M.adviceIdFromQuery('?advice=%E0%A4%A'), '', 'not even decodable');
        eq(M.adviceIdFromQuery(''), '', 'no query');
        eq(M.adviceIdFromQuery(null), '', 'nothing at all');
    },
    the_card_has_the_buttons_the_api_allows() {
        const v = M.adviceView(advice(), { chatAvailable: true });
        eq([v.buttons.apply, v.buttons.chat, v.buttons.prompt, v.buttons.dismiss, v.buttons.markDone, v.buttons.backToNew], [true, true, true, true, true, false], 'a code advice has them all');
        eq([v.band, v.bandLabel, v.score, v.source, v.kind.label], ['high', 'High', 95, 'Rules', 'Exact duplicate'], 'the header');
        const chatAdvice = M.adviceView(advice({ kind: 'overlap', applyMode: 'chat', action: { type: 'review_pair', aId: A1, bId: A2 }, can: { apply: false, chat: true, prompt: true, dismiss: true, markDone: true } }), {});
        eq([chatAdvice.buttons.apply, chatAdvice.buttons.chat], [false, true], 'a chat advice has no Apply');
        const noPlan = M.adviceView(advice({ action: { type: 'note' } }), {});
        eq(noPlan.buttons.apply, false, 'Apply needs an action that code can do, whatever the flag says');
        eq(M.adviceView(advice({ can: {} }), {}).buttons, { apply: false, chat: false, chatDisabled: false, prompt: false, dismiss: false, markDone: false, backToNew: false }, 'no flags, no buttons');
    },
    a_chat_that_is_not_available_greys_its_button() {
        const v = M.adviceView(advice(), { chatAvailable: false });
        eq([v.buttons.chat, v.buttons.chatDisabled], [true, true], 'drawn, but not usable');
        eq(M.adviceView(advice(), { chatAvailable: true }).buttons.chatDisabled, false, 'usable');
    },
    a_closed_advice_has_no_buttons_and_an_expired_one_says_why() {
        ['applied', 'dismissed', 'expired'].forEach((s) => {
            const v = M.adviceView(advice({ status: s }), {});
            // Show prompt stays on an applied or dismissed card, for reference (the node decides with can.prompt); never on an expired one
            eq([v.closed, v.buttons.apply, v.buttons.chat, v.buttons.prompt, v.buttons.dismiss, v.buttons.markDone], [true, false, false, s !== 'expired', false, false], s);
        });
        const expired = M.adviceView(advice({ status: 'expired', expiredReason: 'The note was changed.' }), {});
        eq([expired.expired, expired.note], [true, 'The note was changed.'], 'the reason');
        eq(M.adviceView(advice({ status: 'expired' }), {}).note, 'What this advice describes has changed.', 'no reason given');
        const applying = M.adviceView(advice({ status: 'applying' }), {});
        eq([applying.buttons.apply, applying.buttons.markDone], [false, false], 'not while it is being applied');
        const applied = M.adviceView(advice({ status: 'applied', applied: { at: ago(5), byUserId: 1, result: 'moved from /A to /B' } }), {});
        eq(applied.applied.result, 'moved from /A to /B', 'what was done');
    },
    an_advice_in_chat_can_go_back_to_the_list() {
        const v = M.adviceView(advice({ status: 'in_chat' }), {});
        eq([v.status.label, v.buttons.backToNew, v.buttons.dismiss, v.buttons.markDone], ['In chat', true, true, true], 'in chat');
    },
    the_card_tells_trial_model_and_references() {
        const v = M.adviceView(advice({ trial: true, source: 'model', model: { label: 'Some model', promptVersion: 'pair-1' }, references: { checked: true, count: 2, samples: [] } }), {});
        eq([v.trial, v.source, v.model], [true, 'Model', 'Some model · pair-1'], 'trial and model');
        eq(v.references.text, '2 other notes mention this note by its id.', 'references');
        eq(M.adviceView(advice({ references: { checked: true, count: 0, samples: [] } }), {}).references, null, 'no references, nothing said');
        eq(M.adviceView(advice({ references: { checked: false, count: 3, samples: [] } }), {}).references, null, 'a check that did not run says nothing');
    },
    objects_are_links_to_the_notes_folders_and_tags() {
        const links = M.objectLinks([
            { role: 'subject', type: 'article', id: A1, title: 'Backup notes', treePath: '/Ops' },
            { role: 'other', type: 'article', id: A2, title: 'Copy', treePath: '/Ops/Old' },
            { role: 'target', type: 'folder', id: A3, path: '/Archive/Old notes' },
            { role: 'target', type: 'folder', id: null, path: '/New' },
            { type: 'tag', name: 'c#', articleCount: 12 }
        ], { type: 'trash_article', articleId: A2, keepId: A1 });
        eq(links.map((l) => l.href), ['/Article/View?id=' + A1, '/Article/View?id=' + A2, '/Folder?path=%2FArchive%2FOld%20notes', '', '/Graph?concept=c%23'], 'the links, made safe');
        eq(links.map((l) => l.role), ['kept', 'to the trash', 'target', 'target', ''], 'what each one is for');
        eq([links[3].sub, links[4].sub], ['does not exist yet', '12 notes'], 'a folder to be made, and a tag');
        eq(M.objectLinks([{ type: 'article', id: A1 }])[0].label, 'Untitled note', 'a note without a title');
        eq(M.objectLinks(null), [], 'no objects');
    },
    evidence_is_told_with_percent_for_similarity() {
        const lines = M.evidenceLines([{ type: 'similarity', text: 'Texts match', value: 0.944 }, { type: 'count', text: 'Notes', value: 12 }, { type: 'quote', text: 'a quote' }, { type: 'odd', text: 'x' }]);
        eq(lines.map((l) => l.value), ['94%', '12', '', ''], 'values');
        eq(lines.map((l) => l.label), ['Similarity', 'Count', 'Quote', 'Odd'], 'labels');
    },
    a_card_made_from_nothing_does_not_throw() {
        const v = M.adviceView(null, null);
        eq([v.title, v.score, v.band, v.objects, v.buttons.apply], ['Advice', null, 'low', [], false], 'nothing');
        const w = M.adviceView({ kind: 'strange_kind', score: 'x', evidence: [null], objects: [null, {}] }, {});
        assert(w.title === 'Strange kind' && w.objects.length === 2, 'junk fields');
    },

    // ---- the Apply dialog ----
    apply_says_exactly_what_it_will_do() {
        const trash = M.applyPlan(advice());
        eq(trash.confirm, 'Move to trash', 'the button');
        has(trash.lines[0], 'Move “Backup notes (copy)” to the trash.', 'the note that goes');
        has(trash.lines[1], '“Backup notes”', 'the note that stays');
        has(trash.warnings[0], '30 days', 'the trash is not for ever');

        const move = M.applyPlan(advice({
            kind: 'move_article', action: { type: 'move_article', articleId: A1, toPath: '/Work/Ops' },
            objects: [{ type: 'article', id: A1, title: 'Backup notes', treePath: '/Inbox' }, { type: 'folder', id: A3, path: '/Work/Ops', role: 'target' }]
        }));
        has(move.lines[0], 'Move “Backup notes” from /Inbox to /Work/Ops.', 'the move');
        eq(move.notes, [{ title: 'Backup notes', path: '/Inbox' }], 'the note that moves, by title and path');
        assert(move.warnings.join(' ').indexOf('access rules') >= 0 && move.warnings.join(' ').indexOf('old path') >= 0, 'the two warnings of a move');

        const many = M.applyPlan(advice({
            kind: 'split_folder', action: { type: 'move_articles', toPath: '/Work/New', articleIds: [A1, A2, A3] },
            objects: [{ type: 'folder', id: A3, path: '/Work', role: 'subject' }, { type: 'article', id: A1, title: 'One', treePath: '/Work' },
                { type: 'article', id: A2, title: 'Two', treePath: '/Work' }, { type: 'article', id: A3, title: 'Three', treePath: '/Work/Old' }]
        }));
        eq(many.confirm, 'Move 3 notes', 'the button');
        has(many.lines[0], '“One”, “Two”, “Three”', 'the notes');
        eq(many.notes, [{ title: 'One', path: '/Work' }, { title: 'Two', path: '/Work' }, { title: 'Three', path: '/Work/Old' }], 'every note of the advice, each with its folder, nothing cut');
        has(many.warnings.join(' '), 'others are still moved', 'a partial success is said before it can happen');
        has(many.warnings.join(' '), 'Nothing is moved if the folder', 'and when nothing moves at all');

        const tags = M.applyPlan(advice({ kind: 'merge_tags', action: { type: 'merge_tags', sources: ['Python', 'python3'], target: 'python' }, objects: [] }));
        has(tags.lines[0], 'Merge “Python”, “python3” into “python”', 'the tags');
        const del = M.applyPlan(advice({ kind: 'delete_unused_tags', action: { type: 'delete_tags', names: ['a', 'b'] }, objects: [] }));
        eq(del.confirm, 'Delete 2 tags', 'the button');
        has(del.lines[0], 'that no note uses', 'what it deletes');
        const folder = M.applyPlan(advice({ kind: 'delete_empty_folder', action: { type: 'delete_folder', path: '/Empty', folderId: A3 }, objects: [] }));
        has(folder.lines[0], 'empty folder /Empty', 'the folder');
        has(folder.warnings[0], 'still empty', 'checked again');
    },
    an_advice_that_code_cannot_do_has_no_plan() {
        ['review_pair', 'merge_articles', 'mark_superseded', 'rename_folder', 'merge_folders', 'note', 'review_folder'].forEach((t) => {
            eq(M.applyPlan(advice({ action: { type: t } })), null, t);
        });
        eq(M.applyPlan(null), null, 'nothing');
        eq(M.applyPlan({}), null, 'no action');
    },
    a_long_list_in_the_dialog_is_cut() {
        const names = []; for (let i = 0; i < 30; i++) names.push('tag' + i);
        const del = M.applyPlan(advice({ action: { type: 'delete_tags', names }, objects: [] }));
        has(del.lines[0], 'and 10 more', 'the list is cut');
        has(del.confirm, '30 tags', 'the count is of all of them');
    },

    // ---- the Dismiss dialog ----
    dismiss_offers_four_reaches() {
        const opts = M.dismissOptions(advice());
        eq(opts.map((o) => o.value), ['advice', 'object', 'folder', 'kind'], 'the four scopes of a rule');
        eq(opts.map((o) => o.disabled), [false, false, false, false], 'all usable');
        has(opts[1].label, 'this note', 'the noun follows the object');
        has(opts[2].label, '/Ops', 'the folder is named');
        has(opts[2].hint, 'into it can still be suggested', 'and what the rule does not cover');
        const move = M.dismissOptions(advice({ kind: 'move_article', action: { type: 'move_article', articleId: A1, toPath: '/Archive' },
            objects: [{ role: 'subject', type: 'article', id: A1, title: 'Note', treePath: '/Inbox' }, { role: 'target', type: 'folder', path: '/Archive' }] }));
        has(move[2].label, '/Inbox', 'the folder the note is in, not where it would go');
        const pair = M.dismissOptions(advice({ kind: 'duplicate', objects: [{ role: 'target', type: 'article', id: A1, title: 'Keep', treePath: '/A' },
            { role: 'other', type: 'article', id: A2, title: 'Other', treePath: '/B' }] }));
        has(pair[2].label, '/B', 'the kept note of a merge is its destination');
        has(opts[3].hint, 'exact duplicate', 'the kind is named');
        const tags = M.dismissOptions(advice({ kind: 'merge_tags', objects: [{ type: 'tag', name: 'x', articleCount: 2 }] }));
        has(tags[1].label, 'this tag', 'a tag');
        eq(tags[2].disabled, true, 'no folder, no folder rule');
        const bare = M.dismissOptions(advice({ objects: [] }));
        eq([bare[1].disabled, bare[2].disabled], [true, true], 'nothing it is about');
        eq(M.expiryOptions.map((o) => o.value), ['180', 'never'], 'the two lengths');
    },

    // ---- the live feed ----
    the_feed_adds_what_is_new_in_order_and_never_twice() {
        const first = M.mergeFeed([], [{ id: 'b', seq: 2 }, { id: 'a', seq: 1 }]);
        eq(first.added.map((x) => x.id), ['a', 'b'], 'in the order of seq');
        const second = M.mergeFeed(first.items, [{ id: 'b', seq: 2 }, { id: 'c', seq: 3 }, null, {}]);
        eq([second.added.map((x) => x.id), second.items.map((x) => x.id)], [['c'], ['a', 'b', 'c']], 'a repeat is not added again');
        eq(M.mergeFeed(null, null), { items: [], added: [] }, 'nothing');
        eq(M.lastSeqOf([{ seq: 4 }, { seq: 9 }, { seq: 2 }], 0), 9, 'the last seq');
        eq(M.lastSeqOf([], 7), 7, 'nothing new keeps the old one');
        eq(M.lastSeqOf([{ seq: 3 }], 10), 10, 'never goes back');
    },
    a_long_queue_is_shown_faster_but_never_all_at_once() {
        const d = [1, 3, 7, 13, 50].map(M.revealDelay);
        eq(d, [320, 320, 160, 80, 80], 'the delays');
        assert(d.every((x) => x >= 80), 'always one by one');
    },

    // ---- failures ----
    errors_are_said_in_the_words_of_the_node() {
        // the API's ErrorResponse is { error, code }
        const e = new Error('The node answered 409.');
        e.status = 409;
        e.body = { error: 'The folder is read-only for you.', code: 'not_applicable' };
        eq(M.errorText(e), 'The folder is read-only for you.', 'the words of the node come first, even when there is a code');
        const problem = new Error('x');
        problem.body = { title: 'Conflict', detail: 'It is locked.' };
        eq(M.errorText(problem), 'It is locked.', 'ProblemDetails: the detail');
        const proxy = new Error('x');
        proxy.body = { message: 'Said by a proxy.' };
        eq(M.errorText(proxy), 'Said by a proxy.', 'a message');
        const coded = new Error('x');
        coded.body = { code: 'already_running' };
        has(M.errorText(coded), 'already going on', 'a code with no words');
        eq(M.errorText(new Error('The node did not answer.')), 'The node did not answer.', 'a plain error');
        eq(M.errorText(null), 'Something went wrong.', 'nothing');
        eq(M.loadFailure('the history', new Error('boom')), 'Could not load the history: boom', 'a load that failed');
    },
    every_function_tolerates_a_stub_answer() {
        const empty = [undefined, null, {}, []];
        empty.forEach((x) => {
            M.statusView(x, x, NOW);
            M.warnings(x);
            M.adviceView(x, x);
            M.runStats(x, x, NOW);
            M.outcomeInfo(x);
            M.triggerInfo(x);
            M.counterLine(x);
            M.limitsList(x);
            M.objectLinks(x);
            M.evidenceLines(x);
            M.applyPlan(x);
            M.dismissOptions(x);
            M.mergeFeed(x, x);
            M.lastSeqOf(x);
            M.errorText(x);
            M.buildStartRequest(x);
            M.startForm(x, x);
            M.runWhat(x);
            M.foundBy(x, x);
            M.conflictText(x);
            M.isClosedConflict(x);
            M.ruleBookView(x);
            M.adviceSignature(x);
            M.runSignature(x);
            M.adviceIdFromQuery(x);
            M.formatWhen(x);
            M.relativeWhen(x, NOW);
        });
    }
};

try {
    if (!scenarios[scenario]) say(false, 'no such scenario: ' + scenario);
    scenarios[scenario]();
    say(true, 'ok');
} catch (e) {
    say(false, e && e.message ? e.message : String(e));
}
