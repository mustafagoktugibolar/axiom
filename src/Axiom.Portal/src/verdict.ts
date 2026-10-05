// The API spells verdicts differently per surface (BLOCK / Block / RequireReview / REQUIRE_REVIEW).
// Normalise once so every screen colours and words them the same way.
const key = (v: string) => v.replace(/[_\s]/g, '').toUpperCase();

export function verdictClass(v: string): string {
  switch (key(v)) {
    case 'ALLOW': case 'INFO': return 'ok';
    case 'ALLOWWITHWARNINGS': case 'WARN': case 'REQUIREREVIEW': return 'warn';
    default: return 'err';
  }
}

export function verdictLabel(v: string): string {
  switch (key(v)) {
    case 'ALLOW': return 'Allowed';
    case 'ALLOWWITHWARNINGS': return 'Allowed with warnings';
    case 'REQUIREREVIEW': return 'Needs review';
    case 'BLOCK': return 'Blocked';
    default: return v;
  }
}
