using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Login por cuenta (CONNECT_ACCOUNT). Porta HandleLoginAccount + EntrarCuenta +
/// LoginAccountCharfile (Cuentas.bas).
///
/// Flujo 1:1:
///   1. Cliente manda CONNECT_ACCOUNT (cuenta, password, version, mac, hdserial).
///   2. Server valida la cuenta (.cnt) y responde AddPj con la lista de personajes
///      + AbrirFormularios(1) para que el cliente muestre la pantalla de selección.
///   3. El usuario elige un PJ → el cliente manda LOGIN_EXISTING_CHAR (ya implementado).
/// </summary>
public static class AccountManager
{
    public static string AccountPath = ResolveAccountPath();

    private static string ResolveAccountPath()
    {
        if (!string.IsNullOrEmpty(DataPaths.Root)) return DataPaths.Sub("Cuentas");
        foreach (var c in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Cuentas"),
            Path.Combine(Directory.GetCurrentDirectory(), "Cuentas"),
        })
        {
            if (Directory.Exists(c)) return c + Path.DirectorySeparatorChar;
        }
        return "Cuentas" + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Valida cuenta + password (ya descifrada). EntrarCuenta/CheckDataLoginAccount (Cuentas.bas) 1:1:
    /// la cuenta debe existir, no estar baneada y el SHA256(password+salt) debe coincidir.
    /// </summary>
    public static bool ValidarCuenta(string cuenta, string passwordPlano, out string error)
    {
        error = "";
        cuenta = cuenta.Trim().ToUpperInvariant();
        string cntFile = Path.Combine(AccountPath, cuenta + ".cnt");
        var ini = new IniFile(cntFile);
        if (!ini.Loaded) { error = "La cuenta no existe."; return false; }
        if (ini.GetInt(cuenta, "Ban") == 1) { error = "La cuenta se encuentra baneada."; return false; }

        string hash = ini.Get(cuenta, "Password");
        string salt = ini.Get(cuenta, "Salt");
        if (!Crypto.PasswordValida(passwordPlano, hash, salt))
        { error = "Contraseña incorrecta."; return false; }
        return true;
    }

    /// <summary>LegalCharacter (Cuentas.bas:655): chars válidos para nombre de archivo Win.</summary>
    private static bool LegalCharacter(int c)
    {
        if (c == 8) return true;
        if (c < 32 || c == 44) return false;
        if (c > 126) return false;
        if (c is 34 or 42 or 47 or 58 or 60 or 62 or 63 or 92 or 124) return false;
        return true;
    }

    private static bool TodosLegales(string s)
    { foreach (char c in s) if (!LegalCharacter(c)) return false; return true; }

