namespace ServidorCS.Game;

/// <summary>Tipos de objeto (eOBJType del VB6 - Declares.bas:627-667). Portado 1:1.</summary>
public enum ObjType : byte
{
    UseOnce = 1, Weapon = 2, Armadura = 3, Arboles = 4, Guita = 5, Puertas = 6,
    Contenedores = 7, Carteles = 8, Llaves = 9, Pociones = 11, Libros = 12,
    Bebidas = 13, Lena = 14, Fuego = 15, Escudo = 16, Casco = 17, Anillo = 18,
    Teleport = 19, Muebles = 20, ItemsMagicos = 21, Yacimiento = 22, Minerales = 23,
    Pergaminos = 24, Instrumentos = 26, Barcos = 31, Flechas = 32, Monturas = 44,
    // FASE 1: Tipos críticos faltantes (Declares.bas)
    Pasajes = 36,    // otPasajes: pasaje de transportador (viajes). Antes mal puesto como ArbolElfico.
    Yunque = 27, Fragua = 28, Runa = 38,
    Nudillos = 46, Anillos = 47,
    Pozos = 40,      // otPozos (Pozos Mágicos - Declares.bas:659)
    Puestos = 45,    // otPuestos
    Correo = 48,     // otCorreo (Declares.bas:667)
    BotellaVacia = 33, BotellaLlena = 34, // agua: vacía llena en costa; llena se bebe
    Bolsas = 39,     // otBolsas: bolsa de oro (suma Valor al GLD)
    AnilloEspec = 49, // otAnilloEspec: manuales que aumentan un skill (QueSkill += CuantoAumento)
    // Tipos custom de este server que estaban en obj.dat pero nunca se programaron.
    RunaTransporte = 50, // otInvi (Declares.bas): runas de teletransporte (faccionaria/cercana/donador)
    LlaveCofre = 51,     // llaves de cofres por nivel (abren cofres apuntados)
    Mochila = 52,        // amplían los slots de inventario (+5/+10/+15)
    Regalos = 53,        // otRegalos: caja que entrega los ítems del campo "Items="
}

