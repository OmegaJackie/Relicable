using System;
using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Resolves the data needed to auto-buy the next Trials of the Braves ("Animus") book from
// G'Jusana in Mor Dhona, so the engine can advance from a finished book to the next one
// instead of stopping. Mirrors ZetaData (authored NPC map coord -> world position) and
// NovusData (resolve an ENpcResident by name), with everything derived at runtime.
//
// G'Jusana stands at Rowena's House of Splendors in Revenant's Toll (Mor Dhona), by the
// aetheryte, so teleporting to Revenant's Toll lands the player next to her. The books are
// granted -- and become the active Relic Note -- on purchase, so no separate "activate" step
// is needed; completion is simply that RelicNote.RelicNoteId advanced.
//
// SEAM: the map coordinate is authored and only approximate (VERIFY in-game). It is just an
// approach anchor -- NpcInteractor homes on the loaded NPC once she streams into the object
// table, which she does on arrival at the aetheryte, so precision is not critical.
public static class AnimusBookData
{
    // Fallback if the place-name lookup fails: the Mor Dhona overworld TerritoryType.
    private const uint MorDhonaTerritoryFallback = 156;
    private const float GJusanaMapX = 22.4f;
    private const float GJusanaMapY = 6.7f;

    private static bool _resolved;
    private static uint _territory;
    private static uint _gJusanaNpcId;
    private static uint _aetheryte;
    private static Vector3? _pos;

    // G'Jusana's ENpcResident id (resolved by name), or 0 if unresolved.
    public static uint GJusanaNpcId { get { Ensure(); return _gJusanaNpcId; } }

    // Teleportable aetheryte row id for Mor Dhona (Revenant's Toll), or 0 if unresolved.
    public static uint MorDhonaAetheryte { get { Ensure(); return _aetheryte; } }

    // G'Jusana's world position for navigation, or null if the conversion failed.
    public static Vector3? GJusanaPosition { get { Ensure(); return _pos; } }

    // The next book to buy after `currentBook` (the just-finished active note), as a
    // (RelicNote row id, book name) pair, or (0, "") when there is no next book row (the
    // last book is done -- the final Animus weapon upgrade is a separate step). The name is
    // the book's EventItem name (e.g. "Book of Skyfire II"), used to pick it in G'Jusana's menu.
    public static (uint Book, string Name) NextBook(uint currentBook)
    {
        try
        {
            var sheet = Plugin.DataManager.GetExcelSheet<RelicNote>();
            var nextId = currentBook + 1;
            if (sheet.GetRowOrDefault((uint)nextId) is not { } r || r.RowId == 0)
                return (0, string.Empty);
            var name = r.EventItem.ValueNullable?.Name.ExtractText() ?? string.Empty;
            return ((uint)nextId, name);
        }
        catch { return (0, string.Empty); }
    }

    private static void Ensure()
    {
        if (_resolved)
            return;
        _resolved = true;

        _territory = ResolveMorDhonaTerritory();
        _aetheryte = Locations.AetheryteForTerritory(_territory);

        try
        {
            foreach (var npc in Plugin.DataManager.GetExcelSheet<ENpcResident>())
            {
                if (Fold(npc.Singular.ExtractText()).Equals("G'Jusana", StringComparison.OrdinalIgnoreCase))
                {
                    _gJusanaNpcId = npc.RowId;
                    break;
                }
            }

            var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(_territory);
            if (tt is { } t)
            {
                // MapLinkPayload converts the human map coordinate to the raw position using
                // the map's SizeFactor/Offset; world = Raw / 1000 (as in BraveBookPositions).
                // Y is 0; vnavmesh resolves the floor height.
                var link = new MapLinkPayload(_territory, t.Map.RowId, GJusanaMapX, GJusanaMapY);
                _pos = new Vector3(link.RawX / 1000f, 0f, link.RawY / 1000f);
            }
        }
        catch
        {
            _pos = null;
        }

        if (_gJusanaNpcId == 0)
            Plugin.Log.Warning("Relicable: could not resolve the 'G'Jusana' NPC id (Animus book auto-buy); " +
                               "book auto-advance will stop with guidance instead.");
        if (_aetheryte == 0)
            Plugin.Log.Warning("Relicable: could not resolve the Revenant's Toll aetheryte for the Animus book auto-buy.");
    }

    // The Mor Dhona overworld TerritoryType, resolved by place name (robust to id changes),
    // falling back to the known overworld id if the lookup fails.
    private static uint ResolveMorDhonaTerritory()
    {
        try
        {
            foreach (var t in Plugin.DataManager.GetExcelSheet<TerritoryType>())
            {
                var pn = t.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                if (pn.Equals("Mor Dhona", StringComparison.OrdinalIgnoreCase))
                    return t.RowId;
            }
        }
        catch { /* fall through to the fallback */ }
        return MorDhonaTerritoryFallback;
    }

    // Fold the typographic single quotes to ASCII so "G'Jusana" matches whichever apostrophe
    // glyph the sheet uses (the same posture BaseRelicCatalog takes for item names).
    private static string Fold(string s)
        => (s ?? string.Empty).Replace('’', '\'').Replace('‘', '\'').Trim();
}
