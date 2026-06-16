# Migración Servidor LinkAO: VB6 → C# (.NET 9)

Objetivo: migrar el servidor (Argentum Online, ~75.000 líneas VB6) a C# manteniendo
el protocolo **byte-idéntico** para que el cliente Godot 4 siga funcionando sin tocarlo.

## Estado actual (núcleo hecho a mano — COMPILA)

| Archivo | Qué reemplaza del VB6 | Estado |
|---------|----------------------|--------|
| `Network/ByteQueue.cs` | `clsByteQueue.cls` | ✅ Port 1:1 del wire format (CP1252, LE) |
| `Network/ServerPacketID.cs` | enum `ServerPacketID` (Protocol.bas) | ✅ 134 ids, valores 1:1 |
| `Network/ClientPacketID.cs` | enum `ClientPacketID` (Protocol.bas) | ✅ 139 ids, valores 1:1 |
| `Network/Connection.cs` | Winsock (`wsksock.bas`, `TCP.bas`) | ✅ socket por cliente + colas in/out |
| `Network/GameServer.cs` | listener + timer de flush | ✅ accept loop + flush ~50/seg |
| `Network/PacketHandler.cs` | `HandleIncomingData` (Protocol.bas) | 🟡 dispatch + 10 handlers con parseo 1:1 (login, Walk, ChangeHeading, Talk, Request*, Online, Ping) |
| `Network/ServerPackets.cs` | senders `Write*` (Protocol.bas) | 🟡 7 senders 1:1 (Logged, ChangeMap, ConsoleMsg, ShowMessageBox, UserIndexInServer, UserCharIndexInServer, Pong) |
| `Network/ServerConfig.cs` | `clsIniManager` (parcial) | 🟡 solo lee `Puerto` |
| `Program.cs` | `Sub Main` | ✅ |

### Formato REAL del login (verificado en Protocol.bas:1288)

`HandleLoginExistingChar` lee, en este orden exacto:
`Byte(id)` + `ASCIIString(Cuenta)` + `ASCIIString(Password)` + `Integer(Version)` +
`ASCIIString(UserName)` + `ASCIIString(MacAddress)` + `Long(HDserial)`.

> ⚠️ Anchos de ID que NO son uniformes (verificados): todos los senders portados
> escriben el ID con **WriteByte (1 byte)**. En `ConsoleMsg` el texto va ANTES del
> font, y el font es Integer (2 bytes). En `ShowMessageBox` el cable es solo
> `Byte(id)+ASCIIString(msg)+Byte(Accion)` (el `EsPredefinido` del VB6 NO viaja).

> **Clave del x1:** `ByteQueue` serializa exactamente igual que el VB6:
> Integer=2B LE, Long=4B LE, Single=4B, Double=8B, Bool=1B,
> ASCIIString = Int16 LE (longitud) + bytes CP1252, ASCIIStringFixed = bytes CP1252 sin prefijo.
> Mientras todo pase por `ByteQueue`, el cliente Godot no nota la diferencia.

## Compilar y correr

```powershell
cd "ServidorCS"
dotnet build
dotnet run
```
Escucha en el puerto de `Server.ini` (clave `Puerto`), o 7666 por defecto.
Probar con el cliente Godot apuntando a `127.0.0.1:7666`.

## Lo que falta (el grueso) y cómo encararlo rápido

Falta portar la lógica de juego: los ~139 handlers de packets + módulos
(`SistemaCombate`, `Modulo_UsUaRiOs`, `modHechizos`, `FileIO`, `InvUsuario`, etc.).

### Camino recomendado: VBUC para el bulk

1. Descargar **Mobilize.Net Visual Basic Upgrade Companion** (edición gratuita):
   https://www.mobilize.net/products/vbuc
2. Abrir el proyecto VB6 (`Servidor/Codigo`) y convertir a **C#**.
3. VBUC traduce mecánicamente ~80-90%. Lo que SIEMPRE hay que arreglar a mano
   (y por eso este núcleo ya está hecho):
   - **Winsock** → ya resuelto con `Connection`/`GameServer`. Borrar lo que VBUC genere para sockets.
   - **`Get #`/`Put #` (I/O binario)** → reemplazar por `BinaryReader/Writer`.
     OJO: arrays VB6 son **column-major** al leer `Get #f, , array` (memoria del proyecto).
   - **Strings/archivos** → forzar `Encoding.GetEncoding(1252)` (NO UTF-8), si no se rompen ñ/á/é.
   - **Forms `.frm`** → son UI de admin; descartar o rehacer mínimas.
4. Pegar el código de lógica convertido y reconectar cada `HandleXxx` al `switch` de `PacketHandler.Dispatch`.

### Patrón para portar cada handler

VB6:
```vb
Private Sub HandleWalk(ByVal UserIndex As Integer)
    With UserList(UserIndex).incomingData
        Call .ReadByte()              ' id
        Dim Heading As Byte
        Heading = .ReadByte()
    End With
End Sub
```
C#:
```csharp
private static void HandleWalk(Connection conn)
{
    var b = conn.IncomingData;
    b.ReadByte();              // id
    byte heading = b.ReadByte();
}
```
El orden de lectura **debe ser idéntico** al VB6 (mismo orden = mismos bytes).

## Prioridad sugerida (para tener algo jugable antes)

1. Login real: `HandleLoginExistingChar` + `Cuentas.bas` + `FileIO.bas` (charfile) + flujo
   `Logged`/`UserIndexInServer`/`ChangeMap`/`CharacterCreate`.
2. Movimiento: `HandleWalk` + `MoveUserChar` (ver bugs conocidos en memoria del proyecto).
3. Chat: `HandleTalk` → `ConsoleMsg`/`ChatOverHead`.
4. Luego inventario, combate, hechizos, etc.
