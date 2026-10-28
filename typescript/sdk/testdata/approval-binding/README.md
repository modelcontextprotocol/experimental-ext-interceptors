# Approval-binding fixtures

Copied unchanged from
[probityai/agent-evidence-vectors](https://github.com/probityai/agent-evidence-vectors)
(Apache-2.0) at commit `50d3d312974688297569d62eebe5f45c3139053d`, from
`vectors-approval-binding/cases/<case>/record.json`. `confirmation.test.ts`
checks each file's sha256 against that commit.

| File | Expected |
|---|---|
| `allow-bound.json` | allow |
| `approval-already-consumed.json` | deny (`approval_replayed`) |

The two records differ only in `consumedApprovalIds`.
