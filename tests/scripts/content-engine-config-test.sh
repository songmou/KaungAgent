#!/usr/bin/env bash
set -Eeuo pipefail
readonly PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$PROJECT_DIR/scripts/content-engine-config.sh"
readonly FIXTURE_DIR="$(mktemp -d)"
trap 'rm -rf "$FIXTURE_DIR"' EXIT
unset CONTENT_ENGINE_KEY Content__EngineKey
load_content_engine_configuration "$FIXTURE_DIR/absent.env"
test -z "${CONTENT_ENGINE_KEY:-}"
printf 'CONTENT_ENGINE_KEY=saved-engine-key-with-at-least-32-characters\n' > "$FIXTURE_DIR/engine.env"
load_content_engine_configuration "$FIXTURE_DIR/engine.env"
test "$CONTENT_ENGINE_KEY" = saved-engine-key-with-at-least-32-characters
export CONTENT_ENGINE_KEY=explicit-engine-key-with-at-least-32-characters
load_content_engine_configuration "$FIXTURE_DIR/engine.env"
test "$CONTENT_ENGINE_KEY" = explicit-engine-key-with-at-least-32-characters
unset CONTENT_ENGINE_KEY
export Content__EngineKey=override-engine-key-with-at-least-32-characters
load_content_engine_configuration "$FIXTURE_DIR/engine.env"
test -z "${CONTENT_ENGINE_KEY:-}"
unset Content__EngineKey
printf 'CONTENT_ENGINE_KEY=invalid\n' > "$FIXTURE_DIR/engine.env"
if load_content_engine_configuration "$FIXTURE_DIR/engine.env" 2>/dev/null; then
    printf 'Invalid engine credential was accepted.\n' >&2
    exit 1
fi
printf 'Content engine credential startup checks passed.\n'
