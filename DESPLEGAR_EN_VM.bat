@echo off
REM ============================================================
REM  DESPLEGAR_EN_VM.bat  -  (OBSOLETO)
REM
REM  Este script hacia git reset --hard y corria el exe UNA SOLA
REM  VEZ, sin volver a mirar GitHub: por eso el servidor quedaba
REM  abierto pero NO se auto-actualizaba al hacer SUBIR_A_VM.bat.
REM
REM  Ahora redirige al unico launcher valido (LANZAR_SERVIDOR.bat),
REM  que sondea GitHub cada 60s y reinicia solo cuando subis algo.
REM  Asi no importa cual cliques en la VM: siempre auto-actualiza.
REM ============================================================
cd /d "%~dp0"
echo.
echo  DESPLEGAR_EN_VM.bat quedo obsoleto (no auto-actualizaba).
echo  Lanzando el launcher con auto-update: LANZAR_SERVIDOR.bat
echo.
call "%~dp0LANZAR_SERVIDOR.bat"
