@echo off
REM ============================================================
REM  SUBIR_A_VM.bat  -  Ejecutar EN TU MAQUINA (la de desarrollo)
REM  1) Compila el exe (self-contained, sin necesitar nada en la VM)
REM  2) Lo sube a GitHub reescribiendo siempre el mismo commit
REM     (asi el repo NO crece 71 MB por cada update)
REM  Luego en la VM corres DESPLEGAR_EN_VM.bat para bajarlo.
REM ============================================================
setlocal
cd /d "%~dp0"

if not exist ".git" (
    echo.
    echo  ERROR: esta carpeta no es un repositorio git todavia.
    echo  Hace primero la configuracion inicial. Mira:
    echo     _INSTRUCCIONES_DEPLOY.txt
    echo.
    pause
    exit /b 1
)

echo.
echo === Compilando el servidor (exe unico, self-contained) ===
call GenerarExe.bat
if not exist "ServidorCS.exe" (
    echo.
    echo  ************************************************
    echo  *  ERROR: no se genero ServidorCS.exe          *
    echo  *  Revisa los errores de compilacion arriba.   *
    echo  ************************************************
    pause
    exit /b 1
)

echo.
echo === Subiendo a GitHub (reescribiendo el commit unico) ===
git add -A
git commit --amend -m "servidor %DATE% %TIME%"
if errorlevel 1 (
    REM Si no habia commit previo (primera vez), hace uno normal
    git commit -m "servidor %DATE% %TIME%"
)

git push --force
if errorlevel 1 (
    echo.
    echo  ************************************************
    echo  *  ERROR al hacer push. Revisa conexion /      *
    echo  *  credenciales de GitHub.                     *
    echo  ************************************************
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  LISTO. Exe subido a GitHub.
echo  Ahora en la VM ejecuta: DESPLEGAR_EN_VM.bat
echo ============================================================
pause
endlocal
