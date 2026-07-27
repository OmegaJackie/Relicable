using System;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Resolves the Novus-stage item and action ids from Lumina by name so the data files
// and executors do not hardcode numeric ids. English names; resolved once and cached.
//
// The relic treasure maps can appear as a normal Item or as a Key Item (EventItem),
// under either "Mysterious Map" (undeciphered) or "Alexandrite Map" (deciphered), so
// every combination is resolved and the caller checks both containers. Decipher and
// Dig are General actions that operate on the held map / current location.
public static class NovusData
{
    public static uint AlexandriteItemId { get { Ensure(); return _alexandrite; } }
    public static uint DigGeneralActionId { get { Ensure(); return _dig; } }
    public static uint DecipherGeneralActionId { get { Ensure(); return _decipher; } }

    // Auriana (Revenant's Toll, Mor Dhona) sells Mysterious Maps for Poetics via her
    // "Mysterious Map Exchange". Data id matches the object-table BaseId.
    public static uint AurianaDataId { get { Ensure(); return _auriana; } }

    // Radz-at-Han Quenching Oil (Item 6267): the base-relic FINAL turn-in item, bought from Auriana
    // for 15 Poetics via her tomestone (Poetics) exchange -- same NPC as the Mysterious Maps, which is
    // why it is resolved here. Used by BuyRadzOilExecutor / the base-relic oil turn-in objective.
    public static uint RadzOilItemId { get { Ensure(); return _radzOil; } }

    // A standing spot right in FRONT of Auriana's market stall (user-authored map coord),
    // converted to a world position. Homing on her exact object position paths AROUND behind
    // the stall, so the restock approach targets this instead. Null if the conversion failed
    // (the interactor then falls back to a computed player-side offset).
    public static Vector3? AurianaApproachPosition { get { Ensure(); return _aurianaApproach; } }

    // Auriana's approach spot, captured in-game as the EXACT vnavmesh WORLD position and used verbatim
    // -- no map-coordinate conversion, which rounds to 0.1 map units (~5 yalms) and staged a couple of
    // yalms off. Y is the real height but only advisory: the interactor / vnavmesh resolve the floor.
    // (Was the authored map coord (22.7, 6.7) in Mor Dhona, Territory 156.)
    private static readonly Vector3 AurianaApproachWorld = new(63.143f, 31.288f, -737.786f);
    private static Vector3? _aurianaApproach;

    // Treasure maps. *ItemId = normal inventory (Item sheet); *KeyId = Key Items
    // (EventItem sheet). Any of these may be 0 if that form does not exist.
    public static uint MysteriousMapItemId { get { Ensure(); return _mysMapItem; } }
    public static uint AlexandriteMapItemId { get { Ensure(); return _alexMapItem; } }
    public static uint MysteriousMapKeyId { get { Ensure(); return _mysMapKey; } }
    public static uint AlexandriteMapKeyId { get { Ensure(); return _alexMapKey; } }

    private static uint _alexandrite, _dig, _decipher, _mysMapItem, _alexMapItem, _mysMapKey, _alexMapKey, _auriana, _radzOil;
    private static bool _resolved;

    private static void Ensure()
    {
        if (_resolved)
            return;
        _resolved = true;

        try
        {
            foreach (var item in Plugin.DataManager.GetExcelSheet<Item>())
            {
                var n = item.Name.ExtractText();
                if (_alexandrite == 0 && Eq(n, "Alexandrite")) _alexandrite = item.RowId;
                else if (_mysMapItem == 0 && Eq(n, "Mysterious Map")) _mysMapItem = item.RowId;
                else if (_alexMapItem == 0 && Eq(n, "Alexandrite Map")) _alexMapItem = item.RowId;
                else if (_radzOil == 0 && Eq(n, "Radz-at-Han Quenching Oil")) _radzOil = item.RowId;
            }

            foreach (var ev in Plugin.DataManager.GetExcelSheet<EventItem>())
            {
                var n = ev.Name.ExtractText();
                if (_mysMapKey == 0 && Eq(n, "Mysterious Map")) _mysMapKey = ev.RowId;
                else if (_alexMapKey == 0 && Eq(n, "Alexandrite Map")) _alexMapKey = ev.RowId;
            }

            foreach (var ga in Plugin.DataManager.GetExcelSheet<GeneralAction>())
            {
                var n = ga.Name.ExtractText();
                if (_dig == 0 && Eq(n, "Dig")) _dig = ga.RowId;
                else if (_decipher == 0 && Eq(n, "Decipher")) _decipher = ga.RowId;
                if (_dig != 0 && _decipher != 0) break;
            }

            foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcResident>())
            {
                if (Eq(npc.Singular.ExtractText(), "Auriana"))
                {
                    _auriana = npc.RowId;
                    break;
                }
            }

            // Auriana's approach spot is the exact vnav world position (see AurianaApproachWorld).
            _aurianaApproach = AurianaApproachWorld;

            if (_alexandrite == 0)
                Plugin.Log.Warning("Relicable: could not resolve the 'Alexandrite' item id");
            if (_mysMapItem == 0 && _alexMapItem == 0 && _mysMapKey == 0 && _alexMapKey == 0)
                Plugin.Log.Warning("Relicable: could not resolve any treasure map id (Mysterious Map / Alexandrite Map)");
            if (_dig == 0)
                Plugin.Log.Warning("Relicable: could not resolve the 'Dig' general action id");
            if (_decipher == 0)
                Plugin.Log.Warning("Relicable: could not resolve the 'Decipher' general action id");
            if (_auriana == 0)
                Plugin.Log.Warning("Relicable: could not resolve the 'Auriana' NPC id (map restock)");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning($"Relicable: Novus data resolution failed: {ex.Message}");
        }
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