    /// <summary>
    /// CreateNewAccount (Protocol.bas:17840 + SaveNewAccount) 1:1. Valida (CheckDataNewAccount),
    /// crea el .cnt con SHA256(pass+salt)/SHA256(pin+salt)/Salt y [PJS] vacío, y responde
    /// AbrirFormularios(1)+ShowMessageBox(32). password/pin ya vienen descifrados.
    /// </summary>
    public static void CreateNewAccount(Connection conn, string cuenta, string passwordPlano, string pinPlano)
    {
        cuenta = (cuenta ?? "").Trim().ToUpperInvariant();

        // CheckDataNewAccount 1:1.
        if (cuenta.Length == 0) { ServerPackets.ShowMessageBox(conn, "Ingrese un nombre de cuenta válido."); return; }
        if (cuenta.Length > 20) { ServerPackets.ShowMessageBox(conn, "El nombre de cuenta no puede superar 20 caracteres."); return; }
        if (!TodosLegales(cuenta)) { ServerPackets.ShowMessageBox(conn, "El nombre de cuenta tiene caracteres inválidos."); return; }
        if (passwordPlano.Length == 0) { ServerPackets.ShowMessageBox(conn, "Ingrese una contraseña válida."); return; }
        if (passwordPlano.Length > 30) { ServerPackets.ShowMessageBox(conn, "La contraseña no puede superar 30 caracteres."); return; }
        if (!TodosLegales(passwordPlano)) { ServerPackets.ShowMessageBox(conn, "La contraseña tiene caracteres inválidos."); return; }
        if (pinPlano.Length == 0) { ServerPackets.ShowMessageBox(conn, "Ingrese un código (PIN) válido."); return; }
        if (!int.TryParse(pinPlano, out _)) { ServerPackets.ShowMessageBox(conn, "Solo se permiten números en el código de cuenta."); return; }
        if (pinPlano.Length > 4) { ServerPackets.ShowMessageBox(conn, "El código no puede superar 4 dígitos."); return; }

        string cntFile = Path.Combine(AccountPath, cuenta + ".cnt");
        if (File.Exists(cntFile)) { ServerPackets.ShowMessageBox(conn, "Ya existe la cuenta."); return; }

        string salt = Crypto.RandomString(10);
        string passHash = Crypto.Sha256Hex(passwordPlano + salt);
        string codeHash = Crypto.Sha256Hex(pinPlano + salt);

        Directory.CreateDirectory(AccountPath);
        var doc = new IniDocument(cntFile); // nuevo (no existe): se construye en memoria y se graba
        doc.Set(cuenta, "Cuenta", cuenta);
        doc.Set(cuenta, "Password", passHash);
        doc.Set(cuenta, "Salt", salt);
        doc.Set(cuenta, "Ban", "0");
        doc.Set(cuenta, "UserCodigo", codeHash);
        doc.Set(cuenta, "Conectada", "0");
        doc.Set(cuenta, "CuentaGM", "0");
        doc.Set(cuenta, "Donador", "0");
        doc.Set(cuenta, "MacAdress", "0");
        doc.Set(cuenta, "HDserial", "0");
        doc.Set("PJS", "NumPjs", "0");
        for (int i = 1; i <= 10; i++) doc.Set("PJS", "PJ" + i, "");
        doc.Save(cntFile);

        Console.WriteLine($"[CreateNewAccount] cuenta nueva '{cuenta}' creada.");
        ServerPackets.AbrirFormularios(conn, 1);       // Show Account
        ServerPackets.ShowMessageBoxCode(conn, 32);    // "Cuenta creada con éxito"
    }

