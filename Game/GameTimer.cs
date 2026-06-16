using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Tick periódico de estado del personaje (subset de GameTimer en mMainLoop.bas).
/// Regen de HP/Stamina/Maná, hambre/sed. Valores reales de Server.ini.
///
/// Se llama 1 vez por segundo desde el FlushLoop del servidor. En el VB6 corre a 40ms con
/// DeltaTick; acá usamos timestamps absolutos por usuario para equivalencia de tiempo real.
/// </summary>
public static class GameTimer
{
    // Intervalos reales (ms) — Server.ini.
    private const long SanaSinDescansar = 1600, SanaDescansar = 100;
    private const long StaminaSinDescansar = 1500, StaminaDescansar = 1000; // suavizado (VB6: 5/2 por tick de 40ms ≈ regen continuo)
    private const long IntervaloHambre = 4500, IntervaloSed = 4000;
    // Server.ini IntervaloVeneno=500. VB6 EfectoIncinerado usa el MISMO IntervaloVeneno (no
    // IntervaloIncinerado). Nota: el loop de estados corre a 1Hz, así que el efectivo es ~1s.
    private const long IntervaloVeneno = 500, IntervaloIncinera = 500;
    private static readonly Random _rngTimer = new();

    /// <summary>Procesa regen/hambre/sed de todos los usuarios logueados. Llamado ~1/seg.</summary>
    public static void Tick()
    {
        long now = Environment.TickCount64;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.Conn == null) continue;

            // Casteo de runa de teletransporte (puede ocurrir estando muerto, para volver al hogar).
            if (u.CasteandoRuna > 0) TickRuna(i, u);

            // Portal de teletransporte (hechizo 53): crea el objeto a los 5s y lo cierra a los 15s.
            if (u.PortalTime > 0) TickPortal(u);

            // Casteo de resucitar/resurrección: al completarse revive al objetivo.
            if (u.ResucitandoHasta > 0) Combat.TickResucitar(i, u);

            if (u.flags.Muerto == 1) continue;

            bool descansa = u.flags.Descansar != 0;

