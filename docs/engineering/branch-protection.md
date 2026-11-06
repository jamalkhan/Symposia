# Branch protection

The compatibility checks (#146) only gate merges if GitHub requires them. Apply these settings in **Settings → Branches** (or as a ruleset) on the repository. They are applied by the repo owner, not by CI.

## `qa`, `stage`, `master`

`master` is the production branch; there is no separate `production` branch.

| Setting | Value |
|---|---|
| Require a pull request before merging | On |
| Required approvals | 1 |
| Require review from Code Owners | On (enforces `.github/CODEOWNERS` on protocol paths) |
| Dismiss stale approvals when new commits are pushed | On |
| Require status checks to pass | On, branch must be up to date |
| Required checks | `.NET tests`, `Contracts (forge test)`, `Compatibility tool tests`, `Compatibility vs base`, `gRPC breaking changes (buf)`, `N and N-1 hosts together` |
| Require conversation resolution | On |
| Allow force pushes / deletions | Off |

## `dev`

`dev` is the integration branch. Maintainers merge feature branches into it locally before opening the PR into `qa`. Make the same checks required, but don't require a pull request:

| Setting | Value |
|---|---|
| Require status checks to pass | On (same checks as above) |
| Allow force pushes / deletions | Off |

Community contributions arrive as PRs into `dev` and get the same checks and code-owner review through the PR.

## With the GitHub CLI

```sh
gh api -X PUT repos/jamalkhan/Symposia/branches/qa/protection --input - <<'JSON'
{
  "required_status_checks": { "strict": true, "contexts": [
    ".NET tests", "Contracts (forge test)", "Compatibility tool tests",
    "Compatibility vs base", "gRPC breaking changes (buf)", "N and N-1 hosts together" ] },
  "enforce_admins": false,
  "required_pull_request_reviews": { "required_approving_review_count": 1, "require_code_owner_reviews": true, "dismiss_stale_reviews": true },
  "required_conversation_resolution": true,
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
JSON
```

Repeat for `master` and `stage`.

A check only appears in the picker after it has run once, so open one PR first. With a single maintainer, a required approval means you need a second account or an admin bypass to merge your own PRs. Keep `enforce_admins` false until there is a second maintainer.
