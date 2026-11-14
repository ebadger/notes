#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [[ -e .env ]]; then
    printf 'Refusing to overwrite existing .env; keep it to preserve configured credentials.\n' >&2
    exit 1
fi
if ! command -v openssl >/dev/null 2>&1; then
    printf 'OpenSSL is required to generate cryptographically random demo credentials.\n' >&2
    exit 1
fi

database_password=$(openssl rand -hex 32)
alice_key=$(openssl rand -hex 32)
bob_key=$(openssl rand -hex 32)
carol_key=$(openssl rand -hex 32)
dana_key=$(openssl rand -hex 32)

umask 077
(
    set -o noclobber
    printf '%s\n' \
        "POSTGRES_PASSWORD=$database_password" \
        "DEMO_ALICE_KEY=$alice_key" \
        "DEMO_BOB_KEY=$bob_key" \
        "DEMO_CAROL_KEY=$carol_key" \
        "DEMO_DANA_KEY=$dana_key" \
        'NOTES_PORT=8080' \
        'NOTES_TEST_PORT=15432' > .env
)
# A default ACL on the directory overrides the umask, so restrict the file explicitly.
chmod 600 .env
printf 'Created private .env with independent random database credentials and demo keys.\n'
printf 'Keep this file private and reuse it when restarting the existing database volume.\n'
