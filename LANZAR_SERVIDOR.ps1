# ============================================================
#  LANZAR_SERVIDOR.ps1  -  Launcher del servidor (corre EN LA VM)
#  SIN GIT y SIN la API de GitHub (cero rate-limit / cero loop).
#
#   1) Detecta version nueva bajando solo "version.txt" por RAW (tiny).
#   2) Si cambio, baja el paquete del repo por CODELOAD (zip, sin API,
#      sin limite, siempre la version actual) y extrae el exe + Dat/.
#   3) Arranca ServidorCS.exe.
#   4) Mientras corre, sondea cada $PollSegundos. Si subiste algo
#      con SUBIR_A_VM.bat -> cierra, baja y reinicia.
#   5) Si el server se cae solo -> lo vuelve a levantar.
#
#  POR QUE ESTE CAMBIO: el launcher viejo usaba api.github.com
#  (commits + git/trees), limitada a 60 req/hora SIN token. De tanto
#  poll/reinicio agotaba la cuota -> Sincronizar fallaba -> creia que
#  SIEMPRE habia update y reiniciaba el server en loop cada 90s.
#  Aca NO se toca la API: version.txt (raw, gratis) para detectar y
#  el zip de codeload (gratis, sin cache vieja) para bajar.
#
#  NO toca los datos de jugadores (Charfile, Cuentas, GUILDS, Backups)
#  ni el Server.ini: esos no estan en el repo.
#
#  Se cierra con Ctrl+C en esta ventana.
# ============================================================

# --- Config ---
$Repo           = "Pira97/linkao-servidor"
$Exe            = "ServidorCS.exe"
$PollSegundos   = 60      # cada cuanto revisa GitHub (raw es gratis, sin limite)
$EsperaReinicio = 3       # pausa tras una caida

$RawBase   = "https://raw.githubusercontent.com/$Repo/main"
$ZipUrl    = "https://codeload.github.com/$Repo/zip/refs/heads/main"
$StateFile = "version_remota.txt"
$UA = @{ "User-Agent" = "linkao-launcher" }

$ProgressPreference = "SilentlyContinue"   # acelera muchisimo Invoke-WebRequest
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}
$ErrorActionPreference = "Continue"
Set-Location -Path $PSScriptRoot

# --- Instancia única ---------------------------------------------------------
# Evita que corran DOS launchers a la vez. Si pasara, cada uno relanza su server y
# (con el PortGuard del exe) mata al del otro -> ping-pong: el server nunca queda vivo
# y "no responde" en la VM. Con este candado, el segundo launcher se cierra solo.
$global:__lockMutex = New-Object System.Threading.Mutex($false, "Global\LinkAO_Launcher_ServidorCS")
$tengoLock = $false
try { $tengoLock = $global:__lockMutex.WaitOne(0) }
catch [System.Threading.AbandonedMutexException] { $tengoLock = $true }  # el anterior murió: lo tomo igual
if (-not $tengoLock) {
    Write-Host "Ya hay un launcher corriendo en esta VM. Cierro esta ventana para no pelear por el puerto 7666." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    exit 1
}
# -----------------------------------------------------------------------------

function Log($msg, $color = "Gray") {
    Write-Host ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $msg) -ForegroundColor $color
}

# Version remota = contenido de version.txt (timestamp yyyyMMddHHmmss). $null si no hay conexion.
# Cache-bust con query unica para que el CDN de raw no sirva una copia vieja.
function Get-RemotaVersion {
    try {
        $u = "$RawBase/version.txt?nc=" + [guid]::NewGuid().ToString("N")
        return ((Invoke-WebRequest -Uri $u -Headers $UA -TimeoutSec 20 -UseBasicParsing).Content).Trim()
    } catch { return $null }
}

function Get-LocalVersion {
    if (Test-Path $StateFile) { return (Get-Content $StateFile -Raw).Trim() }
    return ""
}

# True solo si $remoto es ESTRICTAMENTE mas nuevo que $local. Con timestamps
# ordenables esto mata el loop: si raw sirviera por cache una version vieja,
# vieja < local -> se ignora (no reinicia en circulos).
function EsNueva($remoto, $local) {
    if (-not $remoto) { return $false }
    if (-not $local)  { return $true }
    $r = [int64]0; $l = [int64]0
    if ([int64]::TryParse($remoto, [ref]$r) -and [int64]::TryParse($local, [ref]$l)) {
        return ($r -gt $l)
    }
    return ($remoto -ne $local)
}

