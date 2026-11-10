// Runs wwwroot/js/pages/dream-compare-model.js (what the "Compare trial runs" section of the Dream page says: which runs may be picked, how the columns are lettered
// and shuffled, what a cell and a row say, what each run found alone, the filters, the sample to judge by hand) in a bare context, with dream-model.js loaded
// before it as the page does, and prints one JSON line {"ok": true|false, "detail": "..."} for the scenario named on the command line. The scenarios are the ones
// of DreamCompareModelTests.cs.
//
//   node dream-compare-harness.js <path of dream-model.js> <path of dream-compare-model.js> <scenario>
'use strict';
const fs = require('fs');
const vm = require('vm');

const [, , modelPath, comparePath, scenario] = process.argv;

const sandbox = { window: {} };
vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(modelPath, 'utf8'), sandbox, { filename: modelPath });
vm.runInContext(fs.readFileSync(comparePath, 'utf8'), sandbox, { filename: comparePath });
const C = sandbox.window.bmbDreamCompare;

function say(ok, detail) { console.log(JSON.stringify({ ok, detail })); process.exit(0); }
function assert(cond, message) { if (!cond) throw new Error(message); }
function eq(actual, expected, what) {
    const a = JSON.stringify(actual), e = JSON.stringify(expected);
    if (a !== e) throw new Error(what + ': expected ' + e + ' but got ' + a);
}
function has(text, part, what) { if (String(text).indexOf(part) < 0) throw new Error(what + ': "' + part + '" is not in "' + text + '"'); }
function lacks(text, part, what) { if (String(text).indexOf(part) >= 0) throw new Error(what + ': "' + part + '" should not be in "' + text + '"'); }

const NOW = Date.parse('2026-11-10T12:00:00Z');
const ago = (seconds) => new Date(NOW - seconds * 1000).toISOString();

const R1 = '11111111-1111-1111-1111-111111111111';
const R2 = '22222222-2222-2222-2222-222222222222';
const R3 = '33333333-3333-3333-3333-333333333333';
const A1 = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
const A2 = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';

function run(id, extra) {
    return Object.assign({
        id, trigger: 'manual', mode: 'trial', state: 'finished', outcome: 'completed', useModel: true, modelLabel: 'Model ' + id.slice(0, 1),
        createdAt: ago(3600), startedAt: ago(3600), finishedAt: ago(3000),
        counters: { candidatesExamined: 10, adviceCreated: 3, modelCalls: 10, promptTokens: 48000, completionTokens: 2500, errors: 0 },
        limits: { maxAdvice: 50, maxMinutes: 10, maxModelCalls: 200, maxTokens: 1500000 }
    }, extra || {});
}

// What one run made of one candidate, as GET /compare gives it.
function res(verdict, score, kind, outcome, summary) {
    return { verdict, kind: kind || null, score, summary: summary === undefined ? (verdict ? verdict + ' because' : null) : summary, outcome: outcome || (kind ? 'advice' : 'nothing') };
}
const failed = () => ({ verdict: null, kind: null, score: null, summary: null, outcome: 'error' });

function row(key, results, extra) {
    return Object.assign({
        candidateKey: key, task: 'pair_verdict',
        objects: [{ type: 'article', id: A1, title: 'Alpha', treePath: '/X' }, { type: 'article', id: A2, title: 'Beta', treePath: '/Y' }],
        results
    }, extra || {});
}

function response(rows, extra) {
    return Object.assign({ runs: [run(R1), run(R2), run(R3)], rows, common: 0, agreement: 0, agreementBasis: 0, runsWithoutLog: [] }, extra || {});
}

const ORDER = [R1, R2, R3];
const view = (r, order) => C.rowView(r, order || ORDER, 0);

// A random source that gives the numbers it is told to, then repeats the last one.
function seq(...numbers) { let i = 0; return () => numbers[Math.min(i++, numbers.length - 1)]; }

