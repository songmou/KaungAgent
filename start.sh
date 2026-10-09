#!/usr/bin/env bash

set -Eeuo pipefail

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly STATE_DIR="${SCRIPT_DIR}/.kaung"
readonly CONFIG_FILE="${STATE_DIR}/local.env"
readonly SOLUTION_FILE="${SCRIPT_DIR}/KaungAgent.sln"
MCP_LOCAL_PORT=5091
MCP_URL="http://127.0.0.1:${MCP_LOCAL_PORT}"
readonly AGENT_URL="http://127.0.0.1:5090"
readonly MCP_DLL="${SCRIPT_DIR}/src/KaungMcp/bin/Debug/net10.0/KaungMcp.dll"
readonly AGENT_DLL="${SCRIPT_DIR}/src/KaungAgent/bin/Debug/net10.0/KaungAgent.dll"
readonly MCP_RUNTIME_CONFIG="${SCRIPT_DIR}/src/KaungMcp/bin/Debug/net10.0/KaungMcp.runtimeconfig.json"
readonly PLAYWRIGHT_DLL="${SCRIPT_DIR}/src/KaungMcp/bin/Debug/net10.0/Microsoft.Playwright.dll"
readonly SEARXNG_COMPOSE_FILE="${SCRIPT_DIR}/infra/searxng/compose.yml"

MCP_PID=""
AGENT_PID=""
TEMP_RESPONSE=""
CLEANUP_COMPLETE=0
RECONFIGURE=0
NO_OPEN=0
STARTED_SEARXNG=0

KAUNG_MCP_ADMIN_KEY=""
KAUNG_AGENT_API_KEY=""
KAUNG_MCP_CLIENT_ID=""
KAUNG_MCP_API_KEY=""
DEEPSEEK_API_KEY=""
SEARXNG_ENDPOINT="${SEARXNG_ENDPOINT:-}"
SEARXNG_PORT_EXPLICIT=0
if [[ "${SEARXNG_PORT+x}" == "x" ]]; then
    SEARXNG_PORT_EXPLICIT=1
fi
SEARXNG_PORT="${SEARXNG_PORT:-8080}"
SEARXNG_DISABLED="${SEARXNG_DISABLED:-0}"
SEARXNG_MODE=""

log() {
    printf '[launcher] %s\n' "$*"
}

fail() {
    printf '[launcher] ERROR: %s\n' "$*" >&2
    exit 1
}

usage() {
    cat <<'EOF'
Usage: ./start.sh [--reconfigure] [--no-open] [--help]

  (no option)       Start KaungMcp and KaungAgent. The first run needs no input
                    and generates secure local secrets automatically.
  --reconfigure     Generate fresh local secrets and rotate (or recreate) the
                    MCP client key before starting KaungAgent.
  --no-open         Do not open the Agent and MCP pages in a browser.
  --help            Show this help message.

Both services run in this terminal. Press Ctrl+C to stop them together.
EOF
}

parse_arguments() {
    local argument
    for argument in "$@"; do
        case "$argument" in
            --reconfigure) RECONFIGURE=1 ;;
            --no-open) NO_OPEN=1 ;;
            --help|-h) usage; exit 0 ;;
            *) usage >&2; exit 2 ;;
        esac
    done
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command '$1' was not found."
}

check_prerequisites() {
    require_command dotnet
    require_command curl
    require_command openssl
    require_command sed
    case "$SEARXNG_DISABLED" in
        1|true|TRUE|yes|YES) SEARXNG_DISABLED=1 ;;
        0|false|FALSE|no|NO|"") SEARXNG_DISABLED=0 ;;
        *) fail "SEARXNG_DISABLED must be 0/1 or true/false." ;;
    esac

    if [[ -z "$SEARXNG_ENDPOINT" && "$SEARXNG_DISABLED" -eq 0 ]]; then
        [[ "$SEARXNG_PORT" =~ ^[0-9]+$ ]] && (( SEARXNG_PORT >= 1 && SEARXNG_PORT <= 65535 )) \
            || fail "SEARXNG_PORT must be a number between 1 and 65535."
    fi

    local sdk_version
    sdk_version="$(dotnet --version 2>/dev/null)" || fail "The .NET SDK required by global.json is not installed."
    [[ "$sdk_version" == 10.* ]] || fail "KaungAgent requires .NET SDK 10.x; found ${sdk_version}."
}

