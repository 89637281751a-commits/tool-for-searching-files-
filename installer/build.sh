#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "$0")/.."

version=$(dotnet msbuild QuickFind.csproj -nologo -getProperty:Version)
if [[ $# == 1 ]]; then version=$1; fi
if [[ $# -gt 1 || ! $version =~ ^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$ ]] ||
    (( BASH_REMATCH[1] > 255 || BASH_REMATCH[2] > 255 || BASH_REMATCH[3] > 65535 )); then
    echo "Usage: bash installer/build.sh [major.minor.patch]" >&2
    exit 2
fi

# Keep the ProductCode stable for a release version and distinct across releases.
product_code=$(python3 -c \
    'import sys, uuid; print(uuid.uuid5(uuid.UUID("6e6acdcb-46c3-4f7e-bb7b-21f70e4210e1"), sys.argv[1]))' "$version")
output="bin/installer/$version"
dotnet build QuickFind.csproj -c Release --output "$output" -p:Version="$version" --warnaserror
wixl -a x86 -D "Version=$version" -D "ProductCode=$product_code" -D "SourceDir=$output" \
    -o "$output/QuickFind-$version.msi" installer/QuickFind.wxs
