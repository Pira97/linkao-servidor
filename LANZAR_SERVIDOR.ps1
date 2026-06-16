# ============================================================
#  LANZAR_SERVIDOR.ps1  -  Launcher del servidor (corre EN LA VM)
#
#  Hace de "launcher" igual que el del cliente, pero para el server:
#   1) Antes de arrancar, baja de GitHub el ultimo exe si hay uno nuevo.
#   2) Arranca ServidorCS.exe.
#   3) Mientras corre, sondea GitHub cada $PollSegundos. Si detecta un
#      exe nuevo (corriste SUBIR_A_VM.bat en tu PC) -> cierra, baja y
#      reinicia AL TOQUE.
#   4) Si el server se cae solo -> chequea update y lo vuelve a levantar.
#
#  NO toca los datos vivos (..\Servidor\Charfile, Cuentas, GUILDS,
#  Backups, logs): el repo solo trae el exe (y archivos de codigo).
#
#  Se cierra con Ctrl+C en esta ventana.
# ============================================================

# --- Config ---
$PollSegundos = 60      # cada cuanto revisa GitHub mientras el server corre
$Exe          = "ServidorCS.exe"
$EsperaReinicio = 3     # segundos de pausa tras una caida (evita loop frenetico)

$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot

function Log($msg, $color = "Gray") {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg) -ForegroundColor $color
}

if (-not (Test-Path ".git")) {
    Log "ERROR: esta carpeta no es un repositorio git. Mira _INSTRUCCIONES_DEPLOY.txt" "Red"
    Read-Host "Enter para salir"
    exit 1
}

# Devuelve $true si origin/main tiene algo distinto a lo que hay en disco.
function Hay-Update {
    git fetch origin --quiet 2>$null
    $local  = (git rev-parse HEAD 2>$null)
    $remote = (git rev-parse origin/main 2>$null)
    if ([string]::IsNullOrWhiteSpace($remote)) { return $false }   # sin conexion: no tocar
    return ($local -ne $remote)
}

# Baja el ultimo exe (fetch + reset --hard, igual que DESPLEGAR_EN_VM.bat,
# porque el push de tu PC reescribe el commit con --force).
function Bajar-Update {
    Log "Bajando ultimo exe de GitHub..." "Cyan"
    git fetch origin --quiet 2>$null
    git reset --hard origin/main 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Log "Aviso: fallo al bajar (sin conexion?). Sigo con el exe actual." "Yellow"
    } else {
        Log "Exe actualizado." "Green"
    }
}

Log "==== Launcher del servidor LinkAO ====" "White"
Log ("Sondeo de updates cada {0}s. Ctrl+C para detener." -f $PollSegundos) "White"

while ($true) {

    # --- 1) Update antes de arrancar ---
    if (Hay-Update) {
        Log "Hay una version nueva en GitHub." "Cyan"
        Bajar-Update
    }

    if (-not (Test-Path $Exe)) {
        Log "ERROR: no se encontro $Exe. Esperando update..." "Red"
        Start-Sleep -Seconds $PollSegundos
        continue
    }

    # --- 2) Arrancar el server ---
    # Mata cualquier instancia colgada antes de levantar otra.
    taskkill /F /IM $Exe *> $null
    Log "Arrancando $Exe ..." "Green"
    $proc = Start-Process -FilePath (Join-Path $PSScriptRoot $Exe) -NoNewWindow -PassThru

    # --- 3) Mientras corre, sondear GitHub ---
    $reiniciarPorUpdate = $false
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds $PollSegundos
        if ($proc.HasExited) { break }
        if (Hay-Update) {
            Log "Update detectado mientras el server corria. Reiniciando..." "Cyan"
            Bajar-Update
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
            taskkill /F /IM $Exe *> $null
            $reiniciarPorUpdate = $true
            break
        }
    }

    # --- 4) Decidir por que se corto ---
    if ($reiniciarPorUpdate) {
        Log "Relanzando con la version nueva..." "Green"
        # vuelve al inicio del while sin pausa
    } else {
        Log ("El servidor se cerro (exit {0}). Reiniciando en {1}s..." -f $proc.ExitCode, $EsperaReinicio) "Yellow"
        Start-Sleep -Seconds $EsperaReinicio
    }
}
