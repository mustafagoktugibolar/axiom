<script setup lang="ts">
import { ref } from 'vue';
import { searchGovernance, type GovernanceSummary } from '../api';

defineEmits<{ open: [id: string] }>();
const q = ref(''); const kind = ref(''); const status = ref(''); const repository = ref(''); const staleOnly = ref(false);
const items = ref<GovernanceSummary[]>([]); const total = ref(0); const error = ref(''); const loading = ref(false); const searched = ref(false);

async function search() {
  loading.value = true; error.value = '';
  try {
    const r = await searchGovernance({ q: q.value, kind: kind.value, status: status.value, repository: repository.value, staleOnly: staleOnly.value || undefined });
    items.value = r.items; total.value = r.total; searched.value = true;
  } catch (e) { error.value = (e as Error).message; items.value = []; } finally { loading.value = false; }
}
</script>

<template>
  <h1>Governance explorer</h1>
  <form class="row" role="search" @submit.prevent="search">
    <input v-model="q" placeholder="Search text" aria-label="Search text" />
    <select v-model="kind" aria-label="Kind">
      <option value="">Any kind</option><option>Decision</option><option>Standard</option><option>Goal</option><option>Exception</option><option>Principle</option>
    </select>
    <select v-model="status" aria-label="Status">
      <option value="">Any status</option><option>Accepted</option><option>Proposed</option><option>Superseded</option><option>Deprecated</option><option>Rejected</option>
    </select>
    <input v-model="repository" placeholder="Repository" aria-label="Repository" />
    <label><input v-model="staleOnly" type="checkbox" /> Stale only</label>
    <button type="submit" :disabled="loading">Search</button>
  </form>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-else-if="searched && !items.length" class="muted">No records match.</p>
  <p v-if="items.length" class="muted">{{ items.length }} of {{ total }}</p>
  <table v-if="items.length">
    <thead><tr><th>Record</th><th>Kind</th><th>Status</th><th>Owners</th></tr></thead>
    <tbody>
      <tr v-for="r in items" :key="r.id">
        <td><button class="link" @click="$emit('open', r.id)">{{ r.id }}</button><div>{{ r.title }}</div>
          <span v-for="t in r.tags" :key="t" class="tag">{{ t }}</span></td>
        <td>{{ r.kind }}</td>
        <td>{{ r.status }}<span v-if="!r.isAuthoritative" class="warn"> · not authoritative</span><span v-if="r.isStale" class="warn"> · stale</span></td>
        <td>{{ r.owners.join(', ') }}</td>
      </tr>
    </tbody>
  </table>
</template>
