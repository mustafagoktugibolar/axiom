// Thin typed client. The token lives in sessionStorage only (cleared with the tab) and is sent
// only to same-origin /v1 endpoints. Vue escapes all interpolated text, so record content is never
// rendered as HTML.
export interface GovernanceSummary {
  id: string; kind: string; title: string; status: string; isAuthoritative: boolean;
  owners: string[]; tags: string[]; authorityLevel: string; exemptable: boolean;
  revision: number; reviewAfter?: string; isStale: boolean; sourcePath: string; sourceCommit: string;
}
export interface GovernanceRecord {
  id: string; kind: string; title: string; status: string; authoritative: boolean; revision: number;
  owners: string[]; tags: string[]; authorityLevel: string; exemptable: boolean;
  scope: Record<string, string[]>; defaultVerdict: string; statement: string;
  forbidden: string[]; preferred: string[]; relations: { kind: string; targetId: string }[];
  rules: { ruleId: string; mode: string }[]; sourcePath: string; sourceCommit: string; rationale?: string;
}
export interface GovernanceDetail {
  record: GovernanceRecord; sourceRepository: string;
  history: { revision: number; status: string; commitSha: string; committedAt: string; author: string }[];
  incomingRelations: { kind: string; targetId: string }[];
}

import { getToken } from './auth';

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

export async function get<T>(path: string, params: Record<string, string | number | boolean | undefined> = {}): Promise<T> {
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') qs.set(k, String(v));
  const query = qs.toString();
  const res = await fetch(query ? `${path}?${query}` : path, { headers: { Authorization: `Bearer ${getToken()}`, Accept: 'application/json' } });
  if (!res.ok) {
    let detail = res.statusText;
    try { const p = await res.json(); detail = p.detail ?? p.title ?? detail; } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, res.status === 401 ? 'Your session expired or you are not signed in.' : detail);
  }
  return (await res.json()) as T;
}

export async function post<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(path, {
    method: 'POST',
    headers: { Authorization: `Bearer ${getToken()}`, 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    let detail = res.statusText;
    try { const p = await res.json(); detail = p.detail ?? p.title ?? detail; } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, res.status === 401 ? 'Your session expired or you are not signed in.' : detail);
  }
  return (await res.json()) as T;
}

export interface PreflightResult {
  evaluationId: string; verdict: string; significantChange: boolean;
  significanceTriggers: { code: string; explanation: string }[];
  resolvedScope: Record<string, string[]>; topologyGaps: string[];
  applicableGovernance: { id: string; kind: string; title: string; importance: string }[];
  requiredActions: string[]; requiredReviewers: string[];
  findings: { code: string; severity: string; message: string }[];
}
export const preflight = (p: { repository: string; ref: string; task: string; paths?: string[] }) =>
  post<PreflightResult>('/v1/evaluations/preflight', p);
export const countGovernance = () => get<{ total: number }>('/v1/governance', { take: 1 });

export const searchGovernance = (p: { q?: string; kind?: string; status?: string; repository?: string; staleOnly?: boolean }) =>
  get<{ items: GovernanceSummary[]; total: number }>('/v1/governance', { ...p, take: 50 });
export const getRecord = (id: string) => get<GovernanceDetail>(`/v1/governance/${encodeURIComponent(id)}`, { includeRationale: true });
export const getEvaluation = (id: string) => get<Record<string, unknown>>(`/v1/evaluations/${encodeURIComponent(id)}`);
export const getReceipt = (id: string) => get<Record<string, unknown>>(`/v1/receipts/${encodeURIComponent(id)}`);
export const pendingReviews = () => get<unknown>('/v1/reviews/pending');

// ---- evaluations, reviews, receipts ----
export interface EvaluationSummary {
  id: string; stage: string; verdict: string; repository: string; commitSha: string; createdAt: string;
  actorSubject: string; significantChange: boolean; findingCount: number; receiptId: string;
}
export interface Finding {
  code: string; severity: string; message: string; source: string; governanceIds: string[];
  recommendedAction?: string; path?: string; line?: number;
}
export interface EvaluationDetail {
  evaluation: PreflightResult & {
    stage: string; receiptId: string; createdAt: string; findings: Finding[];
    requiredChecks: { ruleId: string; recordId: string; mode: string }[];
    activeExceptions: { id: string; expiresAt: string; coverage: string }[];
    affectedDependencies: { entity: string; depth: number; owners: string[] }[];
  };
  actor: { subject: string; displayName?: string };
  scm?: { repository: string; commitSha: string; baseSha?: string };
  clearance: string;
  reviews: { kind: string; outcome: string; findingCode?: string; reviewer: string; comment: string; decidedAt: string }[];
}
export interface ReviewQueueItem { evaluation: EvaluationSummary; requiredReviewers: string[]; findingCodes: string[] }
export interface Receipt {
  receiptId: string; evaluationId: string; stage: string; verdict: string; repository: string; commitSha: string;
  issuedAt: string; digest: string; chainDigest: string; verified: boolean;
}

