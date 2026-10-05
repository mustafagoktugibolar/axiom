<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { getGraph, type GraphData } from '../api';

const data = ref<GraphData | null>(null);
const error = ref('');
const focus = ref('');
const picked = ref<string | null>(null);

async function load() {
  error.value = '';
  try { data.value = await getGraph(focus.value.trim() || undefined); } catch (e) { error.value = (e as Error).message; }
}
onMounted(load);

// Layered layout: one column per kind, in the order a person reads a system (who owns it -> where it runs).
const order = ['domain', 'system', 'component', 'api', 'repo', 'resource', 'team', 'user'];
const colour: Record<string, string> = {
  domain: '#8250df', system: '#0969da', component: '#1a7f37', api: '#bf8700', repo: '#cf222e', resource: '#6e7781', team: '#bc4c00', user: '#6e7781',
};
const W = 150, H = 38, GAPX = 70, GAPY = 18;

const layout = computed(() => {
  const nodes = data.value?.nodes ?? [];
  const kinds = [...new Set(nodes.map(n => n.kind))].sort((a, b) => (order.indexOf(a) + 100) % 100 - (order.indexOf(b) + 100) % 100);
  const pos = new Map<string, { x: number; y: number }>();
  kinds.forEach((k, ci) => nodes.filter(n => n.kind === k).forEach((n, ri) => pos.set(n.id, { x: 10 + ci * (W + GAPX), y: 30 + ri * (H + GAPY) })));
  const rows = Math.max(1, ...kinds.map(k => nodes.filter(n => n.kind === k).length));
  return { pos, kinds, width: 20 + kinds.length * (W + GAPX), height: 50 + rows * (H + GAPY) };
});
const selectedNode = computed(() => data.value?.nodes.find(n => n.id === picked.value));
const related = computed(() => (data.value?.edges ?? []).filter(e => e.from === picked.value || e.to === picked.value));
</script>

<template>
  <h1>System graph</h1>
  <p class="muted">How domains, systems, components and repositories relate. Solid lines are declared facts, dashed ones are inferred. Click a box for details.</p>
  <form class="row" @submit.prevent="load">
    <input v-model="focus" placeholder="Focus on an entity, e.g. component:gui-platform/api-gateway" aria-label="Focus" size="50" />
    <button type="submit">Show</button>
  </form>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-else-if="data && !data.nodes.length" class="warn">The graph is empty. Axiom reads <code>.axiom/catalog.yaml</code> from the registered governance repository; check the worker log.</p>
  <div v-if="data && data.nodes.length" class="graph">
    <svg :width="layout.width" :height="layout.height" role="img" aria-label="System graph">
      <defs><marker id="arrow" viewBox="0 0 8 8" refX="7" refY="4" markerWidth="7" markerHeight="7" orient="auto"><path d="M0,0 L8,4 L0,8 z" fill="#6e7781" /></marker></defs>
      <text v-for="(k, i) in layout.kinds" :key="k" :x="10 + i * (W + GAPX)" y="16" class="colhead">{{ k }}</text>
      <template v-for="e in data.edges" :key="e.from + e.relation + e.to">
        <g v-if="layout.pos.get(e.from) && layout.pos.get(e.to)">
          <line :x1="layout.pos.get(e.from)!.x + (layout.pos.get(e.from)!.x <= layout.pos.get(e.to)!.x ? W : 0)" :y1="layout.pos.get(e.from)!.y + H / 2" :x2="layout.pos.get(e.to)!.x + (layout.pos.get(e.from)!.x <= layout.pos.get(e.to)!.x ? 0 : W)" :y2="layout.pos.get(e.to)!.y + H / 2"
            stroke="#6e7781" :stroke-dasharray="e.fact ? undefined : '4 3'" marker-end="url(#arrow)" :opacity="!picked || e.from === picked || e.to === picked ? 1 : 0.15" />
        </g>
      </template>
      <g v-for="n in data.nodes" :key="n.id" class="node" tabindex="0" role="button" :aria-label="n.title" @click="picked = n.id" @keydown.enter="picked = n.id">
        <rect :x="layout.pos.get(n.id)!.x" :y="layout.pos.get(n.id)!.y" :width="W" :height="H" rx="6" :fill="colour[n.kind] ?? '#6e7781'" :opacity="!picked || n.id === picked || related.some(r => r.from === n.id || r.to === n.id) ? 1 : 0.35" />
        <text :x="layout.pos.get(n.id)!.x + 8" :y="layout.pos.get(n.id)!.y + 24" fill="#fff" font-size="12">{{ n.title.length > 20 ? n.title.slice(0, 19) + '…' : n.title }}</text>
      </g>
    </svg>
  </div>
  <p v-if="data?.truncated" class="warn">Showing a subset; focus on an entity to see more.</p>
  <article v-if="selectedNode" class="card">
    <h2>{{ selectedNode.title }}</h2>
    <p class="muted">{{ selectedNode.id }}<template v-if="selectedNode.technologies.length"> · {{ selectedNode.technologies.join(', ') }}</template></p>
    <ul><li v-for="e in related" :key="e.from + e.relation + e.to">{{ e.from }} <strong>{{ e.relation }}</strong> {{ e.to }} <span v-if="!e.fact" class="warn">(inferred)</span></li></ul>
    <button @click="focus = selectedNode.id; load()">Focus on this</button> <button @click="picked = null">Clear</button>
  </article>
</template>
