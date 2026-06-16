using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// ACCION() - Migración estricta 1:1 desde Acciones.bas:12-546.
/// Maneja doble-click del usuario (NPCs, objetos especiales, herramientas).
///
/// FASE 1 (completada):
/// - Rango visión (RANGO_VISION_X/Y)
/// - Limpieza Trabajando/Lingoteando
/// - hayObjetoEspecial en 4 tiles
/// - NPCs (con precedencia)
/// - Objetos multi-tile (puertas, correo, pozos, fogatas)
///
/// FASE 2 (TODO):
/// - Herramientas (pesca, minería, leña, tijeras)
/// - Yunque / herrería
/// - Flags Lingoteando
///
/// FASE 3 (TODO):
/// - Facciones (esCiuda, esArmada, etc.)
/// - Subastas
/// - Convertidores
/// </summary>
public static class Accion
{
    // eNPCType (Declares.bas:424)
    public const byte NT_Comun = 0, NT_Revividor = 1, NT_GuardiaCity = 2, NT_Entrenador = 3,
        NT_Banquero = 4, NT_Facciones = 5, NT_BlancosCombate = 6, NT_Transportador = 7,
        NT_Veterinaria = 11, NT_Timbero = 12, NT_Subastador = 16, NT_Convertidor = 18,
        NT_Shop = 19, NT_Dragon = 20;

    private const byte FONT_INFO = 3;

    // Constantes de rango de visión (AI_NPC.bas:50-51)
    private const byte RANGO_VISION_X = 8;
    private const byte RANGO_VISION_Y = 6;

    // FASE 2: Herramientas (Declares.bas)
    private const short RED_PESCA = 138;
    private const short CAÑA_PESCA = 881;
    private const short PIQUETE_MINERO = 187;
    private const short HACHA_LEÑADOR = 127;
    private const short TIJERAS = 885;

    // Sonidos
    private const short SND_PUERTA = 5;
    private const short SND_TRABAJO = 6;