port_is_in_use() {
    local port="$1"

    if command -v lsof >/dev/null 2>&1; then
        lsof -nP -iTCP:"${port}" -sTCP:LISTEN >/dev/null 2>&1
        return
    fi

    if command -v nc >/dev/null 2>&1; then
        nc -z 127.0.0.1 "${port}" >/dev/null 2>&1
        return
    fi

    (exec 3<>"/dev/tcp/127.0.0.1/${port}") >/dev/null 2>&1
}

port_owner_details() {
    local port="$1"
    command -v lsof >/dev/null 2>&1 || return 0

    lsof -nP -iTCP:"${port}" -sTCP:LISTEN -Fpc 2>/dev/null | awk '
        function emit() {
            if (pid == "") return
            if (found) printf ", "
            printf "PID %s", pid
            if (command != "") printf " (%s)", command
            found = 1
        }
        /^p/ { emit(); pid = substr($0, 2); command = ""; next }
        /^c/ { command = substr($0, 2); next }
        END { emit() }
    '
}

fail_if_port_is_in_use() {
    local port="$1"
    local service="$2"
    local owners

    port_is_in_use "$port" || return 0
    owners="$(port_owner_details "$port")"
    if [[ -n "$owners" ]]; then
        fail "Port ${port} required by ${service} is already in use by ${owners}. Stop that process and try again."
    fi
    fail "Port ${port} required by ${service} is already in use. Stop the listening process and try again."
}

check_ports() {
    fail_if_port_is_in_use "$MCP_LOCAL_PORT" KaungMcp
    fail_if_port_is_in_use 5090 KaungAgent
}

load_managed_launch_settings() {
    local line value external_endpoint=""
    while IFS= read -r line; do
        value="${line#*=}"
        case "$line" in
            MCP_LOCAL_PORT=*) MCP_LOCAL_PORT="$value" ;;
            SEARXNG_MODE=*) SEARXNG_MODE="$value" ;;
            SEARXNG_PORT=*) SEARXNG_PORT="$value" ;;
            SEARXNG_EXTERNAL_ENDPOINT=*) external_endpoint="$value" ;;
            SEARXNG_PORT_MANAGED=true) SEARXNG_PORT_EXPLICIT=1 ;;
        esac
    done < <(dotnet "$MCP_DLL" --print-launch-settings)
    [[ "$MCP_LOCAL_PORT" =~ ^[0-9]+$ ]] && (( MCP_LOCAL_PORT >= 1 && MCP_LOCAL_PORT <= 65535 )) \
        || fail "Managed MCP local port is invalid."
    MCP_URL="http://127.0.0.1:${MCP_LOCAL_PORT}"
    case "$SEARXNG_MODE" in
        local) SEARXNG_ENDPOINT=""; SEARXNG_DISABLED=0 ;;
        external) SEARXNG_ENDPOINT="$external_endpoint"; SEARXNG_DISABLED=0 ;;
        disabled) SEARXNG_ENDPOINT=""; SEARXNG_DISABLED=1 ;;
        *) fail "Managed SearXNG mode is invalid." ;;
    esac
}

ensure_state_directory() {
    if [[ -L "$STATE_DIR" ]]; then
        fail "${STATE_DIR} must not be a symbolic link."
    fi

    mkdir -p "$STATE_DIR"
    chmod 700 "$STATE_DIR"
}

config_mode() {
    case "$(uname -s)" in
        Darwin) stat -f '%Lp' "$CONFIG_FILE" ;;
        *) stat -c '%a' "$CONFIG_FILE" ;;
    esac
}

