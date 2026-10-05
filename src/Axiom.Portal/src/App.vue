<script setup lang="ts">
import { ref } from 'vue';
import { getToken, setToken } from './api';
import Explorer from './views/Explorer.vue';
import RecordDetail from './views/RecordDetail.vue';
import Lookup from './views/Lookup.vue';

type View = 'explorer' | 'evaluation' | 'receipt' | 'reviews';
const view = ref<View>('explorer');
const selected = ref<string | null>(null);
const token = ref(getToken());
const saveToken = () => setToken(token.value.trim());
const go = (v: View) => { view.value = v; selected.value = null; };
</script>

<template>
  <header>
    <strong>Axiom</strong>
    <nav aria-label="Primary">
      <button :aria-current="view === 'explorer' ? 'page' : undefined" @click="go('explorer')">Governance</button>
      <button :aria-current="view === 'evaluation' ? 'page' : undefined" @click="go('evaluation')">Evaluation</button>
      <button :aria-current="view === 'receipt' ? 'page' : undefined" @click="go('receipt')">Receipt</button>
      <button :aria-current="view === 'reviews' ? 'page' : undefined" @click="go('reviews')">Review queue</button>
    </nav>
    <form @submit.prevent="saveToken">
      <label class="muted">Token <input v-model="token" type="password" autocomplete="off" /></label>
      <button type="submit">Use</button>
    </form>
  </header>
  <main>
    <template v-if="view === 'explorer'">
      <RecordDetail v-if="selected" :id="selected" @back="selected = null" @open="(id: string) => (selected = id)" />
      <Explorer v-else @open="(id: string) => (selected = id)" />
    </template>
    <Lookup v-else-if="view === 'evaluation'" kind="evaluation" />
    <Lookup v-else-if="view === 'receipt'" kind="receipt" />
    <Lookup v-else kind="reviews" />
  </main>
</template>