export const listEvaluations = (p: { verdict?: string; repository?: string } = {}) =>
  get<{ total: number; items: EvaluationSummary[] }>('/v1/evaluations', { ...p, take: 50 });
export const getEvaluationDetail = (id: string) => get<EvaluationDetail>(`/v1/evaluations/${encodeURIComponent(id)}`);
export const getReceiptById = (id: string) => get<Receipt>(`/v1/receipts/${encodeURIComponent(id)}`);
export const verifyChain = () => get<{ intact: boolean; receipts: number }>('/v1/receipts/chain/verify');
export const listPendingReviews = () => get<ReviewQueueItem[]>('/v1/reviews/pending');
export const submitReview = (id: string, approve: boolean, comment: string) =>
  post<unknown>(`/v1/evaluations/${encodeURIComponent(id)}/review`, { approve, comment });
export const overturnFinding = (id: string, code: string, comment: string) =>
  post<unknown>(`/v1/evaluations/${encodeURIComponent(id)}/findings/${encodeURIComponent(code)}/overturn`, { comment });
export const validateDiff = (p: { repository: string; baseSha: string; headSha: string }) =>
  post<PreflightResult & { findings: Finding[]; receiptId: string }>('/v1/evaluations/diff', p);
export const getDemo = () => get<{ repository: string; baseSha: string; headSha: string; title: string }>('/v1/demo');

// ---- exceptions ----
export interface ExceptionRequest {
  id: string; status: string; requester: string; targets: string[]; title: string; rationale: string;
  startsAt: string; expiresAt: string; trackingIssue?: string; requiredApprovers: string[];
  draftRecordPath: string; draftRecord: string; nextStep: string;
  decision?: { approved: boolean; approver: string; comment: string; decidedAt: string };
}
export const listExceptions = () => get<{ total: number; items: ExceptionRequest[] }>('/v1/exceptions/requests', { take: 50 });
export const requestException = (b: {
  targets: string[]; scope: Record<string, string[]>; title: string; rationale: string; expiresAt: string;
  trackingIssue: string; compensatingControls: string[];
}) => post<ExceptionRequest>('/v1/exceptions/requests', b);
export const decideException = (id: string, approve: boolean, comment: string) =>
  post<ExceptionRequest>(`/v1/exceptions/requests/${encodeURIComponent(id)}/decision`, { approve, comment });

// ---- system graph ----
export interface GraphData {
  truncated: boolean;
  nodes: { id: string; kind: string; title: string; technologies: string[] }[];
  edges: { from: string; relation: string; to: string; fact: boolean }[];
}
export const getGraph = (focus?: string) => get<GraphData>('/v1/graph', { focus, depth: 3, maxNodes: 120 });

// ---- onboarding ----
export interface SourceStatus {
  configured: boolean;
  source?: { repositoryUrl: string; branch: string; rootPath: string };
  snapshot?: { snapshotId: string; sourceCommit: string; publishedAt: string; recordCount: number } | null;
  head?: { sha: string | null; committedAt: string | null; error: string | null };
}
export const getSource = () => get<SourceStatus>('/v1/admin/governance-source');
export async function setSource(b: { repositoryUrl: string; branch: string; rootPath: string }): Promise<SourceStatus> {
  const res = await fetch('/v1/admin/governance-source', {
    method: 'PUT',
    headers: { Authorization: `Bearer ${getToken()}`, 'Content-Type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify(b),
  });
  if (!res.ok) {
    let detail = res.statusText;
    try { const p = await res.json(); detail = p.error?.message ?? p.detail ?? p.title ?? detail; } catch { /* non-JSON error body */ }
    throw new ApiError(res.status, detail);
  }
  return (await res.json()) as SourceStatus;
}