const scenarios = {
    // ---- the columns ----
    letters_are_a_b_c() {
        eq(C.letters(3), ['A', 'B', 'C'], 'three');
        eq(C.letters(2), ['A', 'B'], 'two');
        eq(C.letters(0), [], 'none');
    },

    shuffle_is_a_fair_permutation_and_leaves_the_input_alone() {
        const input = [R1, R2, R3];
        const seen = new Set();
        // Every way the dice can fall: each of the 3! orders is reachable.
        for (const a of [0, 0.5, 0.99]) for (const b of [0, 0.5, 0.99]) {
            const out = C.shuffleIds(input, seq(a, b));
            eq(out.slice().sort(), input.slice().sort(), 'a permutation');
            seen.add(out.join());
        }
        eq(seen.size, 6, 'all six orders are reachable');
        eq(input, [R1, R2, R3], 'the input is not changed');
        eq(C.shuffleIds([R1], seq(0.9)), [R1], 'one run');
        eq(C.shuffleIds(null, seq(0.1)), [], 'a stub');
        // A source that misbehaves (1 or more, below 0) cannot put an element outside the list.
        eq(C.shuffleIds([R1, R2, R3], seq(1, 7)).slice().sort(), [R1, R2, R3], 'a rogue source');
        eq(C.shuffleIds([R1, R2, R3], seq(-1, -1)).slice().sort(), [R1, R2, R3], 'a negative source');
    },

    reshuffle_always_gives_another_order() {
        const same = () => 0.999;   // the dice that never change anything for a 2-run list ... or do
        for (const order of [[R1, R2], [R1, R2, R3], [R3, R1, R2]]) {
            const next = C.reshuffle(order, seq(0.999, 0.999, 0.999));
            assert(!C.sameOrder(next, order), 'the same order came back for ' + order.join());
            eq(next.slice().sort(), order.slice().sort(), 'still the same runs');
        }
        eq(C.reshuffle([R1], same), [R1], 'one run has one order');
        eq(C.reshuffle([], same), [], 'none');
    },

    // ---- picking ----
    toggling_keeps_to_three_runs_and_never_twice() {
        let sel = [];
        sel = C.toggleSelection(sel, R1, true);
        sel = C.toggleSelection(sel, R1, true);
        eq(sel, [R1], 'twice is once');
        sel = C.toggleSelection(sel, R2, true);
        sel = C.toggleSelection(sel, R3, true);
        eq(sel, [R1, R2, R3], 'three');
        eq(C.toggleSelection(sel, 'x4', true), [R1, R2, R3], 'a fourth is refused');
        eq(C.toggleSelection(sel, R2, false), [R1, R3], 'untick');
        eq(C.toggleSelection(sel, 'nope', false), [R1, R2, R3], 'unticking what was not ticked');
        eq(C.toggleSelection(null, R1, true), [R1], 'a stub');
    },

    the_picker_says_what_is_left_to_do() {
        const runs = [run(R1), run(R2), run(R3)];
        let s = C.pickState([], runs);
        eq([s.count, s.canCompare, s.full], [0, false, false], 'none');
        has(s.hint, 'two or three', 'none');
        s = C.pickState([R1], runs);
        eq(s.canCompare, false, 'one is not enough');
        has(s.hint, 'one more', 'one');
        s = C.pickState([R1, R2], runs);
        eq([s.canCompare, s.full], [true, false], 'two');
        s = C.pickState([R1, R2, R3], runs);
        eq([s.canCompare, s.full], [true, true], 'three');
        has(s.hint, 'most', 'three');
    },

    the_picker_warns_about_a_run_that_is_going_or_empty() {
        const runs = [run(R1, { state: 'running', outcome: null, finishedAt: null }), run(R2, { counters: { candidatesExamined: 0 } }), run(R3)];
        const s = C.pickState([R1, R2, R3], runs);
        eq(s.warnings.length, 2, 'two different warnings');
        has(s.warnings.join(' '), 'still going', 'running');
        has(s.warnings.join(' '), 'examined nothing', 'empty');
        eq(C.pickState([R3], runs).warnings, [], 'a good run warns of nothing');
    },

    the_latest_pick_is_the_newest_finished_runs_that_examined_something() {
        const runs = [
            run('a1', { state: 'running', outcome: null }), run('b2'), run('c3', { counters: { candidatesExamined: 0 } }), run('d4'), run('e5'), run('f6')
        ];
        eq(C.latestPick(runs), ['b2', 'd4', 'e5'], 'newest first, three at most');
        eq(C.latestPick([run('x')]), ['x'], 'one');
        eq(C.latestPick(null), [], 'a stub');
    },

    a_run_in_the_picker_hides_its_model_until_names_are_shown() {
        const r = run(R1, { modelLabel: 'Secret Model 9' });
        const hidden = C.runOption(r, false);
        const shown = C.runOption(r, true);
        lacks(JSON.stringify(hidden), 'Secret Model 9', 'hidden');
        has(shown.title, 'Secret Model 9', 'shown');
        has(hidden.title, 'Trial run', 'the time is still there to tell runs apart');
        has(hidden.sub, '10 candidates examined', 'the numbers are there');
        has(hidden.sub, '50.5k tokens', 'tokens');
        eq(hidden.id, R1, 'id');
        eq(hidden.finished, true, 'finished');
        has(C.runOption(run(R2, { useModel: false, modelLabel: null }), true).title, 'rules only', 'a run without a model');
        has(C.runOption(run(R2, { useRules: false, modelLabel: 'Solo' }), true).title, 'Solo (AI only, no rules)', 'a run of the model alone');
        has(C.runOption(run(R2, { modelLabel: 'Pair' }), true).title, 'Pair + rules', 'a run of the model with the rules');
        eq(C.columnTitle('A', run(R2, { useRules: false, modelLabel: 'Solo' }), true).sub, 'Solo (AI only, no rules)', 'the column of a run of the model alone');
        eq(C.columnTitle('A', run(R2, { useRules: false, modelLabel: 'Solo' }), false).sub, '', 'and nothing while the comparison is blind');
        has(C.runOption(run(R3, { state: 'running', outcome: null }), false).note, 'Still going', 'a run going on');
        has(C.runOption(run(R3, { counters: { candidatesExamined: 0 } }), false).note, 'nothing', 'an empty run');
    },

    // ---- a cell ----
    a_cell_is_advice_nothing_error_or_not_examined() {
        const advice = C.cellView(res('duplicate', 91, 'duplicate'));
        eq([advice.tone, advice.text, advice.variant, advice.band, advice.score, advice.kindLabel], ['advice', 'Duplicate', 'primary', 'high', 91, 'Duplicate'], 'advice');
        const nothing = C.cellView(res('unrelated', 12));
        eq([nothing.tone, nothing.text, nothing.variant, nothing.band, nothing.kindLabel], ['nothing', 'Unrelated', 'neutral', 'low', ''], 'nothing');
        const error = C.cellView(failed());
        eq([error.tone, error.text, error.variant, error.score], ['error', 'Failed', 'danger', null], 'error');
        const none = C.cellView(null);
        eq([none.tone, none.empty, none.text], ['none', true, 'Not examined'], 'not examined');
        eq(C.cellView({ verdict: 'overlap', kind: 'overlap', score: 65, summary: 's', outcome: 'advice' }).band, 'medium', 'medium band');
    },

    a_cell_of_an_older_node_without_an_outcome_is_still_read() {
        eq(C.cellView({ verdict: 'duplicate', kind: 'duplicate', score: 80 }).tone, 'advice', 'a kind means advice');
        eq(C.cellView({ verdict: 'related', kind: null, score: 10 }).tone, 'nothing', 'a verdict and no kind means nothing');
        eq(C.cellView({ verdict: null, kind: null, score: null }).tone, 'error', 'no verdict means it failed');
    },

    // ---- a row ----
    a_row_everyone_agrees_on_is_marked_so() {
        const v = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: res('duplicate', 85, 'duplicate') }));
        eq([v.agree, v.disagree, v.odd, v.foundOnly, v.common], [true, false, [], -1, true], 'agreement');
        eq(v.flags.map((f) => f.key), ['agree'], 'flags');
        eq([v.scoreMax, v.scoreMin, v.spread], [90, 80, 10], 'scores');
    },

    a_disagreement_marks_the_odd_one_out_and_with_no_majority_all_of_them() {
        const two = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: res('overlap', 60, 'overlap') }));
        eq([two.disagree, two.odd], [true, [2]], 'two against one: the one is odd');
        const split = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('overlap', 80, 'overlap'), [R3]: res('contradiction', 70, 'contradiction') }));
        eq(split.odd, [0, 1, 2], 'all different: no majority');
        const pair = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('related', 20) }), [R1, R2]);
        eq([pair.disagree, pair.odd], [true, [0, 1]], 'two columns: both');
        eq(pair.flags.map((f) => f.key), ['only-one'], 'one made advice, the other did not');
    },

    advice_from_one_run_only_names_its_letter() {
        const v = view(row('k', { [R1]: res('related', 20), [R2]: res('duplicate', 88, 'duplicate'), [R3]: res('unrelated', 5) }));
        eq([v.foundOnly, v.adviceDiffers, v.disagree], [1, true, true], 'the second run alone');
        eq(v.flags[0].text, 'Only B found advice', 'the flag');
        // Two runs making different advice is a disagreement, but nobody found it alone.
        const both = view(row('k', { [R1]: res('duplicate', 88, 'duplicate'), [R2]: res('overlap', 70, 'overlap'), [R3]: res('related', 5) }));
        eq([both.foundOnly, both.adviceDiffers], [-1, true], 'two found it');
    },

    a_run_that_did_not_look_or_failed_is_not_a_voice_in_the_vote() {
        const partial = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 85, 'duplicate'), [R3]: null }));
        eq([partial.agree, partial.common, partial.missing], [true, false, [2]], 'two agree, one never looked');
        eq(partial.flags.map((f) => f.text), ['Same verdict', 'Not examined by C'], 'flags');
        const err = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: failed(), [R3]: res('duplicate', 80, 'duplicate') }));
        eq([err.agree, err.disagree, err.errors], [true, false, [1]], 'an error is no vote');
        eq(err.flags.map((f) => f.text), ['Same verdict', 'Failed in B'], 'flags');
        const lone = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: null, [R3]: null }));
        eq([lone.agree, lone.disagree, lone.examined], [false, false, 1], 'one voice is not agreement');
        const nobody = view(row('k', { [R1]: null, [R2]: null, [R3]: null }));
        eq(nobody.flags.map((f) => f.text), ['Not examined'], 'nobody');
    },

    a_row_names_its_objects_with_links() {
        const v = view(row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 90, 'duplicate'), [R3]: null }));
        eq(v.objects.map((o) => o.label), ['Alpha', 'Beta'], 'titles');
        has(v.objects[0].href, 'Article/View?id=' + A1, 'link');
        eq(v.key, 'k', 'key');
    },

    // ---- the table ----
    names_stay_out_of_the_table_until_they_are_asked_for() {
        const r = response([row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: null })],
            { runs: [run(R1, { modelLabel: 'Alpha Model' }), run(R2, { modelLabel: 'Beta Model' }), run(R3, { modelLabel: 'Gamma Model' })] });
        const blind = C.tableView(r, ORDER, false);
        const titles = JSON.stringify(blind.columns.map((c) => c.title));
        for (const name of ['Alpha Model', 'Beta Model', 'Gamma Model']) lacks(titles, name, 'blind titles');
        eq(blind.columns.map((c) => c.title.text), ['Run A', 'Run B', 'Run C'], 'letters only');
        const open = C.tableView(r, ORDER, true);
        eq(open.columns.map((c) => c.title.sub), ['Alpha Model + rules', 'Beta Model + rules', 'Gamma Model + rules'], 'names on request, with the rules that ran beside the model');
        eq(open.columns.map((c) => c.letter), ['A', 'B', 'C'], 'letters stay');
    },

    the_columns_follow_the_order_given_not_the_order_of_the_response() {
        const r = response([row('k', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('overlap', 50, 'overlap'), [R3]: res('related', 5) })]);
        const t = C.tableView(r, [R3, R1, R2], true);
        eq(t.columns.map((c) => c.id), [R3, R1, R2], 'columns');
        eq(t.rows[0].cells.map((c) => c.verdict), ['related', 'duplicate', 'overlap'], 'cells follow');
        eq(t.columns.map((c) => c.letter), ['A', 'B', 'C'], 'lettered in that order');
        // A run the order forgot is still a column, at the end; an id the response does not hold is not one.
        const t2 = C.tableView(r, [R2, 'ghost'], false);
        eq(t2.columns.map((c) => c.id), [R2, R1, R3], 'forgotten runs follow');
    },

    each_column_counts_what_it_found_and_what_only_it_found() {
        const rows = [
            row('k1', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('related', 20), [R3]: res('related', 10) }),
            row('k2', { [R1]: res('related', 20), [R2]: res('duplicate', 70, 'duplicate'), [R3]: failed() }),
            row('k3', { [R1]: res('overlap', 60, 'overlap'), [R2]: res('overlap', 64, 'overlap'), [R3]: null }),
            row('k4', { [R1]: res('related', 8), [R2]: null, [R3]: null })
        ];
        const t = C.tableView(response(rows), ORDER, false);
        const [a, b, c] = t.columns;
        eq([a.examined, a.advice, a.foundAlone, a.errors, a.meanScore], [4, 2, 1, 0, 45], 'A');   // (90+20+60+8)/4 = 44.5 -> 45
        eq([b.examined, b.advice, b.foundAlone, b.errors, b.meanScore], [3, 2, 1, 0, 51], 'B');   // (20+70+64)/3 = 51.3
        eq([c.examined, c.advice, c.foundAlone, c.errors, c.meanScore], [2, 0, 0, 1, 10], 'C');
        eq(t.disagreements, 2, 'k1 and k2 disagree; k3 agrees and k4 has one voice');
        eq(t.partial, 2, 'k3 and k4 were not examined by every run');
        eq(c.facts.map((f) => f.label), ['Model calls', 'Tokens in / out', 'Time', 'Ended'], 'the facts of a run');
    },

    agreement_is_a_percent_of_what_every_run_gave_a_verdict_on() {
        const none = C.tableView(response([]), ORDER, false);
        eq([none.agreementText, none.common], ['-', 0], 'nothing to compare');
        has(none.agreementNote, 'no candidate', 'nothing note');
        const some = C.tableView(response([row('k', { [R1]: res('a', 1), [R2]: res('a', 1), [R3]: res('a', 1) })], { common: 7, agreement: 0.6153, agreementBasis: 5 }), ORDER, false);
        eq([some.agreementText, some.common, some.agreementBasis], ['62%', 7, 5], 'the server numbers are used as they are');
        has(some.agreementNote, '5 candidates', 'the basis is said');
    },

    a_run_without_a_log_is_named_by_its_letter() {
        const t = C.tableView(response([], { runsWithoutLog: [R2] }), [R1, R2, R3], false);
        eq(t.journalGone, ['B'], 'letter');
        has(C.emptyWords(t), 'run B', 'words');
        has(C.emptyWords(t), 'seven days', 'why');
        eq(C.tableView(response([]), ORDER, false).journalGone, [], 'none');
        has(C.emptyWords(C.tableView(response([]), ORDER, false)), 'no examined candidates', 'an empty table without a lost log');
        eq(C.emptyWords(C.tableView(response([row('k', { [R1]: null, [R2]: null, [R3]: null })]), ORDER, false)), '', 'a table with rows needs no words');
    },

    // ---- filters, sorting, the sample ----
    the_filters_pick_the_rows_they_name() {
        const rows = [
            row('agree', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: res('duplicate', 85, 'duplicate') }),
            row('differ', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('overlap', 60, 'overlap'), [R3]: res('overlap', 62, 'overlap') }),
            row('alone', { [R1]: res('related', 10), [R2]: res('unrelated', 4), [R3]: res('contradiction', 77, 'contradiction') }),
            row('short', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('duplicate', 91, 'duplicate'), [R3]: null }),
            row('broken', { [R1]: res('related', 10), [R2]: failed(), [R3]: res('related', 12) })
        ];
        const t = C.tableView(response(rows), ORDER, false);
        const keys = (mode) => C.filterRows(t.rows, mode).map((v) => v.key);
        eq(keys('all'), ['agree', 'differ', 'alone', 'short', 'broken'], 'all');
        eq(keys('disagree'), ['differ', 'alone'], 'different verdicts');
        eq(keys('advice-differs'), ['alone'], 'some made advice and some did not');
        eq(keys('only-one'), ['alone'], 'one only');
        eq(keys('partial'), ['short'], 'not examined by every run');
        eq(keys('errors'), ['broken'], 'failed somewhere');
        eq(keys('nonsense'), ['agree', 'differ', 'alone', 'short', 'broken'], 'an unknown filter shows everything');
        eq(t.counts.disagree, 2, 'counts agree with the filters');
        eq(t.counts.all, 5, 'all');
        eq(C.FILTERS.map((f) => f.value), ['all', 'disagree', 'advice-differs', 'only-one', 'partial', 'errors', 'sample'], 'filters offered');
    },

    the_sample_to_judge_by_hand_is_ten_disagreements_and_ten_top_scores() {
        const rows = [];
        for (let i = 0; i < 14; i++) rows.push(row('d' + i, { [R1]: res('duplicate', 20 + 5 * i, 'duplicate'), [R2]: res('related', 10), [R3]: res('related', 10) }));   // disagreements, the score gap (10 + 5i) grows with i
        for (let i = 0; i < 12; i++) rows.push(row('s' + i, { [R1]: res('overlap', 30 + i * 5, 'overlap'), [R2]: res('overlap', 30 + i * 5, 'overlap'), [R3]: res('overlap', 30 + i * 5, 'overlap') }));   // agreements, rising scores
        const t = C.tableView(response(rows), ORDER, false);
        const sample = C.reviewSample(t.rows);
        eq(sample.length, 20, 'twenty rows');
        const keys = sample.map((v) => v.key);
        const disagreements = keys.filter((k) => k[0] === 'd');
        const agreements = keys.filter((k) => k[0] === 's');
        eq(disagreements.length + agreements.length, 20, 'only these two kinds');
        // The ten disagreements with the widest score gap: the gap of d_i is 10 + 5i, so the ten biggest i.
        eq(disagreements.slice().sort(), ['d4', 'd5', 'd6', 'd7', 'd8', 'd9', 'd10', 'd11', 'd12', 'd13'].sort(), 'the widest gaps');
        // The ten highest scores among the others: the 12 agreements rise from 30 to 85 and the four left-over disagreements (d0..d3) top out at 35, so s2..s11 are the ten.
        eq(agreements.length, 10, 'ten more');
        assert(agreements.indexOf('s11') >= 0 && agreements.indexOf('s2') >= 0 && agreements.indexOf('s1') < 0, 'the highest scores: ' + agreements.join());
        eq(keys, sample.slice().sort((x, y) => x.index - y.index).map((v) => v.key), 'in table order');
        eq(C.filterRows(t.rows, 'sample').length, 20, 'the filter gives the same sample');
        eq(C.reviewSample([]).length, 0, 'nothing');
    },

    the_sorts_put_the_biggest_gaps_or_the_highest_scores_first() {
        const rows = [
            row('flat', { [R1]: res('duplicate', 80, 'duplicate'), [R2]: res('duplicate', 80, 'duplicate'), [R3]: res('duplicate', 80, 'duplicate') }),
            row('gap', { [R1]: res('duplicate', 90, 'duplicate'), [R2]: res('related', 10), [R3]: res('related', 12) }),
            row('high', { [R1]: res('duplicate', 99, 'duplicate'), [R2]: res('duplicate', 97, 'duplicate'), [R3]: res('duplicate', 96, 'duplicate') }),
            row('none', { [R1]: failed(), [R2]: null, [R3]: null })
        ];
        const t = C.tableView(response(rows), ORDER, false);
        const keys = (sort) => C.sortRows(t.rows, sort).map((v) => v.key);
        eq(keys('order'), ['flat', 'gap', 'high', 'none'], 'in the order examined');
        eq(keys('spread'), ['gap', 'high', 'flat', 'none'], 'biggest gap first, ties in order');
        eq(keys('top'), ['high', 'gap', 'flat', 'none'], 'highest score first, rows with no score last');
        eq(keys('whatever'), ['flat', 'gap', 'high', 'none'], 'an unknown sort keeps the order');
    },

    // ---- stubs ----
    every_function_tolerates_a_stub_answer() {
        const stubs = [null, undefined, {}, [], 'x', 7, { rows: 'x', runs: 3 }, { runs: [null], rows: [null, {}, { results: 5 }] }];
        stubs.forEach((x) => {
            C.letters(x);
            C.shuffleIds(x, Math.random);
            C.reshuffle(x, Math.random);
            C.runOption(x, true);
            C.toggleSelection(x, 'a', true);
            C.pickState(x, x);
            C.latestPick(x);
            C.cellView(x);
            C.rowView(x, x, 0);
            C.reviewSample(x);
            C.filterRows(x, 'sample');
            C.filterCounts(x);
            C.sortRows(x, 'spread');
            C.columnTitle('A', x, true);
            C.runFacts(x);
            C.emptyWords(C.tableView(x, x, false));
            C.tableView(x, x, true);
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
