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

export const searchGovernance = (p: { q?: string; kind?: string; status?: string; repository?: string; staleOnly?: boolean }) =>
  get<{ items: GovernanceSummary[]; total: number }>('/v1/governance', { ...p, take: 50 });
export const getRecord = (id: string) => get<GovernanceDetail>(`/v1/governance/${encodeURIComponent(id)}`, { includeRationale: true });
export const getEvaluation = (id: string) => get<Record<string, unknown>>(`/v1/evaluations/${encodeURIComponent(id)}`);
export const getReceipt = (id: string) => get<Record<string, unknown>>(`/v1/receipts/${encodeURIComponent(id)}`);
export const pendingReviews = () => get<unknown>('/v1/reviews/pending');
