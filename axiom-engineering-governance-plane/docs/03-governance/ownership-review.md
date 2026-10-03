# Ownership and Review

## Ownership model

Every accepted record has at least one accountable owner entity.
Owners should be teams/roles rather than individuals where possible.

## Review routing

Reviewers derive from:
- governance record owners;
- system/component owners;
- security/compliance owners;
- exception approvers;
- explicit CODEOWNERS-like mappings.

## Conflict review

An unresolved conflict package contains:
- conflicting record IDs/revisions;
- scopes;
- why both apply;
- affected systems/repos;
- proposed change/design;
- suggested resolution options;
- owner list.

Human outcome must be one of:
- clarify/refine scope;
- supersede a record;
- create a new decision;
- grant scoped exception;
- reject change.

## Governance health review

Monthly/quarterly:
- stale decisions;
- orphaned owners;
- frequently waived decisions;
- frequently violated rules;
- semantic findings often overturned;
- duplicate decisions;
- missing topology coverage.
