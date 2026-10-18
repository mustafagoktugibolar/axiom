<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue';
import {
  ApiError, getCoverage, getGovernanceHealth, getSemanticMetrics,
  type CoverageFilter, type CoverageReport, type GovernanceHealth, type HealthItem, type SemanticMetrics,
} from '../api';

const emit = defineEmits<{ (e: 'open', id: string): void; (e: 'open-exceptions'): void }>();

const PAGE = 20;
type Key = 'staleRecords' | 'recordsWithoutOwners' | 'deprecatedStillReferenced' | 'expiringExceptions';
const sections: { key: Key; title: string; help: string; dateLabel?: string }[] = [
  { key: 'staleRecords', title: 'Stale / review-due records', help: 'Accepted records past their review date.', dateLabel: 'Review was due' },
  { key: 'recordsWithoutOwners', title: 'Ownerless records', help: 'Records nobody owns, so nobody is asked to review them.' },
  { key: 'deprecatedStillReferenced', title: 'Deprecated but still referenced', help: 'Deprecated or superseded records that live records still depend on.' },
  { key: 'expiringExceptions', title: 'Exceptions expiring', help: 'Accepted exceptions about to end their waiver.', dateLabel: 'Expires' },
];

interface Sec { items: HealthItem[]; count: number; skip: number; loading: boolean; error: string }
const blank = (): Sec => ({ items: [], count: 0, skip: 0, loading: true, error: '' });
const sec = reactive<Record<Key, Sec>>({ staleRecords: blank(), recordsWithoutOwners: blank(), deprecatedStillReferenced: blank(), expiringExceptions: blank() });
const meta = ref<{ snapshotId: string | null; evaluatedAt: string; expiryWindowDays: number } | null>(null);

function errText(e: unknown) {
  if (e instanceof ApiError && e.status === 403) return 'You do not have the rights to see this section.';
  return (e as Error).message;
}

// The endpoint applies skip/take to all four lists alike, so each section asks for its own page and reads only its list.
async function loadSection(key: Key, skip: number) {
  const s = sec[key];
  s.loading = true; s.error = '';
  try {
    const h: GovernanceHealth = await getGovernanceHealth(skip, PAGE);
    meta.value = { snapshotId: h.snapshotId, evaluatedAt: h.evaluatedAt, expiryWindowDays: h.expiryWindowDays };
    s.items = h[key]; s.skip = skip; s.count = h.counts[key];
  } catch (e) { s.error = errText(e); } finally { s.loading = false; }
}

const cov = reactive({ filter: 'all' as CoverageFilter, skip: 0, data: null as CoverageReport | null, loading: true, error: '' });
const covPage = 20;
async function loadCoverage(skip = 0) {
  cov.loading = true; cov.error = '';
  try { cov.data = await getCoverage(cov.filter, skip, covPage); cov.skip = skip; } catch (e) { cov.error = errText(e); } finally { cov.loading = false; }
}

const sem = reactive({ data: null as SemanticMetrics | null, loading: true, error: '', denied: false });
async function loadSemantic() {
  sem.loading = true; sem.error = ''; sem.denied = false;
  try { sem.data = await getSemanticMetrics(); }
  catch (e) { if (e instanceof ApiError && e.status === 403) sem.denied = true; else sem.error = errText(e); }
  finally { sem.loading = false; }
}

onMounted(() => { sections.forEach(s => loadSection(s.key, 0)); loadCoverage(); loadSemantic(); });

const pct = (r: number) => `${(r * 100).toFixed(1)}%`;
const date = (s?: string | null) => (s ? new Date(s).toLocaleDateString() : '');
const statusText: Record<string, string> = { Uncovered: 'Uncovered: no record applies', AllStale: 'All applicable records stale', Covered: 'Covered' };
const statusClass = (s: string) => (s === 'Covered' ? 'ok' : s === 'AllStale' ? 'warn' : 'err');
const statusMark = (s: string) => (s === 'Covered' ? '✓' : '⚠');
const filters: { id: CoverageFilter; label: string }[] = [
  { id: 'all', label: 'All' }, { id: 'gaps', label: 'Gaps (uncovered or all stale)' }, { id: 'uncovered', label: 'Uncovered' },
  { id: 'allstale', label: 'All stale' }, { id: 'covered', label: 'Covered' },
];
const covTo = computed(() => (cov.data ? Math.min(cov.data.skip + cov.data.take, cov.data.totalRepositories) : 0));
</script>

