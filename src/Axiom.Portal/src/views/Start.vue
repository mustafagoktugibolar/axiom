<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { countGovernance, getDemo, preflight, validateDiff, type Finding, type PreflightResult } from '../api';
import { verdictClass, verdictLabel } from '../verdict';
import { accessToken } from '../auth';

defineEmits<{ openRecord: [id: string]; openEvaluation: [id: string] }>();

// 1. governance loaded
const records = ref<number | null>(null);
const statusError = ref('');
async function refresh() {
  statusError.value = '';
  try { records.value = (await countGovernance()).total; } catch (e) { statusError.value = (e as Error).message; }
}

// 2. preflight
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

// 3. validate a pull request (the bundled demo change)
const demo = ref<{ repository: string; baseSha: string; headSha: string; title: string } | null>(null);
const diff = ref<(PreflightResult & { findings: Finding[]; receiptId: string }) | null>(null);
const diffError = ref('');
const checking = ref(false);
async function checkDiff() {
  if (!demo.value) return;
  checking.value = true; diffError.value = ''; diff.value = null;
  try { diff.value = await validateDiff({ repository: demo.value.repository, baseSha: demo.value.baseSha, headSha: demo.value.headSha }); }
  catch (e) { diffError.value = (e as Error).message; } finally { checking.value = false; }
}

onMounted(async () => {
  await refresh();
  demo.value = await getDemo().catch(() => null);
});

const copied = ref('');
async function copyToken() {
  try { await navigator.clipboard.writeText(accessToken()); copied.value = 'Copied. Paste it into AXIOM_TOKEN; it stops working when your session expires.'; }
  catch { copied.value = 'Could not copy automatically. Your browser blocked clipboard access.'; }
}

const mcpUrl = `${location.origin}/mcp`;
const mcpConfig = JSON.stringify({ mcpServers: { axiom: { type: 'http', url: mcpUrl, headers: { Authorization: 'Bearer ${AXIOM_TOKEN}' } } } }, null, 2);
</script>

<template>
  <h1>Get started</h1>
  <p class="muted">Axiom checks changes against your governance records (decisions, standards, exceptions) before and after code is written. Try it in four steps.</p>

  <section aria-labelledby="s1">
    <h2 id="s1">1. Governance is loaded</h2>
    <p v-if="statusError" class="err" role="alert">{{ statusError }}</p>
    <p v-else-if="records === null" class="muted">Checking...</p>
    <p v-else-if="records > 0" class="ok">{{ records }} governance record(s) are published. Browse them in the Governance tab, or see how they relate in the System graph.</p>
    <template v-else>
      <p class="warn">No records yet. The workers register the configured governance repository and sync it periodically.</p>
      <p class="muted">If this stays empty, check the worker log: <code>kubectl -n axiom logs deploy/axiom-workers</code></p>
    </template>
    <button @click="refresh">Check again</button>
    <p class="muted">Using your own governance repository? Connect it in <strong>Set up</strong>.</p>
  </section>

  <section aria-labelledby="s2">
    <h2 id="s2">2. Preflight: what applies before I start?</h2>
    <p class="muted">This is what an agent does before editing: Axiom resolves which governance applies to the repository and the change, and says what must happen next. The demo covers the repository <code>demo-gateway</code>.</p>
    <form class="row" @submit.prevent="run">
      <input v-model="repository" aria-label="Repository" placeholder="Repository" required />
      <input v-model="task" aria-label="Task" placeholder="What are you changing?" size="40" required />
      <input v-model="paths" aria-label="Paths" placeholder="Paths, comma separated" />
      <button type="submit" :disabled="running">Run preflight</button>
    </form>
    <p v-if="error" class="err" role="alert">{{ error }}</p>
    <article v-if="result" class="card">
      <p>Result: <strong :class="verdictClass(result.verdict)">{{ verdictLabel(result.verdict) }}</strong>
        · significant change: {{ result.significantChange ? 'yes' : 'no' }}</p>
      <template v-if="result.significanceTriggers.length">
        <h3>Why it is significant</h3>
        <ul><li v-for="t in result.significanceTriggers" :key="t.code">{{ t.explanation }}</li></ul>
      </template>
      <h3>Applicable governance</h3>
      <p v-if="!result.applicableGovernance.length" class="muted">None applies to this repository and change.</p>
      <ul>
        <li v-for="g in result.applicableGovernance" :key="g.id">
          <button class="link" @click="$emit('openRecord', g.id)">{{ g.id }}</button> {{ g.title }} <span class="muted">({{ g.kind }}, {{ g.importance }})</span>
        </li>
      </ul>
      <template v-if="result.requiredActions.length">
        <h3>Required next actions</h3>
        <ul><li v-for="a in result.requiredActions" :key="a">{{ a }}</li></ul>
      </template>
      <template v-if="result.topologyGaps.length">
        <h3>Topology gaps</h3>
        <ul><li v-for="g in result.topologyGaps" :key="g" class="warn">{{ g }}</li></ul>
      </template>
      <button class="link" @click="$emit('openEvaluation', result.evaluationId)">Open the full evaluation and receipt</button>
    </article>
  </section>

  <section aria-labelledby="s3">
    <h2 id="s3">3. Check a pull request</h2>
    <template v-if="demo">
      <p class="muted">The demo code repository has a branch with one change: <em>{{ demo.title }}</em>. It makes the Gateway query the database directly, which decision ARCH-001 forbids. Axiom checks the actual code, not the plan.</p>
      <button :disabled="checking" @click="checkDiff">Validate this change</button>
      <p v-if="diffError" class="err" role="alert">{{ diffError }}</p>
      <article v-if="diff" class="card">
        <p>Result: <strong :class="verdictClass(diff.verdict)">{{ verdictLabel(diff.verdict) }}</strong></p>
        <div v-for="f in diff.findings" :key="f.code + f.message">
          <p><strong :class="verdictClass(f.severity)">{{ f.severity }}</strong> {{ f.message }}</p>
          <p v-if="f.recommendedAction" class="muted">What to do: {{ f.recommendedAction }}</p>
        </div>
        <button class="link" @click="$emit('openEvaluation', diff.evaluationId)">Open the evaluation: review it, overturn a finding, or request an exception</button>
      </article>
    </template>
    <p v-else class="muted">In your own setup this runs from CI on every pull request: <code>axiom-cli evaluate-pr</code> (see <code>docs/operations/branch-policy.md</code>). The bundled demo change is only available in the local trial.</p>
  </section>

  <section aria-labelledby="s4">
    <h2 id="s4">4. Connect your agent</h2>
    <p>Axiom speaks MCP at <code>{{ mcpUrl }}</code>. Put this in your agent MCP configuration and export <code>AXIOM_TOKEN</code> (an access token for this deployment), then copy <code>integrations/agents-md/AGENTS.md</code> into your repository.</p>
    <pre>{{ mcpConfig }}</pre>
    <button @click="copyToken">Copy my access token</button>
    <span v-if="copied" class="muted" role="status"> {{ copied }}</span>
    <p class="muted">Starter packs for Kiro and Claude Code live in the <code>integrations/</code> folder of the Axiom repository.</p>
  </section>
</template>