    /// <summary>
    /// Sub Accion(ByVal UserIndex, Map, X, Y) - Acciones.bas:12-546.
    /// Punto de entrada desde HandleDoubleClick (Protocol.bas:2940).
    /// Flujo: validar rango → limpiar flags → detectar NPCs/objetos → ejecutar acción.
    /// </summary>
    public static void DoubleClick(int userIndex, short map, byte x, byte y)
    {
        try
        {
            var u = UserListManager.UserList[userIndex];
            if (u == null) return;

            // VALIDACIÓN 1: Rango de visión (Acciones.bas:23-26 - ToxicWaste comment)
            if (Math.Abs(u.Pos.Y - y) > RANGO_VISION_Y || Math.Abs(u.Pos.X - x) > RANGO_VISION_X)
                return;

            // VALIDACIÓN 2: Si estaba trabajando, detener trabajo (Acciones.bas:28-32)
            if (u.flags.Trabajando)
            {
                u.flags.Trabajando = false;
                u.flags.Lingoteando = 0; // Limpiar flag de fundición
                ServerPackets.ConsoleMsg(u.Conn, "Dejas de trabajar.", FONT_INFO);
            }

            // VALIDACIÓN 3: Posición válida en mapa (Acciones.bas:35)
            var map_data = MapLoader.Get(map);
            if (map_data == null || x < 1 || x > 100 || y < 1 || y > 100)
                return;

            // DETECCIÓN DE OBJETOS ESPECIALES (Acciones.bas:39-72)
            // Busca en 4 tiles para determinar si hay objeto especial que bloquee herramientas
            bool hayObjetoEspecial = DetectarObjetoEspecial(map, x, y);

            // BÚSQUEDA DE NPC EN POSICIÓN (X, Y) (Acciones.bas:187+)
            var npc = NpcManager.NpcAt(map, x, y);

            if (npc != null)
            {
                // === RAMA: HAY NPC ===
                ProcesarNpc(userIndex, npc, map, u);
            }
            // === RAMA: HERRAMIENTA DE TRABAJO equipada (caña/red/piquete/hacha/tijeras) ===
            // Tiene prioridad sobre el objeto del tile (VB6 procesa el tool ANTES, Acciones.bas:74-171):
            // minar/talar requieren un yacimiento/árbol que SÍ tienen FloorObj>0; con el ruteo anterior
            // (tool sólo si FloorObj==0) minería y tala no funcionaban. El martillo (herrería) NO entra
            // acá → sigue al branch de objeto (yunque).
            // PERO los objetos especiales (puertas/correo/pozos/leña) tienen prioridad sobre el tool
            // (VB6 Acciones.bas:74 exige Not hayObjetoEspecial); sin esto, con herramienta equipada
            // las puertas no abrían ("no hay árbol/yacimiento ahí").
            // CUSTOM (no 1:1): además el click debe caer SOBRE el recurso de la herramienta
            // (agua/yacimiento/árbol); si no, el doble click se comporta como sin herramienta
            // en vez de spamear "no hay X aquí" / arrancar a trabajar en cualquier lado.
            else if (!hayObjetoEspecial && EsHerramientaTrabajo(u.Invent.AnilloEqpObjIndex)
                     && EsTargetDeHerramienta(u.Invent.AnilloEqpObjIndex, map_data, x, y))
            {
                ProcesarHerramientas(userIndex, map, x, y, u, map_data);
            }
            else if (map_data.FloorObj[x, y] > 0)
            {
                // === RAMA: Objeto en (X, Y) ===
                var od = ObjData.Get(map_data.FloorObj[x, y]);
                ProcesarObjetoEnTile(userIndex, map, x, y, u, map_data);
            }
            else if (x + 1 <= 100 && map_data.FloorObj[x + 1, y] > 0)
            {
                // === RAMA: Objeto en (X+1, Y) ===
                short objIdx = map_data.FloorObj[x + 1, y];
                if (ObjData.Get(objIdx).Type == ObjType.Puertas)
                {
                    u.TargetObj = objIdx; u.TargetObjMap = map; u.TargetObjX = (byte)(x + 1); u.TargetObjY = y;
                    AccionParaPuerta(map, (byte)(x + 1), y, userIndex, u);
                }
            }
            else if (x + 1 <= 100 && y + 1 <= 100 && map_data.FloorObj[x + 1, y + 1] > 0)
            {
                // === RAMA: Objeto en (X+1, Y+1) ===
                short objIdx = map_data.FloorObj[x + 1, y + 1];
                if (ObjData.Get(objIdx).Type == ObjType.Puertas)
                {
                    u.TargetObj = objIdx; u.TargetObjMap = map; u.TargetObjX = (byte)(x + 1); u.TargetObjY = (byte)(y + 1);
                    AccionParaPuerta(map, (byte)(x + 1), (byte)(y + 1), userIndex, u);
                }
            }
            else if (y + 1 <= 100 && map_data.FloorObj[x, y + 1] > 0)
            {
                // === RAMA: Objeto en (X, Y+1) ===
                short objIdx = map_data.FloorObj[x, y + 1];
                if (ObjData.Get(objIdx).Type == ObjType.Puertas)
                {
                    u.TargetObj = objIdx; u.TargetObjMap = map; u.TargetObjX = x; u.TargetObjY = (byte)(y + 1);
                    AccionParaPuerta(map, x, (byte)(y + 1), userIndex, u);
                }
            }
            // (CUSTOM) Sin NPC, sin objeto y sin recurso bajo el click: no hacer nada.
            // Antes acá se llamaba ProcesarHerramientas y tiraba mensajes de trabajo en tiles vacíos.
        }
        catch (Exception ex)
        {
            // TODO: Loguear error
            Console.WriteLine($"[ERROR] Accion() UserIndex={userIndex}: {ex.Message}");
        }
    }

    /// <summary>
    /// Detecta si hay un objeto especial en los 4 tiles (X,Y), (X+1,Y), (X+1,Y+1), (X,Y+1).
    /// Usado para evitar procesar herramientas si hay objeto importante (Acciones.bas:39-72).
    /// </summary>
    private static bool DetectarObjetoEspecial(short map, byte x, byte y)
    {
        var m = MapLoader.Get(map);
        if (m == null) return false;

        // Check (X, Y)
        if (m.FloorObj[x, y] > 0)
        {
            var od = ObjData.Get(m.FloorObj[x, y]);
            if (od.Type == ObjType.Puertas || od.Type == ObjType.Correo ||
                od.Type == ObjType.Pozos || od.Type == ObjType.Lena)
                return true;
        }

        // Check (X+1, Y)
        if (x + 1 <= 100 && m.FloorObj[x + 1, y] > 0)
        {
            if (ObjData.Get(m.FloorObj[x + 1, y]).Type == ObjType.Puertas)
                return true;
        }

        // Check (X+1, Y+1)
        if (x + 1 <= 100 && y + 1 <= 100 && m.FloorObj[x + 1, y + 1] > 0)
        {
            if (ObjData.Get(m.FloorObj[x + 1, y + 1]).Type == ObjType.Puertas)
                return true;
        }

        // Check (X, Y+1)
        if (y + 1 <= 100 && m.FloorObj[x, y + 1] > 0)
        {
            if (ObjData.Get(m.FloorObj[x, y + 1]).Type == ObjType.Puertas)
                return true;
        }

        return false;
    }

