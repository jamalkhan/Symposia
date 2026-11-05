#!/usr/bin/env bash
# Protocol compatibility checks against a base ref (issue #146). Rules and fixes:
# docs/engineering/protocol-compatibility.md
#
# Usage: tools/protocol-compat/run.sh <base-ref> [check...]
#   checks: schema fixtures registry storage-layout migrations   (default: all)
# Env: ALLOW_DESTRUCTIVE_MIGRATION=1 skips the migrations check (set by CI when a maintainer applies
#      the migration-contract-approved label).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$ROOT"
BASE="${1:?usage: run.sh <base-ref> [check...]}"; shift
CHECKS="${*:-schema fixtures registry storage-layout migrations}"
COMPAT="python3 -I $ROOT/tools/protocol-compat/compat.py"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
status=0
at_base() { git show "$BASE:$1" >"$TMP/base" 2>/dev/null; }  # false when the file is new
run() { echo "── $1"; shift; "$@" || status=1; }

git rev-parse --verify -q "$BASE^{commit}" >/dev/null || { echo "::error::base ref '$BASE' not found (fetch it first)"; exit 2; }
echo "Protocol compatibility vs $BASE ($(git rev-parse --short "$BASE"))"

for check in $CHECKS; do
  case "$check" in
    schema)
      for schema in protocol/schemas/*.schema.json; do
        if at_base "$schema"; then run "schema $schema" $COMPAT schema "$TMP/base" "$schema"; else echo "── schema $schema (new)"; fi
      done ;;
    fixtures)
      # History can't change, and every fixture must still validate against the current schema.
      echo "── fixtures"
      python3 -I - "$BASE" <<'PY' || status=1
import json, subprocess, sys, glob, os
sys.path.insert(0, "tools/protocol-compat")
import compat
base = sys.argv[1]
listed = subprocess.run(["git", "ls-tree", "-r", "--name-only", base, "protocol/fixtures"], capture_output=True, text=True).stdout.split()
base_files = {p: subprocess.run(["git", "show", f"{base}:{p}"], capture_output=True, text=True).stdout for p in listed if p.endswith(".json")}
head_files = {p: open(p).read() for p in glob.glob("protocol/fixtures/**/*.json", recursive=True)}
problems = compat.diff_fixtures(base_files, head_files)
for path, content in head_files.items():
    schema = f"protocol/schemas/{os.path.basename(os.path.dirname(path))}.schema.json"
    if not os.path.exists(schema):
        problems.append(f"{path}: no schema {schema} for this fixture folder")
        continue
    problems += [f"{path}: {p}" for p in compat.validate(json.loads(content), json.load(open(schema)))]
for p in problems:
    print(f"::error::{p} (see {compat.DOC})")
sys.exit(1 if problems else 0)
PY
      ;;
    registry)
      if at_base protocol/features.json; then run "registry" $COMPAT registry "$TMP/base" protocol/features.json
      else run "registry (new)" $COMPAT registry - protocol/features.json; fi ;;
    storage-layout)
      run "storage layout" tools/protocol-compat/storage-layout.sh --check "$BASE" ;;
    migrations)
      if [ "${ALLOW_DESTRUCTIVE_MIGRATION:-0}" = "1" ]; then echo "── migrations (skipped: migration-contract-approved)"; continue; fi
      # From the merge base, so base-branch changes aren't counted, to the working tree, so it also works before committing.
      run "migrations" sh -c "git diff \"\$(git merge-base '$BASE' HEAD)\" -U0 | $COMPAT migrations" ;;
    *) echo "::error::unknown check $check"; status=2 ;;
  esac
done

[ $status -eq 0 ] && echo "Protocol compatibility: OK" || echo "Protocol compatibility: FAILED (fixes: docs/engineering/protocol-compatibility.md#the-checks)"
exit $status
