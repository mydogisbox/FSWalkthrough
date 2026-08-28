#!/bin/bash
# Push the current version's packages from ./nupkgs to nuget.org.
#
# Bump and pack first:   ./publish-local.sh [patch|minor|major|X.Y.Z]
# Then publish:          ./publish-nuget.sh [X.Y.Z]
#
# The API key is read from the macOS Keychain, so it never appears in a
# command line, a dotfile, or shell history. Store it once with:
#   security add-generic-password -a "$USER" -s nuget-api-key -U -w
set -e
cd "$(dirname "$0")"

service="${NUGET_KEYCHAIN_SERVICE:-nuget-api-key}"
source_url="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

version="${1:-$(grep -oE '[0-9]+\.[0-9]+\.[0-9]+' src/Directory.Build.props | head -1)}"

shopt -s nullglob
packages=(nupkgs/*."$version".nupkg)
shopt -u nullglob

if [ ${#packages[@]} -eq 0 ]; then
    echo "✗ No packages for $version in ./nupkgs" >&2
    echo "  run ./publish-local.sh to pack first" >&2
    exit 1
fi

if ! api_key=$(security find-generic-password -s "$service" -w 2>/dev/null); then
    echo "✗ No Keychain entry for service '$service'" >&2
    echo "  store your nuget.org API key with:" >&2
    echo "    security add-generic-password -a \"\$USER\" -s $service -U -w" >&2
    exit 1
fi

echo "→ Pushing $version to $source_url"
printf '    %s\n' "${packages[@]##*/}"

for pkg in "${packages[@]}"; do
    dotnet nuget push "$pkg" --api-key "$api_key" --source "$source_url" --skip-duplicate
done

echo "→ Pushed $version"
