namespace Relicable.Model;

// How the controller picks which stage to work. Auto keeps the original behaviour
// (the lowest incomplete stage for the equipped relic); Manual pins work to a
// user-inserted stage so a farmable stage that was already "passed" can be revisited
// (for example farming more Atma or Alexandrite after the engine thinks Atma/Novus
// is complete).
public enum StageSelectionMode
{
    Auto,
    Manual,
}

// How the controller picks which KIND of Animus book work to do. Auto keeps the original
// behaviour (every kind is eligible, ordered enemies -> leves -> dungeons -> FATEs); Manual
// restricts the engine to the kinds ticked in BookWorkKinds, so a book can be worked without
// e.g. burning leve allowances or queueing dungeons.
public enum BookWorkSelectionMode
{
    Auto,
    Manual,
}

// The four kinds of Trials of the Braves book slot, as a flag set so Manual mode can enable any
// combination. Deliberately NOT the same type as CompletionKind: that enum covers every
// completion condition in the engine (item counts, gauges, upgrades), and only these four are
// user-selectable book work.
[System.Flags]
public enum BookWorkKinds
{
    None = 0,
    Enemies = 1 << 0,
    Leves = 1 << 1,
    Dungeons = 1 << 2,
    Fates = 1 << 3,
    All = Enemies | Leves | Dungeons | Fates,
}

// Market scope for Universalis price lookups. DataCenter is the default: cross-world
// cheapest across the player's data centre.
public enum UniversalisScope
{
    World,
    DataCenter,
    Region,
}

// Which Novus weapon is being melded. The per-stat caps, grade tiers, and meld
// success curve differ for healer weapons and for Paladin (which splits into two
// scrolls). Standard covers every Disciple of War weapon and the two non-healer
// caster weapons (Stardust Rod, The Veil of Wiyu).
public enum NovusWeaponProfile
{
    Standard,   // 75 points; per-stat cap 44 (Piety 31); tiers 11/11/11/11
    Healer,     // 75 points; Piety cap 44, Direct Hit cap 35 (base +9); others 44
    Paladin,    // Curtana 53 + Holy Shield 22; caps 31/13 (Piety 22/9)
}
