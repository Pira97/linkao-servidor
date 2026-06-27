#!/bin/bash
# setup_websocket_vps.sh — Instala websockify como puente WebSocket->TCP para el cliente web.
# Correr UNA sola vez en el VPS (AlmaLinux):  bash setup_websocket_vps.sh
# Deja un servicio systemd "linkao-ws" escuchando en el puerto 7668 (WebSocket)
# que reenvía todo al puerto 7666 (TCP del juego) en localhost.
# El cliente web se conecta a ws://23.175.40.101:7668
set -e

echo "== Instalando websockify =="
dnf install -y python3-pip
pip3 install --quiet websockify

WSPATH=$(command -v websockify)
echo "websockify instalado en: $WSPATH"

echo "== Creando servicio systemd linkao-ws =="
cat > /etc/systemd/system/linkao-ws.service <<EOF
[Unit]
Description=LinkAO WebSocket->TCP bridge (cliente web)
After=network.target linkao.service

[Service]
ExecStart=$WSPATH 0.0.0.0:7668 127.0.0.1:7666
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now linkao-ws

echo "== Estado =="
systemctl status linkao-ws --no-pager | head -8
ss -tlnp | grep -E "7666|7668" || true
echo ""
echo "LISTO. El puente WebSocket escucha en el puerto 7668."
echo "Cliente web -> ws://23.175.40.101:7668 -> TCP 127.0.0.1:7666"