    /// <summary>Procesa interacción con NPC (Acciones.bas:187-473).</summary>
    private static void ProcesarNpc(int userIndex, NpcManager.NpcInstance npc, short map, User u)
    {
        // TODO: Obtener el índice correcto del NPC. Usar npc.CharIndex o similar.
        u.TargetNpcCharIndex = npc.CharIndex;

        // Check distance
        int dist = Math.Abs(npc.X - u.Pos.X) + Math.Abs(npc.Y - u.Pos.Y);

        // === Subastadores (Acciones.bas:193-202) ===
        if (npc.NpcType == NT_Subastador)
        {
            if (u.flags.Muerto == 1) return;
            if (dist > 3) { MensajeLejos(u); return; }
            Subastas.SendList(u.id);
            return;
        }

        // === Comerciantes (flag .Comercia) (Acciones.bas:204-253) ===
        if (npc.Comercia)
        {
            if (u.flags.Muerto == 1) return;
            if (u.Comerciando) return;
            if (dist > 3) { MensajeLejos(u); return; }

            // Validación de facción del NPC (Acciones.bas:218-250). Ver FaccionPermiteNpc.
            if (!FaccionPermiteNpc(u, npc))
            { RechazoFaccion(u, npc); return; }

            Commerce.AbrirComercioNpc(userIndex, npc);
            return;
        }

        // === Tiendas (NT_Shop) (Acciones.bas:256-265) ===
        if (npc.NpcType == NT_Shop)
        {
            if (u.flags.Muerto == 1) return;
            if (dist > 3) { MensajeLejos(u); return; }
            ServerPackets.AbrirFormularios(u.Conn, 5); // frmShop
            return;
        }

        // === Convertidores de facción (Acciones.bas:267-284) ===
        // NPC.Status: 1=Ciudadano imperial, 2=Republicano. Redime renegados pagando PERDON.
        if (npc.NpcType == NT_Convertidor)
        {
            if (u.flags.Muerto == 1) return;
            if (dist > 3) { MensajeLejos(u); return; }
            switch (npc.Status)
            {
                case 1: Facciones.EntrarImperial(u, npc.CharIndex); break;
                case 2: Facciones.EntrarRepublica(u, npc.CharIndex); break;
            }
            return;
        }

        // === Facciones (NT_Facciones) (Acciones.bas:286-318) ===
        // NPC.Status: 1=Armada Real, 2=Milicia, 4=Caos. Si ya pertenece, la función Enlistar*
        // responde "ya perteneces" (las Recompensas por rango aún no están portadas).
        if (npc.NpcType == NT_Facciones)
        {
            if (u.flags.Muerto == 1) return;
            if (dist > 3) { MensajeLejos(u); return; }
            // VB6 Acciones.bas:295-318: si ya es de la facción → Recompensa (sube rango); si no → Enlistar.
            switch (npc.Status)
            {
                case 1:
                    if (Facciones.EsArmada(u)) Facciones.RecompensaArmadaReal(u, npc.CharIndex);
                    else Facciones.EnlistarArmadaReal(u, npc.CharIndex);
                    break;
                case 2:
                    if (Facciones.EsMili(u)) Facciones.RecompensaMilicia(u, npc.CharIndex);
                    else Facciones.EnlistarMilicia(u, npc.CharIndex);
                    break;
                case 4:
                    if (Facciones.EsCaos(u)) Facciones.RecompensaCaos(u, npc.CharIndex);
                    else Facciones.EnlistarCaos(u, npc.CharIndex);
                    break;
            }
            return;
        }

        // === Entrenadores (Acciones.bas:320-333) ===
        if (npc.NpcType == NT_Entrenador)
        {
            if (u.flags.Muerto == 1) return;
            if (u.Comerciando) return;
            if (dist > 3) { MensajeLejos(u); return; }
            // TODO FASE 3: WriteTrainerCreatureList(userIndex, u.TargetNpc);
            ServerPackets.ConsoleMsg(u.Conn, "El entrenador aún no tiene criaturas.", FONT_INFO);
            return;
        }

        // === Banqueros (Acciones.bas:335-383) ===
        if (npc.NpcType == NT_Banquero)
        {
            if (u.flags.Muerto == 1) return;
            u.Comerciando = false; // Soft recovery (Acciones.bas:342)
            if (dist > 3) { MensajeLejos(u); return; }
            // Validación de facción del banquero (Acciones.bas:350-380).
            if (!FaccionPermiteNpc(u, npc))
            { RechazoFaccion(u, npc); return; }
            Bank.AbrirBancoNpc(userIndex, npc);
            return;
        }

        // === Revividores / Sacerdotes (Acciones.bas:386-470) ===
        if (npc.NpcType == NT_Revividor)
        {
            if (dist > 10) { MensajeLejos(u); return; }
            // Validación de facción del sacerdote (Acciones.bas:393-440): por mapa imperial/republicano
            // (excepto Rinkel 20 y DungeonNewbie 37) y por status del NPC. Los GM no se gatean.
            if (!SacerdotePermite(u, npc, map))
            { RechazoFaccion(u, npc); return; }

            if (u.flags.Muerto == 1)
            {
                Combat.Resucitar(userIndex);
                // El sonido de revivir SOLO suena cuando te revive el sacerdote (no en hechizos de resu).
                for (int i = 1; i <= UserListManager.LastUser; i++)
                {
                    var o = UserListManager.UserList[i];
                    if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                    {
                        ServerPackets.PlayWave(o.Conn, Sounds.RESUCITADO, (byte)u.Pos.X, (byte)u.Pos.Y); // 204
                        ServerPackets.PlayWave(o.Conn, Sounds.RESUCITAR, (byte)u.Pos.X, (byte)u.Pos.Y);  // 84
                    }
                }
                ServerPackets.ConsoleMsg(u.Conn, "¡Has sido revivido!", FONT_INFO);
                return;
            }

            if (u.Stats.MinHP < u.Stats.MaxHP)
            {
                // VB6 Acciones.bas:461-468: cura HP, limpia veneno/incineración, sonido SND_SANAR
                // y partícula 28 (100ms, auto-expira) sobre el personaje.
                u.flags.Envenenado = 0;
                u.flags.Incinerado = 0;
                u.Stats.MinHP = u.Stats.MaxHP;
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                for (int i = 1; i <= UserListManager.LastUser; i++)
                {
                    var o = UserListManager.UserList[i];
                    if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
                    {
                        ServerPackets.PlayWave(o.Conn, Sounds.SANAR_HERIDAS, (byte)u.Pos.X, (byte)u.Pos.Y);
                        ServerPackets.EfectoCharParticula(o.Conn, u.Char.CharIndex, 28, 100f, false);
                    }
                }
                ServerPackets.ConsoleMsg(u.Conn, "¡Has sido curado!", FONT_INFO);
            }

            // Si el sacerdote te atiende (tu ciudad o ciudad neutral que atiende a todos),
            // ofrece fijar este mapa como tu hogar (ShowMessageBox accion 5 → SeleccionarHogar caso 1).
            u.TargetNpcCharIndex = npc.CharIndex;
            // Si este mapa YA es tu hogar, no preguntar de nuevo: avisar por consola.
            int equidadHogar = u.Hogar switch
            {
                1 => 34, 2 => 194, 3 => 1, 4 => 59, 5 => 20, 6 => 37, 7 => 62,
                8 => 151, 9 => 218, 10 => 180, 11 => 185, 12 => 111, _ => 0,
            };
            if (u.Pos.Map == equidadHogar)
            {
                ServerPackets.ConsoleMsg(u.Conn, "Ya asignaste esta ciudad como tu hogar.", FONT_INFO);
                return;
            }
            ServerPackets.ShowMessageBox(u.Conn, "¿Deseas establecer esta ciudad como tu hogar?", true, 5);
            return;
        }

        // NPC común sin interacción especial
    }

