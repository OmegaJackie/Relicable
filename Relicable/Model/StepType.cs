namespace Relicable.Model;

// The full step vocabulary required by the ARR relic line. Each value has a
// matching ITaskExecutor in the Steps folder, keyed by its Handles property.
public enum StepType
{
    AetheryteTeleport,
    AethernetTravel,
    MoveTo,
    MoveToFlag,
    KillTarget,
    ParticipateFate,
    StartLeve,
    TurnInLeve,
    EnterDuty,
    InteractNpc,
    TurnInItems,
    UseItem,
    UpgradeRelic,
    MeldMateria,
    MeldNovusRoute,
    WaitForCondition,
    RunTreasureMaps,
    AttachMahatma,
    EnsureRelicEquipped,
    BuyRelicBook,
    AtmaUpgrade,
    AnimusUpgrade,

    // Novus stage, final step: with the Sphere Scroll at its cap (75/75, or Paladin's 53 + 22), turn
    // the UNEQUIPPED Animus weapon and the filled scroll in at Jalzahn's "Relic Weapon Animus
    // Enhancement" branch (CustomTalk 721069) for the il115 Novus weapon.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    NovusUpgrade,

    NexusUpgrade,
    BuyRadzOil,

    // Braves (il125) material-quest report/turn-in: teleport to the quest's NPC (Papana / Guiding
    // Star / Adkin / Brangwine) and hand over the obtained dungeon batch to advance the quest to its
    // next batch. Completes when the quest sequence advances. Data from BravesData.TurnInNpc.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    BravesReport,

    // Braves stage entry: travel to a stage quest's giver and ACCEPT it -- the umbrella "Wherefore Art
    // Thou, Zodiac" from Jalzahn, then the four material quests. Without this a fresh Nexus weapon has
    // no accepted quest, so no dungeon is requested and the stage has no work at all.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    AcceptBravesQuest,

    // Braves stage: drive a summoning bell and pull the material-quest items you already own out of
    // your retainers (the vendor/crafted "Perfect ..." pieces habitually live there). Wants
    // Configuration.AutoWithdrawFromRetainers.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    FetchBravesMaterials,

    // Find a world OBJECT by name (and/or DataId) near an authored position and interact with
    // it -- a quest coffer, a lever, a marker. Distinct from InteractNpc because the finder is
    // name-driven and ObjectKind-tolerant, and because the approach must walk fully ONTO the
    // object (its origin sits above the floor, so a 3D range gate never closes).
    // NOTE: a new member here MUST also be registered in Plugin.cs's executor list, or the
    // controller throws "No executor registered" the moment the step runs.
    InteractObject,

    // Zenith step 1: put the Thavnairian Mist the Furnace trade costs in the bags -- count the
    // bags, refuse to double-buy what a retainer is holding, then buy the shortfall from Auriana
    // at Revenant's Toll (the relic materials sit under her "Special Arms" exchange, not the
    // default gear grid). Skips the trip entirely when the mist is already held.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    AcquireZenithMist,

    // Zenith step 2: trade the finished bare base relic + its mist at the Furnace beside Gerolt
    // (Hyrstmill, North Shroud) for the il90 "<weapon> Zenith" form. One trade per weapon -- the
    // Paladin's Curtana and Holy Shield are two separate entries.
    // NOTE: like every member, this MUST be registered in Plugin.cs's executor list.
    ZenithTrade,
}
