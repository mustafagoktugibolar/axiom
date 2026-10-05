<script setup lang="ts">
import { ref } from 'vue';
import { getEvaluation, getReceipt, pendingReviews } from '../api';

const props = defineProps<{ kind: 'evaluation' | 'receipt' | 'reviews' }>();
const id = ref(''); const data = ref<unknown>(null); const error = ref('');
const titles = { evaluation: 'Evaluation detail', receipt: 'Receipt detail', reviews: 'Review queue' } as const;

async function load() {
  error.value = ''; data.value = null;
  try {
    data.value = props.kind === 'reviews' ? await pendingReviews()
      : props.kind === 'evaluation' ? await getEvaluation(id.value.trim()) : await getReceipt(id.value.trim());
  } catch (e) { error.value = (e as Error).message; }
}
</script>

<template>
  <h1>{{ titles[kind] }}</h1>
  <form class="row" @submit.prevent="load">
    <input v-if="kind !== 'reviews'" v-model="id" placeholder="ID" aria-label="ID" required />
    <button type="submit">{{ kind === 'reviews' ? 'Refresh' : 'Load' }}</button>
  </form>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <pre v-if="data">{{ JSON.stringify(data, null, 2) }}</pre>
</template>