    /// <summary>true si el personaje 'name' está listado en [PJS] de la cuenta.</summary>
    public static bool CuentaTienePersonaje(string cuenta, string name)
    {
        cuenta = cuenta.Trim().ToUpperInvariant();
        var ini = new IniFile(Path.Combine(AccountPath, cuenta + ".cnt"));
        if (!ini.Loaded) return false;
        int n = ini.GetInt("PJS", "NumPjs");
        for (int i = 1; i <= n; i++)
            if (string.Equals(ini.Get("PJS", "PJ" + i).Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Maneja CONNECT_ACCOUNT. 'passwordPlano' ya viene descifrada. true si entró OK.</summary>
    public static bool HandleLoginAccount(Connection conn, string cuenta, string passwordPlano)
    {
        cuenta = cuenta.Trim().ToUpperInvariant();
        if (!ValidarCuenta(cuenta, passwordPlano, out string error))
        {
            // VB6 (Protocol.bas:17957): FlushBuffer + CloseSocket tras el rechazo.
            // Si no se cierra, el cliente queda conectado y no puede reintentar el login.
            ServerPackets.ShowMessageBox(conn, error);
            conn.FlushAndClose();
            return false;
        }
        EnviarListaPersonajes(conn, cuenta);
        return true;
    }

    /// <summary>Reenvía la lista de personajes de la cuenta (AddPj + ShowAccount) SIN validar password.
    /// La usa el login (ya validado) y el "Cambiar Personaje" del menú escape (HandleQuit), donde el
    /// usuario ya está autenticado y NO debe re-ingresar la contraseña.</summary>
    public static void EnviarListaPersonajes(Connection conn, string cuenta)
    {
        cuenta = cuenta.Trim().ToUpperInvariant();
        var ini = new IniFile(Path.Combine(AccountPath, cuenta + ".cnt"));

        var u = UserListManager.UserList[conn.UserIndex];
        u.Account = cuenta;

        // Leer lista de personajes de [PJS].
        int n = ini.GetInt("PJS", "NumPjs");
        var chars = new List<ServerPackets.AccountChar>();
        for (int i = 1; i <= n; i++)
        {
            string name = ini.Get("PJS", "PJ" + i).Trim();
            if (string.IsNullOrEmpty(name) || name.Equals("nada", StringComparison.OrdinalIgnoreCase))
                continue;
            chars.Add(BuildAccountChar(name));
        }

        ServerPackets.AddPj(conn, cuenta, chars.ToArray());
        ServerPackets.AbrirFormularios(conn, 1); // ShowAccount
        Console.WriteLine($"[ServidorCS] Cuenta '{cuenta}' — {chars.Count} personajes enviados");
    }

    /// <summary>
    /// BorrarPersonaje (Protocol.bas:18432, ProcesosLogin tipo 8) 1:1. Valida la contraseña de la
    /// cuenta, borra el .chr y lo saca de [PJS] (compacta + decrementa), y reenvía la lista (AddPj).
    /// El personaje debe pertenecer a la cuenta (protección anti-borrado de PJ ajeno).
    /// </summary>
    public static void BorrarPersonaje(Connection conn, string cuenta, string passwordPlano, string charName)
    {
        cuenta = cuenta.Trim().ToUpperInvariant();
        if (!ValidarCuenta(cuenta, passwordPlano, out string error))
        {
            ServerPackets.ConsoleMsg(conn, error, 3);
            return;
        }
        if (string.IsNullOrWhiteSpace(charName) || !CuentaTienePersonaje(cuenta, charName))
        {
            ServerPackets.ConsoleMsg(conn, "Personaje no encontrado.", 3);
            return;
        }

        string chrFile = Path.Combine(CharLoader.CharPath, charName.ToUpperInvariant() + ".chr");
        if (!File.Exists(chrFile)) { ServerPackets.ConsoleMsg(conn, "Personaje no encontrado.", 3); return; }

        try { File.Delete(chrFile); }
        catch (Exception ex) { Console.WriteLine($"[BorrarPersonaje] No se pudo borrar {chrFile}: {ex.Message}"); ServerPackets.ConsoleMsg(conn, "Error al borrar el personaje.", 3); return; }

        // Borrar también los macros del personaje: si queda el .mac huérfano, un PJ nuevo
        // creado con el mismo nombre heredaría los macros del personaje borrado.
        Macros.Delete(charName);

        // Quitar de [PJS]: compactar y decrementar NumPjs (lectura con IniFile, escritura con IniDocument).
        string cntFile = Path.Combine(AccountPath, cuenta + ".cnt");
        var ini = new IniFile(cntFile);
        int num = ini.GetInt("PJS", "NumPjs");
        int slot = 0;
        for (int i = 1; i <= num; i++)
            if (string.Equals(ini.Get("PJS", "PJ" + i).Trim(), charName.Trim(), StringComparison.OrdinalIgnoreCase)) { slot = i; break; }
        if (slot > 0)
        {
            var doc = new IniDocument(cntFile);
            for (int i = slot; i < num; i++) doc.Set("PJS", "PJ" + i, ini.Get("PJS", "PJ" + (i + 1)));
            doc.Set("PJS", "PJ" + num, "");
            doc.Set("PJS", "NumPjs", (num - 1).ToString());
            doc.Save(cntFile);
        }

        Console.WriteLine($"[BorrarPersonaje] cuenta='{cuenta}' borró PJ '{charName}'");

        // Reenviar la lista actualizada (sin AbrirFormularios, igual que el VB6).
        var chars = new List<ServerPackets.AccountChar>();
        var ini2 = new IniFile(cntFile);
        int n2 = ini2.GetInt("PJS", "NumPjs");
        for (int i = 1; i <= n2; i++)
        {
            string name = ini2.Get("PJS", "PJ" + i).Trim();
            if (!string.IsNullOrEmpty(name) && !name.Equals("nada", StringComparison.OrdinalIgnoreCase))
                chars.Add(BuildAccountChar(name));
        }
        ServerPackets.AddPj(conn, cuenta, chars.ToArray());
    }

    /// <summary>Lee el resumen de un personaje desde su .chr para la pantalla de selección.</summary>
    private static ServerPackets.AccountChar BuildAccountChar(string name)
    {
        var c = new ServerPackets.AccountChar { Name = name, LastSeen = "" };
        string chr = Path.Combine(CharLoader.CharPath, name.ToUpperInvariant() + ".chr");
        var ini = new IniFile(chr);
        if (ini.Loaded)
        {
            c.Head   = (short)ini.GetInt("INIT", "Head");
            c.Body   = (short)ini.GetInt("INIT", "Body");
            c.Casco  = (short)ini.GetInt("INIT", "Casco");
            c.Weapon = (short)ini.GetInt("INIT", "Arma");
            c.Shield = (short)ini.GetInt("INIT", "Escudo");
            c.Clase  = (byte)ini.GetInt("INIT", "Clase");
            c.Nivel  = (byte)ini.GetInt("STATS", "ELV");
            var pos = ini.Get("INIT", "Position").Split('-');
            c.Mapa = pos.Length == 3 && short.TryParse(pos[0], out var m) ? m : (short)1;

            // Auras de los objetos equipados (para mostrarlas en la tarjeta de selección).
            // Las claves *EqpSlot las escribe CharSaver; cada Obj<slot> = "ObjIndex-Amount-Equipped".
            c.ArmaAura   = AuraDeSlot(ini, "WeaponEqpSlot");
            if (c.ArmaAura == 0) c.ArmaAura = AuraDeSlot(ini, "NudiEqpSlot"); // nudillos van a la ranura de arma
            c.BodyAura   = AuraDeSlot(ini, "ArmourEqpSlot");
            c.EscudoAura = AuraDeSlot(ini, "EscudoEqpSlot");
            c.HeadAura   = AuraDeSlot(ini, "CascoEqpSlot");
            c.AnilloAura = AuraDeSlot(ini, "AnilloSlot");
            if (c.AnilloAura == 0) c.AnilloAura = AuraDeSlot(ini, "MagicSlot"); // ítem mágico también da aura de anillo

            // El [INIT] guarda SIEMPRE la apariencia a pie (CharSaver.AparienciaAPie). Si el PJ se
            // deslogueó montado/navegando, la tarjeta de selección debe mostrarlo tal cual se veía en
            // el render (montado/en barca), igual que reconstruye CharLoader al reloguear.
            if (ini.GetInt("FLAGS", "Navegando") == 1)
            {
                int barco = ObjIndexDeSlot(ini, "BarcoSlot");
                if (barco > 0)
                {
                    int rop = ObjData.Get(barco).Ropaje;
                    c.Body = (short)(rop > 0 ? rop : 87);
                    c.Head = 0; c.Weapon = 0; c.Shield = 0; c.Casco = 0;
                }
            }
            else if (ini.GetInt("FLAGS", "Montando") != 0)
            {
                int montura = ObjIndexDeSlot(ini, "MonturaSlot");
                if (montura > 0)
                {
                    c.Body = (short)ObjData.Get(montura).Ropaje;
                    c.Weapon = 0; // montado: sin arma a la vista (DoEquita)
                }
            }
        }
        return c;
    }

    /// <summary>ObjIndex del objeto equipado en el slot indicado por la clave de [Inventory], o 0.</summary>
    private static int ObjIndexDeSlot(IniFile ini, string slotKey)
    {
        int slot = ini.GetInt("Inventory", slotKey);
        if (slot <= 0) return 0;
        var parts = ini.Get("Inventory", "Obj" + slot).Split('-');
        return parts.Length >= 1 && int.TryParse(parts[0], out int oi) && oi > 0 ? oi : 0;
    }

    /// <summary>Devuelve el aura (obj.dat "Aura") del objeto equipado en el slot indicado por la clave, o 0.</summary>
    private static byte AuraDeSlot(IniFile ini, string slotKey)
    {
        int slot = ini.GetInt("Inventory", slotKey);
        if (slot <= 0) return 0;
        var parts = ini.Get("Inventory", "Obj" + slot).Split('-');
        if (parts.Length < 1 || !int.TryParse(parts[0], out int objIndex) || objIndex <= 0) return 0;
        return (byte)ObjData.Get(objIndex).Aura;
    }
}
