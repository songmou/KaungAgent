#!/usr/bin/env bash
set -Eeuo pipefail
readonly PROJECT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ENGINE_CONFIG="$PROJECT_DIR/.kaung/content-engine.env"
readonly ENGINE_COMPOSE="$PROJECT_DIR/infra/content-engine/compose.yml"
source "$PROJECT_DIR/scripts/content-engine-config.sh"
command -v docker >/dev/null || { printf 'Docker is required for the container deployment.\n' >&2; exit 1; }
docker compose version >/dev/null
docker info >/dev/null
umask 077
mkdir -p "$PROJECT_DIR/.kaung"
load_content_engine_configuration "$ENGINE_CONFIG"
CONTENT_ENGINE_KEY="${Content__EngineKey:-${CONTENT_ENGINE_KEY:-}}"
if [[ -z "$CONTENT_ENGINE_KEY" ]]; then
    CONTENT_ENGINE_KEY="$(openssl rand -hex 32)"
fi
if [[ ${#CONTENT_ENGINE_KEY} -lt 32 || ! "$CONTENT_ENGINE_KEY" =~ ^[A-Za-z0-9._~+=/-]+$ ]]; then
    printf 'Content engine credential must have at least 32 safe characters.\n' >&2
    exit 1
fi
export CONTENT_ENGINE_KEY
printf 'CONTENT_ENGINE_KEY=%s\n' "$CONTENT_ENGINE_KEY" > "$ENGINE_CONFIG"
chmod 600 "$ENGINE_CONFIG"
compose_engine() { docker compose --env-file "$ENGINE_CONFIG" --file "$ENGINE_COMPOSE" "$@"; }

if ! docker image inspect kaung-content-engine:1.0.0 >/dev/null 2>&1; then
    compose_engine build
fi
# Optional offline seed from a previously prepared model set. The source is
# copied into the named volume so temporary directories are not runtime inputs.
if [[ -n "${CONTENT_ENGINE_MODEL_SOURCE:-}" ]]; then
    test -f "$CONTENT_ENGINE_MODEL_SOURCE/model-lock.json"
    compose_engine run --rm --no-deps -T \
        --volume "$CONTENT_ENGINE_MODEL_SOURCE:/seed:ro" engine \
        sh -c 'cp -R /seed/. /models/'
fi
if ! compose_engine run --rm --no-deps -T engine python -c \
    'from app import health; assert all(health(c)["ready"] for c in ("ocr", "document", "speech"))'; then
    printf 'Preparing content engine models; the first download may take several minutes.\n'
    compose_engine run --rm --no-deps -T engine python prefetch.py
fi
compose_engine up --detach --no-build --wait --wait-timeout 120
printf 'Content engine is ready on loopback port %s. Restart ./start.sh to load its saved credential.\n' "${CONTENT_ENGINE_PORT:-8092}"
