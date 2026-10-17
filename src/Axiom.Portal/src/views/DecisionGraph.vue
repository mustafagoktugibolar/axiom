<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { getDecisionGraph, type DecisionGraphData } from '../api';

const emit = defineEmits<{ open: [id: string] }>();
const data = ref<DecisionGraphData | null>(null);
const error = ref('');
const loading = ref(false);
const focus = ref('');
const depth = ref(2);
const kind = ref('');
const status = ref('');
const scope = ref('');
const picked = ref<string | null>(null);

async function load() {
  loading.value = true; error.value = '';
  try {
    data.value = await getDecisionGraph({ focus: focus.value.trim() || undefined, depth: depth.value, kind: kind.value, status: status.value, scope: scope.value.trim() || undefined });
    if (picked.value && !data.value.nodes.some(n => n.id === picked.value)) picked.value = null;
  } catch (e) { error.value = (e as Error).message; data.value = null; } finally { loading.value = false; }
}
onMounted(load);

// One column per lifecycle status, in the order a decision ages. Status is also written in each box, so colour is never the only cue.
const order = ['Accepted', 'Proposed', 'Deprecated', 'Superseded', 'Rejected', 'Draft', 'Candidate'];
// Relation kinds differ by line pattern (and are listed by name in the table), not by colour alone.
const dash: Record<string, string | undefined> = { Supersedes: undefined, Refines: '8 3', ConflictsWith: '2 3', Related: '5 5 1 5' };
const W = 170, H = 46, GAPX = 80, GAPY = 18;

const layout = computed(() => {
  const nodes = data.value?.nodes ?? [];
  const statuses = [...new Set(nodes.map(n => n.status))].sort((a, b) => (order.indexOf(a) + 100) % 100 - (order.indexOf(b) + 100) % 100);
  const pos = new Map<string, { x: number; y: number }>();
  statuses.forEach((s, ci) => nodes.filter(n => n.status === s).forEach((n, ri) => pos.set(n.id, { x: 10 + ci * (W + GAPX), y: 30 + ri * (H + GAPY) })));
  const rows = Math.max(1, ...statuses.map(s => nodes.filter(n => n.status === s).length));
  return { pos, statuses, width: 20 + statuses.length * (W + GAPX), height: 50 + rows * (H + GAPY) };
});
const titleOf = (id: string) => data.value?.nodes.find(n => n.id === id)?.title ?? id;
const selectedNode = computed(() => data.value?.nodes.find(n => n.id === picked.value));
const touching = (id: string) => (data.value?.edges ?? []).some(e => (e.from === id && e.to === picked.value) || (e.to === id && e.from === picked.value));
const related = computed(() => (data.value?.edges ?? []).filter(e => e.from === picked.value || e.to === picked.value));
const dim = (id: string) => !!picked.value && id !== picked.value && !touching(id);

function line(e: { from: string; to: string }) {
  const a = layout.value.pos.get(e.from), b = layout.value.pos.get(e.to);
  if (!a || !b) return null;
  const forward = a.x <= b.x;
  return { x1: a.x + (forward ? W : 0), y1: a.y + H / 2, x2: b.x + (forward ? 0 : W), y2: b.y + H / 2 };
}
function pick(id: string) { picked.value = id; }
async function refocus(id: string) { focus.value = id; await load(); }
</script>

