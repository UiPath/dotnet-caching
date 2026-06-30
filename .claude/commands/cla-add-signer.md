---
description: Add a contributor to the CLA registry (signatures/cla.json) after legal has confirmed a signed CLA is on file. For CODEOWNERS/maintainers only.
argument-hint: <github-username>
---

You are helping a **CODEOWNER/maintainer** record a contributor in the CLA registry
(`signatures/cla.json`). Adding an entry is what turns a `cla-required` pull request's
`legal/cla` check green, so treat this as a legal-record write: be precise and never
invent data.

Target signer (GitHub username), if provided: **$1**

## Hard prerequisite — confirm before doing anything
A registry entry means "UiPath legal has a valid signed CLA on file for this person/entity."
Ask the maintainer to confirm they have **verified with legal that the signed CLA is recorded**
(this is a manual, out-of-band step). If they cannot confirm, **stop** — do not add the entry.

## Steps

1. **Collect the entry fields.** Use `$1` as `githubUsername` if given; otherwise ask. Then gather the rest (ask via AskUserQuestion or prompt for any missing):
   - `githubUsername` — the contributor's GitHub login (verify it exists, e.g. `gh api users/<name>`).
   - `type` — `individual` or `corporate`.
   - `entity` — the person's name, or the company name for a corporate CLA.
   - `legalRef` — the UiPath legal record reference for the signed CLA (required; ask legal/the maintainer for it).
   - `date` — date the signed CLA was recorded, today's date in `YYYY-MM-DD` unless told otherwise.
   - `note` — optional free text.

2. **Update the registry.** Read `signatures/cla.json`. If `githubUsername` (case-insensitive) is already present, stop and report it's already recorded. Otherwise append the new entry to `signatories`, keep the file valid JSON and 2-space indented, and leave existing entries untouched. Validate it parses (`python -m json.tool signatures/cla.json` or equivalent).

3. **Open a PR — never commit to `main` directly.** The `signatures/` path is CODEOWNERS-gated so the addition gets a second pair of eyes and an audit trail:
   - Create a branch: `chore/cla-signer-<githubUsername>`.
   - Commit with sign-off (`git commit -s`); message e.g. `chore(cla): record signed CLA for <githubUsername> (<legalRef>)`.
   - Push and open a PR to `main`. In the PR body, state the legal reference and that legal confirmation was obtained. Do **not** paste the CLA document or personal contact details beyond what the schema needs.

4. **Tell the maintainer the follow-up.** After this PR merges, the contributor's own `cla-required` pull request must re-evaluate against the updated registry — it does not happen automatically. Instruct them to **toggle the `cla-required` label off and back on** on that PR (or have the contributor push a commit) so `cla.yml` re-runs and `legal/cla` turns green.

## Notes
- One entry per authorized GitHub username. For a corporate CLA covering several people, add one `type: corporate` entry per username, all referencing the same `entity`/`legalRef`.
- See `signatures/README.md` for the schema and `CONTRIBUTING.md` for the full flow.
