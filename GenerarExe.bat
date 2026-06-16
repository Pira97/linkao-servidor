@echo off
REM ============================================================
REM  GenerarExe.bat - Compila ServidorCS y publica el exe
REM  directamente en la carpeta del Servidor (junto a los datos).
REM ============================================================
setlocal

cd /d "%~dp0"

REM Carpeta destino = la propia carpeta ServidorCS (raiz del proyecto).
REM Desde aqui DataPaths sube y encuentra la carpeta hermana "Servidor" con los datos.
set "DESTINO=%~dp0."
set "EXE=%DESTINO%\ServidorCS.exe"

echo.
echo === Cerrando instancias previas del servidor ===
taskkill /F /IM ServidorCS.exe >nul 2>&1

:waitloop
tasklist /FI "IMAGENAME eq ServidorCS.exe" 2>nul | find /I "ServidorCS.exe" >nul
if not errorlevel 1 (
    echo    Esperando que el servidor cierre...
    timeout /t 1 /nobreak >nul
    goto waitloop
)
echo    Servidor cerrado.

echo.
echo === Borrando exe anterior ===
if exist "%EXE%" del /F /Q "%EXE%"

echo.
echo === Publicando ServidorCS (exe unico, self-contained) ===
echo     Destino: %DESTINO%
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -o "%DESTINO%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ************************************************
    echo *  ERROR DE COMPILACION - revisa los mensajes  *
    echo *  EL EXE NO SE GENERO                          *
    echo ************************************************
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo.
    echo ************************************************
    echo *  ERROR: el exe no se genero pese a publicar  *
    echo ************************************************
    pause
    exit /b 1
)

echo.
echo ============================================================
echo  PUBLICACION EXITOSA - exe en la carpeta del Servidor:
for %%F in ("%EXE%") do echo    %%~tF   %%~fF
echo ============================================================
echo.

if /I "%~1"=="run" (
    echo Iniciando servidor...
    echo.
    "%EXE%"
) else (
    echo Para iniciar: GenerarExe.bat run
    pause
)

endlocal
