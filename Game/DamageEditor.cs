using ServidorCS.Network;

namespace ServidorCS.Game;

/// <summary>
/// Preview de daño mágico para el editor de balance (GM panel, pestaña "Daño"). NO edita datos por
/// sí mismo: la pestaña reutiliza SpellEditorList/ObjEditorList para poblarse y SpellEditorSave/
/// ObjEditorSave (ya existentes) para persistir cambios en Hechizos.dat/obj.dat. Esta clase sólo
/// calcula el desglose de daño con Combat.PreviewSpellDamage, la misma fórmula que usa el combate real.
/// </summary>
public static class DamageEditor
{
    private const byte MIN_PRIV = AdminLoader.STATUS_SEMIDIOS;

    private static User ValidarGM(Connection conn)
    {
        var u = UserListManager.UserList[conn.UserIndex];
        if (u == null || !u.flags.UserLogged) return null;
        if (AdminLoader.GetFaccionStatus(u.Name) < MIN_PRIV)
        {
            ServerPackets.ConsoleMsg(conn, "No tenés privilegios para usar el editor de daño.", 6);
            return null;
        }
        return u;
    }

    /// <summary>spellIndex/staffObjIndex &lt;= 0 se ignoran (sin hechizo/báculo seleccionado todavía).</summary>
    public static void Preview(Connection conn, int spellIndex, int staffObjIndex, int casterLevel,
        int casterINT, bool isPvP, int targetResistencia)
    {
        var u = ValidarGM(conn);
        if (u == null) return;
        var sp = spellIndex > 0 ? SpellData.Get(spellIndex) : default;
        if (string.IsNullOrEmpty(sp.Nombre))
        {
            ServerPackets.ConsoleMsg(conn, $"Índice de hechizo inválido: {spellIndex}.", 6);
            return;
        }

        int magnitud = (sp.MinHP + sp.MaxHP) / 2;
        int staffBonus = 0;
        if (staffObjIndex > 0 && staffObjIndex <= ObjData.Count)
        {
            var staff = ObjData.Get(staffObjIndex);
            if (staff.EfectoMagico == 14) staffBonus = staff.CuantoAumento;
        }

        var b = Combat.PreviewSpellDamage(sp, magnitud, casterLevel, casterINT, staffBonus, isPvP, u.raza,
            targetResistencia);
        ServerPackets.DamageEditorPreviewResult(conn, b);
    }
}
