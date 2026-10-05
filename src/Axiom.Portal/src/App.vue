<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { completeSignIn, isSignedIn, loadConfig, signIn, signOut } from './auth';
import Explorer from './views/Explorer.vue';
import RecordDetail from './views/RecordDetail.vue';
import Lookup from './views/Lookup.vue';

type View = 'explorer' | 'evaluation' | 'receipt' | 'reviews';
const view = ref<View>('explorer');
const selected = ref<string | null>(null);
const signedIn = ref(isSignedIn());
const authError = ref('');
const busy = ref(true);
const go = (v: View) => { view.value = v; selected.value = null; };

onMounted(async () => {
  try { if (await completeSignIn()) signedIn.value = true; } catch (e) { authError.value = (e as Error).message; }
  busy.value = false;
});

async function login() {
  authError.value = '';
  try { await signIn(await loadConfig()); signedIn.value = isSignedIn(); } catch (e) { authError.value = (e as Error).message; }
}
function logout() { signOut(); signedIn.value = false; selected.value = null; }
</script>

<template>
  <header>
    <strong>Axiom</strong>
    <nav v-if="signedIn" aria-label="Primary">
      <button :aria-current="view === 'explorer' ? 'page' : undefined" @click="go('explorer')">Governance</button>
      <button :aria-current="view === 'evaluation' ? 'page' : undefined" @click="go('evaluation')">Evaluation</button>
      <button :aria-current="view === 'receipt' ? 'page' : undefined" @click="go('receipt')">Receipt</button>
      <button :aria-current="view === 'reviews' ? 'page' : undefined" @click="go('reviews')">Review queue</button>
    </nav>
    <button v-if="signedIn" @click="logout">Sign out</button>
  </header>
  <main v-if="!signedIn">
    <h1>Sign in</h1>
    <p v-if="authError" class="err" role="alert">{{ authError }}</p>
    <button :disabled="busy" @click="login">Sign in</button>
  </main>
  <main v-else>
    <template v-if="view === 'explorer'">
      <RecordDetail v-if="selected" :id="selected" @back="selected = null" @open="(id: string) => (selected = id)" />
      <Explorer v-else @open="(id: string) => (selected = id)" />
    </template>
    <Lookup v-else-if="view === 'evaluation'" kind="evaluation" />
    <Lookup v-else-if="view === 'receipt'" kind="receipt" />
    <Lookup v-else kind="reviews" />
  </main>
</template>
