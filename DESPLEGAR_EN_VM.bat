@echo off
REM ============================================================
REM  DESPLEGAR_EN_VM.bat  -  Ejecutar EN LA MAQUINA VIRTUAL
REM  1) Cierra el server si esta corriendo
REM  2) Baja el ultimo exe de GitHub
REM  3) Arranca el servidor
REM
REM  No necesita .NET instalado: el exe ya viene compilado.
REM  NO toca los datos vivos (..\Servidor\Charfile, Cuentas,
REM  GUILDS, Backups, logs): quedan intactos en la VM.
REM ============================================================
setlocal
cd /d "%~dp0"

if not exist ".git" (
    echo.
    echo  ERROR: esta carpeta no es un repositorio git.
    echo  Hace primero el clone. Mira _INSTRUCCIONES_DEPLOY.txt
    echo.
    pause
    exit /b 1
)

echo.
echo === Cerrando servidor si esta corriendo ===
taskkill /F /IM ServidorCS.exe >nul 2>&1

echo.
echo === Bajando ultimo exe de GitHub ===
REM El push de tu maquina reescribe el commit, por eso se usa
REM fetch + reset --hard (un pull normal daria "diverged").
git fetch origin
git reset --hard origin/main
if errorlevel 1 (
    echo.
    echo  ************************************************
    echo  *  ERROR al bajar. Revisa la conexion.        *
    echo  ************************************************
    pause
    exit /b 1
)

if not exist "ServidorCS.exe" (
    echo.
    echo  ERROR: no se encontro ServidorCS.exe tras el pull.
    pause
    exit /b 1
)

echo.
echo === Arrancando el servidor ===
echo.
ServidorCS.exe

endlocal