# Baja el repo (zip de codeload, sin API, sin cache vieja) y copia exe + Dat/.
function Sincronizar($ver) {
    Log "Bajando version $ver (exe + Dat) desde GitHub... puede tardar con el exe (~68MB)." "Cyan"
    $tmp = Join-Path $env:TEMP ("linkao_" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    $zip = Join-Path $tmp "repo.zip"

    try { Invoke-WebRequest -Uri $ZipUrl -OutFile $zip -Headers $UA -TimeoutSec 600 }
    catch {
        Log "No pude bajar el paquete de GitHub (sin conexion?). Sigo con lo que hay." "Yellow"
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; return $false
    }

    try { Expand-Archive -Path $zip -DestinationPath $tmp -Force }
    catch {
        Log "Fallo al descomprimir el paquete." "Yellow"
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; return $false
    }

    # El zip trae una unica carpeta raiz: linkao-servidor-<sha>/
    $root = Get-ChildItem $tmp -Directory | Select-Object -First 1
    if (-not $root) {
        Log "Paquete vacio o con formato inesperado." "Yellow"
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; return $false
    }

    $ok = $true

    # exe (hay que matar la instancia vieja para poder reemplazarlo)
    $srcExe = Join-Path $root.FullName $Exe
    if (Test-Path $srcExe) {
        taskkill /F /IM $Exe *> $null
        Start-Sleep -Milliseconds 500
        try { Copy-Item $srcExe (Join-Path $PSScriptRoot $Exe) -Force }
        catch { Log "No pude reemplazar el exe (sigue en uso?)." "Yellow"; $ok = $false }
    } else {
        Log "El paquete no traia $Exe." "Yellow"; $ok = $false
    }

    # El propio launcher (.ps1): asi se auto-actualiza sin tener que
    # borrarlo a mano en la VM. Toma efecto en el proximo arranque.
    $srcPs1 = Join-Path $root.FullName "LANZAR_SERVIDOR.ps1"
    if (Test-Path $srcPs1) {
        try { Copy-Item $srcPs1 (Join-Path $PSScriptRoot "LANZAR_SERVIDOR.ps1") -Force }
        catch { Log "No pude actualizar LANZAR_SERVIDOR.ps1 (en uso?)." "Yellow" }
    }

    # Dat/ (obj.dat, NPCs.dat, etc.)
    $srcDat = Join-Path $root.FullName "Dat"
    if (Test-Path $srcDat) {
        try { Copy-Item $srcDat $PSScriptRoot -Recurse -Force }
        catch { Log "No pude copiar Dat/." "Yellow"; $ok = $false }
    }

    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    if ($ok) { Log "Sincronizado a version $ver." "Green" }
    return $ok
}

Log "==== Launcher del servidor LinkAO (sin git, sin API) ====" "White"
Log ("Sondeo cada {0}s. Ctrl+C para detener." -f $PollSegundos) "White"

while ($true) {

    # --- 1) Update antes de arrancar ---
    $remoto = Get-RemotaVersion
    $local  = Get-LocalVersion
    $localTxt = $local; if (-not $localTxt) { $localTxt = "(ninguna)" }
    if (-not $remoto) {
        Log "No pude leer version.txt de GitHub (sin conexion o rate?). Sigo con lo local ($localTxt)." "Yellow"
    } else {
        Log ("Version -> GitHub: {0} | local: {1}" -f $remoto, $localTxt) "DarkGray"
    }
    if (EsNueva $remoto $local) {
        Log "Hay una version nueva en GitHub ($remoto). Bajando..." "Cyan"
        taskkill /F /IM $Exe *> $null
        if (Sincronizar $remoto) { Set-Content -Path $StateFile -Value $remoto }
    } elseif ($remoto) {
        Log "Server al dia (version $remoto). Sin novedades." "Green"
    }

    # Si no hay exe todavia (primer arranque), bajar todo.
    if (-not (Test-Path $Exe)) {
        Log "No hay $Exe local, bajando todo de GitHub..." "Yellow"
        if ($remoto -and (Sincronizar $remoto)) { Set-Content -Path $StateFile -Value $remoto }
        if (-not (Test-Path $Exe)) { Log "No se pudo bajar el exe. Reintento en ${PollSegundos}s." "Red"; Start-Sleep -Seconds $PollSegundos; continue }
    }

    # --- 2) Arrancar el server ---
    taskkill /F /IM $Exe *> $null
    Log "Arrancando $Exe ..." "Green"
    $proc = Start-Process -FilePath (Join-Path $PSScriptRoot $Exe) -NoNewWindow -PassThru

    # --- 3) Mientras corre, sondear GitHub ---
    $reiniciarPorUpdate = $false
    while (-not $proc.HasExited) {
        Start-Sleep -Seconds $PollSegundos
        if ($proc.HasExited) { break }
        $r = Get-RemotaVersion
        if (-not (EsNueva $r (Get-LocalVersion))) {
            if ($r) { Log ("Sondeo OK: sin novedades (version {0}). Server corriendo." -f $r) "DarkGray" }
            else    { Log "Sondeo: no pude leer GitHub esta vez. Server sigue corriendo." "Yellow" }
        }
        if (EsNueva $r (Get-LocalVersion)) {
            Log "Update detectado mientras el server corria ($r). Reiniciando..." "Cyan"
            try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {}
            taskkill /F /IM $Exe *> $null
            if (Sincronizar $r) { Set-Content -Path $StateFile -Value $r }
            $reiniciarPorUpdate = $true
            break
        }
    }

    # --- 4) Por que se corto ---
    if ($reiniciarPorUpdate) {
        Log "Relanzando con la version nueva..." "Green"
    } else {
        Log ("El servidor se cerro (exit {0}). Reiniciando en {1}s..." -f $proc.ExitCode, $EsperaReinicio) "Yellow"
        Start-Sleep -Seconds $EsperaReinicio
    }
}
