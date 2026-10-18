<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import { decideCandidate, listCandidates, type Candidate } from '../api';

const items = ref<Candidate[]>([]);
const error = ref('');
const notice = ref('');
const loaded = ref(false);
const selectedId = ref<string | null>(null);
const comment = ref('');
const busy = ref(false);
const ownerFilter = ref('');
const stateFilter = ref('');

const selected = computed(() => items.value.find(c => c.id === selectedId.value) ?? null);
const pending = computed(() => items.value.filter(c => c.state === 'Proposed').length);

async function load() {
  error.value = '';
  try {
    items.value = (await listCandidates({ owner: ownerFilter.value.trim() || undefined, state: stateFilter.value || undefined })).items;
    loaded.value = true;
  } catch (e) { error.value = (e as Error).message; }
}
onMounted(load);

function open(id: string) { selectedId.value = id; comment.value = ''; notice.value = ''; error.value = ''; }

async function decide(approve: boolean) {
  if (!selected.value) return;
  if (comment.value.trim().length < 10) { error.value = 'A comment of at least 10 characters is required for every decision.'; return; }
  busy.value = true; error.value = ''; notice.value = '';
  try {
    await decideCandidate(selected.value.id, approve, comment.value.trim());
    comment.value = '';
    notice.value = approve ? 'Approved. Commit the draft record to the governance repository for it to govern.' : 'Rejected. Nothing was added to governance.';
    await load();
  } catch (e) { error.value = (e as Error).message; } finally { busy.value = false; }
}

const copied = ref(false);
async function copyDraft() {
  if (!selected.value) return;
  try { await navigator.clipboard.writeText(selected.value.draftRecord); copied.value = true; setTimeout(() => (copied.value = false), 2000); }
  catch { error.value = 'Copy failed: select the draft text and copy it manually.'; }
}

const stateLabel = (s: Candidate['state']) => ({ Proposed: 'Pending review', Approved: 'Approved, not yet merged', Rejected: 'Rejected', MergedAsRecord: 'Merged as record' }[s]);
const stateClass = (s: Candidate['state']) => (s === 'MergedAsRecord' ? 'ok' : s === 'Rejected' ? 'err' : 'warn');
</script>

<template>
  <h1>Candidates</h1>
  <p class="muted">Knowledge found in ADRs, docs or proposed by agents. A candidate never governs, whatever its state. Approving it only means a reviewer agrees the draft record should be committed to the governance repository.</p>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-if="notice" class="ok" role="status">{{ notice }}</p>

  <form class="row" @submit.prevent="load">
    <label>Owner team <input v-model="ownerFilter" placeholder="e.g. platform-architecture" /></label>
    <label>State
      <select v-model="stateFilter">
        <option value="">All (pending first)</option><option value="Proposed">Pending review</option><option value="Approved">Approved</option>
        <option value="Rejected">Rejected</option><option value="MergedAsRecord">Merged as record</option>
      </select>
    </label>
    <button type="submit">Apply</button>
  </form>

  <p v-if="loaded && !items.length" class="muted">No candidates match.</p>
  <p v-else-if="loaded" class="muted" role="status">{{ pending }} pending review in this list.</p>
  <table v-if="items.length">
    <caption class="muted">Review queue, pending candidates first</caption>
    <thead><tr><th scope="col">Title</th><th scope="col">Kind</th><th scope="col">Owner</th><th scope="col">State</th><th scope="col">Source</th></tr></thead>
    <tbody>
      <tr v-for="c in items" :key="c.id">
        <td><button class="link" :aria-current="c.id === selectedId ? 'true' : undefined" @click="open(c.id)">{{ c.title }}</button></td>
        <td>{{ c.kind }}</td>
        <td>{{ c.suggestedOwner || 'unassigned' }}</td>
        <td :class="stateClass(c.state)">{{ stateLabel(c.state) }}</td>
        <td>{{ c.extractionMethod }}</td>
      </tr>
    </tbody>
  </table>

  <section v-if="selected" class="card" aria-labelledby="cand-title">
    <h2 id="cand-title">{{ selected.title }}</h2>
    <p><span :class="stateClass(selected.state)">{{ stateLabel(selected.state) }}</span> · not authoritative · {{ selected.id }}</p>
    <p class="muted">{{ selected.nextStep }}</p>
    <h3>Statement</h3>
    <p>{{ selected.statement }}</p>
    <h3>Evidence</h3>
    <ul>
      <li>Source: <code>{{ selected.sourceLocator }}</code></li>
      <li>Excerpt hash: <code>{{ selected.excerptHash }}</code></li>
      <li>Extracted by: {{ selected.extractionMethod }}, confidence {{ Math.round(selected.confidence * 100) }}%</li>
      <li>Proposed by {{ selected.createdBy }} on {{ new Date(selected.createdAt).toLocaleString() }}</li>
      <li v-if="Object.keys(selected.scopeHints).length">Scope hints: {{ Object.entries(selected.scopeHints).map(([k, v]) => `${k}: ${v.join(', ')}`).join('; ') }}</li>
    </ul>
    <p v-if="selected.decidedBy"><strong>{{ selected.state === 'Rejected' ? 'Rejected' : 'Approved' }}</strong> by {{ selected.decidedBy }}: {{ selected.decisionComment }}</p>
    <p v-if="selected.mergedRecordId">Became governance record <strong>{{ selected.mergedRecordId }}</strong>.</p>

    <h3>Draft record</h3>
    <p class="muted">Commit this to <code>{{ selected.draftRecordPath }}</code> (rename the placeholder ID) and merge it as accepted.</p>
    <pre tabindex="0" aria-label="Draft record markdown">{{ selected.draftRecord }}</pre>
    <button type="button" @click="copyDraft">Copy draft record</button>
    <span v-if="copied" role="status" class="ok" style="margin-left:.75rem">Copied.</span>

    <div v-if="selected.state === 'Proposed'" class="stack" style="margin-top:1rem">
      <label>Comment (required)
        <textarea v-model="comment" rows="2" aria-required="true" placeholder="Why approve or reject this candidate?" />
      </label>
      <div class="row">
        <button :disabled="busy" @click="decide(true)">Approve</button>
        <button :disabled="busy" @click="decide(false)">Reject</button>
      </div>
      <p class="muted">You cannot decide a candidate you proposed. Only approvers of the owner team, decision owners and platform admins can decide.</p>
    </div>
  </section>
</template>