<template>
  <h1>Decisions graph</h1>
  <p class="muted">How governance records relate: supersedes, refines, conflicts with, related. Columns are lifecycle status; a dashed box is not authoritative. The relations table below lists the same information as text.</p>
  <form class="row" role="search" @submit.prevent="load">
    <input v-model="focus" placeholder="Focus on a record, e.g. ARCH-042" aria-label="Focus record" size="30" />
    <label>Depth <select v-model.number="depth" aria-label="Depth"><option v-for="d in [1, 2, 3, 4, 5]" :key="d" :value="d">{{ d }}</option></select></label>
    <select v-model="kind" aria-label="Kind">
      <option value="">Any kind</option><option>Decision</option><option>Standard</option><option>Goal</option><option>Exception</option><option>Principle</option>
    </select>
    <select v-model="status" aria-label="Status">
      <option value="">Any status</option><option>Accepted</option><option>Proposed</option><option>Superseded</option><option>Deprecated</option><option>Rejected</option>
    </select>
    <input v-model="scope" placeholder="Scope, e.g. repository:gateway" aria-label="Scope" size="26" />
    <button type="submit" :disabled="loading">Show</button>
  </form>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-else-if="data && !data.nodes.length" class="muted">No records match.</p>
  <p v-if="data?.truncated" class="warn" role="status">Showing a subset (the node or relation limit was reached); focus on a record or add filters to see more.</p>

  <div v-if="data && data.nodes.length" class="graph">
    <svg :width="layout.width" :height="layout.height" role="group" aria-label="Decisions graph. The relations table below has the same information as text.">
      <defs><marker id="dg-arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#6e7781" /></marker></defs>
      <text v-for="(s, i) in layout.statuses" :key="s" :x="10 + i * (W + GAPX)" y="16" class="colhead">{{ s }}</text>
      <template v-for="e in data.edges" :key="e.from + e.relation + e.to">
        <line v-if="line(e)" v-bind="line(e)!" stroke="#6e7781" stroke-width="1.5" :stroke-dasharray="dash[e.relation]" marker-end="url(#dg-arrow)"
          :opacity="!picked || e.from === picked || e.to === picked ? 1 : 0.15" />
      </template>
      <g v-for="n in data.nodes" :key="n.id" class="node" tabindex="0" role="button"
        :aria-label="`${n.id}, ${n.title}, ${n.status}, authority ${n.authorityLevel}${n.authoritative ? '' : ', not authoritative'}. Press Enter to select.`"
        :aria-pressed="picked === n.id" @click="pick(n.id)" @keydown.enter.prevent="pick(n.id)" @keydown.space.prevent="pick(n.id)">
        <rect :x="layout.pos.get(n.id)!.x" :y="layout.pos.get(n.id)!.y" :width="W" :height="H" rx="6" fill="#0969da" :opacity="dim(n.id) ? 0.3 : 1"
          :stroke="n.id === picked || n.focus ? '#1f2328' : n.authoritative ? 'none' : '#fff'" :stroke-width="n.id === picked || n.focus ? 3 : 2" :stroke-dasharray="n.authoritative ? undefined : '5 3'" />
        <text :x="layout.pos.get(n.id)!.x + 8" :y="layout.pos.get(n.id)!.y + 18" fill="#fff" font-size="12" font-weight="600">{{ n.id.length > 22 ? n.id.slice(0, 21) + '…' : n.id }}</text>
        <text :x="layout.pos.get(n.id)!.x + 8" :y="layout.pos.get(n.id)!.y + 36" fill="#fff" font-size="11">{{ n.status }} · {{ n.authorityLevel }}</text>
      </g>
    </svg>
    <p class="muted">Line styles: solid = supersedes, long dashes = refines, dotted = conflicts with, dash-dot = related. Arrows point from the record that declares the relation.</p>
  </div>

  <article v-if="selectedNode" class="card" aria-live="polite">
    <h2>{{ selectedNode.id }}: {{ selectedNode.title }}</h2>
    <p class="muted">{{ selectedNode.kind }} · {{ selectedNode.status }} · authority {{ selectedNode.authorityLevel }} · {{ selectedNode.authoritative ? 'authoritative' : 'not authoritative' }}</p>
    <ul><li v-for="e in related" :key="e.from + e.relation + e.to">{{ e.from }} <strong>{{ e.relation }}</strong> {{ e.to }}</li></ul>
    <button @click="emit('open', selectedNode.id)">Open record</button>
    <button @click="refocus(selectedNode.id)">Focus on this</button>
    <button @click="picked = null">Clear</button>
  </article>

  <section v-if="data && data.nodes.length">
    <h2>Relations</h2>
    <p v-if="!data.edges.length" class="muted">These records have no relations between them.</p>
    <table v-else>
      <caption class="muted">{{ data.edges.length }} relation(s) between {{ data.nodes.length }} record(s)</caption>
      <thead><tr><th>From</th><th>Relation</th><th>To</th></tr></thead>
      <tbody>
        <tr v-for="e in data.edges" :key="e.from + e.relation + e.to" :class="{ picked: picked === e.from || picked === e.to }">
          <td><button class="link" @click="emit('open', e.from)">{{ e.from }}</button><div>{{ titleOf(e.from) }}</div></td>
          <td><strong>{{ e.relation }}</strong></td>
          <td><button class="link" @click="emit('open', e.to)">{{ e.to }}</button><div>{{ titleOf(e.to) }}</div></td>
        </tr>
      </tbody>
    </table>
    <h2>Records</h2>
    <table>
      <thead><tr><th>Record</th><th>Kind</th><th>Status</th><th>Authority</th><th></th></tr></thead>
      <tbody>
        <tr v-for="n in data.nodes" :key="n.id">
          <td><button class="link" @click="emit('open', n.id)">{{ n.id }}</button><div>{{ n.title }}</div></td>
          <td>{{ n.kind }}</td>
          <td>{{ n.status }}<span v-if="!n.authoritative" class="warn"> · not authoritative</span></td>
          <td>{{ n.authorityLevel }}</td>
          <td><button @click="pick(n.id)">Select</button></td>
        </tr>
      </tbody>
    </table>
  </section>
</template>
