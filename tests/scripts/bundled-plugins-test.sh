#!/usr/bin/env bash
set -Eeuo pipefail
readonly REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "$REPO_DIR/scripts/prepare-bundled-plugins.sh"
readonly FIXTURE_DIR="$(mktemp -d)"
trap 'rm -rf "$FIXTURE_DIR"' EXIT
readonly BUNDLES="$FIXTURE_DIR/bundles"
for plugin_id in content diagnostics jewelry web; do
    mkdir -p "$BUNDLES/$plugin_id"
    printf '{"id":"%s"}\n' "$plugin_id" > "$BUNDLES/$plugin_id/plugin.json"
done

# Fresh install includes current bundles except the retired web package.
prepare_bundled_plugins "$BUNDLES" "$FIXTURE_DIR/fresh install"
for plugin_id in content diagnostics jewelry; do
    test -f "$FIXTURE_DIR/fresh install/$plugin_id/plugin.json"
done
test ! -e "$FIXTURE_DIR/fresh install/web"

# Upgrade without restoring deliberately removed old plugins or overwriting code.
mkdir -p "$FIXTURE_DIR/legacy/jewelry"
printf 'custom package' > "$FIXTURE_DIR/legacy/jewelry/plugin.json"
prepare_bundled_plugins "$BUNDLES" "$FIXTURE_DIR/legacy"
test -f "$FIXTURE_DIR/legacy/content/plugin.json"
test ! -e "$FIXTURE_DIR/legacy/diagnostics"
test "$(cat "$FIXTURE_DIR/legacy/jewelry/plugin.json")" = 'custom package'

# Repeated launches preserve uninstall state and existing content packages.
printf 'custom content' > "$FIXTURE_DIR/fresh install/content/plugin.json"
prepare_bundled_plugins "$BUNDLES" "$FIXTURE_DIR/fresh install"
test "$(cat "$FIXTURE_DIR/fresh install/content/plugin.json")" = 'custom content'
rm -rf "$FIXTURE_DIR/legacy/content"
prepare_bundled_plugins "$BUNDLES" "$FIXTURE_DIR/legacy"
test ! -e "$FIXTURE_DIR/legacy/content"

# Future bundles are installed once; incomplete packages are skipped.
mkdir -p "$BUNDLES/future" "$BUNDLES/incomplete"
printf '{"id":"future"}\n' > "$BUNDLES/future/plugin.json"
prepare_bundled_plugins "$BUNDLES" "$FIXTURE_DIR/legacy"
test -f "$FIXTURE_DIR/legacy/future/plugin.json"
test ! -e "$FIXTURE_DIR/legacy/incomplete"
printf 'Bundled plugin startup checks passed.\n'