    /// <summary>
    /// ¿El NPC (mercader/banco) atiende al jugador según su facción? (Acciones.bas:218-250/350-380).
    /// Status del NPC: 0/3 = neutral (atiende a todos); 1=Imperial (ciudadanos/armada), 2=Republicano
    /// (repu/milicia), 4=Caos. Los GM (FaccionStatus≥7) nunca se gatean.
    /// </summary>
    private static bool FaccionPermiteNpc(User u, NpcManager.NpcInstance npc)
    {
        if (u.FaccionStatus >= 7) return true;
        if (npc.Status <= 0 || npc.Status == 3) return true;
        return npc.Status switch
        {
            1 => Facciones.EsCiuda(u) || Facciones.EsArmada(u),
            2 => Facciones.EsRepu(u) || Facciones.EsMili(u),
            4 => Facciones.EsCaos(u),
            _ => true,
        };
    }

    /// <summary>
    /// ¿El sacerdote revive/cura al jugador? (Acciones.bas:393-440). Rinkel(20)/DungeonNewbie(37): a todos.
    /// Mapas imperiales (1/34/59): sólo ciudadanos/armada. Republicanos (194/63/184): sólo repu/milicia.
    /// Otros mapas: según el status del NPC (igual que mercader/banco).
    /// </summary>
    private static bool SacerdotePermite(User u, NpcManager.NpcInstance npc, short map)
    {
        if (u.FaccionStatus >= 7) return true;
        if (map == 20 || map == 37) return true; // sin restricción
        if (map == 1 || map == 34 || map == 59) return Facciones.EsCiuda(u) || Facciones.EsArmada(u);
        if (map == 194 || map == 63 || map == 184) return Facciones.EsRepu(u) || Facciones.EsMili(u);
        return FaccionPermiteNpc(u, npc);
    }

