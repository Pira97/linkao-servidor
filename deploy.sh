#!/bin/bash
# ============================================================
#  deploy.sh - Actualizar el server LinkAO en el VPS Linux.
#  Uso:  bash /root/linkao-servidor/deploy.sh
#
#  Flujo completo:
#   1) En tu PC: corres SUBIR_A_VM.bat (sube el codigo a GitHub).
#   2) En el VPS: corres este script (baja, compila y reinicia).
#
#  Usa "git reset --hard" porque SUBIR_A_VM.bat reescribe el commit
#  (amend + push --force) y un "git pull" normal fallaria. El reset
#  SOLO toca archivos versionados -> Cuentas/ Charfile/ GUILDS/ Backups/
#  Maps/ Server.ini estan en .gitignore y NUNCA se tocan (datos de jugadores).
# ============================================================
set -e
cd /root/linkao-servidor || { echo "No existe /root/linkao-servidor"; exit 1; }

echo "=== 1/3 Bajando ultima version de GitHub ==="
git fetch origin
git reset --hard origin/main

echo "=== 2/3 Compilando (dotnet publish) ==="
dotnet publish -c Release -o publish

echo "=== 3/3 Reiniciando el servicio ==="
systemctl restart linkao
sleep 2
systemctl status linkao --no-pager | head -5
echo "=== Deploy terminado. Puerto: ==="
ss -tlnp | grep 7666 || echo "(el puerto 7666 todavia no aparece, revisa journalctl -u linkao -f)"
