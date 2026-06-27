using System.Collections.Concurrent;

namespace ServidorCS.Game;

/// <summary>
/// Snapshot inmutable de todo lo que <see cref="CharSaver.SaveUser"/> necesita persistir de un
/// <see cref="User"/>. Se captura con <see cref="CharSaver.CaptureSnapshot"/> (lecturas puras,
/// sin I/O) mientras se tiene el GameLock, y se aplica a disco después con
/// <see cref="CharSaver.ApplyAndSave"/>, típicamente en otro hilo, sin volver a tocar el User.
/// No contiene ninguna referencia a User/Inventario/UserStats vivos: todos los arrays y listas
/// están clonados (son structs o strings inmutables, así que la copia es independiente).
/// </summary>
public sealed class CharSaveSnapshot
{
    public string Name = "";

    // [INIT]
    public bool VivoParaApariencia; // u.flags.Muerto == 0 al capturar
    public short Body, Head, Weap, Shield, Casco;
    public byte ArmaAura, BodyAura, EscudoAura, HeadAura, AnilloAura;
    public byte Heading;
    public short PosMap, PosX, PosY;
    public int UpTime;
    public byte Hogar;

    // [FLAGS]
    public byte FlagsMuerto;
    public bool Navegando;
    public byte Montando;
    public byte Envenenado;
    public byte Incinerado;
    public byte Hambre;
    public byte Sed;
    public byte Desnudo;
    public byte RecibioCorreo;
    public int ScrollExpMult, ScrollExpSeg, ScrollOroMult, ScrollOroSeg;
    public byte Donador;
    public string Tag = "";
    public bool PuedeRenombrar;
    public int UltimaMascotaNpc;
    public byte PetTipo;
    public byte PetNivel;
    public int PetExp;
    public string PetNombre = "";
    public bool PetDead;
    public int PetHogarRestante;   // segundos que le faltan de descanso en el hogar (0 = libre)
    // Mochila de la mascota: se copia al snapshot (objIndex, amount) por slot, como el resto de
    // los contenedores — el guardado corre en otro hilo y no puede mirar el User vivo.
    public (short obj, int cant)[] PetInv = System.Array.Empty<(short, int)>();
    public int Pena;

    // [STATS]
    public short MaxHP, MinHP, MaxMAN, MinMAN, MaxSta, MinSta, MaxHIT, MinHIT;
    public short MaxHam, MinHam, MaxAGU, MinAGU;
    public byte ELV;
    public int ELU;
    public long Exp;
    public int GLD, Banco;
    public bool BovedaPremiumDesbloqueada;
    public short SkillPts;
    public int ArenaPoints;
    public int PocionesVida, BonusVidaPociones;

    // [MUERTES]
    public short UsuariosMatados, NPCsMuertos;

    // [ATRIBUTOS] / [SKILLS] / [HECHIZOS] — clones independientes, índice 0 sin usar (base-1).
    public byte[] Atributos = Array.Empty<byte>();
    public byte[] Skills = Array.Empty<byte>();
    public int[] EluSkills = Array.Empty<int>();
    public int[] ExpSkills = Array.Empty<int>();
    public short[] Hechizos = Array.Empty<short>();

    // [Inventory]
    public short InventNroItems;
    public UserObj[] InventObjects = Array.Empty<UserObj>();
    public byte NudiEqpSlot, WeaponEqpSlot, EscudoEqpSlot, CascoEqpSlot, BarcoSlot,
        MunicionSlot, AnilloSlot, ArmourEqpSlot, MonturaSlot;
    public short MagicSlot;

    // [GUILD]
    public int GuildIndex;

    // [CASAMIENTO]
    public byte CasamientoCasado;
    public string CasamientoPareja = "";

    // [FACCIONES]
    public byte FaccionStatus;
    public int CiudMatados, ReneMatados, RepuMatados, MiliMatados, ArmiMatados, CaosMatados;
    public int FaccionRango;

