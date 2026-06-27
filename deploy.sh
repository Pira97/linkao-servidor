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

# ------------------------------------------------------------
# RED DE SEGURIDAD para el progreso por personaje.
#
# Logros/ y BattlePass/ ESTABAN versionados por error (8 y 12 archivos dentro
# de git, uno por jugador). Con eso, el "git reset --hard" de abajo le pisaba a
# todos los jugadores sus logros y su pase con las copias viejas de la PC de
# desarrollo. Se sacaron del control de git el 31-jul-2026.
#
# El problema es que sacarlos tiene su propio filo: el commit que los quita hace
# que el reset los BORRE en este servidor. Por eso se guardan antes y se
# reponen despues, y queda puesto para siempre: si alguna vez se vuelven a
# versionar por accidente, esto los salva igual.
# ------------------------------------------------------------
RESGUARDO="/root/.deploy_progreso"
rm -rf "$RESGUARDO"
mkdir -p "$RESGUARDO"
for d in Logros BattlePass MercadoPago; do
    [ -d "$d" ] && cp -a "$d" "$RESGUARDO/"
done
echo "=== 0/3 Progreso resguardado en $RESGUARDO ==="

echo "=== 1/3 Bajando ultima version de GitHub ==="
git fetch origin
git reset --hard origin/main

# Reponer sin pisar lo que ya este: -n = no sobrescribir archivos existentes.
for d in Logros BattlePass MercadoPago; do
    [ -d "$RESGUARDO/$d" ] || continue
    mkdir -p "$d"
    cp -an "$RESGUARDO/$d/." "$d/" 2>/dev/null || true
done
echo "=== Progreso repuesto: $(ls Logros 2>/dev/null | wc -l) logros, $(ls BattlePass 2>/dev/null | wc -l) pases ==="

echo "=== 2/3 Compilando (dotnet publish) ==="
dotnet publish -c Release -o publish

echo "=== 3/3 Reiniciando el servicio ==="
systemctl restart linkao
sleep 2
systemctl status linkao --no-pager | head -5
echo "=== Deploy terminado. Puerto: ==="
ss -tlnp | grep 7666 || echo "(el puerto 7666 todavia no aparece, revisa journalctl -u linkao -f)"