    /// <summary>Mensaje de rechazo por facción sobre la cabeza del NPC (WriteChatOverHeadLocale 592).</summary>
    private static void RechazoFaccion(User u, NpcManager.NpcInstance npc)
    {
        ServerPackets.ChatOverHead(u.Conn, "No tengo nada que hacer con alguien de tu facción.", (short)npc.CharIndex, 4);
    }

    /// <summary>Procesa objetos en el tile (X, Y) (Acciones.bas:476-498).</summary>
    private static void ProcesarObjetoEnTile(int userIndex, short map, byte x, byte y, User u, MapData map_data)
    {
        short objIdx = map_data.FloorObj[x, y];
        u.TargetObj = objIdx; u.TargetObjMap = map; u.TargetObjX = x; u.TargetObjY = y;

        var od = ObjData.Get(objIdx);
        if (od.Type == ObjType.Puertas)
        {
            AccionParaPuerta(map, x, y, userIndex, u);
        }
        else if (od.Type == ObjType.Correo)
        {
            AccionParaCorreo(map, x, y, userIndex, u);
        }
        else if (od.Type == ObjType.Lena)
        {
            // Rama apagada (FOGATA_APAG) → intentar prender fogata (Acciones.bas:658).
            if (objIdx == FOGATA_APAG && u.flags.Muerto == 0)
                AccionParaRamita(map, x, y, userIndex, u);
        }
        else if (od.Type == ObjType.Pozos)
        {
            AccionParaPozos(map, x, y, userIndex, u);
        }
        else if (od.Type == ObjType.Yunque)
        {
            // Herrería (Acciones.bas:173-183): doble-click sobre el yunque con el MARTILLO equipado
            // → envía las listas de items fabricables + abre el formulario de herrería.
            if (u.Invent.AnilloEqpObjIndex == Crafting.MARTILLO_HERRERO)
                Crafting.AbrirCrafteo(userIndex, Crafting.MARTILLO_HERRERO);
        }
    }

    /// <summary>
    /// AccionParaPuerta (Acciones.bas:593-656). Abre/cierra puerta sin llave.
    /// Portado 1:1 con BlockPosition broadcast.
    /// </summary>
    private static void AccionParaPuerta(short map, byte x, byte y, int userIndex, User u)
    {
        var map_data = MapLoader.Get(map);
        if (map_data == null) return;

        short objIdx = map_data.FloorObj[x, y];
        if (objIdx <= 0) return;

        var od = ObjData.Get(objIdx);
        if (od.Type != ObjType.Puertas) return;

        // Distancia ≤ 2 (Acciones.bas:597)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        // Sin llave (Acciones.bas:598)
        if (od.Llave != 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "La puerta está cerrada con llave.", FONT_INFO);
            return;
        }

