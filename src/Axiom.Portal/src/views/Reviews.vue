<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { listPendingReviews, type ReviewQueueItem } from '../api';
import { verdictClass, verdictLabel } from '../verdict';

defineEmits<{ openEvaluation: [id: string] }>();
const items = ref<ReviewQueueItem[]>([]);
const error = ref('');
const loaded = ref(false);

async function load() {
  error.value = '';
  try { items.value = await listPendingReviews(); loaded.value = true; } catch (e) { error.value = (e as Error).message; }
}
onMounted(load);
</script>

<template>
  <h1>Review queue</h1>
  <p class="muted">Checks that need a human decision before the change can proceed.</p>
  <button @click="load">Refresh</button>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <p v-else-if="loaded && !items.length" class="ok">Nothing is waiting for review.</p>
  <table v-if="items.length">
    <caption class="muted">Evaluations waiting for review</caption>
    <thead><tr><th scope="col">Check</th><th scope="col">Repository</th><th scope="col">Verdict</th><th scope="col">Findings</th><th scope="col">Reviewers</th></tr></thead>
    <tbody>
      <tr v-for="i in items" :key="i.evaluation.id">
        <td><button class="link" @click="$emit('openEvaluation', i.evaluation.id)">{{ i.evaluation.stage }}</button></td>
        <td>{{ i.evaluation.repository }}</td>
        <td :class="verdictClass(i.evaluation.verdict)">{{ verdictLabel(i.evaluation.verdict) }}</td>
        <td>{{ i.findingCodes.join(', ') }}</td>
        <td>{{ i.requiredReviewers.join(', ') }}</td>
      </tr>
    </tbody>
  </table>
</template>
