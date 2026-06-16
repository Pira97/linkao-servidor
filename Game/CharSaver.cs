namespace ServidorCS.Game;

/// <summary>
/// Persistencia del personaje. Equivale a SaveUser (FileIO.bas): reescribe el .chr
/// actualizando SOLO las claves que el servidor modela (INIT, STATS, ATRIBUTOS,
/// HECHIZOS, Inventory), preservando el resto de secciones (FLAGS, FACCIONES, GUILD,
/// CORREO, DONADOR...) que aún no portamos, para no perder datos.
///
/// Se llama al desconectar (logout) y se podría llamar periódicamente (autosave).
/// </summary>
public static class CharSaver
{
    /// <summary>Guarda todos los usuarios logueados (autosave periódico).</summary>
    public static void SaveAllOnline()
    {
        int n = 0;
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && !string.IsNullOrEmpty(u.Name)) { SaveUser(u); n++; }
        }
        if (n > 0) Console.WriteLine($"[ServidorCS] Autosave: {n} personaje(s) guardado(s).");
    }

    public static void SaveUser(User u)
    {
        if (string.IsNullOrEmpty(u.Name)) return;
        string file = System.IO.Path.Combine(CharLoader.CharPath, u.Name.ToUpperInvariant() + ".chr");
        var doc = new IniDocument(file);
        if (!doc.Loaded) return; // sin charfile base no escribimos (evita crear uno corrupto)

        // [INIT] — apariencia y posición. OJO: si está muerto, NO guardar body/head de
        // fantasma; guardar los originales del charfile (no los pisamos al morir aquí).
        if (u.flags.Muerto == 0)
        {
            // Si está montado/navegando/metamorfoseado (puede pasar en el autosave, sin pasar por
            // CloseUser que restaura la apariencia), guardamos el cuerpo A PIE en [INIT], NO el del
            // caballo/barca/morph. Así OrigChar y la apariencia base quedan correctas al recargar.
            var (body, head, weap, shield, casco) = AparienciaAPie(u);
            doc.Set("INIT", "Body", body.ToString());
            doc.Set("INIT", "Head", head.ToString());
            doc.Set("INIT", "Arma", weap.ToString());
            doc.Set("INIT", "Escudo", shield.ToString());
            doc.Set("INIT", "Casco", casco.ToString());
            // Auras del equipo (se ven al loguear; AuraToChar en CharacterCreate las envía).
            doc.Set("INIT", "ArmaAura", u.Char.Arma_Aura.ToString());
            doc.Set("INIT", "BodyAura", u.Char.Body_Aura.ToString());
            doc.Set("INIT", "EscudoAura", u.Char.Escudo_Aura.ToString());
            doc.Set("INIT", "HeadAura", u.Char.Head_Aura.ToString());
            doc.Set("INIT", "AnilloAura", u.Char.Anillo_Aura.ToString());
        }
        doc.Set("INIT", "Heading", u.Char.heading.ToString());
        doc.Set("INIT", "Position", $"{u.Pos.Map}-{u.Pos.X}-{u.Pos.Y}");
        doc.Set("INIT", "UpTime", u.UpTime.ToString());
        // Hogar: cambia al salir del Dungeon Newbie (nivel 15) o al cambiar de facción.
        doc.Set("INIT", "Hogar", u.Hogar.ToString());

        // [FLAGS] de estado persistente (TCP.bas:2156-2189). Antes NO se guardaban → al reloguear
        // el servidor perdía Muerto (un muerto revivía), veneno, incinerado, hambre/sed, etc.
        doc.Set("FLAGS", "Muerto", u.flags.Muerto.ToString());
        doc.Set("FLAGS", "Navegando", (u.flags.Navegando ? 1 : 0).ToString());
        doc.Set("FLAGS", "Montando", u.flags.Montando.ToString());
        doc.Set("FLAGS", "Envenenado", u.flags.Envenenado.ToString());
        doc.Set("FLAGS", "Incinerado", u.flags.Incinerado.ToString());
        doc.Set("FLAGS", "Hambre", u.flags.Hambre.ToString());
        doc.Set("FLAGS", "Sed", u.flags.Sed.ToString());
        doc.Set("FLAGS", "Desnudo", u.flags.Desnudo.ToString());
        doc.Set("FLAGS", "Recibiocorreo", u.flags.RecibioCorreo.ToString());
        // [COUNTERS] — condena de cárcel (Pena). Se preserva entre sesiones.
        doc.Set("COUNTERS", "Pena", u.flags.Pena.ToString());

        // [STATS]
        doc.Set("STATS", "MaxHP", u.Stats.MaxHP.ToString());
        doc.Set("STATS", "MinHP", u.Stats.MinHP.ToString());
        doc.Set("STATS", "MaxMAN", u.Stats.MaxMAN.ToString());
        doc.Set("STATS", "MinMAN", u.Stats.MinMAN.ToString());
        doc.Set("STATS", "MaxSTA", u.Stats.MaxSta.ToString());
        doc.Set("STATS", "MinSTA", u.Stats.MinSta.ToString());
        doc.Set("STATS", "MaxHIT", u.Stats.MaxHIT.ToString());
        doc.Set("STATS", "MinHIT", u.Stats.MinHIT.ToString());
        // Hambre y sed (FileIO.bas:2061-2064): antes NO se guardaban → se reseteaban al valor del .chr
        // en cada save (no persistían comer/beber ni el desgaste por tiempo).
        doc.Set("STATS", "MaxHAM", u.Stats.MaxHam.ToString());
        doc.Set("STATS", "MinHAM", u.Stats.MinHam.ToString());
        doc.Set("STATS", "MaxAGU", u.Stats.MaxAGU.ToString());
        doc.Set("STATS", "MinAGU", u.Stats.MinAGU.ToString());
        doc.Set("STATS", "ELV", u.Stats.ELV.ToString());
        doc.Set("STATS", "ELU", u.Stats.ELU.ToString());
        doc.Set("STATS", "EXP", ((long)u.Stats.Exp).ToString());
        doc.Set("STATS", "GLD", u.Stats.GLD.ToString());
        doc.Set("STATS", "BANCO", u.Stats.Banco.ToString());
        doc.Set("STATS", "SkillPtsLibres", u.Stats.SkillPts.ToString());
        doc.Set("STATS", "ArenaPoints", u.Stats.ArenaPoints.ToString());

        // [MUERTES] — frags de usuarios y NPCs (FileIO.bas:2072-2075). Los usa MiniStats.
        doc.Set("MUERTES", "UserMuertes", u.Stats.UsuariosMatados.ToString());
        doc.Set("MUERTES", "NpcsMuertes", u.Stats.NPCsMuertos.ToString());

        // [ATRIBUTOS]
        for (int a = 1; a <= Constants.NUMATRIBUTOS; a++)
            doc.Set("ATRIBUTOS", "AT" + a, u.Stats.UserAtributos[a].ToString());

        // [SKILLS] SK1..SK27 + ELUSK/EXPSK (persistir las subidas de skill en juego).
        for (int s = 1; s <= Constants.NUMSKILLS; s++)
        {
            doc.Set("SKILLS", "SK" + s, u.Stats.UserSkills[s].ToString());
            doc.Set("SKILLS", "ELUSK" + s, u.Stats.EluSkills[s].ToString());
            doc.Set("SKILLS", "EXPSK" + s, u.Stats.ExpSkills[s].ToString());
        }

        // [HECHIZOS]
        for (int h = 1; h <= Constants.MAXUSERHECHIZOS; h++)
            doc.Set("HECHIZOS", "H" + h, u.Stats.UserHechizos[h].ToString());

        // [Inventory] — ObjN = objindex-amount-equipped
        doc.Set("Inventory", "CantidadItems", u.Invent.NroItems.ToString());
        for (int slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
        {
            var o = u.Invent.Object[slot];
            doc.Set("Inventory", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}-{(o.Equipped ? 1 : 0)}");
        }
        // Punteros de equipo (TCP.bas:2314-2414): se guarda el SLOT equipado de cada ranura, para
        // poder reconstruir los *EqpObjIndex al cargar. Sin esto, al loguear con cosas equipadas el
        // servidor no sabía qué arma/armadura/etc. estaba puesta (armadura no defendía, body desnudo al desmontar).
        doc.Set("Inventory", "NudiEqpSlot",   u.Invent.NudiEqpSlot.ToString());
        doc.Set("Inventory", "WeaponEqpSlot", u.Invent.WeaponEqpSlot.ToString());
        doc.Set("Inventory", "EscudoEqpSlot", u.Invent.EscudoEqpSlot.ToString());
        doc.Set("Inventory", "MagicSlot",     u.Invent.MagicSlot.ToString());
        doc.Set("Inventory", "CascoEqpSlot",  u.Invent.CascoEqpSlot.ToString());
        doc.Set("Inventory", "BarcoSlot",     u.Invent.BarcoSlot.ToString());
        doc.Set("Inventory", "MunicionSlot",  u.Invent.MunicionEqpSlot.ToString());
        doc.Set("Inventory", "AnilloSlot",    u.Invent.AnilloEqpSlot.ToString());
        doc.Set("Inventory", "ArmourEqpSlot", u.Invent.ArmourEqpSlot.ToString());
        doc.Set("Inventory", "MonturaSlot",   u.Invent.MonturaSlot.ToString());

        // [GUILD] — clan al que pertenece. Lo modifica GuildManager (fundar/unirse/salir/expulsar);
        // antes NO se guardaba → al reloguear el GuildIndex volvía al valor viejo del .chr.
        doc.Set("GUILD", "GUILDINDEX", u.GuildIndex.ToString());

        // [CASAMIENTO] — estado civil del personaje.
        doc.Set("CASAMIENTO", "Casado", u.CasamientoCasado.ToString());
        doc.Set("CASAMIENTO", "Pareja", u.CasamientoPareja ?? "");

        // [FACCIONES] — facción y frags por facción (claves exactas: TCP.bas:2199-2206).
        doc.Set("FACCIONES", "Status", u.Faccion.Status.ToString());
        doc.Set("FACCIONES", "CiudMatados", u.Faccion.CiudadanosMatados.ToString());
        doc.Set("FACCIONES", "ReneMatados", u.Faccion.RenegadosMatados.ToString());
        doc.Set("FACCIONES", "RepuMatados", u.Faccion.RepublicanosMatados.ToString());
        doc.Set("FACCIONES", "MiliMatados", u.Faccion.MilicianosMatados.ToString());
        doc.Set("FACCIONES", "ArmiMatados", u.Faccion.ArmadaMatados.ToString());
        doc.Set("FACCIONES", "CaosMatados", u.Faccion.CaosMatados.ToString());
        doc.Set("FACCIONES", "RANGO", u.Faccion.Rango.ToString());

        // [AMIGOS] — NOMBRE1..5 + CantidadAmigos en [FLAGS]
        for (int a = 1; a <= Constants.MAXAMIGOS; a++)
            doc.Set("AMIGOS", "NOMBRE" + a, string.IsNullOrEmpty(u.Amigos[a].Nombre) ? "Vacio" : u.Amigos[a].Nombre);
        doc.Set("FLAGS", "CantidadAmigos", u.flags.CantidadAmigos.ToString());
        doc.Set("FLAGS", "Murio", u.flags.MuertesUsuario.ToString());

        // [CORREO] — CartaN/EmisorN/LeidaN/ObjetoN
        for (int c = 1; c <= Constants.MAX_CORREOS_SLOTS; c++)
        {
            if (c <= u.Correos.Count)
            {
                var co = u.Correos[c - 1];
                doc.Set("CORREO", "Carta" + c, co.Mensaje);
                doc.Set("CORREO", "Emisor" + c, co.Emisor);
                doc.Set("CORREO", "Leida" + c, co.Leida ? "1" : "0");
                doc.Set("CORREO", "Objeto" + c, $"{co.ObjIndex}-{co.Cantidad}");
            }
            else
            {
                doc.Set("CORREO", "Carta" + c, "0");
                doc.Set("CORREO", "Emisor" + c, "0");
                doc.Set("CORREO", "Leida" + c, "0");
                doc.Set("CORREO", "Objeto" + c, "0-0");
            }
        }

        // [BancoInventory] — ObjN = objindex-amount
        doc.Set("BancoInventory", "CantidadItems", u.BancoInvent.NroItems.ToString());
        for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
        {
            var o = u.BancoInvent.Object[slot];
            doc.Set("BancoInventory", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}");
        }

        try { doc.Save(file); }
        catch (Exception ex) { Console.WriteLine($"[ServidorCS] Error guardando {u.Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Calcula la apariencia "a pie" (body + anims) según el equipo equipado, SIN mutar el runtime.
    /// Se usa para no persistir en [INIT] el cuerpo del caballo/barca/morph durante el autosave.
    /// </summary>
    private static (short body, short head, short weap, short shield, short casco) AparienciaAPie(User u)
    {
        bool transformado = u.flags.Montando != 0 || u.flags.Navegando || u.flags.Metamorfoseado == 1;
        if (!transformado)
            return (u.Char.body, u.Char.Head, u.Char.WeaponAnim, u.Char.ShieldAnim, u.Char.CascoAnim);

        short body = u.Invent.ArmourEqpObjIndex > 0
            ? (short)ObjData.Get(u.Invent.ArmourEqpObjIndex).Ropaje
            : (u.OrigChar.body != 0 ? u.OrigChar.body : u.Char.body);
        // La barca/montura/morph pone Char.Head=0; la cabeza a-pie está en OrigChar (capturada al
        // transformarse). Sin esto se guardaba Head=0 → al desembarcar quedaba sin cabeza.
        short head   = u.OrigChar.Head != 0 ? u.OrigChar.Head : u.Char.Head;
        short weap   = u.Invent.WeaponEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.WeaponEqpObjIndex).WeaponAnim : (short)0;
        short shield = u.Invent.EscudoEqpObjIndex > 0 ? (short)ObjData.Get(u.Invent.EscudoEqpObjIndex).ShieldAnim : (short)0;
        short casco  = u.Invent.CascoEqpObjIndex  > 0 ? (short)ObjData.Get(u.Invent.CascoEqpObjIndex).CascoAnim  : (short)0;
        return (body, head, weap, shield, casco);
    }
}
