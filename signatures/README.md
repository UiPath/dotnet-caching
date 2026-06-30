# CLA registry

`cla.json` is the **authoritative record** of contributors who have a signed
[Contributor License Agreement](../CLA.md) on file with UiPath. The `legal/cla`
status check ([`.github/workflows/cla.yml`](../.github/workflows/cla.yml)) reads
this file from the default branch: when a maintainer marks a pull request
`cla-required`, the check passes only once the PR author appears here.

This is intentionally a committed, reviewed, version-controlled record rather than
a label — every addition is an auditable change with who/what/when and a reference
to the legal record.

## Entry schema

```jsonc
{
  "signatories": [
    {
      "githubUsername": "octocat",          // GitHub login (case-insensitive match)
      "type": "individual",                  // "individual" or "corporate"
      "entity": "Jane Doe",                  // person, or company name for corporate
      "legalRef": "CLA-2026-0042",           // UiPath legal record reference
      "date": "2026-06-30",                  // date the signed CLA was recorded (YYYY-MM-DD)
      "note": "optional free-text"           // optional
    }
  ]
}
```

For a **corporate** CLA, add one entry per authorized GitHub username covered by
that company's agreement (set `type: "corporate"` and the company name in `entity`).

## Adding a signatory

Entries are added **only after a CODEOWNER has verified with UiPath legal that the
signed CLA is on file.** Additions go through a pull request and are gated by
[`CODEOWNERS`](../.github/CODEOWNERS) on this directory, so they require maintainer
approval and leave an audit trail.

Maintainers: use the `cla-add-signer` skill (in `.claude/skills/`) to do this, or
edit `cla.json` by hand following the schema above. After the addition merges,
re-trigger the contributor's `legal/cla` check (toggle the `cla-required` label off
and on, or have them push) so it re-evaluates against the updated registry.

The CLA covers a contributor's present and future contributions, so a signatory
stays in this file permanently (unless they change employer — see clause 5.3 of the
CLA, which requires a new agreement).
