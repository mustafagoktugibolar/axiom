<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { listEvaluations, verifyChain, type EvaluationSummary } from '../api';
import EvaluationDetail from './EvaluationDetail.vue';
import { verdictClass, verdictLabel } from '../verdict';

const props = defineProps<{ initialId?: string | null }>();
const emit = defineEmits<{ open: [id: string] }>();

const items = ref<EvaluationSummary[]>([]);
const total = ref(0);
const verdict = ref('');
const repository = ref('');
const error = ref('');
const selected = ref<string | null>(props.initialId ?? null);
const chain = ref('');

async function load() {
  error.value = '';
  try {
    const r = await listEvaluations({ verdict: verdict.value || undefined, repository: repository.value.trim() || undefined });
    items.value = r.items; total.value = r.total;
  } catch (e) { error.value = (e as Error).message; }
}
async function checkChain() {
  chain.value = '';
  try {
    const r = await verifyChain();
    chain.value = r.intact ? `Receipt chain intact (${r.receipts} receipts).` : 'RECEIPT CHAIN BROKEN - investigate.';
  } catch (e) { chain.value = (e as Error).message; }
}
onMounted(load);
const when = (iso: string) => new Date(iso).toLocaleString();
</script>

<template>
  <EvaluationDetail v-if="selected" :id="selected" @back="selected = null; load()" @open-record="(id: string) => emit('open', id)" />
  <template v-else>
    <h1>Evaluations</h1>
    <p class="muted">Every preflight, design check and diff/PR check Axiom ran, each with a tamper-evident receipt.</p>
    <form class="row" @submit.prevent="load">
      <select v-model="verdict" aria-label="Verdict">
        <option value="">Any verdict</option><option value="Block">Block</option><option value="RequireReview">Require review</option>
        <option value="AllowWithWarnings">Allow with warnings</option><option value="Allow">Allow</option>
      </select>
      <input v-model="repository" placeholder="Repository" aria-label="Repository" />
      <button type="submit">Filter</button>
      <button type="button" @click="checkChain">Verify receipt chain</button>
    </form>
    <p v-if="chain" role="status">{{ chain }}</p>
    <p v-if="error" class="err" role="alert">{{ error }}</p>
    <p v-else-if="!items.length" class="muted">No evaluations yet. Run a preflight or a PR check from Get started.</p>
    <table v-else>
      <thead><tr><th>When</th><th>Check</th><th>Repository</th><th>Verdict</th><th>Findings</th><th>By</th></tr></thead>
      <tbody>
        <tr v-for="e in items" :key="e.id">
          <td>{{ when(e.createdAt) }}</td>
          <td><button class="link" @click="selected = e.id">{{ e.stage }}</button></td>
          <td>{{ e.repository }}<div class="muted"><code>{{ e.commitSha.slice(0, 8) }}</code></div></td>
          <td :class="verdictClass(e.verdict)"><strong>{{ verdictLabel(e.verdict) }}</strong></td>
          <td>{{ e.findingCount }}</td>
          <td>{{ e.actorSubject }}</td>
        </tr>
      </tbody>
    </table>
    <p v-if="items.length" class="muted">{{ items.length }} of {{ total }}</p>
  </template>
</template>
