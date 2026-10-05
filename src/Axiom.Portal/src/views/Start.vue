<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { countGovernance, preflight, type PreflightResult } from '../api';

defineEmits<{ open: [id: string] }>();

const records = ref<number | null>(null);
const statusError = ref('');
async function refresh() {
  statusError.value = '';
  try { records.value = (await countGovernance()).total; } catch (e) { statusError.value = (e as Error).message; }
}
onMounted(refresh);

const repository = ref('demo-gateway');
const task = ref('Add per-user landing page routing to the gateway');
const paths = ref('src/Routing/**');
const result = ref<PreflightResult | null>(null);
const error = ref('');
const running = ref(false);
async function run() {
  running.value = true; error.value = ''; result.value = null;
  try {
    result.value = await preflight({
      repository: repository.value.trim(), ref: 'main', task: task.value.trim(),
      paths: paths.value.split(',').map(p => p.trim()).filter(Boolean),
    });
  } catch (e) { error.value = (e as Error).message; } finally { running.value = false; }
}
const verdictClass = (v: string) => (v === 'ALLOW' ? 'ok' : v === 'ALLOW_WITH_WARNINGS' || v === 'REQUIRE_REVIEW' ? 'warn' : 'err');

const mcpUrl = `${location.origin}/mcp`;
const mcpConfig = JSON.stringify({ mcpServers: { axiom: { type: 'http', url: mcpUrl, headers: { Authorization: 'Bearer ${AXIOM_TOKEN}' } } } }, null, 2);
</script>

<template>
  <h1>Get started</h1>
  <p class="muted">Three steps to see Axiom govern a change: load governance, run a preflight, connect your agent.</p>

  <section aria-labelledby="s1">
    <h2 id="s1">1. Governance is loaded</h2>
    <p v-if="statusError" class="err" role="alert">{{ statusError }}</p>
    <p v-else-if="records === null" class="muted">Checking...</p>
    <p v-else-if="records > 0" class="ok">{{ records }} governance record(s) are published. Browse them in the Governance tab.</p>
    <template v-else>
      <p class="warn">No records yet. The workers register the configured governance repository and sync it every minute.</p>
      <p class="muted">If this stays empty, check the worker log: <code>kubectl -n axiom logs deploy/axiom-workers</code></p>
    </template>
    <button @click="refresh">Check again</button>
  </section>

  <section aria-labelledby="s2">
    <h2 id="s2">2. Run a preflight</h2>
    <p class="muted">This is what an agent does before editing: Axiom resolves which governance applies to the repository and the change, and says what must happen next. The demo data covers the repository <code>demo-gateway</code>.</p>
    <form class="row" @submit.prevent="run">
      <input v-model="repository" aria-label="Repository" placeholder="Repository" required />
      <input v-model="task" aria-label="Task" placeholder="What are you changing?" size="40" required />
      <input v-model="paths" aria-label="Paths" placeholder="Paths, comma separated" />
      <button type="submit" :disabled="running">Run preflight</button>
    </form>
    <p v-if="error" class="err" role="alert">{{ error }}</p>
    <article v-if="result">
      <p>Verdict: <strong :class="verdictClass(result.verdict)">{{ result.verdict }}</strong>
        · significant change: {{ result.significantChange ? 'yes' : 'no' }}</p>
      <template v-if="result.significanceTriggers.length">
        <h3>Why it is significant</h3>
        <ul><li v-for="t in result.significanceTriggers" :key="t.code">{{ t.explanation }}</li></ul>
      </template>
      <h3>Applicable governance</h3>
      <p v-if="!result.applicableGovernance.length" class="muted">None applies to this repository and change.</p>
      <ul>
        <li v-for="g in result.applicableGovernance" :key="g.id">
          <button class="link" @click="$emit('open', g.id)">{{ g.id }}</button> {{ g.title }} <span class="muted">({{ g.kind }}, {{ g.importance }})</span>
        </li>
      </ul>
      <template v-if="result.requiredActions.length">
        <h3>Required next actions</h3>
        <ul><li v-for="a in result.requiredActions" :key="a">{{ a }}</li></ul>
      </template>
      <template v-if="result.findings.length">
        <h3>Findings</h3>
        <ul><li v-for="f in result.findings" :key="f.code + f.message"><strong>{{ f.severity }}</strong> {{ f.code }}: {{ f.message }}</li></ul>
      </template>
      <template v-if="result.topologyGaps.length">
        <h3>Topology gaps</h3>
        <ul><li v-for="g in result.topologyGaps" :key="g" class="warn">{{ g }}</li></ul>
      </template>
      <p class="muted">Evaluation <code>{{ result.evaluationId }}</code> was recorded with a receipt (see the Evaluation tab).</p>
    </article>
  </section>

  <section aria-labelledby="s3">
    <h2 id="s3">3. Connect your agent</h2>
    <p>Axiom speaks MCP at <code>{{ mcpUrl }}</code>. Put this in your agent MCP configuration and export <code>AXIOM_TOKEN</code> (an access token for this deployment), then copy <code>integrations/agents-md/AGENTS.md</code> into your repository.</p>
    <pre>{{ mcpConfig }}</pre>
    <p class="muted">Starter packs for Kiro and Claude Code live in the <code>integrations/</code> folder of the Axiom repository.</p>
  </section>
</template>
