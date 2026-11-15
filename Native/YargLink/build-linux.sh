#!/usr/bin/env bash
# Build the Linux plugin against glibc 2.31 (Ubuntu 20.04), the floor YargAudio and Unity 6 use,
# and copy it to Assets/Plugins/YargLink/Linux/x86_64. Needs Docker.
#
# A plain `cargo build --release` works for local testing, but links against the host's glibc,
# which may be newer than what players have.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
toolchain="$(sed -n 's/^channel = "\(.*\)"/\1/p' "$here/rust-toolchain.toml")"

docker run --rm \
    -v "$here:/src" \
    -v yarg-link-cargo:/root/.cargo/registry \
    -w /src \
    -e CARGO_TARGET_DIR=/src/target/glibc-2.31 \
    -e CARGO_INCREMENTAL=0 \
    ubuntu:20.04 bash -euo pipefail -c "
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq && apt-get install -y -qq --no-install-recommends curl ca-certificates gcc libc6-dev >/dev/null
        curl -sSf https://sh.rustup.rs | sh -s -- -y -q --profile minimal --default-toolchain $toolchain
        . /root/.cargo/env
        cargo build --release --locked
        chown -R $(id -u):$(id -g) /src/target/glibc-2.31
    "

lib="$here/target/glibc-2.31/release/libyarg_link.so"
newest="$(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)"
echo "newest glibc symbol: $newest"
if [[ "$(printf '%s\n' "$newest" GLIBC_2.31 | sort -V | tail -1)" != GLIBC_2.31 ]]; then
    echo "error: needs a glibc newer than 2.31" >&2
    exit 1
fi

dest="$repo/Assets/Plugins/YargLink/Linux/x86_64"
mkdir -p "$dest"
cp "$lib" "$dest/libyarg_link.so"
echo "copied to $dest/libyarg_link.so ($(du -h "$lib" | cut -f1))"
