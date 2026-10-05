<script setup lang="ts">
import { ref, watchEffect } from 'vue';
import {
  getEvaluationDetail, getReceiptById, overturnFinding, submitReview,
  type EvaluationDetail, type Receipt,
} from '../api';
import { verdictClass, verdictLabel } from '../verdict';

const props = defineProps<{ id: string }>();
defineEmits<{ back: []; openRecord: [id: string] }>();

const detail = ref<EvaluationDetail | null>(null);
const receipt = ref<Receipt | null>(null);
const error = ref('');
const notice = ref('');
const comment = ref('');
const busy = ref(false);

async function load() {
  error.value = '';
  try {
    detail.value = await getEvaluationDetail(props.id);
    receipt.value = await getReceiptById(detail.value.evaluation.receiptId).catch(() => null);
  } catch (e) { error.value = (e as Error).message; }
}
watchEffect(load);

async function act(label: string, fn: () => Promise<unknown>) {
  if (!comment.value.trim()) { error.value = 'A comment is required for every decision.'; return; }
  busy.value = true; error.value = ''; notice.value = '';
  try { await fn(); notice.value = `${label} recorded.`; comment.value = ''; await load(); }
  catch (e) { error.value = (e as Error).message; } finally { busy.value = false; }
}
const approve = (yes: boolean) => act(yes ? 'Approval' : 'Rejection', () => submitReview(props.id, yes, comment.value.trim()));
const overturn = (code: string) => act(`Overturn of ${code}`, () => overturnFinding(props.id, code, comment.value.trim()));
const canOverturn = (f: { source: string; severity: string }) => f.source === 'semantic' && f.severity !== 'INFO' && detail.value?.clearance !== 'Blocked';
const needsReview = () => detail.value && /review/i.test(detail.value.clearance + detail.value.evaluation.verdict);
const when = (iso?: string) => (iso ? new Date(iso).toLocaleString() : '');
</script>

<template>
  <button @click="$emit('back')">Back to evaluations</button>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-if="notice" class="ok" role="status">{{ notice }}</p>

  <article v-if="detail">
    <h1>{{ detail.evaluation.stage }} check
      <span :class="verdictClass(detail.evaluation.verdict)">{{ verdictLabel(detail.evaluation.verdict) }}</span></h1>
    <p class="muted">
      {{ detail.scm?.repository ?? '' }}
      <template v-if="detail.scm?.commitSha"> @ <code>{{ detail.scm.commitSha.slice(0, 10) }}</code></template>
      · by {{ detail.actor.subject }} · {{ when(detail.evaluation.createdAt) }} · clearance: <strong>{{ detail.clearance }}</strong>
    </p>

    <section v-if="detail.evaluation.findings.length">
      <h2>Findings</h2>
      <div v-for="f in detail.evaluation.findings" :key="f.code + f.message" class="card">
        <p><strong :class="verdictClass(f.severity)">{{ f.severity }}</strong> {{ f.code }}
          <span v-if="f.path" class="muted"> · <code>{{ f.path }}<template v-if="f.line">:{{ f.line }}</template></code></span></p>
        <p>{{ f.message }}</p>
        <p v-if="f.governanceIds.length">Governance:
          <button v-for="g in f.governanceIds" :key="g" class="link" @click="$emit('openRecord', g)">{{ g }} </button></p>
        <p v-if="f.recommendedAction" class="muted">What to do: {{ f.recommendedAction }}</p>
        <button v-if="canOverturn(f)" :disabled="busy" title="Needs a comment below" @click="overturn(f.code)">Overturn this finding (reviewer)</button>
      </div>
    </section>
    <p v-else class="ok">No findings.</p>

    <section v-if="detail.evaluation.requiredActions.length">
      <h2>Required next actions</h2>
      <ul><li v-for="a in detail.evaluation.requiredActions" :key="a">{{ a }}</li></ul>
    </section>

    <section v-if="detail.evaluation.applicableGovernance.length">
      <h2>Governance that applied</h2>
      <ul>
        <li v-for="g in detail.evaluation.applicableGovernance" :key="g.id">
          <button class="link" @click="$emit('openRecord', g.id)">{{ g.id }}</button> {{ g.title }}
          <span class="muted">({{ g.kind }}, {{ g.importance }})</span>
        </li>
      </ul>
    </section>

    <section v-if="detail.evaluation.affectedDependencies.length">
      <h2>Possibly affected</h2>
      <ul><li v-for="d in detail.evaluation.affectedDependencies" :key="d.entity">{{ d.entity }} <span class="muted">(depth {{ d.depth }}<template v-if="d.owners.length">, owners {{ d.owners.join(', ') }}</template>)</span></li></ul>
    </section>

    <section>
      <h2>Review</h2>
      <ul v-if="detail.reviews.length">
        <li v-for="(r, i) in detail.reviews" :key="i">
          <strong>{{ r.outcome }}</strong><template v-if="r.findingCode"> (overturned {{ r.findingCode }})</template> by {{ r.reviewer }} {{ when(r.decidedAt) }} - {{ r.comment }}
        </li>
      </ul>
      <p v-else class="muted">No review decisions yet.</p>
      <p v-if="detail.clearance === 'Blocked'" class="warn">
        This check is blocked by a deterministic rule, so a review cannot clear it. Change the code, or request an exception
        (Exceptions tab) and have the approved record merged into the governance repository.
      </p>
      <div v-else class="row">
        <input v-model="comment" aria-label="Comment" placeholder="Comment (required)" size="50" />
        <button :disabled="busy" @click="approve(true)">Approve</button>
        <button :disabled="busy" @click="approve(false)">Reject</button>
      </div>
      <p class="muted" v-if="needsReview()">This check is waiting for a reviewer from: {{ detail.evaluation.requiredReviewers.join(', ') || 'the owning team' }}.</p>
    </section>

    <section v-if="receipt">
      <h2>Receipt</h2>
      <p>
        <code>{{ receipt.receiptId }}</code> issued {{ when(receipt.issuedAt) }} ·
        <strong :class="receipt.verified ? 'ok' : 'err'">{{ receipt.verified ? 'verified' : 'NOT verified' }}</strong>
      </p>
      <p class="muted">Digest <code>{{ receipt.digest.slice(0, 24) }}...</code> chained to <code>{{ receipt.chainDigest.slice(0, 24) }}...</code></p>
    </section>
  </article>
</template>
