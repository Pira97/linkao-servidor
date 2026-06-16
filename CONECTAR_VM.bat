@echo off
REM ============================================================
REM  CONECTAR_VM.bat  -  Ejecutar UNA SOLA VEZ en la VM
REM  Conecta esta carpeta con el repo de GitHub para que el
REM  launcher (LANZAR_SERVIDOR.bat) pueda auto-actualizar.
REM  NO borra datos (Charfile, Cuentas, GUILDS, Backups, Server.ini).
REM ============================================================
cd /d "%~dp0"

echo.
echo === Conectando la carpeta con GitHub ===
if not exist ".git" git init -b main

REM (re)configura el remoto por las dudas
git remote remove origin 2>nul
git remote add origin https://github.com/Pira97/linkao-servidor.git

git fetch origin
if errorlevel 1 (
    echo.
    echo  ERROR al conectar con GitHub. Revisa login/conexion.
    pause
    exit /b 1
)

git reset --mixed origin/main
git checkout -- .
git branch --set-upstream-to=origin/main main 2>nul

echo.
echo ============================================================
echo  LISTO. Carpeta conectada.
echo  Ahora hace doble clic en  LANZAR_SERVIDOR.bat
echo ============================================================
pause