check_config_permissions() {
    [[ -e "$CONFIG_FILE" ]] || return 0
    [[ -f "$CONFIG_FILE" && ! -L "$CONFIG_FILE" ]] || fail "${CONFIG_FILE} must be a regular file, not a symbolic link."

    local mode
    mode="$(config_mode)" || fail "Unable to inspect permissions for ${CONFIG_FILE}."
    case "$mode" in
        600) ;;
        *) fail "${CONFIG_FILE} contains secrets and must have mode 600 (currently ${mode}). Run: chmod 600 '${CONFIG_FILE}'" ;;
    esac
}

read_config_value() {
    local key="$1"
    sed -n "s/^${key}=//p" "$CONFIG_FILE" | head -n 1
}

is_safe_secret() {
    local value="$1"
    [[ ${#value} -ge 32 && "$value" =~ ^[A-Za-z0-9._~+=/-]+$ ]]
}

is_valid_client_id() {
    local value="$1"
    [[ "$value" =~ ^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$ ]]
}

is_valid_mcp_api_key() {
    local value="$1"
    [[ "$value" =~ ^kmcp_[A-Za-z0-9._~-]+$ ]]
}

load_config() {
    [[ -f "$CONFIG_FILE" ]] || return 1

    KAUNG_MCP_ADMIN_KEY="$(read_config_value KAUNG_MCP_ADMIN_KEY)"
    KAUNG_AGENT_API_KEY="$(read_config_value KAUNG_AGENT_API_KEY)"
    KAUNG_MCP_CLIENT_ID="$(read_config_value KAUNG_MCP_CLIENT_ID)"
    KAUNG_MCP_API_KEY="$(read_config_value KAUNG_MCP_API_KEY)"
    DEEPSEEK_API_KEY="$(read_config_value DEEPSEEK_API_KEY)"

    is_safe_secret "$KAUNG_MCP_ADMIN_KEY" || return 1
    is_safe_secret "$KAUNG_AGENT_API_KEY" || return 1
    is_valid_client_id "$KAUNG_MCP_CLIENT_ID" || return 1
    is_valid_mcp_api_key "$KAUNG_MCP_API_KEY" || return 1
    [[ -z "$DEEPSEEK_API_KEY" ]] || is_safe_secret "$DEEPSEEK_API_KEY" || return 1
}

load_previous_configuration() {
    [[ -f "$CONFIG_FILE" ]] || return 0
    local candidate
    candidate="$(read_config_value KAUNG_MCP_CLIENT_ID)"
    if is_valid_client_id "$candidate"; then
        KAUNG_MCP_CLIENT_ID="$candidate"
    fi

    candidate="$(read_config_value DEEPSEEK_API_KEY)"
    if [[ -z "$candidate" ]] || is_safe_secret "$candidate"; then
        DEEPSEEK_API_KEY="$candidate"
    fi
}

generate_secret() {
    openssl rand -hex 32
}

generate_local_secrets() {
    KAUNG_MCP_ADMIN_KEY="$(generate_secret)" || fail "Unable to generate the KaungMcp administrator key."
    KAUNG_AGENT_API_KEY="$(generate_secret)" || fail "Unable to generate the KaungAgent API key."
    log "Generated secure local service keys automatically."
}

write_config() {
    local temp_config
    umask 077
    temp_config="$(mktemp "${STATE_DIR}/local.env.tmp.XXXXXX")" || fail "Unable to create a temporary config file."

    {
        printf 'KAUNG_MCP_ADMIN_KEY=%s\n' "$KAUNG_MCP_ADMIN_KEY"
        printf 'KAUNG_AGENT_API_KEY=%s\n' "$KAUNG_AGENT_API_KEY"
        printf 'KAUNG_MCP_CLIENT_ID=%s\n' "$KAUNG_MCP_CLIENT_ID"
        printf 'KAUNG_MCP_API_KEY=%s\n' "$KAUNG_MCP_API_KEY"
        printf 'DEEPSEEK_API_KEY=%s\n' "$DEEPSEEK_API_KEY"
    } >"$temp_config"

    chmod 600 "$temp_config"
    mv -f "$temp_config" "$CONFIG_FILE"
    log "Saved local configuration to ${CONFIG_FILE}."
}

prefix_output() {
    local service="$1"
    local line
    while IFS= read -r line || [[ -n "$line" ]]; do
        printf '[%s] %s\n' "$service" "$line"
    done
}

stop_process() {
    local pid="$1"
    local attempts=0
    [[ -n "$pid" ]] || return 0
    if kill -0 "$pid" >/dev/null 2>&1; then
        kill -TERM "$pid" >/dev/null 2>&1 || true
    fi

    while kill -0 "$pid" >/dev/null 2>&1 && [[ $attempts -lt 5 ]]; do
        sleep 1
        attempts=$((attempts + 1))
    done

    if kill -0 "$pid" >/dev/null 2>&1; then
        kill -KILL "$pid" >/dev/null 2>&1 || true
    fi
}

wait_for_process() {
    local pid="$1"
    [[ -n "$pid" ]] || return 0
    wait "$pid" >/dev/null 2>&1 || true
}

cleanup() {
    if [[ $CLEANUP_COMPLETE -eq 1 ]]; then
        return
    fi
    CLEANUP_COMPLETE=1

    stop_process "$AGENT_PID"
    stop_process "$MCP_PID"
    wait_for_process "$AGENT_PID"
    wait_for_process "$MCP_PID"

    if [[ $STARTED_SEARXNG -eq 1 ]]; then
        docker compose --file "$SEARXNG_COMPOSE_FILE" down >/dev/null 2>&1 || true
    fi

    if [[ -n "$TEMP_RESPONSE" && -f "$TEMP_RESPONSE" ]]; then
        rm -f "$TEMP_RESPONSE"
    fi
}

handle_signal() {
    printf '\n'
    log "Stopping KaungAgent and KaungMcp..."
    exit 130
}

trap cleanup EXIT
trap handle_signal INT TERM HUP

restore_and_build() {
    log "Restoring dependencies..."
    dotnet restore "$SOLUTION_FILE" --nologo
    log "Building solution..."
    dotnet build "$SOLUTION_FILE" --configuration Debug --no-restore --nologo

    [[ -f "$MCP_DLL" ]] || fail "Build succeeded but ${MCP_DLL} was not found."
    [[ -f "$AGENT_DLL" ]] || fail "Build succeeded but ${AGENT_DLL} was not found."
}

install_chromium() {
    [[ -f "$PLAYWRIGHT_DLL" && -f "$MCP_RUNTIME_CONFIG" ]] || fail "Playwright build output was not found."
    log "Ensuring the Playwright Chromium headless shell is installed..."
    dotnet exec --runtimeconfig "$MCP_RUNTIME_CONFIG" "$PLAYWRIGHT_DLL" install --only-shell chromium \
        || fail "Unable to install the Playwright Chromium headless shell."
}

docker_compose_is_available() {
    command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1
}

select_searxng_port() {
    local candidate
    if ! port_is_in_use "$SEARXNG_PORT"; then
        return 0
    fi

    if [[ $SEARXNG_PORT_EXPLICIT -eq 1 ]]; then
        log "Bundled SearXNG port ${SEARXNG_PORT} is already in use; static HTML search fallback will be used."
        return 1
    fi

    candidate=8081
    while [[ $candidate -le 8099 ]]; do
        if ! port_is_in_use "$candidate"; then
            log "Port ${SEARXNG_PORT} is already in use; bundled SearXNG will use 127.0.0.1:${candidate}."
            SEARXNG_PORT="$candidate"
            export SEARXNG_PORT
            return 0
        fi
        candidate=$((candidate + 1))
    done

    log "No free bundled SearXNG port was found between 8080 and 8099; static HTML search fallback will be used."
    return 1
}

start_searxng() {
    if [[ "$SEARXNG_DISABLED" -eq 1 ]]; then
        log "SearXNG is disabled; static HTML search fallback will be used."
        return
    fi

    if [[ -n "$SEARXNG_ENDPOINT" ]]; then
        log "Using external SearXNG endpoint ${SEARXNG_ENDPOINT}."
        return
    fi

    if ! docker_compose_is_available; then
        log "Docker Compose is unavailable; continuing with the static HTML search fallback."
        SEARXNG_DISABLED=1
        return
    fi

    if docker compose --file "$SEARXNG_COMPOSE_FILE" ps --status running --services 2>/dev/null | grep -qx core; then
        local published_address
        published_address="$(docker compose --file "$SEARXNG_COMPOSE_FILE" port core 8080 2>/dev/null | tail -n 1)"
        if [[ "$published_address" == *:* ]]; then
            SEARXNG_PORT="${published_address##*:}"
        fi
        SEARXNG_ENDPOINT="http://127.0.0.1:${SEARXNG_PORT}"
        log "Using the existing bundled SearXNG container."
        return
    fi

    if ! select_searxng_port; then
        SEARXNG_DISABLED=1
        return
    fi

    SEARXNG_ENDPOINT="http://127.0.0.1:${SEARXNG_PORT}"
    log "Starting bundled SearXNG on ${SEARXNG_ENDPOINT}..."
    STARTED_SEARXNG=1
    if ! docker compose --file "$SEARXNG_COMPOSE_FILE" up --detach; then
        docker compose --file "$SEARXNG_COMPOSE_FILE" down >/dev/null 2>&1 || true
        STARTED_SEARXNG=0
        SEARXNG_ENDPOINT=""
        SEARXNG_DISABLED=1
        log "Bundled SearXNG failed to start; continuing with the static HTML search fallback."
    fi
}

wait_for_searxng() {
    if [[ "$SEARXNG_DISABLED" -eq 1 ]]; then
        return
    fi

    local attempts=0
    local max_attempts=90
    local health_url="${SEARXNG_ENDPOINT%/}/search?q=kaung+health+check&format=json"

    while [[ $attempts -lt $max_attempts ]]; do
        if curl --silent --show-error --fail --max-time 5 "$health_url" >/dev/null 2>&1; then
            log "SearXNG is ready."
            return 0
        fi
        attempts=$((attempts + 1))
        sleep 1
    done

    if [[ $STARTED_SEARXNG -eq 1 ]]; then
        docker compose --file "$SEARXNG_COMPOSE_FILE" down >/dev/null 2>&1 || true
        STARTED_SEARXNG=0
    fi
    log "Timed out waiting for SearXNG at ${SEARXNG_ENDPOINT}; continuing with the static HTML search fallback."
    SEARXNG_ENDPOINT=""
    SEARXNG_DISABLED=1
}

start_mcp() {
    log "Starting KaungMcp on ${MCP_URL}..."
    (
        cd "$SCRIPT_DIR"
        export ASPNETCORE_ENVIRONMENT=Development
        export ASPNETCORE_URLS="$MCP_URL"
        export KAUNG_MCP_ADMIN_KEY
        export SEARXNG_ENDPOINT
        export SEARXNG_DISABLED
        export SEARXNG_MODE
        export SEARXNG_PORT
        exec dotnet "$MCP_DLL"
    ) > >(prefix_output KaungMcp) 2>&1 &
    MCP_PID=$!
}

start_agent() {
    log "Starting KaungAgent on ${AGENT_URL}..."
    (
        cd "$SCRIPT_DIR"
        export ASPNETCORE_ENVIRONMENT=Development
        export ASPNETCORE_URLS="$AGENT_URL"
        export KAUNG_AGENT_API_KEY
        export KAUNG_MCP_API_KEY
        export DEEPSEEK_API_KEY
        export KAUNG_MCP_ENDPOINT="${MCP_URL}/mcp"
        exec dotnet "$AGENT_DLL"
    ) > >(prefix_output KaungAgent) 2>&1 &
    AGENT_PID=$!
}

wait_for_health() {
    local service="$1"
    local url="$2"
    local pid="$3"
    local attempts=0
    local max_attempts=45

    while [[ $attempts -lt $max_attempts ]]; do
        if curl --silent --show-error --fail --max-time 2 "$url" >/dev/null 2>&1; then
            log "${service} is ready."
            return 0
        fi

        if ! kill -0 "$pid" >/dev/null 2>&1; then
            fail "${service} exited before becoming ready. Review the prefixed service logs above."
        fi

        attempts=$((attempts + 1))
        sleep 1
    done

    fail "Timed out waiting for ${service} at ${url}."
}

create_response_file() {
    TEMP_RESPONSE="$(mktemp "${TMPDIR:-/tmp}/kaung-launcher-response.XXXXXX")" || fail "Unable to create a temporary response file."
    chmod 600 "$TEMP_RESPONSE"
}

json_string_field() {
    local field="$1"
    sed -n "s/.*\"${field}\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\1/p" "$TEMP_RESPONSE" | head -n 1
}

request_client_creation() {
    local client_name="$1"
    curl --silent --show-error \
        --output "$TEMP_RESPONSE" \
        --write-out '%{http_code}' \
        --request POST "${MCP_URL}/api/admin/clients" \
        --header "X-Kaung-Admin-Key: ${KAUNG_MCP_ADMIN_KEY}" \
        --header 'Content-Type: application/json' \
        --data "{\"name\":\"${client_name}\",\"description\":\"Local one-click launcher\"}"
}

create_mcp_client() {
    local status
    local client_name="kaung-agent-local"
    status="$(request_client_creation "$client_name")" || fail "Unable to call the KaungMcp client administration API."

    if [[ "$status" == "409" ]]; then
        client_name="kaung-agent-local-$(date +%s)"
        log "The default local client name already exists; creating ${client_name} instead."
        status="$(request_client_creation "$client_name")" || fail "Unable to call the KaungMcp client administration API."
    fi

    [[ "$status" == "201" ]] || fail "Creating the local MCP client failed with HTTP ${status}: $(tr '\n' ' ' <"$TEMP_RESPONSE")"

    KAUNG_MCP_CLIENT_ID="$(json_string_field id)"
    KAUNG_MCP_API_KEY="$(json_string_field apiKey)"
    is_valid_client_id "$KAUNG_MCP_CLIENT_ID" || fail "KaungMcp returned an invalid client ID."
    is_valid_mcp_api_key "$KAUNG_MCP_API_KEY" || fail "KaungMcp returned an invalid client API key."
    log "Created the local MCP client."
}

rotate_mcp_client() {
    local status
    status="$(curl --silent --show-error \
        --output "$TEMP_RESPONSE" \
        --write-out '%{http_code}' \
        --request POST "${MCP_URL}/api/admin/clients/${KAUNG_MCP_CLIENT_ID}/rotate-key" \
        --header "X-Kaung-Admin-Key: ${KAUNG_MCP_ADMIN_KEY}")" || fail "Unable to call the KaungMcp key rotation API."

    if [[ "$status" == "404" ]]; then
        log "The previous MCP client no longer exists; creating a new one."
        KAUNG_MCP_CLIENT_ID=""
        create_mcp_client
        return
    fi

    [[ "$status" == "200" ]] || fail "Rotating the local MCP client key failed with HTTP ${status}: $(tr '\n' ' ' <"$TEMP_RESPONSE")"

    KAUNG_MCP_API_KEY="$(json_string_field apiKey)"
    is_valid_mcp_api_key "$KAUNG_MCP_API_KEY" || fail "KaungMcp returned an invalid rotated client API key."
    log "Rotated the local MCP client key."
}

provision_client_if_needed() {
    if [[ $RECONFIGURE -eq 1 && -n "$KAUNG_MCP_CLIENT_ID" ]]; then
        rotate_mcp_client
    elif [[ -z "$KAUNG_MCP_API_KEY" ]]; then
        create_mcp_client
    fi
}

ensure_client_key_works() {
    local status
    status="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
        --request POST "${MCP_URL}/mcp" \
        --header "Authorization: Bearer ${KAUNG_MCP_API_KEY}" \
        --header 'Content-Type: application/json' \
        --header 'Accept: application/json, text/event-stream' \
        --data '{"jsonrpc":"2.0","id":1,"method":"ping"}')" \
        || fail "Unable to validate the local MCP client key."
    if [[ "$status" == "401" ]]; then
        log "The saved local MCP client key is invalid for this database; creating a replacement."
        create_mcp_client
        write_config
    fi
}

verify_tool_discovery() {
    local tools
    if ! tools="$(curl --silent --show-error --fail --max-time 5 \
        --header "X-Kaung-Agent-Key: ${KAUNG_AGENT_API_KEY}" \
        "${AGENT_URL}/api/mcp/tools")"; then
        fail "KaungAgent started, but MCP tool discovery failed. Run './start.sh --reconfigure' if the local database or client credentials changed."
    fi
    [[ "$tools" == *'"kaung_web_search"'* && "$tools" == *'"kaung_web_fetch"'* ]] \
        || fail "KaungAgent connected, but the web search and fetch tools were not discovered."
    log "Verified the complete Agent-to-MCP connection."
}

open_websites() {
    [[ $NO_OPEN -eq 0 ]] || return 0

    case "$(uname -s)" in
        Darwin)
            if command -v open >/dev/null 2>&1; then
                open "${AGENT_URL}/" "${MCP_URL}/" >/dev/null 2>&1 &
                log "Opened the Agent workspace and MCP status page in your browser."
            else
                log "Browser launcher was not found; open ${AGENT_URL}/ and ${MCP_URL}/ manually."
            fi
            ;;
        Linux)
            if command -v xdg-open >/dev/null 2>&1 && [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
                xdg-open "${AGENT_URL}/" >/dev/null 2>&1 &
                xdg-open "${MCP_URL}/" >/dev/null 2>&1 &
                log "Opened the Agent workspace and MCP status page in your browser."
            else
                log "No graphical browser session was detected; open ${AGENT_URL}/ and ${MCP_URL}/ manually."
            fi
            ;;
        *)
            log "Automatic browser launch is unavailable on this platform; open ${AGENT_URL}/ and ${MCP_URL}/ manually."
            ;;
    esac
}

