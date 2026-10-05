<script setup lang="ts">
import { ref, watchEffect } from 'vue';
import { getRecord, type GovernanceDetail } from '../api';

const props = defineProps<{ id: string }>();
defineEmits<{ back: []; open: [id: string] }>();
const detail = ref<GovernanceDetail | null>(null); const error = ref('');

watchEffect(async () => {
  detail.value = null; error.value = '';
  try { detail.value = await getRecord(props.id); } catch (e) { error.value = (e as Error).message; }
});
</script>

<template>
  <button @click="$emit('back')">Back to results</button>
  <p v-if="error" class="err" role="alert">{{ error }}</p>
  <article v-if="detail">
    <h1>{{ detail.record.title }}</h1>
    <p class="muted">{{ detail.record.id }} · {{ detail.record.kind }} · {{ detail.record.status }} · rev {{ detail.record.revision }}
      <span v-if="!detail.record.authoritative" class="warn"> · not authoritative</span></p>
    <p>{{ detail.record.statement }}</p>
    <h2>Authority</h2>
    <p>{{ detail.record.authorityLevel }} · default verdict {{ detail.record.defaultVerdict }} · {{ detail.record.exemptable ? 'exemptable' : 'not exemptable' }}</p>
    <h2>Scope</h2>
    <ul><li v-for="(v, k) in detail.record.scope" :key="k">{{ k }}: {{ v.join(', ') }}</li></ul>
    <template v-if="detail.record.forbidden.length"><h2>Forbidden</h2><ul><li v-for="f in detail.record.forbidden" :key="f">{{ f }}</li></ul></template>
    <template v-if="detail.record.preferred.length"><h2>Preferred</h2><ul><li v-for="f in detail.record.preferred" :key="f">{{ f }}</li></ul></template>
    <template v-if="detail.record.relations.length || detail.incomingRelations.length">
      <h2>Relations</h2>
      <ul>
        <li v-for="r in detail.record.relations" :key="r.kind + r.targetId">{{ r.kind }} to <button class="link" @click="$emit('open', r.targetId)">{{ r.targetId }}</button></li>
        <li v-for="r in detail.incomingRelations" :key="'in' + r.kind + r.targetId">
          <button class="link" @click="$emit('open', r.targetId)">{{ r.targetId }}</button> {{ r.kind }} this record</li>
      </ul>
    </template>
    <template v-if="detail.record.rationale"><h2>Rationale</h2><pre>{{ detail.record.rationale }}</pre></template>
    <h2>Revision history</h2>
    <table><thead><tr><th>Rev</th><th>Status</th><th>Commit</th><th>Author</th><th>When</th></tr></thead>
      <tbody><tr v-for="h in detail.history" :key="h.revision">
        <td>{{ h.revision }}</td><td>{{ h.status }}</td><td><code>{{ h.commitSha.slice(0, 10) }}</code></td><td>{{ h.author }}</td><td>{{ h.committedAt }}</td>
      </tr></tbody></table>
    <p class="muted">Source: {{ detail.sourceRepository }} / {{ detail.record.sourcePath }}</p>
  </article>
</template>
