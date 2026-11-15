#!/usr/bin/env bash
# Build the library and the C harness, then run the harness against the library.
#   tests/c/run.sh [--release]
set -euo pipefail
cd "$(dirname "$0")/../.."

profile=debug
args=()
if [[ "${1:-}" == "--release" ]]; then
    profile=release
    args=(--release)
fi

cargo build --locked "${args[@]}"

libs=()
case "$(uname -s)" in
    Darwin) lib="target/$profile/libyarg_link.dylib" ;;
    MINGW*|MSYS*|CYGWIN*) lib="target/$profile/yarg_link.dll" ;;
    *) lib="target/$profile/libyarg_link.so"; libs=(-ldl) ;;
esac

out="target/c-harness"
mkdir -p "$out"
cc -std=c11 -Wall -Wextra -Werror -O1 -o "$out/harness" tests/c/harness.c "${libs[@]}"
"$out/harness" "$lib"