    // [AMIGOS]
    public string[] AmigosNombres = Array.Empty<string>(); // índice 1..MAXAMIGOS
    public byte CantidadAmigos;
    public int MuertesUsuario;

    // [CORREO] — copia independiente (Correo es struct de strings inmutables).
    public List<Correo> Correos = new();

    // [BancoInventory] / Premium1 / Premium2
    public short BancoNroItems;
    public UserObj[] BancoObjects = Array.Empty<UserObj>();
    public short BancoPremium1NroItems;
    public UserObj[] BancoPremium1Objects = Array.Empty<UserObj>();
    public short BancoPremium2NroItems;
    public UserObj[] BancoPremium2Objects = Array.Empty<UserObj>();
}

/// <summary>
/// Persistencia del personaje. Equivale a SaveUser (FileIO.bas): reescribe el .chr
/// actualizando SOLO las claves que el servidor modela (INIT, STATS, ATRIBUTOS,
/// HECHIZOS, Inventory), preservando el resto de secciones (FLAGS, FACCIONES, GUILD,
/// CORREO, DONADOR...) que aún no portamos, para no perder datos.
///
/// Se llama al desconectar (logout), desde comandos de GM/chat, y periódicamente
/// (autosave). El trabajo se divide en dos pasos independientes para poder sacar el I/O
/// de disco del GameLock (ver [[b3_autosave_sin_lock]]):
///   1. CaptureSnapshot/CaptureAllOnline — lecturas puras de un User vivo, SIN tocar disco.
///      Deben llamarse bajo el GameLock, junto al resto de la simulación.
///   2. ApplyAndSave — toma el snapshot (ya independiente del User) y hace el I/O de disco.
///      No necesita el GameLock: no lee ni escribe ningún estado mutable del mundo, solo el
///      snapshot inmutable y el archivo .chr correspondiente.
/// SaveUser(User) sigue existiendo con el mismo comportamiento síncrono de siempre (captura +
/// aplica en la misma llamada), para no tocar ninguno de sus call sites actuales (logout,
/// /guardar, comandos de GM, CharRename).
/// </summary>
public static class CharSaver
{
    // Lock por-archivo (clave = nombre en mayúsculas): evita que dos escrituras del MISMO .chr
    // se pisen si corren en hilos distintos — por ejemplo, un autosave ya encolado en el worker de
    // persistencia terminando de escribir justo cuando el jugador se desconecta y SaveUser corre
    // síncrono desde el hilo del game loop. Sin esto, dos IniDocument distintos (uno con datos algo
    // viejos del snapshot, otro con los datos frescos del logout) podrían intercalar sus
    // File.ReadAllBytes/File.WriteAllBytes y corromper el .chr o perder el guardado más nuevo.
    private static readonly ConcurrentDictionary<string, object> _fileLocks = new();

    private static object FileLock(string name) => _fileLocks.GetOrAdd(name.ToUpperInvariant(), _ => new object());

    /// <summary>Guarda todos los usuarios logueados de forma síncrona e inmediata (se usa en el
    /// cierre del servidor: en ese punto el game loop ya paró, no hay lock que liberar rápido).</summary>
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

    /// <summary>Captura el snapshot de todos los usuarios logueados, SIN tocar disco. Pensado para
    /// llamarse bajo el GameLock desde el tick periódico de autosave/backup; el resultado se
    /// procesa después (I/O de disco) fuera del lock, vía ApplyAndSave.</summary>
    public static List<CharSaveSnapshot> CaptureAllOnline()
    {
        var list = new List<CharSaveSnapshot>();
        for (int i = 1; i <= UserListManager.LastUser; i++)
        {
            var u = UserListManager.UserList[i];
            if (u.flags.UserLogged && !string.IsNullOrEmpty(u.Name))
                list.Add(CaptureSnapshot(u));
        }
        return list;
    }