/// <summary>
/// Base de datos de objetos (obj.dat, INI [OBJN]). Carga el subconjunto de campos que
/// el núcleo necesita: nombre, tipo, gráfico, daño (armas), defensa (armaduras),
/// anims de equipo, requisitos (nivel/sexo/clase), y efectos de pociones.
/// Equivale a la lectura de ObjData de FileIO.bas (LoadOBJData).
/// </summary>
public static class ObjData
{
    public struct Obj
    {
        public string Name;
        public ObjType Type;
        public int GrhIndex;
        public int Valor;
        // Armas
        public int MinHIT, MaxHIT;
        public int MinHITPVP, MaxHITPVP;   // daño fijo vs usuarios (CalcularDanio)
        public int MinHITPVE, MaxHITPVE;   // daño fijo vs NPCs
        public int WeaponAnim;
        // Armadura/escudo/casco
        public int MinDef, MaxDef;
        public int Ropaje;     // body al equipar armadura/montura/barco (NumRopaje)
        public int ShieldAnim, CascoAnim;
        public int Aura;       // aura del item (obj.dat "Aura"); se muestra al equiparlo (AuraToChar)
        public int SndEspecial; // sonido de aura al equipar (obj.dat "SndEspecial"); solo si Aura>0
        // Requisitos
        public byte MinELV, Mujer, Hombre;
        // Pociones (otPociones): SubTipo (1=azul/maná, 2=amarilla/agi, 3=roja/vida,
        // 4=verde/fuerza, 5=violeta/cura veneno...) y cuánto restaura.
        public int SubTipo;
        public int MinModificador, MaxModificador;
        public int DuracionEfecto;   // pociones de atributo (agi/fza)
        public int LanzaHechizo;     // pociones que lanzan hechizo (subtipo 5)
        public int ExtraTimer;       // armas: modifica el intervalo de ataque (neg=más rápido, pos=más lento)
        public int HechizoIndex;     // otPergaminos: índice del hechizo que enseña al usarlo
        public int ResistenciaMagica; // armadura/casco/escudo/anillo/montura: resta daño mágico recibido
        public int EfectoMagico;     // arma/anillo: tipo de efecto mágico (eMagicType: 9=dañoMagico, etc.)
        public int CuantoAumento;    // báculos (EfectoMagico=dañoMagico): suma fija al daño de hechizos
        // Comida / bebida
        public int MinHam;           // otUseOnce: cuánto restaura de hambre
        public int MinSed;           // otbebidas: cuánto restaura de sed
        public int Proyectil;        // armas: 1=proyectil/arco/arpón, 2=arrojadiza (daga)
        public int Municion;         // armas: 1 = usa munición (arco/ballesta → necesita flechas). obj.dat "Municiones"
        public int Apunala;          // armas: 1 = puede apuñalar (dagas) — obj.dat "Apuñala"
        public int StaRequerido;     // armas/nudillos: energía por golpe (FileIO.bas:1132; default 10 si <1)
        public int Envenena;         // armas/munición: 1 = envenena al golpear (UserEnvenena)
        public int Snd3;             // munición tipo "orbe": Snd3>0 → puede incinerar (UserIncinera)
        public int NoSeCae;          // 1 = no se cae al morir (obj.dat "NoSeCae")
        public int Permanente;       // 2 = newbie/mapa/runa (NoSeCae no bloquea destruirlo al confirmar)
        public int Real;             // 1 = item faccionario de Armada Real (obj.dat "Real")
        public int Caos;             // 1 = item faccionario del Caos (obj.dat "Caos")
        public int Milicia;          // 1 = item faccionario de Milicia (obj.dat "Milicia")
        public int Destruir;         // 1 = al tirarlo pide confirmación para destruir (ShowMessageBox accion 1)
        public int Newbie;           // 1 = item newbie (los jugadores comunes no pueden tirarlo/venderlo)
        // Pasaje de transportador (otPasajes): viaje de DesdeMap → (HastaMap,HastaX,HastaY).
        public int DesdeMap;         // obj.dat "Desde" (mapa de origen requerido)
        public int HastaMap;         // obj.dat "Map" (mapa destino)
        public int HastaX, HastaY;   // obj.dat "X","Y" (destino)
        // Crafteo (skill mínima + materiales por unidad). 0 = no fabricable con esa skill.
        public int SkHerreria, SkCarpinteria, SkSastreria, SkPociones;
        public int LingH, LingP, LingO;   // lingotes (hierro/plata/oro) por unidad — herrería
        public int LingoteIndex;          // otMinerales: ObjIndex del lingote que produce al fundir (DoLingotes)
        public int MineralIndex;          // otYacimiento: ObjIndex del mineral que se extrae al minar (DoMineria)
        public int Madera;                // leña por unidad — carpintería
        public int Raices;                // raíces por unidad — alquimia
        public int PielLobo, PielOso, PielOsoPolar; // pieles por unidad — sastrería
        // Puertas (otPuertas)
        public int Cerrada, Llave, IndexAbierta, IndexCerrada, IndexCerradaLlave, Clave;
        // Misc uso de inventario
        public int Snd1;        // otInstrumentos: sonido al tocar; munición: sonido del disparo
        public int Snd2;        // munición: GrhFX de impacto (CreateFX) — VB6 lo usa como índice de FX
        public int QueSkill;    // otAnilloEspec (manuales) / anillos ModificaSkill(3): skill que aumenta
        public int QueAtributo; // anillos ModificaAtributo(2): atributo que aumenta (1=Fza,2=Agi,3=Int,4=Car,5=Cons)
        public int LevelItem;   // nivel mínimo para USAR el item (UseInvItem, obj.dat "levelItem")
        public int MinSkill;    // skill mínima para usar el item (barcos→Navegación, monturas→Equitación)
        // Restricciones de uso (obj.dat): clases y razas que NO pueden usarlo (listas "a,b,c").
        public int[] ClasesProhibidas;
        public int[] RazasProhibidas;
        // otRegalos (53): pares (objIndex, cantidad) parseados del campo "Items=608-1,1081-2,1147-1".
        public (short ObjIndex, int Amount)[] RegaloItems;
        // otMochila (52): cuántos slots de inventario suma al usarse (derivado del nombre "+N slot").
        public int SlotsExtra;
        // Fuegos artificiales (cañitas/cohetes): índice de partícula que lanza al usarse (obj.dat "Particula").
        public int Particula;
    }

