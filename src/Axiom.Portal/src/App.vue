<script setup lang="ts">
import { nextTick, onMounted, ref, watch } from 'vue';
import { completeSignIn, isSignedIn, loadConfig, signIn, signOut, whoAmI } from './auth';
import Explorer from './views/Explorer.vue';
import RecordDetail from './views/RecordDetail.vue';
import Start from './views/Start.vue';
import Evaluations from './views/Evaluations.vue';
import Reviews from './views/Reviews.vue';
import Exceptions from './views/Exceptions.vue';
import Candidates from './views/Candidates.vue';
import Graph from './views/Graph.vue';
import DecisionGraph from './views/DecisionGraph.vue';
import Health from './views/Health.vue';
import Setup from './views/Setup.vue';

type View = 'start' | 'explorer' | 'evaluations' | 'reviews' | 'candidates' | 'exceptions' | 'graph' | 'decisions' | 'health' | 'setup';
const tabs: { id: View; label: string }[] = [
  { id: 'start', label: 'Get started' }, { id: 'explorer', label: 'Governance' }, { id: 'evaluations', label: 'Evaluations' },
  { id: 'reviews', label: 'Review queue' }, { id: 'candidates', label: 'Candidates' }, { id: 'exceptions', label: 'Exceptions' }, { id: 'graph', label: 'System graph' }, { id: 'decisions', label: 'Decisions graph' }, { id: 'health', label: 'Governance health' }, { id: 'setup', label: 'Set up' },
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

// WCAG 2.4.3 / 2.4.2: the app swaps views without a page load, so move focus to the new view's heading
// and keep the document title in step. The watcher does not run on load, so the page never steals focus then.
watch([view, selectedRecord, selectedEvaluation, signedIn], async () => {
  await nextTick();
  const label = signedIn.value ? (tabs.find(t => t.id === view.value)?.label ?? 'Axiom') : 'Sign in';
  document.title = `${label} - Axiom`;
  const target = document.querySelector<HTMLElement>('#main-content h1') ?? document.getElementById('main-content');
  if (target) { target.setAttribute('tabindex', '-1'); target.focus({ preventScroll: false }); }
});

function skipToMain() { document.getElementById('main-content')?.focus(); }

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
  <a class="skip-link" href="#main-content" @click.prevent="skipToMain">Skip to main content</a>
  <header>
    <strong>Axiom</strong>
    <nav v-if="signedIn" aria-label="Primary">
      <button v-for="t in tabs" :key="t.id" type="button" :aria-current="view === t.id ? 'page' : undefined" @click="go(t.id)">{{ t.label }}</button>
    </nav>
    <span v-if="signedIn" class="muted">{{ whoAmI() }}</span>
    <button v-if="signedIn" type="button" @click="logout">Sign out</button>
  </header>

  <main v-if="!signedIn" id="main-content" tabindex="-1">
    <h1>Sign in</h1>
    <p v-if="authError" id="auth-error" class="err" role="alert">{{ authError }}</p>
    <form class="row" @submit.prevent="login">
      <label v-if="devMode">Sign in as <input v-model="user" autocomplete="username" :aria-describedby="authError ? 'auth-error' : undefined" /></label>
      <button type="submit" :disabled="busy">Sign in</button>
    </form>
    <p v-if="devMode" class="muted">Development mode: no identity provider is involved. Use two different names to try approvals (a requester cannot approve their own request).</p>
  </main>

  <main v-else id="main-content" tabindex="-1">
    <Start v-if="view === 'start'" @open-record="openRecord" @open-evaluation="openEvaluation" />
    <template v-else-if="view === 'explorer'">
      <RecordDetail v-if="selectedRecord" :id="selectedRecord" @back="selectedRecord = null" @open="openRecord" />
      <Explorer v-else @open="openRecord" />
    </template>
    <Evaluations v-else-if="view === 'evaluations'" :key="selectedEvaluation ?? 'list'" :initial-id="selectedEvaluation" @open="openRecord" />
    <Reviews v-else-if="view === 'reviews'" @open-evaluation="openEvaluation" />
    <Candidates v-else-if="view === 'candidates'" />
    <Exceptions v-else-if="view === 'exceptions'" />
    <Graph v-else-if="view === 'graph'" />
    <DecisionGraph v-else-if="view === 'decisions'" @open="openRecord" />
    <Health v-else-if="view === 'health'" @open="openRecord" @open-exceptions="go('exceptions')" />
    <Setup v-else />
  </main>
</template>
