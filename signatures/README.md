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

An entry means "UiPath legal has a valid signed CLA on file for this person or entity",
and it is what turns a `cla-required` pull request's `legal/cla` check green. Treat it as
a legal-record write: be precise, and never fill a field with a guess.

**Prerequisite.** Confirm out of band that legal has recorded the signed CLA, and obtain
the `legalRef` for it. Without that confirmation, stop — do not add the entry. This is the
one step nothing in the repository can check for you.

For maintainers, in order:

1. **Gather the fields** from the schema above. `legalRef` comes from legal, not from you;
   `date` is the date the signed CLA was recorded. Verify the GitHub login exists
   (`gh api users/<name>`), since the check matches on it.
2. **Check it is not already there.** Match `githubUsername` case-insensitively — if it is
   present, the contributor is already covered and there is nothing to add.
3. **Append to `signatories`** in `cla.json`, leaving existing entries untouched, and
   confirm the file still parses (`python -m json.tool signatures/cla.json`).
4. **Open a pull request — never commit to `main`.** This directory is gated by
   [`CODEOWNERS`](../.github/CODEOWNERS), so the addition gets a second pair of eyes and an
   audit trail. Sign off the commit (`git commit -s`). In the PR body, state the legal
   reference and that legal confirmation was obtained — **do not paste the CLA document or
   personal contact details beyond what the schema needs.**
5. **Re-trigger the contributor's check** once the addition merges; it does not re-evaluate
   on its own. Toggle the `cla-required` label off and back on, or have them push a commit.

The CLA covers a contributor's present and future contributions, so a signatory
stays in this file permanently (unless they change employer — see clause 5.3 of the
CLA, which requires a new agreement).
