@echo off
REM ============================================================
REM  LANZAR_SERVIDOR.bat  -  Ejecutar EN LA MAQUINA VIRTUAL
REM
REM  Doble clic y listo. NO necesita git. El launcher se baja
REM  el exe + Dat de GitHub (repo publico) con PowerShell.
REM   - actualiza solo cuando subis algo con SUBIR_A_VM.bat
REM   - reinicia el server si se cae
REM  No toca datos de jugadores (Charfile, Cuentas, GUILDS, Backups).
REM
REM  Para detenerlo: Ctrl+C en esta ventana (o cerrarla).
REM ============================================================
cd /d "%~dp0"

REM Si falta el .ps1, lo baja solo de GitHub (sin git).
if not exist "%~dp0LANZAR_SERVIDOR.ps1" (
    echo Descargando el launcher de GitHub...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue';[Net.ServicePointManager]::SecurityProtocol='Tls12';try{Invoke-WebRequest 'https://raw.githubusercontent.com/Pira97/linkao-servidor/main/LANZAR_SERVIDOR.ps1' -OutFile '%~dp0LANZAR_SERVIDOR.ps1' -UseBasicParsing}catch{Write-Host 'No pude bajar el launcher. Revisa internet.' -ForegroundColor Red}"
)

if not exist "%~dp0LANZAR_SERVIDOR.ps1" (
    echo.
    echo  ERROR: no se pudo obtener LANZAR_SERVIDOR.ps1
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0LANZAR_SERVIDOR.ps1"
pause
