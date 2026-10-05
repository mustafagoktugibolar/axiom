<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { getSource, setSource, type SourceStatus } from '../api';

const status = ref<SourceStatus | null>(null);
const error = ref('');
const notice = ref('');
const busy = ref(false);
const form = ref({ repositoryUrl: '', branch: 'main', rootPath: 'governance' });

async function load() {
  error.value = '';
  try {
    status.value = await getSource();
    if (status.value.source) {
      form.value = { repositoryUrl: status.value.source.repositoryUrl, branch: status.value.source.branch, rootPath: status.value.source.rootPath };
    }
  } catch (e) { error.value = (e as Error).message; }
}
onMounted(load);

async function connect() {
  busy.value = true; error.value = ''; notice.value = '';
  try {
    status.value = await setSource({ ...form.value, repositoryUrl: form.value.repositoryUrl.trim() });
    notice.value = 'Connected. The workers publish its records within about a minute; use Refresh to follow along.';
  } catch (e) { error.value = (e as Error).message; } finally { busy.value = false; }
}

// "Published" means the snapshot Axiom serves was built from the repository head we can see now.
const state = () => {
  const s = status.value;
  if (!s?.configured) return { text: 'No governance repository connected yet.', cls: 'warn' };
  if (s.head?.error) return { text: `Cannot reach the repository: ${s.head.error}`, cls: 'err' };
  if (!s.snapshot) return { text: 'Connected. Waiting for the first publication.', cls: 'warn' };
  if (s.snapshot.sourceCommit === s.head?.sha) return { text: `Up to date: ${s.snapshot.recordCount} records published from ${s.snapshot.sourceCommit.slice(0, 8)}.`, cls: 'ok' };
  return { text: `Not yet published from ${s.head?.sha?.slice(0, 8)} (serving ${s.snapshot.sourceCommit.slice(0, 8)}). If this persists, the new commit has validation errors: run axiom-cli validate-governance on it; Axiom keeps the last good version.`, cls: 'warn' };
};
</script>

<template>
  <h1>Set up</h1>
  <p class="muted">Governance lives in a Git repository you own: decisions, standards, goals and exceptions as files, reviewed like code. Connect it here. Axiom only reads it, and it also reads <code>.axiom/catalog.yaml</code> from the same repository to learn your systems and repositories.</p>

  <section aria-labelledby="st">
    <h2 id="st">Status</h2>
    <p v-if="status" :class="state().cls" role="status">{{ state().text }}</p>
    <button @click="load">Refresh</button>
  </section>

  <section aria-labelledby="cn">
    <h2 id="cn">Connect your governance repository</h2>
    <p v-if="error" class="err" role="alert">{{ error }}</p>
    <p v-if="notice" class="ok" role="status">{{ notice }}</p>
    <form class="stack" @submit.prevent="connect">
      <label>Repository URL <input v-model="form.repositoryUrl" required placeholder="https://github.com/your-org/governance.git" /></label>
      <label>Branch <input v-model="form.branch" required /></label>
      <label>Records directory <input v-model="form.rootPath" placeholder="governance" /></label>
      <button type="submit" :disabled="busy">Test and connect</button>
    </form>
    <p class="muted">Axiom checks that it can read the repository before saving. <strong>Never put a token in the URL.</strong> For a private repository, give the workers a read-only token through the deployment secrets (<code>governance.credentials</code> in Helm, see <code>deploy/README.md</code>); the portal never handles credentials.</p>
  </section>

  <section aria-labelledby="how">
    <h2 id="how">What goes in the repository</h2>
    <pre>governance/
  decisions/ARCH-001-something.md     # Decision records (YAML front matter + Markdown rationale)
  exceptions/EXC-001-something.yaml   # Time-limited exceptions
.axiom/catalog.yaml                   # Systems, components, repositories and who owns them</pre>
    <p class="muted">A working example is the demo repository in the Axiom source tree: <code>deploy/local/demo/governance</code>. Run <code>axiom-cli validate-governance</code> in CI so a bad record never reaches the main branch.</p>
  </section>
</template>