            // --- Frío (EfectoFrio, General.bas:1094; mMainLoop:259): desnudo y no-GM ---
            // Mapa de nieve (Terreno=NIEVE) → pierde 5% de MaxHP (puede morir); resto → 5% de MaxSta.
            // VB6 IntervaloFrio=50ms ≈ cada tick; acá el loop corre a 1Hz → 1 vez por segundo.
            if (u.flags.Desnudo != 0 && u.FaccionStatus < AdminLoader.STATUS_CONSEJERO)
            {
                if (MapLoader.Get(u.Pos.Map).Info.Terreno.Equals("NIEVE", StringComparison.OrdinalIgnoreCase))
                {
                    ServerPackets.ConsoleMsg(u.Conn, "¡¡Estás muriendo de frío, abrígate o morirás!!", 4); // locale 46
                    u.Stats.MinHP = (short)(u.Stats.MinHP - u.Stats.MaxHP * 5 / 100);
                    if (u.Stats.MinHP < 1) { u.Stats.MinHP = 0; Combat.UserDie(i); continue; }
                    ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
                }
                else if (u.Stats.MinSta > 0)
                {
                    u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - u.Stats.MaxSta * 5 / 100);
                    ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                }
            }

            // --- Hambre / Sed ---
            if (u.Stats.MaxHam > 0 && now - u._timerHambre >= IntervaloHambre)
            {
                u._timerHambre = now;
                if (u.Stats.MinHam > 0) { u.Stats.MinHam--; if (u.Stats.MinHam == 0) u.flags.Hambre = 1; ServerPackets.UpdateHungerAndThirst(u.Conn, u); }
            }
            if (u.Stats.MaxAGU > 0 && now - u._timerSed >= IntervaloSed)
            {
                u._timerSed = now;
                if (u.Stats.MinAGU > 0) { u.Stats.MinAGU--; if (u.Stats.MinAGU == 0) u.flags.Sed = 1; ServerPackets.UpdateHungerAndThirst(u.Conn, u); }
            }

            // --- Regen HP: solo si no tiene hambre ni sed (regla VB6) ---
            long intHP = descansa ? SanaDescansar : SanaSinDescansar;
            if (u.flags.Hambre == 0 && u.flags.Sed == 0 && u.Stats.MinHP < u.Stats.MaxHP && now - u._timerSanar >= intHP)
            {
                u._timerSanar = now;
                int rec = Math.Max(1, u.Stats.MaxHP / 100 + u.Stats.ELV / 5);
                // Anillo de Regeneración (670, AceleraVida(4)): duplica la regeneración de vida.
                if (Inventory.TieneEfectoMagico(u, 4)) rec *= 2;
                u.Stats.MinHP = (short)Math.Min(u.Stats.MaxHP, u.Stats.MinHP + rec);
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
            }

            // --- Regen de maná por Anillo de la Quinta Esencia (706, AceleraMana(5)): recupera
            //     2% del maná máximo por tick de regen aun sin meditar (+ bonus al meditar en DoMeditar) ---
            if (u.Stats.MaxMAN > 0 && u.Stats.MinMAN < u.Stats.MaxMAN
                && u.flags.Hambre == 0 && u.flags.Sed == 0
                && now - u._timerManaAnillo >= intHP && Inventory.TieneEfectoMagico(u, 5))
            {
                u._timerManaAnillo = now;
                int recM = Math.Max(1, u.Stats.MaxMAN * 2 / 100);
                u.Stats.MinMAN = (short)Math.Min(u.Stats.MaxMAN, u.Stats.MinMAN + recM);
                ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
            }

            // --- Regen Stamina (RecStamina, General.bas:1361) ---
            // No regenera trabajando, ni desnudo (sin armadura) salvo que esté montado.
            // Con hambre o sed no recupera: PIERDE 5% del máximo (mín. 5) por tick.
            long intSta = descansa ? StaminaDescansar : StaminaSinDescansar;
            if (!u.flags.Trabajando && (u.flags.Desnudo == 0 || u.flags.Montando != 0)
                && now - u._timerSta >= intSta)
            {
                if (u.flags.Hambre == 1 || u.flags.Sed == 1)
                {
                    if (u.Stats.MinSta > 0)
                    {
                        u._timerSta = now;
                        int perdida = Math.Max(5, u.Stats.MaxSta * 5 / 100);
                        u.Stats.MinSta = (short)Math.Max(0, u.Stats.MinSta - perdida);
                        ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                    }
                }
                else if (u.Stats.MinSta < u.Stats.MaxSta)
                {
                    u._timerSta = now;
                    // Base suavizada (VB6 regenera 15% cada 50ms ≈ continuo; acá MaxSta/10 por tick)
                    // + bonus de Supervivencia ×2 como el VB6 (General.bas:1416).
                    int rec = Math.Max(1, u.Stats.MaxSta / 10) + u.Stats.UserSkills[16] * 2; // eSkill.Supervivencia=16
                    u.Stats.MinSta = (short)Math.Min(u.Stats.MaxSta, u.Stats.MinSta + rec);
                    ServerPackets.UpdateSta(u.Conn, u.Stats.MinSta);
                }
            }

            // --- Meditación: regen de maná (DoMeditar, Trabajo.bas:2369) ---
            DoMeditar(u, now);

            // --- Trabajo: pesca/minería/talar (DoTrabajar, Trabajo.bas:2899) ---
            if (u.flags.Trabajando) Work.DoTrabajar(i);

            // Veneno/incineración salen de acá: tickean cada IntervaloVeneno=500ms y se procesan
            // en TickEfectosDanio (cadencia ~500ms del GameServer), no en este loop de 1Hz.
        }
    }

    /// <summary>Efectos de daño periódico que el VB6 aplica cada IntervaloVeneno=500ms (veneno e
    /// incineración). Se llama desde el GameServer a ~500ms para respetar el rate 2Hz del VB6,
    /// en vez del loop de 1Hz de Tick(). El gate por reloj (now - timer >= 500) ya estaba.</summary>
    public static void TickEfectosDanio()
    {
        long now = Environment.TickCount64;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (!u.flags.UserLogged || u.Conn == null || u.flags.Muerto == 1) continue;

            // --- Veneno (EfectoVeneno, General.bas:1437): Random(1,5) cada IntervaloVeneno ---
            if (u.flags.Envenenado == 1 && now - u._timerVeneno >= IntervaloVeneno)
            {
                u._timerVeneno = now;
                int dano = _rngTimer.Next(1, 6) + u.flags.NivelVeneno; // nivel suma daño
                u.Stats.MinHP = (short)(u.Stats.MinHP - dano);
                ServerPackets.ConsoleMsg(u.Conn, $"¡El veneno te quita {dano} puntos de vida!", 4);
                if (u.Stats.MinHP < 1) { u.Stats.MinHP = 0; Combat.UserDie(i); continue; }
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
            }

            // --- Incineración (EfectoIncinerado, FileIO.bas:2337): Random(8,15) cada IntervaloVeneno ---
            if (u.flags.Incinerado == 1 && now - u._timerIncinera >= IntervaloIncinera)
            {
                u._timerIncinera = now;
                int dano = _rngTimer.Next(8, 16);
                u.Stats.MinHP = (short)(u.Stats.MinHP - dano);
                ServerPackets.ConsoleMsg(u.Conn, $"¡El fuego te quita {dano} puntos de vida!", 4);
                if (u.Stats.MinHP < 1) { u.Stats.MinHP = 0; Combat.UserDie(i); continue; }
                ServerPackets.UpdateHP(u.Conn, u.Stats.MinHP);
            }
        }
    }

    /// <summary>
    /// Avanza el casteo de la runa (General.bas:1823). Cada segundo decrementa; al llegar a 0
    /// teletransporta al hogar (vivo → ciudad; muerto → cementerio de la ciudad).
    /// </summary>
    private static void TickRuna(int userIndex, User u)
    {
        u.CasteandoRuna--;
        if (u.CasteandoRuna > 0)
        {
            ServerPackets.RunaCastProgress(u.Conn, u.Char.CharIndex, u.CasteandoRuna, 6);
            return;
        }

        ServerPackets.RunaCastProgress(u.Conn, u.Char.CharIndex, 0, 6);
        byte slot = u.RunaSlot;
        u.RunaSlot = 0;
        if (slot < 1 || slot > Constants.MAX_INVENTORY_SLOTS) return;

        short oi = u.Invent.Object[slot].ObjIndex;
        if (oi <= 0 || ObjData.Get(oi).Type != ObjType.Runa) return; // ya no tiene la runa

        var c = CityData.Get(u.Hogar);
        if (u.flags.Muerto == 1)
            Movement.WarpUser(userIndex, c.DeadMap != 0 ? c.DeadMap : c.Map, c.DeadX, c.DeadY);
        else
            Movement.WarpUser(userIndex, c.Map, c.X, c.Y);
    }

    /// <summary>
    /// Avance por segundo del portal (PasarSegundotelep, General.bas:2456). A los 5s crea el objeto 672
    /// con TileExit→Intermundia en (PortalX,PortalY); a los 15s lo destruye. Se cancela si el usuario
    /// cambia de mapa o muere.
    /// </summary>
    private static void TickPortal(User u)
    {
        if (u.flags.Muerto == 1 || u.Pos.Map != u.PortalMap) { CancelarPortal(u); return; }

        u.PortalTime++;

        if (u.PortalTime == 5 && !u.PortalCreado)
        {
            var map = MapLoader.Get(u.PortalMap);
            if (map == null) { u.PortalTime = 0; return; }
            int x = u.PortalX, y = u.PortalY;
            // Revalidar: el lugar pudo ocuparse durante el casteo.
            if (map.FloorObj[x, y] != 0
                || (map.Exits[x, y].HasValue && map.Exits[x, y].Value.DestMap > 0)
                || map.Blocked[x, y])
            {
                ServerPackets.ConsoleMsg(u.Conn, "El portal no se pudo formar, el lugar está ocupado.", 1);
                u.PortalTime = 0;
                return;
            }

            var inter = CityData.Get(15); // cIntermundia (destino fijo del portal)
            map.FloorObj[x, y] = Combat.PortalObjIndex;
            map.FloorAmount[x, y] = 1;
            map.Exits[x, y] = new TileExit { DestMap = inter.Map, DestX = inter.X, DestY = inter.Y };
            AreaVisibility.ObjectAppeared(u.PortalMap, x, y, Combat.PortalObjIndex, 1);
            // La partícula del teleport la maneja el CLIENTE por el objeto (obj 672, tipo 19):
            // handle_object_create borra la del cast (remove_map_particle) y crea la 34; handle_object_delete
            // (al cerrar, via ObjectRemoved en CancelarPortal) la borra. No hace falta partícula server-side.
            u.PortalCreado = true;
            ServerPackets.ConsoleMsg(u.Conn, "¡Has abierto un portal de teletransporte a Intermundia!", 1);
        }
        else if (u.PortalTime >= 15)
        {
            CancelarPortal(u); // a los 15s el portal se cierra
        }
    }

    /// <summary>Cierra el portal: borra el objeto y la salida del tile, y resetea el estado del usuario.</summary>
    public static void CancelarPortal(User u)
    {
        if (u.PortalCreado)
        {
            var map = MapLoader.Get(u.PortalMap);
            if (map != null)
            {
                int x = u.PortalX, y = u.PortalY;
                map.FloorObj[x, y] = 0; map.FloorAmount[x, y] = 0; map.Exits[x, y] = null;
                AreaVisibility.ObjectRemoved(u.PortalMap, x, y);
            }
            if (u.Conn != null) ServerPackets.ConsoleMsg(u.Conn, "El portal de teletransporte se ha cerrado.", 1);
        }
        // Quitar la partícula del cast si se cancela antes de formarse el objeto (después la maneja el objeto).
        if (u.PortalMap > 0) Combat.PortalFxQuitar(u.PortalMap, (byte)u.PortalX, (byte)u.PortalY);
        u.PortalTime = 0; u.PortalCreado = false; u.PortalMap = 0; u.PortalX = 0; u.PortalY = 0;
    }

    private const long TiempoInicioMeditar = 2000; // delay antes de regenerar (Declares.bas:239)
    private const long IntervaloMeditar = 1100;    // tick de regen al meditar

    /// <summary>Regenera maná mientras el usuario medita (tras 2s de concentración).</summary>
    private static void DoMeditar(User u, long now)
    {
        if (!u.flags.Meditando) return;
        if (u.Stats.MaxMAN <= 0 || u.Stats.MinMAN >= u.Stats.MaxMAN) return;
        if (now - u._tInicioMeditar < TiempoInicioMeditar) return; // aún concentrándose
        if (now - u._timerMeditar < IntervaloMeditar) return;
        u._timerMeditar = now;

        // Recupero por porcentaje del maná máximo (escala con skill Meditar).
        int skill = u.Stats.UserSkills[10]; // eSkill.Meditar = 10
        int pct = 4 + skill / 20;           // 4%..9% por tick
        // Anillo de la Quinta Esencia (706, AceleraMana(5)): +3% por tick al meditar.
        if (Inventory.TieneEfectoMagico(u, 5)) pct += 3;
        int cant = Math.Max(1, u.Stats.MaxMAN * pct / 100);
        u.Stats.MinMAN = (short)Math.Min(u.Stats.MaxMAN, u.Stats.MinMAN + cant);
        ServerPackets.UpdateMana(u.Conn, u.Stats.MinMAN);
        Skills.SubirSkill(u.id, 10); // SubirSkill Meditar 1:1 (Trabajo.bas:2446)
    }
}
