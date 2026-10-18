# Receipt chain verification failure

Receipts form a per-organization hash chain. `GET /v1/receipts/chain/verify` recomputes every receipt (payload digest, chain link, id, and the readable columns
against the signed payload) and returns `{ intact, receipts, firstBrokenReceiptId, headChainDigest }`. Reading a single receipt (`GET /v1/receipts/{id}`)
also recomputes `Verified`.

## Symptoms

- `chain/verify` returns `intact: false` with a `firstBrokenReceiptId`, or a single receipt shows `verified: false`.
- Alert `AxiomReceiptGenerationFailing` (`axiom_receipt_failures_total`): a different problem. Receipts cannot be written; evaluations return
  `GOVERNANCE_UNAVAILABLE`. Treat it with database-unavailable.md first.
- An audit export disagrees with the stored `headChainDigest` you recorded earlier.

## Diagnosis

1. Re-run the verification and keep the output: `curl -H "Authorization: Bearer $TOKEN" https://<host>/v1/receipts/chain/verify` (needs the audit-read right).
2. Fetch the broken receipt and its neighbours: `GET /v1/receipts/{firstBrokenReceiptId}`; look at the receipts issued just before and after it for the same commit
   (`GET /v1/receipts?repository=<repo>&commitSha=<sha>&stage=<stage>`).
3. Check whether the database was restored, migrated by hand or edited: `kubectl -n axiom logs deploy/axiom-api | grep -i receipt`, database audit log, backup/restore history.
4. Known limit (threat-model R-4): verification does not detect deletion of the newest receipts (a valid prefix) and does not compare the evaluation document with its
   receipt. Compare `headChainDigest` and `receipts` with the last value you stored out of band.
5. Do not assume corruption: a stored `BLOCK` changed to `ALLOW`, a changed commit sha, or a changed issue time all break verification by design; that is the control working.

## Mitigation

1. Preserve evidence: snapshot the `axiom.receipt` and `axiom.evaluation` rows of the organization (`pg_dump -t axiom.receipt -t axiom.evaluation`), the verify output
   and database audit logs. Restrict write access to the schema to the migration identity.
2. Treat evaluations covered by receipts at and after `firstBrokenReceiptId` as untrusted. Re-evaluate the affected commits to obtain new evaluations (new receipts append
   to the chain; old ones are not rewritten). Merge gates keep using the new results.
3. If cause is an operational restore, record the incident, the restore point and the range of lost receipts; the chain continues from the restored head.
4. Fix the cause (credentials with write access to the schema, restore procedure), then rotate the database credential (secret-rotation.md).

## Escalate

Always notify the security owner on an unexplained break; it is a tamper signal. Provide the verify output, `firstBrokenReceiptId`, the evidence dump and the
database access list for the period.

## Do not

- **Never bypass the receipt chain**: do not update, delete, re-hash, re-order or re-insert receipt rows, and do not recompute and overwrite digests to make verification pass.
- Do not disable receipt generation or serve verdicts without a receipt.
- Do not truncate `axiom.receipt` to "start a new chain".
