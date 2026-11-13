#!/usr/bin/env bash

# Keep the private engine credential separate from rotating Agent/MCP keys.
load_content_engine_configuration() {
    local engine_config="$1" saved_engine_key
    if [[ -z "${CONTENT_ENGINE_KEY:-}" && -z "${Content__EngineKey:-}" && -f "$engine_config" ]]; then
        saved_engine_key="$(sed -n 's/^CONTENT_ENGINE_KEY=//p' "$engine_config" | head -n 1)"
        if [[ ${#saved_engine_key} -lt 32 || ! "$saved_engine_key" =~ ^[A-Za-z0-9._~+=/-]+$ ]]; then
            printf 'Invalid content engine credential in %s\n' "$engine_config" >&2
            return 1
        fi
        export CONTENT_ENGINE_KEY="$saved_engine_key"
    fi
}
