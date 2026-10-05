<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { completeSignIn, isSignedIn, loadConfig, signIn, signOut, whoAmI } from './auth';
import Explorer from './views/Explorer.vue';
import RecordDetail from './views/RecordDetail.vue';
import Start from './views/Start.vue';
import Evaluations from './views/Evaluations.vue';
import Reviews from './views/Reviews.vue';
import Exceptions from './views/Exceptions.vue';
import Graph from './views/Graph.vue';
import Setup from './views/Setup.vue';

type View = 'start' | 'explorer' | 'evaluations' | 'reviews' | 'exceptions' | 'graph' | 'setup';
const tabs: { id: View; label: string }[] = [
  { id: 'start', label: 'Get started' }, { id: 'explorer', label: 'Governance' }, { id: 'evaluations', label: 'Evaluations' },
  { id: 'reviews', label: 'Review queue' }, { id: 'exceptions', label: 'Exceptions' }, { id: 'graph', label: 'System graph' }, { id: 'setup', label: 'Set up' },
];
const view = ref<View>('start');
const selectedRecord = ref<string | null>(null);
const selectedEvaluation = ref<string | null>(null);
const signedIn = ref(isSignedIn());
const authError = ref('');
const busy = ref(true);
const devMode = ref(false);
const user = ref('dev-user');

const go = (v: View) => { view.value = v; selectedRecord.value = null; selectedEvaluation.value = null; };
const openRecord = (id: string) => { view.value = 'explorer'; selectedRecord.value = id; selectedEvaluation.value = null; };
const openEvaluation = (id: string) => { view.value = 'evaluations'; selectedEvaluation.value = id; selectedRecord.value = null; };

onMounted(async () => {
  try { if (await completeSignIn()) signedIn.value = true; } catch (e) { authError.value = (e as Error).message; }
  try { devMode.value = (await loadConfig()).mode === 'dev'; } catch { /* sign-in will report it */ }
  busy.value = false;
});

async function login() {
  authError.value = '';
  try { await signIn(await loadConfig(), user.value.trim() || undefined); signedIn.value = isSignedIn(); } catch (e) { authError.value = (e as Error).message; }
}
function logout() { signOut(); signedIn.value = false; go('start'); }
</script>

<template>
  <header>
    <strong>Axiom</strong>
    <nav v-if="signedIn" aria-label="Primary">
      <button v-for="t in tabs" :key="t.id" :aria-current="view === t.id ? 'page' : undefined" @click="go(t.id)">{{ t.label }}</button>
    </nav>
    <span v-if="signedIn" class="muted">{{ whoAmI() }}</span>
    <button v-if="signedIn" @click="logout">Sign out</button>
  </header>

  <main v-if="!signedIn">
    <h1>Sign in</h1>
    <p v-if="authError" class="err" role="alert">{{ authError }}</p>
    <form class="row" @submit.prevent="login">
      <label v-if="devMode">Sign in as <input v-model="user" aria-label="User name" /></label>
      <button type="submit" :disabled="busy">Sign in</button>
    </form>
    <p v-if="devMode" class="muted">Development mode: no identity provider is involved. Use two different names to try approvals (a requester cannot approve their own request).</p>
  </main>

  <main v-else>
    <Start v-if="view === 'start'" @open-record="openRecord" @open-evaluation="openEvaluation" />
    <template v-else-if="view === 'explorer'">
      <RecordDetail v-if="selectedRecord" :id="selectedRecord" @back="selectedRecord = null" @open="openRecord" />
      <Explorer v-else @open="openRecord" />
    </template>
    <Evaluations v-else-if="view === 'evaluations'" :key="selectedEvaluation ?? 'list'" :initial-id="selectedEvaluation" @open="openRecord" />
    <Reviews v-else-if="view === 'reviews'" @open-evaluation="openEvaluation" />
    <Exceptions v-else-if="view === 'exceptions'" />
    <Graph v-else-if="view === 'graph'" />
    <Setup v-else />
  </main>
</template>
