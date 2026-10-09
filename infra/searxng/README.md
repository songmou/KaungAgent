# Local SearXNG dependency

This Compose stack is intended only for KaungAgent's loopback development environment. It binds SearXNG to a loopback port, enables JSON search responses, and is started automatically by `start.sh` when `SEARXNG_ENDPOINT` is not set and `SEARXNG_DISABLED` is false. The launcher prefers port 8080 and automatically selects an available port from 8081 through 8099 when 8080 is already occupied; set `SEARXNG_PORT` to require a specific port.

SearXNG is licensed under AGPL-3.0-or-later. The unmodified upstream container and corresponding source are available at <https://github.com/searxng/searxng>. Valkey is distributed under its upstream license at <https://github.com/valkey-io/valkey>.

Do not expose this local configuration to a network. Supply `SEARXNG_ENDPOINT` to use a separately secured and maintained SearXNG deployment, or set `SEARXNG_DISABLED=1` to skip this stack and use KaungMcp's best-effort static HTML search fallback.
