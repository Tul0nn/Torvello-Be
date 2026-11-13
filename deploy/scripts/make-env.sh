#!/bin/bash
set -euo pipefail
umask 077
target=/srv/torvello/app/.env
[[ ! -e "$target" ]] || { echo "$target already exists; refusing to overwrite" >&2; exit 1; }
acme=${1:-}
[[ -n "$acme" ]] || read -r -p "ACME email: " acme
owner=${2:-}
[[ -n "$owner" ]] || read -r -p "Owner email: " owner
# Keep answers safe as unquoted dotenv values.
[[ "$acme" =~ ^[a-zA-Z0-9._+%-]+@[a-zA-Z0-9.-]+$ && "$owner" =~ ^[a-zA-Z0-9._+%-]+@[a-zA-Z0-9.-]+$ ]] || { echo "Invalid email" >&2; exit 1; }
admin=$(openssl rand -hex 32)
app=$(openssl rand -hex 32)
jwt=$(openssl rand -hex 48)
password="Aa9$(openssl rand -hex 16)"
template="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)/.env.example"
mkdir -p /srv/torvello/app
# noclobber prevents racing another invocation.
(set -o noclobber; cat "$template" > "$target")
sed -i \
    -e "s|^ACME_EMAIL=.*|ACME_EMAIL=$acme|" \
    -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$admin|" \
    -e "s|^TORVELLO_APP_PASSWORD=.*|TORVELLO_APP_PASSWORD=$app|" \
    -e "s|Password=REPLACE_WITH_RANDOM_HEX|Password=$app|" \
    -e "s|^Jwt__SigningKey=.*|Jwt__SigningKey=$jwt|" \
    -e "s|^Bootstrap__OwnerEmail=.*|Bootstrap__OwnerEmail=$owner|" \
    -e "s|^Bootstrap__OwnerPassword=.*|Bootstrap__OwnerPassword=$password|" "$target"
chmod 600 "$target"
printf 'Bootstrap owner password: %s\nChange it at first sign-in. Store it securely now; it will not be printed again.\nEmails are written to a pickup folder until SMTP is configured in %s.\n' "$password" "$target"
