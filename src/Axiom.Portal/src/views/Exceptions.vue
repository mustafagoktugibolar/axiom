<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { decideException, listExceptions, requestException, type ExceptionRequest } from '../api';

const items = ref<ExceptionRequest[]>([]);
const error = ref('');
const notice = ref('');
const expanded = ref<string | null>(null);
const comment = ref('');
const busy = ref(false);

const form = ref({
  target: 'ARCH-001', repository: 'demo-gateway', paths: 'src/Gateway/Routing/**', title: '', rationale: '',
  expires: new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10), issue: '', controls: '',
});

async function load() {
  error.value = '';
  try { items.value = (await listExceptions()).items; } catch (e) { error.value = (e as Error).message; }
}
onMounted(load);

async function submit() {
  busy.value = true; error.value = ''; notice.value = '';
  try {
    const f = form.value;
    const created = await requestException({
      targets: [f.target.trim()],
      scope: { repositories: [f.repository.trim()], ...(f.paths.trim() ? { paths: f.paths.split(',').map(p => p.trim()).filter(Boolean) } : {}) },
      title: f.title.trim(), rationale: f.rationale.trim(),
      expiresAt: new Date(`${f.expires}T00:00:00Z`).toISOString(), trackingIssue: f.issue.trim(),
      compensatingControls: f.controls.split('\n').map(c => c.trim()).filter(Boolean),
    });
    notice.value = `Request ${created.id} sent to ${created.requiredApprovers.join(', ')}.`;
    await load();
  } catch (e) { error.value = (e as Error).message; } finally { busy.value = false; }
}

async function decide(id: string, approve: boolean) {
  if (!comment.value.trim()) { error.value = 'A comment is required for every decision.'; return; }
  busy.value = true; error.value = ''; notice.value = '';
  try { await decideException(id, approve, comment.value.trim()); comment.value = ''; notice.value = approve ? 'Approved.' : 'Rejected.'; await load(); }
  catch (e) { error.value = (e as Error).message; } finally { busy.value = false; }
}
const statusClass = (s: string) => (/approved|accepted/i.test(s) ? 'ok' : /reject|denied/i.test(s) ? 'err' : 'warn');
</script>

<template>
  <h1>Exceptions</h1>
  <p class="muted">An exception waives a governance rule for a limited scope and time. Approval alone waives nothing: a maintainer must merge the generated record into the governance repository.</p>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-if="notice" class="ok" role="status">{{ notice }}</p>

  <h2>Requests</h2>
  <p v-if="!items.length" class="muted">No exception requests yet.</p>
  <div v-for="x in items" :key="x.id" class="card">
    <p><button class="link" @click="expanded = expanded === x.id ? null : x.id"><strong>{{ x.title }}</strong></button>
      <span :class="statusClass(x.status)" style="margin-left:.75rem">{{ x.status }}</span></p>
    <p class="muted">{{ x.id }} · waives {{ x.targets.join(', ') }} · by {{ x.requester }} · expires {{ new Date(x.expiresAt).toLocaleDateString() }} · approvers {{ x.requiredApprovers.join(', ') }}</p>
    <template v-if="expanded === x.id">
      <p>{{ x.rationale }}</p>
      <p class="muted">{{ x.nextStep }}</p>
      <template v-if="x.draftRecord"><p class="muted">Draft record for <code>{{ x.draftRecordPath }}</code>:</p><pre>{{ x.draftRecord }}</pre></template>
      <p v-if="x.decision"><strong>{{ x.decision.approved ? 'Approved' : 'Rejected' }}</strong> by {{ x.decision.approver }}: {{ x.decision.comment }}</p>
      <div v-else class="row">
        <input v-model="comment" aria-label="Comment" placeholder="Comment (required)" size="40" />
        <button :disabled="busy" @click="decide(x.id, true)">Approve</button>
        <button :disabled="busy" @click="decide(x.id, false)">Reject</button>
      </div>
      <p v-if="!x.decision" class="muted">The requester cannot decide their own request: sign in as another user to approve.</p>
    </template>
  </div>

  <h2>Request an exception</h2>
  <form class="stack" @submit.prevent="submit">
    <label>Rule to waive (record ID) <input v-model="form.target" required /></label>
    <label>Repository <input v-model="form.repository" required /></label>
    <label>Paths (comma separated, optional) <input v-model="form.paths" /></label>
    <label>Title <input v-model="form.title" required /></label>
    <label>Why is this needed? <textarea v-model="form.rationale" rows="3" required /></label>
    <label>Expires <input v-model="form.expires" type="date" required /></label>
    <label>Tracking issue <input v-model="form.issue" required placeholder="e.g. GUI-912" /></label>
    <label>Compensating controls (one per line) <textarea v-model="form.controls" rows="2" /></label>
    <button type="submit" :disabled="busy">Send request</button>
  </form>
</template>
