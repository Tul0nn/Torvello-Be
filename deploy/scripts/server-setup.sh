#!/bin/bash
set -euo pipefail
[[ $EUID -eq 0 ]] || { echo "Run as root" >&2; exit 1; }
. /etc/os-release
[[ "$ID" == ubuntu && "$VERSION_ID" == 24.04 ]] || { echo "Ubuntu 24.04 required" >&2; exit 1; }
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get upgrade -y
apt-get install -y ca-certificates curl gnupg unattended-upgrades ufw fail2ban openssl
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu $VERSION_CODENAME stable" > /etc/apt/sources.list.d/docker.list
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw allow 443/udp
ufw --force enable
if [[ ! -e /swapfile ]]; then
    fallocate -l 2G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile
fi
swapon --show=NAME --noheadings | grep -qx /swapfile || swapon /swapfile
grep -q '^/swapfile ' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
install -d -m 0755 /srv/torvello/sites/{app,cafe,landing} /srv/torvello/app
install -d -m 0700 /srv/torvello/backups
# Early filename wins over cloud-init's later snippets.
cat > /etc/ssh/sshd_config.d/00-torvello.conf <<'EOF'
PasswordAuthentication no
PermitRootLogin prohibit-password
EOF
/usr/sbin/sshd -t
systemctl reload ssh
cat > /etc/fail2ban/jail.d/torvello.local <<'EOF'
[sshd]
enabled = true
backend = systemd
EOF
systemctl enable --now fail2ban
systemctl restart fail2ban
echo "Setup complete. Verify a second key-authenticated SSH session before closing this one."
