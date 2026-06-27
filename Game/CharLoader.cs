namespace ServidorCS.Game;

/// <summary>
/// Carga un personaje desde su charfile (.chr, formato INI CP1252).
/// Versión mínima: lee [INIT] (apariencia + posición) y [STATS] básicos.
/// Equivale a la parte de lectura de personaje de Cuentas.bas/Modulo_UsUaRiOs.bas.
/// </summary>
public static class CharLoader
{
    /// <summary>Ruta a la carpeta Charfile (junto al ejecutable o en la carpeta Servidor hermana).</summary>
    public static string CharPath = ResolveCharPath();

    private static string ResolveCharPath()
    {
        if (!string.IsNullOrEmpty(DataPaths.Root)) return DataPaths.Sub("Charfile");
        foreach (var c in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Charfile"),
            Path.Combine(Directory.GetCurrentDirectory(), "Charfile"),
        })
        {
            if (Directory.Exists(c)) return c + Path.DirectorySeparatorChar;
        }
        return "Charfile" + Path.DirectorySeparatorChar;
    }

    /// <summary>PersonajeExiste (VB6): true si existe el .chr del personaje.</summary>
    public static bool PersonajeExiste(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return File.Exists(Path.Combine(CharPath, name.ToUpperInvariant() + ".chr"));
    }

    public static bool LoadCharacter(User u, string name)
    {
        string file = Path.Combine(CharPath, name.ToUpperInvariant() + ".chr");
        var ini = new IniFile(file);
        if (!ini.Loaded) return false;

        // Resetear el estado por-personaje ANTES de cargar: el slot de UserList se reutiliza entre
        // logins de la misma conexión y los slots/punteros que el .chr no sobrescribe arrastraban los
        // del PJ anterior (ítems duplicados, cuerpo/equipo del otro personaje al morir).
        u.ResetForLogin();

        // [INIT] — apariencia y posición
        u.GuildIndex = ini.GetInt("GUILD", "GUILDINDEX");
        u.Genero = (byte)ini.GetInt("INIT", "Genero");
        u.raza   = (byte)ini.GetInt("INIT", "Raza");
        u.Hogar  = (byte)ini.GetInt("INIT", "Hogar");
        u.Clase  = (byte)ini.GetInt("INIT", "Clase");
        u.desc   = ini.Get("INIT", "Desc");
        // Casamiento (persistido entre sesiones).
        u.CasamientoCasado = (byte)ini.GetInt("CASAMIENTO", "Casado");
        u.CasamientoPareja = ini.Get("CASAMIENTO", "Pareja");

        // Facción del jugador. VB6: Cuentas.bas:203 lee [FACCIONES] del .chr (tFacciones).
        // Nombres exactos de las claves: TCP.bas:2199-2206 (carga del .chr en VB6).
        u.Faccion.Status              = (byte)ini.GetInt("FACCIONES", "Status");
        u.Faccion.CiudadanosMatados   = ini.GetInt("FACCIONES", "CiudMatados");
        u.Faccion.RenegadosMatados    = ini.GetInt("FACCIONES", "ReneMatados");
        u.Faccion.RepublicanosMatados = ini.GetInt("FACCIONES", "RepuMatados");
        u.Faccion.MilicianosMatados   = ini.GetInt("FACCIONES", "MiliMatados");
        u.Faccion.ArmadaMatados       = ini.GetInt("FACCIONES", "ArmiMatados");
        u.Faccion.CaosMatados         = ini.GetInt("FACCIONES", "CaosMatados");
        u.Faccion.Rango               = ini.GetInt("FACCIONES", "RANGO");

        u.Char.heading    = (byte)ini.GetInt("INIT", "Heading");
        u.Char.Head       = (short)ini.GetInt("INIT", "Head");
        u.Char.body       = (short)ini.GetInt("INIT", "Body");
        u.Char.WeaponAnim = (short)ini.GetInt("INIT", "Arma");
        u.Char.ShieldAnim = (short)ini.GetInt("INIT", "Escudo");
        u.Char.CascoAnim  = (short)ini.GetInt("INIT", "Casco");
        // Auras del equipo (persistidas en INIT; se envían en el CharacterCreate del login).
        u.Char.Arma_Aura   = (byte)ini.GetInt("INIT", "ArmaAura");
        u.Char.Body_Aura   = (byte)ini.GetInt("INIT", "BodyAura");
        u.Char.Escudo_Aura = (byte)ini.GetInt("INIT", "EscudoAura");
        u.Char.Head_Aura   = (byte)ini.GetInt("INIT", "HeadAura");
        u.Char.Anillo_Aura = (byte)ini.GetInt("INIT", "AnilloAura");

        // VB6 OrigChar: apariencia original completa (TCP.bas:2270-2277). Se usa para restaurar al
        // resucitar / desmontar sin recargar inventario. Antes sólo se copiaban Head/body → al
        // resucitar se perdían los anims de arma/escudo/casco originales.
        u.OrigChar.Head       = u.Char.Head;
        u.OrigChar.body       = u.Char.body;
        u.OrigChar.WeaponAnim = u.Char.WeaponAnim;
        u.OrigChar.ShieldAnim = u.Char.ShieldAnim;
        u.OrigChar.CascoAnim  = u.Char.CascoAnim;
        u.OrigChar.heading    = u.Char.heading;

        // [FLAGS]/[COUNTERS] de estado persistente (TCP.bas:2127-2189). Antes NO se cargaban: el
        // servidor olvidaba que el PJ estaba muerto (revivía al reloguear), envenenado, incinerado,
        // hambriento/sediento, o con condena de cárcel. Paralizado/Inmovilizado/Oculto se limpian
        // SIEMPRE al loguear (VB6 los fuerza a 0), así que NO se restauran.
        u.flags.Muerto      = (byte)ini.GetInt("FLAGS", "Muerto");
        u.flags.Navegando   = ini.GetInt("FLAGS", "Navegando") == 1;
        u.flags.Montando    = (byte)ini.GetInt("FLAGS", "Montando");
        u.flags.Envenenado  = (byte)ini.GetInt("FLAGS", "Envenenado");
        u.flags.Incinerado  = (byte)ini.GetInt("FLAGS", "Incinerado");
        u.flags.Hambre      = (byte)ini.GetInt("FLAGS", "Hambre");
        u.flags.Sed         = (byte)ini.GetInt("FLAGS", "Sed");
        u.flags.RecibioCorreo = (byte)ini.GetInt("FLAGS", "Recibiocorreo");
        // Scrolls de EXP/Oro: re-armar el vencimiento con los segundos restantes guardados.
        int scrollExpMult = ini.GetInt("FLAGS", "ScrollExpMult");
        int scrollExpSeg  = ini.GetInt("FLAGS", "ScrollExpSeg");
        if (scrollExpMult > 1 && scrollExpSeg > 0)
        {
            u.flags.ScrollExpMult = scrollExpMult;
            u.flags.ScrollExpExpira = Environment.TickCount64 / 1000.0 + scrollExpSeg;
        }
        int scrollOroMult = ini.GetInt("FLAGS", "ScrollOroMult");
        int scrollOroSeg  = ini.GetInt("FLAGS", "ScrollOroSeg");
        if (scrollOroMult > 1 && scrollOroSeg > 0)
        {
            u.flags.ScrollOroMult = scrollOroMult;
            u.flags.ScrollOroExpira = Environment.TickCount64 / 1000.0 + scrollOroSeg;
        }
        // Donador / tag personal / vale de cambio de nombre / última mascota (pociones custom).
        // En los .chr viejos no existen: 0 y "" son los valores de siempre.
        u.Char.Donador      = (byte)ini.GetInt("FLAGS", "Donador");
        u.TagPersonal       = ini.Get("FLAGS", "Tag") ?? "";
        u.PuedeRenombrar    = ini.GetInt("FLAGS", "PuedeRenombrar") == 1;
        u.UltimaMascotaNpc  = ini.GetInt("FLAGS", "UltimaMascota");
        u.PetTipo           = (byte)ini.GetInt("MASCOTA", "Tipo");
        u.PetNivel          = (byte)Math.Max(1, ini.GetInt("MASCOTA", "Nivel"));
        u.PetExp            = ini.GetInt("MASCOTA", "Exp");
        u.PetNombre         = ini.Get("MASCOTA", "Nombre") ?? "";
        u.PetDead           = ini.GetInt("MASCOTA", "Muerta") == 1;
        int descanso        = ini.GetInt("MASCOTA", "DescansoHogar");
        u.PetHogarHasta     = descanso > 0 ? Environment.TickCount64 / 1000.0 + descanso : 0;
        // Mochila de la mascota (personajes viejos: la sección no existe y quedan los slots en 0).
        for (int i = 1; i <= Constants.MAX_PETINVENTORY_SLOTS; i++)
        {
            var partes = (ini.Get("MASCOTA_INV", "Obj" + i) ?? "0-0").Split('-');
            short obj = partes.Length > 0 && short.TryParse(partes[0], out var o) ? o : (short)0;
            int cant = partes.Length > 1 && int.TryParse(partes[1], out var c) ? c : 0;
            if (obj <= 0 || cant <= 0) { obj = 0; cant = 0; }
            u.PetInvent.Object[i].ObjIndex = obj;
            u.PetInvent.Object[i].Amount = cant;
            if (obj > 0) u.PetInvent.NroItems++;
        }
        u.flags.Pena        = ini.GetInt("COUNTERS", "Pena");
        u.UpTime            = ini.GetInt("INIT", "UpTime");

        // Position=Map-X-Y
        var pos = ini.Get("INIT", "Position").Split('-');
        if (pos.Length == 3)
        {
            u.Pos.Map = short.TryParse(pos[0], out var m) ? m : (short)1;
            u.Pos.X   = short.TryParse(pos[1], out var x) ? x : (short)50;
            u.Pos.Y   = short.TryParse(pos[2], out var y) ? y : (short)50;
        }

        // [STATS] básicos (nombres exactos del charfile)
        u.Stats.MaxHP = (short)ini.GetInt("STATS", "MaxHP");
        u.Stats.MinHP = (short)ini.GetInt("STATS", "MinHP");
        u.Stats.MaxMAN = (short)ini.GetInt("STATS", "MaxMAN");
        u.Stats.MinMAN = (short)ini.GetInt("STATS", "MinMAN");
        u.Stats.MaxSta = (short)ini.GetInt("STATS", "MaxSTA");
        u.Stats.MinSta = (short)ini.GetInt("STATS", "MinSTA");
        u.Stats.MaxHIT = (short)ini.GetInt("STATS", "MaxHIT");
        u.Stats.MinHIT = (short)ini.GetInt("STATS", "MinHIT");
        u.Stats.MaxHam = (short)ini.GetInt("STATS", "MaxHAM");
        u.Stats.MinHam = (short)ini.GetInt("STATS", "MinHAM");
        u.Stats.MaxAGU = (short)ini.GetInt("STATS", "MaxAGU");
        u.Stats.MinAGU = (short)ini.GetInt("STATS", "MinAGU");
        u.Stats.ELV = (byte)ini.GetInt("STATS", "ELV");
        u.Stats.ELU = ini.GetInt("STATS", "ELU");
        u.Stats.GLD = ini.GetInt("STATS", "GLD");
        u.Stats.Banco = ini.GetInt("STATS", "BANCO");
        u.BovedaPremiumDesbloqueada = ini.GetInt("STATS", "BOVEDA_PREMIUM") == 1;
        u.Stats.Exp = ini.GetInt("STATS", "EXP");
        u.Stats.SkillPts = (short)ini.GetInt("STATS", "SkillPtsLibres");
        u.Stats.ArenaPoints = ini.GetInt("STATS", "ArenaPoints");
        // Pociones mágicas de vida (0 en los .chr viejos: nunca tomaron ninguna).
        u.Stats.PocionesVida = ini.GetInt("STATS", "PocionesVida");
        u.Stats.BonusVidaPociones = ini.GetInt("STATS", "BonusVidaPociones");
        // [MUERTES] — frags persistidos (los usa MiniStats); antes quedaban en 0 al reloguear.
        u.Stats.UsuariosMatados = (short)ini.GetInt("MUERTES", "UserMuertes");
        u.Stats.NPCsMuertos     = (short)ini.GetInt("MUERTES", "NpcsMuertes");

        // [ATRIBUTOS] AT1..AT5 (Fuerza=1, Agilidad=2, Inteligencia=3, Carisma=4, Constitucion=5)
        // El BackUP guarda los valores BASE (sin buffs); se restaura al expirar un buff/debuff o al morir.
        for (int a = 1; a <= Constants.NUMATRIBUTOS; a++)
        {
            u.Stats.UserAtributos[a] = (byte)ini.GetInt("ATRIBUTOS", "AT" + a);
            u.Stats.UserAtributosBackUP[a] = u.Stats.UserAtributos[a];
        }

        // [SKILLS] SK1..SK27 (puntos), ELUSK/EXPSK (progreso de cada skill).
        for (int s = 1; s <= Constants.NUMSKILLS; s++)
        {
            u.Stats.UserSkills[s] = (byte)ini.GetInt("SKILLS", "SK" + s);
            u.Stats.EluSkills[s] = ini.GetInt("SKILLS", "ELUSK" + s);
            u.Stats.ExpSkills[s] = ini.GetInt("SKILLS", "EXPSK" + s);
        }

        // [HECHIZOS] H1..HN — índice del hechizo en cada slot (0 = vacío)
        for (int h = 1; h <= Constants.MAXUSERHECHIZOS; h++)
            u.Stats.UserHechizos[h] = (short)ini.GetInt("HECHIZOS", "H" + h);

        // [AMIGOS] NOMBRE1..5 ("Vacio"/"Vacío" = libre). index se cachea online (no se persiste).
        for (int a = 1; a <= Constants.MAXAMIGOS; a++)
        {
            string nom = ini.Get("AMIGOS", "NOMBRE" + a);
            u.Amigos[a].Nombre = (string.IsNullOrEmpty(nom) || nom.StartsWith("Vac")) ? "Vacio" : nom;
            u.Amigos[a].index = 0;
        }
        // [FLAGS] CantidadAmigos / CheckAmigos / Murio (muertes a manos de otros jugadores)
        u.flags.CantidadAmigos = (byte)ini.GetInt("FLAGS", "CantidadAmigos");
        u.flags.CheckAmigos = u.flags.CantidadAmigos > 0 ? (byte)1 : (byte)0;
        u.flags.MuertesUsuario = ini.GetInt("FLAGS", "Murio");

        // [CORREO] CartaN/EmisorN/LeidaN/ObjetoN (objindex-cantidad)
        u.Correos.Clear();
        for (int c = 1; c <= Constants.MAX_CORREOS_SLOTS; c++)
        {
            string emisor = ini.Get("CORREO", "Emisor" + c);
            if (string.IsNullOrEmpty(emisor) || emisor == "0") continue;
            var obj = ini.Get("CORREO", "Objeto" + c).Split('-');
            u.Correos.Add(new Correo
            {
                Emisor = emisor,
                Mensaje = ini.Get("CORREO", "Carta" + c),
                Leida = ini.GetInt("CORREO", "Leida" + c) == 1,
                ObjIndex = obj.Length >= 2 && short.TryParse(obj[0], out var oi) ? oi : (short)0,
                Cantidad = obj.Length >= 2 && int.TryParse(obj[1], out var ca) ? ca : 0,
            });
        }

        // [BancoInventory] ObjN = objindex-amount (80 slots)
        u.BancoInvent.NroItems = (short)ini.GetInt("BancoInventory", "CantidadItems");
        for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
        {
            var parts = ini.Get("BancoInventory", "Obj" + slot).Split('-');
            if (parts.Length >= 2)
            {
                u.BancoInvent.Object[slot].ObjIndex = short.TryParse(parts[0], out var oi) ? oi : (short)0;
                u.BancoInvent.Object[slot].Amount = int.TryParse(parts[1], out var am) ? am : 0;
            }
        }

        // [BancoInventoryPremium1] / [BancoInventoryPremium2] (NUEVO, no VB6) — mismo formato.
        u.BancoPremium1.NroItems = (short)ini.GetInt("BancoInventoryPremium1", "CantidadItems");
        for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
        {
            var parts = ini.Get("BancoInventoryPremium1", "Obj" + slot).Split('-');
            if (parts.Length >= 2)
            {
                u.BancoPremium1.Object[slot].ObjIndex = short.TryParse(parts[0], out var oi) ? oi : (short)0;
                u.BancoPremium1.Object[slot].Amount = int.TryParse(parts[1], out var am) ? am : 0;
            }
        }
        u.BancoPremium2.NroItems = (short)ini.GetInt("BancoInventoryPremium2", "CantidadItems");
        for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
        {
            var parts = ini.Get("BancoInventoryPremium2", "Obj" + slot).Split('-');
            if (parts.Length >= 2)
            {
                u.BancoPremium2.Object[slot].ObjIndex = short.TryParse(parts[0], out var oi) ? oi : (short)0;
                u.BancoPremium2.Object[slot].Amount = int.TryParse(parts[1], out var am) ? am : 0;
            }
        }

        // [Inventory] — formato ObjN = ObjIndex-Amount-Equipped (slots 1..MAX_INVENTORY_SLOTS)
        u.Invent.NroItems = (short)ini.GetInt("Inventory", "CantidadItems");
        for (int slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            var parts = ini.Get("Inventory", "Obj" + slot).Split('-');
            if (parts.Length == 3)
            {
                u.Invent.Object[slot].ObjIndex = short.TryParse(parts[0], out var oi) ? oi : (short)0;
                u.Invent.Object[slot].Amount   = int.TryParse(parts[1], out var am) ? am : 0;
                u.Invent.Object[slot].Equipped = parts[2] == "1";
            }
        }

        // Reconstruir los punteros de equipo desde los slots guardados (TCP.bas:2311-2414).
        // Sin esto, al loguear con ítems equipados los *EqpObjIndex quedaban en 0: la armadura no
        // defendía, el arma no sumaba daño, y al desmontar el body quedaba desnudo (aunque el slot
        // figuraba equipado). También restaura las auras y el flag Desnudo.
        bool vivo = u.flags.Muerto == 0;
        var inv = u.Invent;

        inv.NudiEqpSlot = (byte)ini.GetInt("Inventory", "NudiEqpSlot");
        if (inv.NudiEqpSlot > 0)
        {
            inv.NudiEqpObjIndex = inv.Object[inv.NudiEqpSlot].ObjIndex;
            if (vivo) u.Char.Arma_Aura = (byte)ObjData.Get(inv.NudiEqpObjIndex).Aura;
        }

        inv.WeaponEqpSlot = (byte)ini.GetInt("Inventory", "WeaponEqpSlot");
        if (inv.WeaponEqpSlot > 0)
        {
            inv.WeaponEqpObjIndex = inv.Object[inv.WeaponEqpSlot].ObjIndex;
            if (vivo) u.Char.Arma_Aura = (byte)ObjData.Get(inv.WeaponEqpObjIndex).Aura;
        }

        inv.EscudoEqpSlot = (byte)ini.GetInt("Inventory", "EscudoEqpSlot");
        if (inv.EscudoEqpSlot > 0)
        {
            inv.EscudoEqpObjIndex = inv.Object[inv.EscudoEqpSlot].ObjIndex;
            if (vivo) u.Char.Escudo_Aura = (byte)ObjData.Get(inv.EscudoEqpObjIndex).Aura;
        }

        inv.MagicSlot = (byte)ini.GetInt("Inventory", "MagicSlot");
        if (inv.MagicSlot > 0)
        {
            inv.MagicIndex = inv.Object[inv.MagicSlot].ObjIndex;
            if (vivo) u.Char.Anillo_Aura = (byte)ObjData.Get(inv.MagicIndex).Aura;
        }

        inv.CascoEqpSlot = (byte)ini.GetInt("Inventory", "CascoEqpSlot");
        if (inv.CascoEqpSlot > 0)
        {
            inv.CascoEqpObjIndex = inv.Object[inv.CascoEqpSlot].ObjIndex;
            if (vivo) u.Char.Head_Aura = (byte)ObjData.Get(inv.CascoEqpObjIndex).Aura;
        }

        inv.BarcoSlot = (byte)ini.GetInt("Inventory", "BarcoSlot");
        if (inv.BarcoSlot > 0) inv.BarcoObjIndex = inv.Object[inv.BarcoSlot].ObjIndex;

        inv.MunicionEqpSlot = (byte)ini.GetInt("Inventory", "MunicionSlot");
        if (inv.MunicionEqpSlot > 0) inv.MunicionEqpObjIndex = inv.Object[inv.MunicionEqpSlot].ObjIndex;

        inv.AnilloEqpSlot = (byte)ini.GetInt("Inventory", "AnilloSlot");
        if (inv.AnilloEqpSlot > 0) inv.AnilloEqpObjIndex = inv.Object[inv.AnilloEqpSlot].ObjIndex;

        inv.ArmourEqpSlot = (byte)ini.GetInt("Inventory", "ArmourEqpSlot");
        if (inv.ArmourEqpSlot > 0)
        {
            inv.ArmourEqpObjIndex = inv.Object[inv.ArmourEqpSlot].ObjIndex;
            u.flags.Desnudo = 0;
            if (vivo) u.Char.Body_Aura = (byte)ObjData.Get(inv.ArmourEqpObjIndex).Aura;
        }
        else
        {
            u.flags.Desnudo = 1;
        }

        inv.MonturaSlot = (byte)ini.GetInt("Inventory", "MonturaSlot");
        if (inv.MonturaSlot > 0) inv.MonturaObjIndex = inv.Object[inv.MonturaSlot].ObjIndex;

        // Fallback de compatibilidad: los .chr guardados ANTES de este fix no tienen las claves
        // *EqpSlot, así que los punteros quedarían en 0 pese a tener ítems con Equipped=1. Reconstruimos
        // por tipo de objeto desde el flag Equipped, sin pisar lo que ya quedó seteado por las claves.
        for (byte s = 1; s <= Constants.MAX_INVENTORY_SLOTS; s++)
        {
            ref var it = ref inv.Object[s];
            if (it.ObjIndex == 0 || !it.Equipped) continue;
            var od = ObjData.Get(it.ObjIndex);
            switch (od.Type)
            {
                case ObjType.Weapon:
                    if (inv.WeaponEqpSlot == 0) { inv.WeaponEqpSlot = s; inv.WeaponEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Arma_Aura = (byte)od.Aura; }
                    break;
                case ObjType.Nudillos:
                    if (inv.NudiEqpSlot == 0) { inv.NudiEqpSlot = s; inv.NudiEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Arma_Aura = (byte)od.Aura; }
                    break;
                case ObjType.Armadura:
                    if (od.SubTipo == 1) { if (inv.CascoEqpSlot == 0) { inv.CascoEqpSlot = s; inv.CascoEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Head_Aura = (byte)od.Aura; } }
                    else if (od.SubTipo == 2) { if (inv.EscudoEqpSlot == 0) { inv.EscudoEqpSlot = s; inv.EscudoEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Escudo_Aura = (byte)od.Aura; } }
                    else { if (inv.ArmourEqpSlot == 0) { inv.ArmourEqpSlot = s; inv.ArmourEqpObjIndex = it.ObjIndex; u.flags.Desnudo = 0; if (vivo) u.Char.Body_Aura = (byte)od.Aura; } }
                    break;
                case ObjType.Escudo:
                    if (inv.EscudoEqpSlot == 0) { inv.EscudoEqpSlot = s; inv.EscudoEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Escudo_Aura = (byte)od.Aura; }
                    break;
                case ObjType.Casco:
                    if (inv.CascoEqpSlot == 0) { inv.CascoEqpSlot = s; inv.CascoEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Head_Aura = (byte)od.Aura; }
                    break;
                case ObjType.Anillo:
                case ObjType.Anillos:
                case ObjType.ItemsMagicos:
                    if (inv.AnilloEqpSlot == 0) { inv.AnilloEqpSlot = s; inv.AnilloEqpObjIndex = it.ObjIndex; if (vivo) u.Char.Anillo_Aura = (byte)od.Aura; }
                    break;
                case ObjType.Flechas:
                    if (inv.MunicionEqpSlot == 0) { inv.MunicionEqpSlot = s; inv.MunicionEqpObjIndex = it.ObjIndex; }
                    break;
                // El "else it.Equipped = false" repara personajes viejos: hasta el 1-ago-2026
                // DoEquita/DoNavega eran un toggle ciego y al cambiar de montura dejaban la
                // anterior marcada como equipada, cosa que se guardaba en el .chr. Acá solo
                // sobrevive la primera (la que realmente se usa); las repetidas se apagan y
                // quedan limpias en el próximo guardado.
                // OJO: MonturaSlot ya viene leído del .chr más arriba, así que el slot bueno
                // puede llegar acá con el puntero YA seteado — por eso se compara contra s en
                // vez de usar un else pelado, que le desequiparía la montura al que la tiene bien.
                case ObjType.Monturas:
                    if (inv.MonturaSlot == 0) { inv.MonturaSlot = s; inv.MonturaObjIndex = it.ObjIndex; }
                    else if (s != inv.MonturaSlot) it.Equipped = false;
                    break;
                case ObjType.Barcos:
                    if (inv.BarcoSlot == 0) { inv.BarcoSlot = s; inv.BarcoObjIndex = it.ObjIndex; }
                    else if (s != inv.BarcoSlot) it.Equipped = false;
                    break;
            }
        }

        // Saneamiento de posición al loguear (TCP.bas:170-242,283): mapa inválido→Intermundia,
        // clamp de bordes y anti-telefrag. Antes del bloque de navegación para usar la pos final.
        Movement.SanearPosicionLogin(u);

        // Reconstrucción de navegación al loguear (TCP.bas:250) 1:1: navega SOLO si tiene barco
        // equipado y está sobre agua (o su body persistido ya es de barca). Si no, se baja — así no
        // queda trabado en tierra con el flag pegado (mover navegando exige agua). El flag persistido
        // no se respeta tal cual: se recalcula desde la posición/barco, igual que el VB6.
        {
            var mp = MapLoader.Get(u.Pos.Map);
            bool sobreAgua = mp != null && mp.HasWater(u.Pos.X, u.Pos.Y);
            if (inv.BarcoObjIndex > 0 && (sobreAgua || BodyIsBoat(u.Char.body)))
                u.flags.Navegando = true;
            else
            {
                u.flags.Navegando = false;
                inv.BarcoObjIndex = 0; inv.BarcoSlot = 0;
            }
        }

        // Apariencia final según estado (TCP.bas:2279-2287 + login navegando/montando).
        if (u.flags.Muerto == 1)
        {
            // Fantasma: navegando → barca fantasma (87) con cabeza 0; a pie → cuerpo 8, cabeza muerto
            // (500). Igual que Combat.UserDie / login con Muerto (TCP.bas:260).
            u.Char.body = u.flags.Navegando ? (short)87 : (short)8;
            u.Char.Head = u.flags.Navegando ? (short)0 : (short)500;
            u.Char.WeaponAnim = 0;
            u.Char.ShieldAnim = 0;
            u.Char.CascoAnim = 0;
        }
        else if (u.flags.Navegando && inv.BarcoObjIndex > 0)
        {
            // Embarcado: body del barco (Ropaje), sin cabeza/anims (DoNavega).
            var ob = ObjData.Get(inv.BarcoObjIndex);
            u.Char.body = (short)(ob.Ropaje > 0 ? ob.Ropaje : 87);
            u.Char.Head = 0; u.Char.WeaponAnim = 0; u.Char.ShieldAnim = 0; u.Char.CascoAnim = 0;
        }
        else if (u.flags.Montando != 0 && inv.MonturaObjIndex > 0)
        {
            // Montado: body de la montura (Ropaje), sin arma a la vista (DoEquita).
            u.Char.body = (short)ObjData.Get(inv.MonturaObjIndex).Ropaje;
            u.Char.WeaponAnim = 0;
        }

        return true;
    }

    /// <summary>BodyIsBoat (Modulo_UsUaRiOs.bas:3072) 1:1: true si el body es de una barca/galera/
    /// galeón/fragata fantasma/barca imperial/republicana (iGraficos 84/85/86/87/295/296).</summary>
    private static bool BodyIsBoat(int body)
        => body is 84 or 85 or 86 or 87 or 295 or 296;
}
