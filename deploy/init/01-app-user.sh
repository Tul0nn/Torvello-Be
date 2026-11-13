#!/bin/bash
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres --set=app_password="$TORVELLO_APP_PASSWORD" <<'SQL'
CREATE ROLE torvello_app LOGIN PASSWORD :'app_password' NOSUPERUSER NOCREATEROLE NOCREATEDB;
CREATE DATABASE torvello OWNER torvello_app;
REVOKE ALL ON DATABASE torvello FROM PUBLIC;
SQL
