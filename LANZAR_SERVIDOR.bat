@echo off
REM ============================================================
REM  LANZAR_SERVIDOR.bat  -  Ejecutar EN LA MAQUINA VIRTUAL
REM
REM  Doble clic aca y listo. Es el "launcher" del servidor:
REM   - baja de GitHub el ultimo exe si hay uno nuevo
REM   - arranca el servidor
REM   - mientras corre, revisa GitHub cada 60s: si subiste un
REM     update con SUBIR_A_VM.bat, reinicia solo con el nuevo
REM   - si el server se cae, lo vuelve a levantar
REM
REM  Para detenerlo: Ctrl+C en esta ventana (o cerrarla).
REM  No toca los datos vivos (Charfile, Cuentas, GUILDS, Backups).
REM ============================================================
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0LANZAR_SERVIDOR.ps1"
pause
