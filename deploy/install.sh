#!/usr/bin/env bash
# Run as root ON THE SERVER after copying a published build to /tmp/atlas-publish.tar.gz.
# Idempotent. /etc/atlas/atlas.env must already exist (see atlas.env.example).
#   ATLAS_HOST=archive.example.org ./install.sh [/tmp/atlas-publish.tar.gz]
# Prerequisites: .NET 8 ASP.NET Core runtime, Caddy, poppler-utils qpdf tesseract-ocr ffmpeg,
# and (optionally) a data disk mounted at /srv/atlas.
set -euo pipefail

TARBALL="${1:-/tmp/atlas-publish.tar.gz}"
SRC_DIR="$(cd "$(dirname "$0")" && pwd)"
: "${ATLAS_HOST:?set ATLAS_HOST to the public hostname Caddy should serve}"

id atlas-app >/dev/null 2>&1 || useradd --system --home-dir /var/lib/atlas --shell /usr/sbin/nologin atlas-app
[ -f /etc/atlas/atlas.env ] || { echo "missing /etc/atlas/atlas.env" >&2; exit 1; }
chown root:atlas-app /etc/atlas/atlas.env && chmod 640 /etc/atlas/atlas.env
mkdir -p /srv/atlas/data && chown atlas-app:atlas-app /srv/atlas/data && chmod 700 /srv/atlas/data

# Atomic-ish app swap
rm -rf /opt/atlas.new && mkdir -p /opt/atlas.new
tar -xzf "$TARBALL" -C /opt/atlas.new
chown -R root:root /opt/atlas.new
systemctl stop atlas 2>/dev/null || true
rm -rf /opt/atlas.old; [ -d /opt/atlas ] && mv /opt/atlas /opt/atlas.old
mv /opt/atlas.new /opt/atlas

install -m 644 "$SRC_DIR/atlas.service" /etc/systemd/system/atlas.service

# Caddy: keep Atlas in its own file and import it from the main Caddyfile (other sites on the same box are left alone).
mkdir -p /etc/caddy/conf.d
sed "s/__ATLAS_HOST__/${ATLAS_HOST}/g" "$SRC_DIR/Caddyfile.template" > /etc/caddy/conf.d/atlas.caddy
grep -q 'import /etc/caddy/conf.d/\*.caddy' /etc/caddy/Caddyfile 2>/dev/null || echo 'import /etc/caddy/conf.d/*.caddy' >> /etc/caddy/Caddyfile

systemctl daemon-reload
systemctl enable atlas
systemctl restart atlas
systemctl reload caddy || systemctl restart caddy
sleep 3
systemctl is-active atlas caddy
