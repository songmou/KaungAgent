#!/usr/bin/env bash

# Seed newly bundled plugins once. Preserve existing packages and uninstall state.
prepare_bundled_plugins() {
    local source_directory="$1" install_directory="$2"
    local state_file="${install_directory}/.bundled-plugins" package plugin_id
    local existing_install=0
    [[ ! -d "$install_directory" ]] || existing_install=1
    [[ -d "$source_directory" ]] || { printf 'Bundled plugin directory not found: %s\n' "$source_directory" >&2; return 1; }
    mkdir -p "$install_directory"
    if [[ ! -f "$state_file" ]]; then
        # These two bundles predate tracking. Preserve their removal in legacy
        # installations; content and future bundles are new default installs.
        if [[ "$existing_install" -eq 1 ]]; then
            printf '%s\n' diagnostics jewelry > "$state_file"
        else
            : > "$state_file"
        fi
    fi
    for package in "$source_directory"/*; do
        [[ -d "$package" && -f "$package/plugin.json" ]] || continue
        plugin_id="${package##*/}"
        [[ "$plugin_id" != web ]] || continue
        if ! grep -Fxq -- "$plugin_id" "$state_file"; then
            if [[ ! -e "$install_directory/$plugin_id" ]]; then
                cp -R "$package" "$install_directory/" || return 1
                printf '[launcher] Installed bundled plugin: %s\n' "$plugin_id"
            fi
            printf '%s\n' "$plugin_id" >> "$state_file"
        fi
    done
}