    /// <summary>Guarda un usuario de forma síncrona e inmediata (logout, /guardar, comandos de GM,
    /// cambio de nombre). Comportamiento idéntico al de siempre: captura + escribe en la misma
    /// llamada. Internamente reusa CaptureSnapshot/ApplyAndSave para no duplicar la lógica de
    /// mapeo de campos con el camino de autosave asincrónico.</summary>
    public static void SaveUser(User u)
    {
        if (string.IsNullOrEmpty(u.Name)) return;
        ApplyAndSave(CaptureSnapshot(u));
    }

    /// <summary>Lee todos los campos de un User vivo y arma un snapshot inmutable. Solo lecturas de
    /// memoria (arrays clonados, structs copiados, strings inmutables): no toca disco ni bloquea.
    /// Debe llamarse con el GameLock tomado (el User puede mutar en cualquier momento fuera de él).</summary>
    public static CharSaveSnapshot CaptureSnapshot(User u)
    {
        var s = new CharSaveSnapshot { Name = u.Name };

        s.VivoParaApariencia = u.flags.Muerto == 0;
        if (s.VivoParaApariencia)
        {
            var (body, head, weap, shield, casco) = AparienciaAPie(u);
            s.Body = body; s.Head = head; s.Weap = weap; s.Shield = shield; s.Casco = casco;
            s.ArmaAura = u.Char.Arma_Aura;
            s.BodyAura = u.Char.Body_Aura;
            s.EscudoAura = u.Char.Escudo_Aura;
            s.HeadAura = u.Char.Head_Aura;
            s.AnilloAura = u.Char.Anillo_Aura;
        }
        s.Heading = u.Char.heading;
        s.PosMap = u.Pos.Map; s.PosX = u.Pos.X; s.PosY = u.Pos.Y;
        s.UpTime = u.UpTime;
        s.Hogar = u.Hogar;

        s.FlagsMuerto = u.flags.Muerto;
        s.Navegando = u.flags.Navegando;
        s.Montando = u.flags.Montando;
        s.Envenenado = u.flags.Envenenado;
        s.Incinerado = u.flags.Incinerado;
        s.Hambre = u.flags.Hambre;
        s.Sed = u.flags.Sed;
        s.Desnudo = u.flags.Desnudo;
        s.RecibioCorreo = u.flags.RecibioCorreo;

        double ahoraScroll = Environment.TickCount64 / 1000.0;
        int expSeg = u.flags.ScrollExpMult > 1 ? (int)Math.Max(0, u.flags.ScrollExpExpira - ahoraScroll) : 0;
        int oroSeg = u.flags.ScrollOroMult > 1 ? (int)Math.Max(0, u.flags.ScrollOroExpira - ahoraScroll) : 0;
        s.ScrollExpMult = expSeg > 0 ? u.flags.ScrollExpMult : 0;
        s.ScrollExpSeg = expSeg;
        s.ScrollOroMult = oroSeg > 0 ? u.flags.ScrollOroMult : 0;
        s.ScrollOroSeg = oroSeg;

        s.Donador = u.Char.Donador;
        s.Tag = u.TagPersonal ?? "";
        s.PuedeRenombrar = u.PuedeRenombrar;
        s.UltimaMascotaNpc = u.UltimaMascotaNpc;
        s.PetTipo = u.PetTipo;
        s.PetNivel = u.PetNivel;
        s.PetExp = u.PetExp;
        s.PetNombre = u.PetNombre ?? "";
        s.PetDead = u.PetDead;
        // Se guarda lo que FALTA, no el instante: TickCount64 se reinicia con el server y un
        // "momento absoluto" guardado no significaría nada al volver a levantarlo.
        s.PetHogarRestante = (int)Math.Max(0, u.PetHogarHasta - Environment.TickCount64 / 1000.0);
        var petInv = new (short, int)[Constants.MAX_PETINVENTORY_SLOTS + 1];
        for (int i = 1; i <= Constants.MAX_PETINVENTORY_SLOTS; i++)
            petInv[i] = (u.PetInvent.Object[i].ObjIndex, u.PetInvent.Object[i].Amount);
        s.PetInv = petInv;
        s.Pena = u.flags.Pena;

        s.MaxHP = u.Stats.MaxHP; s.MinHP = u.Stats.MinHP;
        s.MaxMAN = u.Stats.MaxMAN; s.MinMAN = u.Stats.MinMAN;
        s.MaxSta = u.Stats.MaxSta; s.MinSta = u.Stats.MinSta;
        s.MaxHIT = u.Stats.MaxHIT; s.MinHIT = u.Stats.MinHIT;
        s.MaxHam = u.Stats.MaxHam; s.MinHam = u.Stats.MinHam;
        s.MaxAGU = u.Stats.MaxAGU; s.MinAGU = u.Stats.MinAGU;
        s.ELV = u.Stats.ELV; s.ELU = u.Stats.ELU;
        s.Exp = (long)u.Stats.Exp;
        s.GLD = u.Stats.GLD; s.Banco = u.Stats.Banco;
        s.BovedaPremiumDesbloqueada = u.BovedaPremiumDesbloqueada;
        s.SkillPts = u.Stats.SkillPts;
        s.ArenaPoints = u.Stats.ArenaPoints;
        s.PocionesVida = u.Stats.PocionesVida;
        s.BonusVidaPociones = u.Stats.BonusVidaPociones;

        s.UsuariosMatados = u.Stats.UsuariosMatados;
        s.NPCsMuertos = u.Stats.NPCsMuertos;

        s.Atributos = (byte[])u.Stats.UserAtributos.Clone();
        s.Skills = (byte[])u.Stats.UserSkills.Clone();
        s.EluSkills = (int[])u.Stats.EluSkills.Clone();
        s.ExpSkills = (int[])u.Stats.ExpSkills.Clone();
        s.Hechizos = (short[])u.Stats.UserHechizos.Clone();

        s.InventNroItems = u.Invent.NroItems;
        s.InventObjects = (UserObj[])u.Invent.Object.Clone();
        s.NudiEqpSlot = u.Invent.NudiEqpSlot;
        s.WeaponEqpSlot = u.Invent.WeaponEqpSlot;
        s.EscudoEqpSlot = u.Invent.EscudoEqpSlot;
        s.MagicSlot = u.Invent.MagicSlot;
        s.CascoEqpSlot = u.Invent.CascoEqpSlot;
        s.BarcoSlot = u.Invent.BarcoSlot;
        s.MunicionSlot = u.Invent.MunicionEqpSlot;
        s.AnilloSlot = u.Invent.AnilloEqpSlot;
        s.ArmourEqpSlot = u.Invent.ArmourEqpSlot;
        s.MonturaSlot = u.Invent.MonturaSlot;

        s.GuildIndex = u.GuildIndex;

        s.CasamientoCasado = u.CasamientoCasado;
        s.CasamientoPareja = u.CasamientoPareja ?? "";

        s.FaccionStatus = u.Faccion.Status;
        s.CiudMatados = u.Faccion.CiudadanosMatados;
        s.ReneMatados = u.Faccion.RenegadosMatados;
        s.RepuMatados = u.Faccion.RepublicanosMatados;
        s.MiliMatados = u.Faccion.MilicianosMatados;
        s.ArmiMatados = u.Faccion.ArmadaMatados;
        s.CaosMatados = u.Faccion.CaosMatados;
        s.FaccionRango = u.Faccion.Rango;

        s.AmigosNombres = new string[Constants.MAXAMIGOS + 1];
        for (int a = 1; a <= Constants.MAXAMIGOS; a++)
            s.AmigosNombres[a] = u.Amigos[a].Nombre;
        s.CantidadAmigos = u.flags.CantidadAmigos;
        s.MuertesUsuario = u.flags.MuertesUsuario;

        s.Correos = new List<Correo>(u.Correos);

        s.BancoNroItems = u.BancoInvent.NroItems;
        s.BancoObjects = (UserObj[])u.BancoInvent.Object.Clone();
        s.BancoPremium1NroItems = u.BancoPremium1.NroItems;
        s.BancoPremium1Objects = (UserObj[])u.BancoPremium1.Object.Clone();
        s.BancoPremium2NroItems = u.BancoPremium2.NroItems;
        s.BancoPremium2Objects = (UserObj[])u.BancoPremium2.Object.Clone();

        return s;
    }