    // Parsea "608-1,1081-2,1147-1" → pares (608,1),(1081,2),(1147,1). Vacío/null → null.
    private static (short, int)[] ParseRegaloItems(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var list = new List<(short, int)>();
        foreach (var par in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = par.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (kv.Length >= 1 && short.TryParse(kv[0], out var oi) && oi > 0)
            {
                int amt = kv.Length >= 2 && int.TryParse(kv[1], out var a) && a > 0 ? a : 1;
                list.Add((oi, amt));
            }
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    // Extrae el primer número del nombre de la mochila ("Mochila Nivel 1 (+5 slot)" → 5).
    private static int ParseSlotsDelNombre(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        int plus = name.IndexOf('+');
        if (plus < 0) return 0;
        int j = plus + 1, num = 0; bool any = false;
        while (j < name.Length && char.IsDigit(name[j])) { num = num * 10 + (name[j] - '0'); j++; any = true; }
        return any ? num : 0;
    }

    // Parsea "2,7,5" → int[]{2,7,5}. Vacío/null → null. Acepta separadores coma o guion.
    private static int[] ParseLista(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Split(new[] { ',', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<int>();
        foreach (var p in parts) if (int.TryParse(p, out var v) && v > 0) list.Add(v);
        return list.Count > 0 ? list.ToArray() : null;
    }

    // QueRazaEs (FileIO.bas:2783): obj.dat numera HUMANO=1,ENANO=2,ELFO=3,DROW=4,GNOMO=5,ORCO=6
    // pero eRaza interno es Humano=1,Elfo=2,Drow=3,Gnomo=4,Enano=5,Orco=6. Sin esta conversión
    // las armaduras de enano/altas validaban contra la raza equivocada.
    private static int[] MapRazas(int[] razas)
    {
        if (razas == null) return null;
        for (int i = 0; i < razas.Length; i++)
            razas[i] = razas[i] switch { 2 => 5, 3 => 2, 4 => 3, 5 => 4, _ => razas[i] };
        return razas;
    }

    private static Obj[] _objs;
    public static int Count => (_objs?.Length ?? 1) - 1;
    public static void Reload() { _objs = null; EnsureLoaded(); Console.WriteLine($"[ObjData] Recargado: {Count} objetos."); }

    public static Obj Get(int index)
    {
        EnsureLoaded();
        if (index < 1 || index >= _objs.Length) return default;
        return _objs[index];
    }

    private static void EnsureLoaded()
    {
        if (_objs != null) return;
        string file = FindFile();
        var ini = file != null ? new IniFile(file) : null;

        // Determinar cantidad: clave NumOBJs en [INIT], o barrer hasta un máximo.
        int max = 0;
        if (ini != null)
        {
            max = ini.GetInt("INIT", "NumOBJs");
            if (max <= 0) max = 12000; // fallback: barrido amplio
        }
        _objs = new Obj[max + 1];
        if (ini == null) return;

        for (int i = 1; i <= max; i++)
            _objs[i] = ParseObj(ini, i);

        Console.WriteLine($"[ServidorCS] obj.dat cargado: {max} objetos");
    }

    /// <summary>
    /// Recarga UN solo objeto desde obj.dat en disco (usado por el editor de objetos GM
    /// después de persistir cambios, para garantizar memoria == disco con la misma semántica
    /// de parseo que la carga inicial).
    /// </summary>
    public static void ReloadOne(int index)
    {
        EnsureLoaded();
        if (index < 1 || index >= _objs.Length) return;
        string file = FindFile();
        if (file == null) return;
        _objs[index] = ParseObj(new IniFile(file), index);
    }

    /// <summary>Ruta del obj.dat activo (null si no se encontró). La usa el editor de objetos.</summary>
    public static string FilePath => FindFile();

    /// <summary>Parsea la sección [OBJi]. Devuelve default (Name=null) si no tiene nombre.</summary>
    private static Obj ParseObj(IniFile ini, int i)
    {
            string name = ini.Get("OBJ" + i, "Name");
            if (string.IsNullOrEmpty(name)) name = ini.Get("OBJ" + i, "Nombre");
            if (string.IsNullOrEmpty(name)) return default;

            int objType = ini.GetInt("OBJ" + i, "ObjType");
            int cerradaVal = ini.GetInt("OBJ" + i, "abierta");  // campo mal nombrado en obj.dat

            var obj = new Obj
            {
                Name = name,
                Type = (ObjType)objType,
                GrhIndex = ini.GetInt("OBJ" + i, "GrhIndex"),
                Valor = ini.GetInt("OBJ" + i, "Valor"),
                MinHIT = ini.GetInt("OBJ" + i, "MinHIT"),
                MaxHIT = ini.GetInt("OBJ" + i, "MaxHIT"),
                MinHITPVP = ini.GetInt("OBJ" + i, "MinHITPVP"),
                MaxHITPVP = ini.GetInt("OBJ" + i, "MaxHITPVP"),
                MinHITPVE = ini.GetInt("OBJ" + i, "MinHITPVE"),
                MaxHITPVE = ini.GetInt("OBJ" + i, "MaxHITPVE"),
                WeaponAnim = ini.GetInt("OBJ" + i, "Anim"),     // arma: campo "Anim"
                MinDef = ini.GetInt("OBJ" + i, "MinDef"),
                MaxDef = ini.GetInt("OBJ" + i, "MaxDef"),
                Ropaje = ini.GetInt("OBJ" + i, "NumRopaje"),
                // CascoAnim/ShieldAnim NO existen como campos en obj.dat: el VB6 (FileIO.bas:955-958)
                // los lee del MISMO campo "Anim" según SubTipo (1=casco, 2=escudo). Se setean abajo.
                Aura = ini.GetInt("OBJ" + i, "Aura"),
                SndEspecial = ini.GetInt("OBJ" + i, "SndEspecial"), // sonido de aura al equipar
                Snd1 = ini.GetInt("OBJ" + i, "Snd1"),       // instrumentos/munición: sonido
                Snd2 = ini.GetInt("OBJ" + i, "Snd2"),       // munición: GrhFX de impacto
                QueSkill = ini.GetInt("OBJ" + i, "QueSkill"),// manuales (otAnilloEspec): skill que sube
                QueAtributo = ini.GetInt("OBJ" + i, "QueAtributo"), // anillos ModificaAtributo: atributo que sube
                LevelItem = ini.GetInt("OBJ" + i, "levelItem"),
                MinSkill = ini.GetInt("OBJ" + i, "MinSkill"),
                MinELV = (byte)ini.GetInt("OBJ" + i, "MinELV"),
                // Restricción de sexo: el obj.dat usa el tag "Genero" (1=Hombre, 2=Mujer) — FileIO.bas:1079.
                // Algunos objetos viejos usan Mujer=/Hombre= directo; se respetan ambos.
                Mujer = ini.GetInt("OBJ" + i, "Genero") == 2 ? (byte)1 : (byte)ini.GetInt("OBJ" + i, "Mujer"),
                Hombre = ini.GetInt("OBJ" + i, "Genero") == 1 ? (byte)1 : (byte)ini.GetInt("OBJ" + i, "Hombre"),
                ClasesProhibidas = ParseLista(ini.Get("OBJ" + i, "ClasesProhibidas")),
                RazasProhibidas = MapRazas(ParseLista(ini.Get("OBJ" + i, "RazasProhibidas"))),
                SubTipo = ini.GetInt("OBJ" + i, "SubTipo"),
                MinModificador = ini.GetInt("OBJ" + i, "MinModificador"),
                MaxModificador = ini.GetInt("OBJ" + i, "MaxModificador"),
                DuracionEfecto = ini.GetInt("OBJ" + i, "DuracionEfecto"),
                LanzaHechizo = ini.GetInt("OBJ" + i, "LanzaHechizo"),
                ExtraTimer = ini.GetInt("OBJ" + i, "ExtraTimer"),
                HechizoIndex = ini.GetInt("OBJ" + i, "HechizoIndex"),
                ResistenciaMagica = ini.GetInt("OBJ" + i, "ResistenciaMagica"),
                EfectoMagico = ini.GetInt("OBJ" + i, "EfectoMagico"),
                CuantoAumento = ini.GetInt("OBJ" + i, "CuantoAumento"),
                MinHam = ini.GetInt("OBJ" + i, "MinHam"),
                MinSed = ini.GetInt("OBJ" + i, "MinAgu"), // VB6 FileIO.bas:1093 lee el tag "MinAgu" (no "MinSed")
                Proyectil = ini.GetInt("OBJ" + i, "Proyectil"),
                Municion = ini.GetInt("OBJ" + i, "Municiones"),
                Apunala = ini.GetInt("OBJ" + i, "Apuñala"),
                // VB6 FileIO.bas:1132-1134: si StaRequerido < 1 → default 10 (para todo objeto).
                StaRequerido = ini.GetInt("OBJ" + i, "StaRequerido") is var staReq && staReq < 1 ? 10 : staReq,
                Envenena = ini.GetInt("OBJ" + i, "Envenena"),
                Snd3 = ini.GetInt("OBJ" + i, "Snd3"),
                NoSeCae = ini.GetInt("OBJ" + i, "NoSeCae"),
                Permanente = ini.GetInt("OBJ" + i, "Permanente"),
                // Facción del item: obj.dat usa el tag "Faccion" (1=Imperial→Real, 2=Milicia, 3=Caos)
                // — FileIO.bas:936. En este obj.dat NO hay Real=/Caos=/Milicia= directos, todo es Faccion=.
                Real = ini.GetInt("OBJ" + i, "Faccion") == 1 ? 1 : ini.GetInt("OBJ" + i, "Real"),
                Milicia = ini.GetInt("OBJ" + i, "Faccion") == 2 ? 1 : ini.GetInt("OBJ" + i, "Milicia"),
                Caos = ini.GetInt("OBJ" + i, "Faccion") == 3 ? 1 : ini.GetInt("OBJ" + i, "Caos"),
                Destruir = ini.GetInt("OBJ" + i, "Destruir"),
                Newbie = ini.GetInt("OBJ" + i, "Newbie"),
                DesdeMap = ini.GetInt("OBJ" + i, "Desde"),
                HastaMap = ini.GetInt("OBJ" + i, "Map"),
                HastaX = ini.GetInt("OBJ" + i, "X"),
                HastaY = ini.GetInt("OBJ" + i, "Y"),
                SkHerreria = ini.GetInt("OBJ" + i, "SkHerreria"),
                SkCarpinteria = ini.GetInt("OBJ" + i, "SkCarpinteria"),
                SkSastreria = ini.GetInt("OBJ" + i, "SkSastreria"),
                SkPociones = ini.GetInt("OBJ" + i, "SkPociones"),
                LingH = ini.GetInt("OBJ" + i, "LingH"),
                LingP = ini.GetInt("OBJ" + i, "LingP"),
                LingO = ini.GetInt("OBJ" + i, "LingO"),
                LingoteIndex = ini.GetInt("OBJ" + i, "LingoteIndex"),
                MineralIndex = ini.GetInt("OBJ" + i, "MineralIndex"),
                Madera = ini.GetInt("OBJ" + i, "Madera"),
                Raices = ini.GetInt("OBJ" + i, "Raices"),
                PielLobo = ini.GetInt("OBJ" + i, "PielLobo"),
                PielOso = ini.GetInt("OBJ" + i, "PielOsoPardo"),
                PielOsoPolar = ini.GetInt("OBJ" + i, "PielOsoPolar"),
                Cerrada = cerradaVal,  // campo "abierta" es en realidad Cerrada: 0=abierta, 1=cerrada
                Llave = ini.GetInt("OBJ" + i, "Llave"),
                IndexAbierta = ini.GetInt("OBJ" + i, "IndexAbierta"),
                IndexCerrada = ini.GetInt("OBJ" + i, "IndexCerrada"),
                IndexCerradaLlave = ini.GetInt("OBJ" + i, "IndexCerradaLlave"),
                Clave = ini.GetInt("OBJ" + i, "Clave"),
                Particula = ini.GetInt("OBJ" + i, "Particula"),
            };

            // otRegalos (53): lista de ítems que entrega al usarlo (campo "Items=").
            if (obj.Type == ObjType.Regalos)
                obj.RegaloItems = ParseRegaloItems(ini.Get("OBJ" + i, "Items"));

            // otMochila (52): slots extra que otorga. Campo "Slots=" si existe; si no, del nombre.
            if (obj.Type == ObjType.Mochila)
            {
                obj.SlotsExtra = ini.GetInt("OBJ" + i, "Slots");
                if (obj.SlotsExtra <= 0) obj.SlotsExtra = ParseSlotsDelNombre(name);
            }

            // Casco/Escudo: el anim sale del campo "Anim" según SubTipo (FileIO.bas:955-958).
            // (Armadura SubTipo 0 usa NumRopaje para el body; no tiene CascoAnim/ShieldAnim.)
            if (obj.Type == ObjType.Armadura)
            {
                int anim = ini.GetInt("OBJ" + i, "Anim");
                if (obj.SubTipo == 1) obj.CascoAnim = anim;
                else if (obj.SubTipo == 2) obj.ShieldAnim = anim;
            }
            return obj;
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            Path.Combine(DataPaths.Sub("Dat"), "obj.dat"),
            DataPaths.Root + "obj.dat",
            Path.Combine(AppContext.BaseDirectory, "Dat", "obj.dat"),
            Path.Combine(AppContext.BaseDirectory, "obj.dat"),
        })
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }
}
