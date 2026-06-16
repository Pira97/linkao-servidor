namespace ServidorCS.Game;

/// <summary>
/// Datos de las ciudades (Ciudades.dat) — CargarCiudades (FileIO.bas:2428). Por ciudad:
/// posición de spawn (Map/X/Y) y posición de muerto/cementerio (Dead_Map/X/Y), usadas por
/// el sistema de Hogar (/hogar) y resucitación. Indexado por eCiudad (1..15).
/// </summary>
public static class CityData
{
    public struct City { public short Map, X, Y, DeadMap, DeadX, DeadY; }

    // Índice eCiudad → sección en Ciudades.dat (FileIO.bas:2604-2618).
    private static readonly string[] _section =
    {
        "",            // 0 sin usar
        "NIX",         // 1 cNix
        "ILLIANDOR",   // 2 cIlliandor
        "ULLATHORPE",  // 3 cUllathorpe
        "BANDERBILL",  // 4 cBanderbill
        "RINKEL",      // 5 cRinkel
        "DUNGEONNEWBIE", // 6 cDungeonNewbie
        "LINDOS",      // 7 cLindos
        "ARGHAL",      // 8 cARGHAL
        "TIAMA",       // 9 cTIAMA
        "ORAC",        // 10 cORAC
        "SURAMEI",     // 11 cSURAMEI
        "NUEVA",       // 12 cNueva
        "PRISION",     // 13 cPrision
        "LIBERTAD",    // 14 cLibertad
        "INTERMUNDIA", // 15 cIntermundia
    };

    private static City[] _cities;

    public static City Get(int ciudad)
    {
        EnsureLoaded();
        if (ciudad < 1 || ciudad >= _cities.Length) ciudad = 2; // default Illiandor
        return _cities[ciudad];
    }

    private static void EnsureLoaded()
    {
        if (_cities != null) return;
        _cities = new City[_section.Length];
        string file = FindFile();
        if (file == null) return;
        var ini = new IniFile(file);
        for (int i = 1; i < _section.Length; i++)
        {
            string s = _section[i];
            _cities[i] = new City
            {
                Map = (short)ini.GetInt(s, "MAPA"),
                X = (short)ini.GetInt(s, "X"),
                Y = (short)ini.GetInt(s, "Y"),
                DeadMap = (short)ini.GetInt(s, "MAPA_DEAD"),
                DeadX = (short)ini.GetInt(s, "X_DEAD"),
                DeadY = (short)ini.GetInt(s, "Y_DEAD"),
            };
        }
    }

    private static string FindFile()
    {
        foreach (var c in new[]
        {
            System.IO.Path.Combine(DataPaths.Sub("Dat"), "Ciudades.dat"),
            System.IO.Path.Combine(System.AppContext.BaseDirectory, "Dat", "Ciudades.dat"),
        })
            if (System.IO.File.Exists(c)) return c;
        return null;
    }
}