    /// <summary>Aplica un snapshot ya capturado al .chr en disco: lee la base, pisa las claves que
    /// el servidor modela, escribe. No toca ningún estado mutable del mundo — seguro para llamar
    /// desde cualquier hilo, incluido el worker de persistencia, en paralelo con el game loop.</summary>
    public static void ApplyAndSave(CharSaveSnapshot s)
    {
        if (string.IsNullOrEmpty(s.Name)) return;
        lock (FileLock(s.Name))
        {
            string file = System.IO.Path.Combine(CharLoader.CharPath, s.Name.ToUpperInvariant() + ".chr");
            var doc = new IniDocument(file);
            if (!doc.Loaded) return; // sin charfile base no escribimos (evita crear uno corrupto)

            if (s.VivoParaApariencia)
            {
                doc.Set("INIT", "Body", s.Body.ToString());
                doc.Set("INIT", "Head", s.Head.ToString());
                doc.Set("INIT", "Arma", s.Weap.ToString());
                doc.Set("INIT", "Escudo", s.Shield.ToString());
                doc.Set("INIT", "Casco", s.Casco.ToString());
                doc.Set("INIT", "ArmaAura", s.ArmaAura.ToString());
                doc.Set("INIT", "BodyAura", s.BodyAura.ToString());
                doc.Set("INIT", "EscudoAura", s.EscudoAura.ToString());
                doc.Set("INIT", "HeadAura", s.HeadAura.ToString());
                doc.Set("INIT", "AnilloAura", s.AnilloAura.ToString());
            }
            doc.Set("INIT", "Heading", s.Heading.ToString());
            doc.Set("INIT", "Position", $"{s.PosMap}-{s.PosX}-{s.PosY}");
            doc.Set("INIT", "UpTime", s.UpTime.ToString());
            doc.Set("INIT", "Hogar", s.Hogar.ToString());

            doc.Set("FLAGS", "Muerto", s.FlagsMuerto.ToString());
            doc.Set("FLAGS", "Navegando", (s.Navegando ? 1 : 0).ToString());
            doc.Set("FLAGS", "Montando", s.Montando.ToString());
            doc.Set("FLAGS", "Envenenado", s.Envenenado.ToString());
            doc.Set("FLAGS", "Incinerado", s.Incinerado.ToString());
            doc.Set("FLAGS", "Hambre", s.Hambre.ToString());
            doc.Set("FLAGS", "Sed", s.Sed.ToString());
            doc.Set("FLAGS", "Desnudo", s.Desnudo.ToString());
            doc.Set("FLAGS", "Recibiocorreo", s.RecibioCorreo.ToString());
            doc.Set("FLAGS", "ScrollExpMult", s.ScrollExpMult.ToString());
            doc.Set("FLAGS", "ScrollExpSeg", s.ScrollExpSeg.ToString());
            doc.Set("FLAGS", "ScrollOroMult", s.ScrollOroMult.ToString());
            doc.Set("FLAGS", "ScrollOroSeg", s.ScrollOroSeg.ToString());
            doc.Set("FLAGS", "Donador", s.Donador.ToString());
            doc.Set("FLAGS", "Tag", s.Tag);
            doc.Set("FLAGS", "PuedeRenombrar", (s.PuedeRenombrar ? 1 : 0).ToString());
            doc.Set("FLAGS", "UltimaMascota", s.UltimaMascotaNpc.ToString());
            doc.Set("MASCOTA", "Tipo", s.PetTipo.ToString());
            doc.Set("MASCOTA", "Nivel", s.PetNivel.ToString());
            doc.Set("MASCOTA", "Exp", s.PetExp.ToString());
            doc.Set("MASCOTA", "Nombre", s.PetNombre);
            doc.Set("MASCOTA", "Muerta", (s.PetDead ? 1 : 0).ToString());
            doc.Set("MASCOTA", "DescansoHogar", s.PetHogarRestante.ToString());
            // Mochila de la mascota: mismo formato que [Inventory] pero sin "equipped" (no se
            // equipa nada desde ahí). Slot vacío = "0-0", igual que el banco.
            for (int i = 1; i <= Constants.MAX_PETINVENTORY_SLOTS; i++)
            {
                var (obj, cant) = i < s.PetInv.Length ? s.PetInv[i] : ((short)0, 0);
                doc.Set("MASCOTA_INV", "Obj" + i, $"{obj}-{cant}");
            }
            doc.Set("COUNTERS", "Pena", s.Pena.ToString());

            doc.Set("STATS", "MaxHP", s.MaxHP.ToString());
            doc.Set("STATS", "MinHP", s.MinHP.ToString());
            doc.Set("STATS", "MaxMAN", s.MaxMAN.ToString());
            doc.Set("STATS", "MinMAN", s.MinMAN.ToString());
            doc.Set("STATS", "MaxSTA", s.MaxSta.ToString());
            doc.Set("STATS", "MinSTA", s.MinSta.ToString());
            doc.Set("STATS", "MaxHIT", s.MaxHIT.ToString());
            doc.Set("STATS", "MinHIT", s.MinHIT.ToString());
            doc.Set("STATS", "MaxHAM", s.MaxHam.ToString());
            doc.Set("STATS", "MinHAM", s.MinHam.ToString());
            doc.Set("STATS", "MaxAGU", s.MaxAGU.ToString());
            doc.Set("STATS", "MinAGU", s.MinAGU.ToString());
            doc.Set("STATS", "ELV", s.ELV.ToString());
            doc.Set("STATS", "ELU", s.ELU.ToString());
            doc.Set("STATS", "EXP", s.Exp.ToString());
            doc.Set("STATS", "GLD", s.GLD.ToString());
            doc.Set("STATS", "BANCO", s.Banco.ToString());
            doc.Set("STATS", "BOVEDA_PREMIUM", s.BovedaPremiumDesbloqueada ? "1" : "0");
            doc.Set("STATS", "SkillPtsLibres", s.SkillPts.ToString());
            doc.Set("STATS", "ArenaPoints", s.ArenaPoints.ToString());
            doc.Set("STATS", "PocionesVida", s.PocionesVida.ToString());
            doc.Set("STATS", "BonusVidaPociones", s.BonusVidaPociones.ToString());

            doc.Set("MUERTES", "UserMuertes", s.UsuariosMatados.ToString());
            doc.Set("MUERTES", "NpcsMuertes", s.NPCsMuertos.ToString());

            for (int a = 1; a <= Constants.NUMATRIBUTOS; a++)
                doc.Set("ATRIBUTOS", "AT" + a, s.Atributos[a].ToString());

            for (int sk = 1; sk <= Constants.NUMSKILLS; sk++)
            {
                doc.Set("SKILLS", "SK" + sk, s.Skills[sk].ToString());
                doc.Set("SKILLS", "ELUSK" + sk, s.EluSkills[sk].ToString());
                doc.Set("SKILLS", "EXPSK" + sk, s.ExpSkills[sk].ToString());
            }

            for (int h = 1; h <= Constants.MAXUSERHECHIZOS; h++)
                doc.Set("HECHIZOS", "H" + h, s.Hechizos[h].ToString());

            doc.Set("Inventory", "CantidadItems", s.InventNroItems.ToString());
            for (int slot = 1; slot <= Constants.MAX_INVENTORY_SLOTS; slot++)
            {
                var o = s.InventObjects[slot];
                doc.Set("Inventory", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}-{(o.Equipped ? 1 : 0)}");
            }
            doc.Set("Inventory", "NudiEqpSlot", s.NudiEqpSlot.ToString());
            doc.Set("Inventory", "WeaponEqpSlot", s.WeaponEqpSlot.ToString());
            doc.Set("Inventory", "EscudoEqpSlot", s.EscudoEqpSlot.ToString());
            doc.Set("Inventory", "MagicSlot", s.MagicSlot.ToString());
            doc.Set("Inventory", "CascoEqpSlot", s.CascoEqpSlot.ToString());
            doc.Set("Inventory", "BarcoSlot", s.BarcoSlot.ToString());
            doc.Set("Inventory", "MunicionSlot", s.MunicionSlot.ToString());
            doc.Set("Inventory", "AnilloSlot", s.AnilloSlot.ToString());
            doc.Set("Inventory", "ArmourEqpSlot", s.ArmourEqpSlot.ToString());
            doc.Set("Inventory", "MonturaSlot", s.MonturaSlot.ToString());

            doc.Set("GUILD", "GUILDINDEX", s.GuildIndex.ToString());

            doc.Set("CASAMIENTO", "Casado", s.CasamientoCasado.ToString());
            doc.Set("CASAMIENTO", "Pareja", s.CasamientoPareja);

            doc.Set("FACCIONES", "Status", s.FaccionStatus.ToString());
            doc.Set("FACCIONES", "CiudMatados", s.CiudMatados.ToString());
            doc.Set("FACCIONES", "ReneMatados", s.ReneMatados.ToString());
            doc.Set("FACCIONES", "RepuMatados", s.RepuMatados.ToString());
            doc.Set("FACCIONES", "MiliMatados", s.MiliMatados.ToString());
            doc.Set("FACCIONES", "ArmiMatados", s.ArmiMatados.ToString());
            doc.Set("FACCIONES", "CaosMatados", s.CaosMatados.ToString());
            doc.Set("FACCIONES", "RANGO", s.FaccionRango.ToString());

            for (int a = 1; a <= Constants.MAXAMIGOS; a++)
                doc.Set("AMIGOS", "NOMBRE" + a, string.IsNullOrEmpty(s.AmigosNombres[a]) ? "Vacio" : s.AmigosNombres[a]);
            doc.Set("FLAGS", "CantidadAmigos", s.CantidadAmigos.ToString());
            doc.Set("FLAGS", "Murio", s.MuertesUsuario.ToString());

            for (int c = 1; c <= Constants.MAX_CORREOS_SLOTS; c++)
            {
                if (c <= s.Correos.Count)
                {
                    var co = s.Correos[c - 1];
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

            doc.Set("BancoInventory", "CantidadItems", s.BancoNroItems.ToString());
            for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
            {
                var o = s.BancoObjects[slot];
                doc.Set("BancoInventory", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}");
            }

            doc.Set("BancoInventoryPremium1", "CantidadItems", s.BancoPremium1NroItems.ToString());
            for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
            {
                var o = s.BancoPremium1Objects[slot];
                doc.Set("BancoInventoryPremium1", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}");
            }
            doc.Set("BancoInventoryPremium2", "CantidadItems", s.BancoPremium2NroItems.ToString());
            for (int slot = 1; slot <= Constants.MAX_BANCOINVENTORY_SLOTS; slot++)
            {
                var o = s.BancoPremium2Objects[slot];
                doc.Set("BancoInventoryPremium2", "Obj" + slot, $"{o.ObjIndex}-{o.Amount}");
            }

            try { doc.Save(file); }
            catch (Exception ex) { Console.WriteLine($"[ServidorCS] Error guardando {s.Name}: {ex.Message}"); }
        }
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
