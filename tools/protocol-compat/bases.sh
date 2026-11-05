#!/usr/bin/env bash
# Prints the refs compatibility checks compare against (issue #146), one per line as key=value:
#   ref=<base branch or previous commit>   tag=<latest v* release reachable from that base, or empty>
# The release is the newest tag *reachable from the base*, so hotfix or pre-release tags on other
# branches never become the baseline.
set -euo pipefail
event="${1:?event}" base_ref="${2:-}" before="${3:-}"
if [ "$event" = "pull_request" ]; then base="origin/$base_ref"; else base="$before"; fi
git rev-parse -q --verify "$base^{commit}" >/dev/null 2>&1 || base="HEAD~1"
echo "ref=$base"
echo "tag=$(git describe --tags --abbrev=0 --match 'v[0-9]*' --exclude '*-*' "$base" 2>/dev/null || true)"
