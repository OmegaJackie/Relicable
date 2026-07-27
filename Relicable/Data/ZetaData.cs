using System.Numerics;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Lumina.Excel.Sheets;

namespace Relicable.Data;

// Resolves the Zeta-stage NPC navigation data once, from Lumina plus the authored
// Remon map coordinate, so it is not hand-authored as static numbers in the objective
// JSON (the world position needs a MapLinkPayload conversion and the aetheryte row id
// is derived from the territory).
//
// Remon: ENpcResident 1011791, Swiftperch (Western La Noscea, TerritoryType 138), map
// (34.3, 31.7). He stands on the Swiftperch aetheryte, so teleporting there lands the
// player next to him. Verified via XIVAPI and the FFXIV wiki.
public static class ZetaData
{
    public const uint RemonNpcId = 1011791;
    public const uint RemonTerritory = 138;
    private const float RemonMapX = 34.3f;
    private const float RemonMapY = 31.7f;

    private static bool _resolved;
    private static uint _remonAetheryte;
    private static Vector3? _remonPos;

    // Teleportable aetheryte row id next to Remon (Swiftperch), or 0 if unresolved.
    public static uint RemonAetheryte { get { Ensure(); return _remonAetheryte; } }

    // Remon's world position for navigation, or null if the conversion failed.
    public static Vector3? RemonPosition { get { Ensure(); return _remonPos; } }

    private static void Ensure()
    {
        if (_resolved)
            return;
        _resolved = true;

        _remonAetheryte = Locations.AetheryteForTerritory(RemonTerritory);

        try
        {
            var tt = Plugin.DataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(RemonTerritory);
            if (tt is { } t)
            {
                // MapLinkPayload converts the human map coordinate to the raw position
                // using the map's SizeFactor/Offset; world = Raw / 1000 (as in
                // BraveBookPositions). Y is 0; vnavmesh resolves the floor height.
                var link = new MapLinkPayload(RemonTerritory, t.Map.RowId, RemonMapX, RemonMapY);
                _remonPos = new Vector3(link.RawX / 1000f, 0f, link.RawY / 1000f);
            }
        }
        catch
        {
            _remonPos = null;
        }

        if (_remonAetheryte == 0)
            Plugin.Log.Warning("Relicable: could not resolve the Swiftperch aetheryte for the Zeta Mahatma attach; the attach step will rely on navigation only.");
    }
}