monitor_services() {
    local exit_code

    while true; do
        if ! kill -0 "$MCP_PID" >/dev/null 2>&1; then
            set +e
            wait "$MCP_PID"
            exit_code=$?
            set -e
            [[ $exit_code -ne 0 ]] || exit_code=1
            fail "KaungMcp stopped unexpectedly (exit code ${exit_code})."
        fi

        if ! kill -0 "$AGENT_PID" >/dev/null 2>&1; then
            set +e
            wait "$AGENT_PID"
            exit_code=$?
            set -e
            [[ $exit_code -ne 0 ]] || exit_code=1
            fail "KaungAgent stopped unexpectedly (exit code ${exit_code})."
        fi

        sleep 1
    done
}

main() {
    parse_arguments "$@"
    check_prerequisites
    ensure_state_directory
    check_config_permissions

    if [[ $RECONFIGURE -eq 1 ]]; then
        load_previous_configuration
        generate_local_secrets
        KAUNG_MCP_API_KEY=""
    elif ! load_config; then
        if [[ -e "$CONFIG_FILE" ]]; then
            fail "${CONFIG_FILE} is incomplete or invalid. Run './start.sh --reconfigure' to repair it."
        fi
        generate_local_secrets
    else
        log "Using saved local configuration from ${CONFIG_FILE}."
    fi

    restore_and_build
    load_managed_launch_settings
    check_ports
    if [[ $RECONFIGURE -eq 1 ]]; then
        export KAUNG_MCP_ADMIN_KEY
        dotnet "$MCP_DLL" --sync-admin-bootstrap >/dev/null \
            || fail "Unable to synchronize the rotated administrator key."
    fi
    install_chromium
    start_searxng
    wait_for_searxng
    create_response_file
    start_mcp
    wait_for_health KaungMcp "${MCP_URL}/health/ready" "$MCP_PID"

    if [[ $RECONFIGURE -eq 1 || -z "$KAUNG_MCP_API_KEY" ]]; then
        provision_client_if_needed
        write_config
    else
        ensure_client_key_works
    fi

    start_agent
    wait_for_health KaungAgent "${AGENT_URL}/health/ready" "$AGENT_PID"
    verify_tool_discovery
    open_websites

    printf '\n'
    log "KaungAgent is available at ${AGENT_URL}."
    log "KaungMcp is available at ${MCP_URL}."
    log "Press Ctrl+C to stop both services."
    printf '\n'

    monitor_services
}

main "$@"