<template>
  <h1>Governance health</h1>
  <p class="muted">Where governance needs attention. This page only reports: it changes nothing.
    <template v-if="meta">Snapshot <code>{{ meta.snapshotId ?? 'none published' }}</code>, evaluated {{ new Date(meta.evaluatedAt).toLocaleString() }}.</template></p>

  <section v-for="s in sections" :key="s.key" :aria-labelledby="`h-${s.key}`">
    <h2 :id="`h-${s.key}`">{{ s.title }} <span class="muted">({{ sec[s.key].count }})</span></h2>
    <p class="muted">{{ s.help }}<template v-if="s.key === 'expiringExceptions' && meta"> Window: {{ meta.expiryWindowDays }} days.</template></p>
    <p v-if="sec[s.key].loading" role="status">Loading...</p>
    <p v-else-if="sec[s.key].error" class="err" role="alert">Could not load: {{ sec[s.key].error }} <button @click="loadSection(s.key, sec[s.key].skip)">Retry</button></p>
    <p v-else-if="!sec[s.key].items.length && sec[s.key].skip === 0" class="ok">Nothing to report.</p>
    <p v-else-if="!sec[s.key].items.length" class="muted">No more entries.</p>
    <table v-else>
      <caption class="muted">{{ s.title }}, showing {{ sec[s.key].skip + 1 }} to {{ sec[s.key].skip + sec[s.key].items.length }} of {{ sec[s.key].count }}</caption>
      <thead><tr>
        <th scope="col">Record</th><th scope="col">Kind</th><th scope="col">Owners</th>
        <th v-if="s.dateLabel" scope="col">{{ s.dateLabel }}</th>
        <th v-if="s.key === 'deprecatedStillReferenced'" scope="col">Referenced by</th>
      </tr></thead>
      <tbody>
        <tr v-for="i in sec[s.key].items" :key="i.recordId">
          <td>
            <button class="link" @click="emit('open', i.recordId)">{{ i.recordId }}</button>
            <button v-if="s.key === 'expiringExceptions'" class="link" style="margin-left:.75rem" @click="emit('open-exceptions')">Exceptions view</button>
            <div class="muted">{{ i.title }}</div>
          </td>
          <td>{{ i.kind }}</td>
          <td>{{ i.owners.length ? i.owners.join(', ') : 'No owner' }}</td>
          <td v-if="s.dateLabel">{{ date(i.dueAt) }}</td>
          <td v-if="s.key === 'deprecatedStillReferenced'">
            <button v-for="r in i.referencedBy" :key="r" class="link" style="margin-right:.5rem" @click="emit('open', r)">{{ r }}</button>
          </td>
        </tr>
      </tbody>
    </table>
    <div v-if="!sec[s.key].error && sec[s.key].count > PAGE" class="row">
      <button :disabled="sec[s.key].loading || sec[s.key].skip === 0" @click="loadSection(s.key, Math.max(0, sec[s.key].skip - PAGE))">Previous page</button>
      <button :disabled="sec[s.key].loading || sec[s.key].skip + PAGE >= sec[s.key].count" @click="loadSection(s.key, sec[s.key].skip + PAGE)">Next page</button>
    </div>
  </section>

  <section aria-labelledby="h-cov">
    <h2 id="h-cov">Coverage by repository</h2>
    <p class="muted">How much governance reaches each repository of the system graph.</p>
    <div class="row">
      <label>Show <select v-model="cov.filter" @change="loadCoverage(0)">
        <option v-for="f in filters" :key="f.id" :value="f.id">{{ f.label }}</option>
      </select></label>
    </div>
    <p v-if="cov.loading" role="status">Loading...</p>
    <p v-else-if="cov.error" class="err" role="alert">Could not load: {{ cov.error }} <button @click="loadCoverage(cov.skip)">Retry</button></p>
    <template v-else-if="cov.data">
      <p>Repositories {{ cov.data.skip + 1 }} to {{ covTo }} of {{ cov.data.totalRepositories }} examined:
        {{ cov.data.summary.uncovered }} uncovered, {{ cov.data.summary.allStale }} all stale, {{ cov.data.summary.covered }} covered.
        The filter applies within each page of repositories.</p>
      <p v-if="!cov.data.items.length" class="muted">No repositories on this page match the filter.</p>
      <table v-else>
        <caption class="muted">Governance coverage per repository</caption>
        <thead><tr><th scope="col">Repository</th><th scope="col">Status</th><th scope="col">Applicable records</th><th scope="col">Stale</th><th scope="col">Bound rules</th><th scope="col">Topology gaps</th></tr></thead>
        <tbody>
          <tr v-for="r in cov.data.items" :key="r.repository">
            <th scope="row">{{ r.repository }}</th>
            <td :class="statusClass(r.status)"><span aria-hidden="true">{{ statusMark(r.status) }}</span> {{ statusText[r.status] ?? r.status }}</td>
            <td>
              {{ r.applicableRecords }}
              <details v-if="r.recordIds.length"><summary>Show records</summary>
                <button v-for="id in r.recordIds" :key="id" class="link" style="margin-right:.5rem" @click="emit('open', id)">{{ id }}</button>
              </details>
            </td>
            <td>{{ r.staleRecords }}</td><td>{{ r.boundRules }}</td><td>{{ r.gaps }}</td>
          </tr>
        </tbody>
      </table>
      <div class="row">
        <button :disabled="cov.loading || cov.skip === 0" @click="loadCoverage(Math.max(0, cov.skip - covPage))">Previous page</button>
        <button :disabled="cov.loading || !cov.data.hasMore" @click="loadCoverage(cov.skip + covPage)">Next page</button>
      </div>
    </template>
  </section>

  <section aria-labelledby="h-sem">
    <h2 id="h-sem">Semantic quality</h2>
    <p class="muted">The overturn rate is the share of semantic findings a reviewer overturned: a proxy for false positives.</p>
    <p v-if="sem.loading" role="status">Loading...</p>
    <p v-else-if="sem.denied" class="muted" role="status">Semantic quality metrics need audit rights, which your account does not have. The rest of this page is unaffected.</p>
    <p v-else-if="sem.error" class="err" role="alert">Could not load: {{ sem.error }} <button @click="loadSemantic">Retry</button></p>
    <template v-else-if="sem.data">
      <p>{{ new Date(sem.data.from).toLocaleDateString() }} to {{ new Date(sem.data.to).toLocaleDateString() }}:
        {{ sem.data.raised }} raised, {{ sem.data.overturned }} overturned, overall overturn rate {{ pct(sem.data.overturnRate) }}.</p>
      <p v-if="sem.data.truncated" class="warn" role="status">Warning: the window held more observations than could be scanned, so these figures are partial.</p>
      <p v-if="!sem.data.raised && !sem.data.overturned" class="muted">No semantic findings in this window.</p>
      <template v-else>
        <h3>Overturn rate per code, provider and model</h3>
        <table>
          <caption class="muted">Overturn rate by finding code, provider and model</caption>
          <thead><tr><th scope="col">Code</th><th scope="col">Provider</th><th scope="col">Model</th><th scope="col">Raised</th><th scope="col">Overturned</th><th scope="col">Overturn rate</th></tr></thead>
          <tbody><tr v-for="g in sem.data.groups" :key="`${g.code}|${g.provider}|${g.model}`">
            <th scope="row">{{ g.code }}</th><td>{{ g.provider }}</td><td>{{ g.model }}</td><td>{{ g.raised }}</td><td>{{ g.overturned }}</td><td>{{ pct(g.overturnRate) }}</td>
          </tr></tbody>
        </table>
        <h3>Overturn rate per confidence band</h3>
        <table>
          <caption class="muted">Overturn rate by confidence band, all findings</caption>
          <thead><tr><th scope="col">Confidence</th><th scope="col">Raised</th><th scope="col">Overturned</th><th scope="col">Overturn rate</th></tr></thead>
          <tbody><tr v-for="b in sem.data.bands" :key="b.lower">
            <th scope="row">{{ b.lower.toFixed(1) }} to {{ b.upper.toFixed(1) }}</th><td>{{ b.raised }}</td><td>{{ b.overturned }}</td><td>{{ pct(b.overturnRate) }}</td>
          </tr></tbody>
        </table>
      </template>
      <h3>Threshold suggestion</h3>
      <div class="card">
        <p><strong>Suggestion only. Nothing is applied automatically.</strong> Thresholds change only by configuration and review.</p>
        <p>Current: minimum confidence {{ sem.data.suggestion.currentMinConfidence }}, review confidence {{ sem.data.suggestion.currentReviewConfidence }}.</p>
        <p v-if="sem.data.suggestion.hasSuggestion">Suggested: minimum confidence {{ sem.data.suggestion.minConfidence ?? 'unchanged' }},
          review confidence {{ sem.data.suggestion.reviewConfidence ?? 'unchanged' }}.</p>
        <p v-else class="muted">No change is suggested.</p>
        <p class="muted">{{ sem.data.suggestion.note }}</p>
      </div>
    </template>
  </section>
</template>