        // Abre/cierra (Acciones.bas:599+)
        bool abrir = od.Cerrada == 1;
        OperarPuerta(map, x, y, abrir);
        u.TargetObj = abrir ? (short)od.IndexAbierta : (short)od.IndexCerrada;
    }

    /// <summary>
    /// Núcleo de abrir/cerrar una puerta SIN llave en su tile ancla (x,y): cambia el FloorObj y el
    /// bloqueo (ancla y ancla-1) y lo difunde (gráfico por AOI, bloqueo+sonido al mapa). Sin chequeos
    /// de usuario/distancia: lo usan tanto el jugador (AccionParaPuerta) como la IA de guardias.
    /// Devuelve false si no hay puerta operable o ya está en el estado pedido.
    /// </summary>
    public static bool OperarPuerta(short map, byte x, byte y, bool abrir)
    {
        var map_data = MapLoader.Get(map);
        if (map_data == null) return false;

        short objIdx = map_data.FloorObj[x, y];
        if (objIdx <= 0) return false;

        var od = ObjData.Get(objIdx);
        if (od.Type != ObjType.Puertas || od.Llave != 0) return false;

        bool estaCerrada = od.Cerrada == 1;
        if (abrir == !estaCerrada) return false; // ya está en el estado pedido

        short nuevoIndex = abrir ? (short)od.IndexAbierta : (short)od.IndexCerrada;
        map_data.FloorObj[x, y] = nuevoIndex;
        map_data.Blocked[x, y] = !abrir;
        if (x - 1 >= 1) map_data.Blocked[x - 1, y] = !abrir;

        // Difundir cambio (Acciones.bas:606-617). El gráfico de la puerta va por área (AOI); el bloqueo
        // y el sonido se mandan al mapa (inofensivo: bloqueo de tile lejano no afecta, sonido posicional).
        const short SND_PUERTA = 5;
        AreaVisibility.ObjectAppeared(map, x, y, nuevoIndex, 0);
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var o = UserListManager.UserList[i];
            if (o != null && o.flags.UserLogged && o.Conn != null && o.Pos.Map == map)
            {
                ServerPackets.BlockPosition(o.Conn, x, y, map_data.Blocked[x, y]);
                if (x - 1 >= 1)
                    ServerPackets.BlockPosition(o.Conn, (byte)(x - 1), y, map_data.Blocked[x - 1, y]);
                ServerPackets.PlayWave(o.Conn, SND_PUERTA, x, y);
            }
        }
        return true;
    }

    /// <summary>
    /// AccionParaCorreo (Acciones.bas:736-785). Abre correo del jugador.
    /// TODO: Verificar implementación actual y ajustar si es necesario.
    /// </summary>
    private static void AccionParaCorreo(short map, byte x, byte y, int userIndex, User u)
    {
        // Distancia ≤ 3 (Acciones.bas:749)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 3)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        // Muerto check (Acciones.bas:756)
        if (u.flags.Muerto == 1)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás muerto.", FONT_INFO);
            return;
        }

        // Soft recovery (Acciones.bas:763)
        if (u.Comerciando)
            u.Comerciando = false;

        // Abrir correo (Acciones.bas:768-778)
        ServerPackets.CorreoList(u.Conn, u.Correos);
        ServerPackets.AbrirFormularios(u.Conn, 6); // frmCorreo
        u.flags.RecibioCorreo = 0;
        // TODO: WriteMensajeSigno(userIndex, 0);
    }

    /// <summary>
    /// AccionParaPozos (Acciones.bas:548-590). Bebe de pozo (maná/agua).
    /// </summary>
    private static void AccionParaPozos(short map, byte x, byte y, int userIndex, User u)
    {
        // Distancia ≤ 3 (Acciones.bas:554)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 3)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        var map_data = MapLoader.Get(map);
        if (map_data == null) return;

        short objIdx = map_data.FloorObj[x, y];
        var od = ObjData.Get(objIdx);

        // SubTipo: 1=maná, 2=agua (Acciones.bas:556-576)
        if (od.SubTipo == 1)
        {
            // Pozo de maná
            if (u.Stats.MinMAN < u.Stats.MaxMAN)
            {
                u.Stats.MinMAN = u.Stats.MaxMAN;
                ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
                ServerPackets.ConsoleMsg(u.Conn, "Has bebido maná.", FONT_INFO);
                const short SND_BEBER = 4;
                ServerPackets.PlayWave(u.Conn, SND_BEBER, x, y);
            }
        }
        else if (od.SubTipo == 2)
        {
            // Pozo de agua
            if (u.Stats.MinAGU < u.Stats.MaxAGU)
            {
                u.Stats.MinAGU = u.Stats.MaxAGU;
                ServerPackets.UpdateHungerAndThirst(u.Conn, u);
                ServerPackets.ConsoleMsg(u.Conn, "Has bebido agua.", FONT_INFO);
                const short SND_BEBER = 4;
                ServerPackets.PlayWave(u.Conn, SND_BEBER, x, y);
            }
        }
    }

    /// <summary>
    /// ProcesarHerramientas (Acciones.bas:74-185) - FASE 2.
    /// Valida herramienta equipada y ejecuta la acción correspondiente.
    /// </summary>
    /// <summary>¿El objeto es una herramienta de trabajo que se procesa por tile (no el martillo)?</summary>
    private static bool EsHerramientaTrabajo(short obj)
        => obj == RED_PESCA || obj == CAÑA_PESCA || obj == PIQUETE_MINERO || obj == HACHA_LEÑADOR || obj == TIJERAS;

    /// <summary>
    /// CUSTOM (no 1:1): ¿el tile clickeado contiene el recurso que usa esta herramienta?
    /// La acción de trabajar solo se dispara clickeando el recurso (agua/yacimiento/árbol);
    /// las validaciones de distancia/zona/trigger siguen dentro de cada Procesar*.
    /// </summary>
    private static bool EsTargetDeHerramienta(short tool, MapData m, byte x, byte y)
    {
        switch (tool)
        {
            case RED_PESCA:
            case CAÑA_PESCA:
                return m.HasWater(x, y);
            case PIQUETE_MINERO:
                return m.FloorObj[x, y] > 0 && ObjData.Get(m.FloorObj[x, y]).Type == ObjType.Yacimiento;
            case HACHA_LEÑADOR:
            case TIJERAS:
                return m.FloorObj[x, y] > 0 && ObjData.Get(m.FloorObj[x, y]).Type == ObjType.Arboles;
        }
        return false;
    }

    private static void ProcesarHerramientas(int userIndex, short map, byte x, byte y, User u, MapData map_data)
    {
        // Solo procesar si tiene herramienta en slot anillo (AnilloEqpObjIndex)
        if (u.Invent.AnilloEqpObjIndex <= 0)
            return;

        short toolIndex = u.Invent.AnilloEqpObjIndex;

        // Limpiar flag Lingoteando al usar otra herramienta
        u.flags.Lingoteando = 0;

        // Seleccionar herramienta según objeto equipado
        switch (toolIndex)
        {
            case RED_PESCA:
            case CAÑA_PESCA:
                ProcesarPesca(map, x, y, userIndex, u);
                break;

            case PIQUETE_MINERO:
                ProcesarMineria(map, x, y, userIndex, u, map_data);
                break;

            case HACHA_LEÑADOR:
                ProcesarLena(map, x, y, userIndex, u, map_data);
                break;

            case TIJERAS:
                ProcesarTijeras(map, x, y, userIndex, u, map_data);
                break;
        }
    }

    /// <summary>
    /// ProcesarPesca (Acciones.bas:79-91) 1:1 VB6.
    /// </summary>
    private static void ProcesarPesca(short map, byte x, byte y, int userIndex, User u)
    {
        // VB6: Trigger=1 (bajo techo) → no se puede pescar
        var mapData = MapLoader.Get(map);
        if (mapData != null && mapData.Trigger[u.Pos.X, u.Pos.Y] == 1)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No puedes pescar desde aquí adentro.", FONT_INFO);
            return;
        }
        // Validar agua en el tile clickeado (HayAgua = map.HasWater). El tick DoPescar revalida al frente.
        if (mapData == null || !mapData.HasWater(x, y))
        {
            ServerPackets.ConsoleMsg(u.Conn, "Haz click sobre el agua para pescar.", FONT_INFO);
            return;
        }
        ServerPackets.ConsoleMsg(u.Conn, "Comienzas a trabajar.", FONT_INFO);
        u.flags.Trabajando = true;
        u.flags.WorkSkill = Work.SkillPesca;
        u.flags.WorkX = (byte)u.Pos.X;
        u.flags.WorkY = (byte)u.Pos.Y;
    }

    /// <summary>
    /// ProcesarMineria (Acciones.bas:94-117). Comienza a minar si hay yacimiento.
    /// </summary>
    private static void ProcesarMineria(short map, byte x, byte y, int userIndex, User u, MapData map_data)
    {
        // Validar distancia ≤ 2 (Acciones.bas:98)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        // Validar que hay objeto y es tipo Yacimiento (Acciones.bas:100-109)
        if (map_data.FloorObj[x, y] <= 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No hay ningún yacimiento aquí.", FONT_INFO);
            return;
        }

        var od = ObjData.Get(map_data.FloorObj[x, y]);
        if (od.Type != ObjType.Yacimiento)
        {
            ServerPackets.ConsoleMsg(u.Conn, "No hay ningún yacimiento aquí.", FONT_INFO);
            return;
        }

        ServerPackets.ConsoleMsg(u.Conn, "Comienzas a trabajar.", FONT_INFO);
        u.flags.Trabajando = true;
        u.flags.WorkSkill = Work.SkillMineria;
        u.flags.WorkX = x;
        u.flags.WorkY = y;
    }

    /// <summary>
    /// ProcesarLena (Acciones.bas:120-147). Comienza a cortar leña si hay árbol.
    /// Solo permitido en zonas PK.
    /// </summary>
    private static void ProcesarLena(short map, byte x, byte y, int userIndex, User u, MapData map_data)
    {
        // Validar distancia ≤ 2 (Acciones.bas:123)
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        // Solo en zona PK (Acciones.bas:124-127).
        var miL = MapLoader.Get(map)?.Info;
        if (miL != null && !miL.Pk)
        { ServerPackets.ConsoleMsg(u.Conn, "Esta es una zona segura, no puedes trabajar aquí.", FONT_INFO); return; }

        // Validar que hay objeto y es tipo Árbol (Acciones.bas:135-145)
        if (map_data.FloorObj[x, y] <= 0)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Haz click derecho sobre un árbol.", FONT_INFO);
            return;
        }

        var od = ObjData.Get(map_data.FloorObj[x, y]);
        if (od.Type != ObjType.Arboles)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Haz click derecho sobre un árbol.", FONT_INFO);
            return;
        }

        ServerPackets.ConsoleMsg(u.Conn, "Comienzas a trabajar.", FONT_INFO);
        u.flags.Trabajando = true;
        u.flags.WorkSkill = Work.SkillTalar;
        u.flags.WorkX = x;
        u.flags.WorkY = y;
    }

    /// <summary>
    /// ProcesarTijeras (Acciones.bas:149-177). Uso de tijeras (TODO: qué target específico).
    /// </summary>
    private static void ProcesarTijeras(short map, byte x, byte y, int userIndex, User u, MapData map_data)
    {
        // Validar distancia ≤ 2
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        {
            ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);
            return;
        }

        // Tijeras: botánica sobre un árbol, en zona PK (Acciones.bas:144-171).
        var mi = MapLoader.Get(map)?.Info;
        if (mi != null && !mi.Pk)
        { ServerPackets.ConsoleMsg(u.Conn, "Esta es una zona segura, no puedes trabajar aquí.", FONT_INFO); return; }
        if (map_data.FloorObj[x, y] <= 0 || ObjData.Get(map_data.FloorObj[x, y]).Type != ObjType.Arboles)
        { ServerPackets.ConsoleMsg(u.Conn, "Haz click derecho sobre un árbol.", FONT_INFO); return; }

        ServerPackets.ConsoleMsg(u.Conn, "Comienzas a trabajar.", FONT_INFO);
        u.flags.Trabajando = true;
        u.flags.WorkSkill = Work.SkillBotanica;
        u.flags.WorkX = x;
        u.flags.WorkY = y;
    }

    /// <summary>
    /// AccionParaRamita (Acciones.bas:658-734). Prende fogata (skill Supervivencia).
    /// TODO FASE 1: Implementar si FOGATA_APAG está definida.
    /// </summary>
    private const short FOGATA_APAG = 136, FOGATA = 63;
    private const byte SkSupervivencia = 16; // eSkill.Supervivencia

    private static void AccionParaRamita(short map, byte x, byte y, int userIndex, User u)
    {
        // Distancia ≤ 2 (Acciones.bas:681).
        if (Math.Abs(u.Pos.X - x) + Math.Abs(u.Pos.Y - y) > 2)
        { ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO); return; }

        var md = MapLoader.Get(map);
        // Sólo en zona PK y no ZONASEGURA (Acciones.bas:687).
        bool zonaSegura = md != null && md.GetTrigger(x, y) == eTrigger.ZONASEGURA;
        if (md == null || !md.Info.Pk || zonaSegura)
        { ServerPackets.ConsoleMsg(u.Conn, "Esta es una zona segura, no puedes trabajar aquí.", FONT_INFO); return; }

        // Suerte por skill Supervivencia (Acciones.bas:693-700): 1-5→3, 6-10→2, >10→1.
        int sup = u.Stats.UserSkills[SkSupervivencia];
        int suerte = sup <= 5 ? 3 : sup <= 10 ? 2 : 1;
        if (Random.Shared.Next(1, suerte + 1) == 1)
        {
            md.FloorObj[x, y] = FOGATA;
            md.FloorAmount[x, y] = 1;
            AreaVisibility.ObjectAppeared(map, x, y, FOGATA, 1);
            ServerPackets.ConsoleMsg(u.Conn, "¡Has prendido una fogata!", FONT_INFO);
            Skills.SubirSkill(u.id, SkSupervivencia); // SubirSkill 1:1 (Acciones.bas:720)
        }
        else
        {
            ServerPackets.ConsoleMsg(u.Conn, "No has podido prender la fogata.", FONT_INFO);
        }
    }

    // === Helpers ===

    private static void MensajeLejos(User u)
        => ServerPackets.ConsoleMsg(u.Conn, "Estás demasiado lejos.", FONT_INFO);

    private static void MensajeChatFaccion(User u, NpcManager.NpcInstance npc)
        => ServerPackets.ChatOverHead(u.Conn, "No comercio con tu facción.", npc.CharIndex, 4);
}
