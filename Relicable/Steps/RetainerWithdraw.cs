using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Relicable.Diagnostics;

namespace Relicable.Steps;

// Retrieves an item from the open retainer's bags into the player's inventory by driving
// the GAME'S OWN retainer item-command function -- the exact code path the right-click
// "Retrieve from Retainer" uses. This is how AutoRetainer and AllaganTools do it.
//
// A raw InventoryManager.MoveItemSlot for a retainer<->player transfer access-violates
// (C0000005): the retainer bag is a server-authoritative container, and the low-level
// move helper dereferences container/agent state that is only valid mid-transfer. The
// native command instead validates state, finds the destination slot, merges stacks, and
// sends the proper packet, so there is nothing for us to corrupt.
//
// Requires: at a summoning bell with the retainer's "Entrust or withdraw items" window
// (InventoryRetainer / InventoryRetainerLarge) open and AgentRetainer active. When those
// are not present every method here is a safe no-op (returns false; never crashes).
internal static unsafe class RetainerWithdraw
{
    // Native "AgentRetainer item command" (Entrust / Retrieve / Sell). The signature is
    // the one AutoRetainer scans (Internal/Memory.cs). It is game-version dependent; if it
    // no longer resolves the scan returns 0 and every retrieve becomes a safe no-op.
    private const string RetainerItemCommandSig =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 30 48 8B 5C 24 ?? 41 8B F0";

    private enum RetainerItemCommand : long
    {
        RetrieveFromRetainer = 0, // retainer bag -> player
        EntrustToRetainer = 1,    // player -> retainer (whole stack)
        EntrustQuantity = 4,      // player -> retainer (partial; opens InputNumeric)
        HaveRetainerSellItem = 5,
    }

    // void (AgentRetainer item-command module, source slot, source container, a4=0, command).
    private static delegate* unmanaged<nint, uint, InventoryType, uint, long, void> _fn;
    private static bool _scanned;
    private static string? _scanError;

    // Force the signature scan at plugin load and log the outcome once.
    //
    // Left purely lazy, Resolve() only ran the first time a retainer item window was ALREADY
    // open -- i.e. mid-run, deep inside a Novus material restock -- so a signature broken by a
    // game patch surfaced as "the restock quietly stopped pulling from retainers" hours in,
    // rather than as a version problem at load. Probing here turns that into one loud line
    // before anything depends on it. A sig scan only reads the module image, so this is safe
    // off the framework tick.
    public static bool ProbeSignature() => Resolve() != null;

    private static readonly string[] ItemWindowAddons = { "InventoryRetainer", "InventoryRetainerLarge" };

    // True when the retainer's item-transfer window is open and visible -- the state the
    // native retrieve requires. Distinct from a retainer merely being entered at the bell.
    public static bool IsItemWindowOpen()
    {
        foreach (var name in ItemWindowAddons)
        {
            var ptr = Plugin.GameGui.GetAddonByName(name, 1);
            if (!ptr.IsNull && ((AtkUnitBase*)ptr.Address)->IsVisible)
                return true;
        }
        return false;
    }

    // Retrieve the WHOLE stack in (page, slot) of the open retainer into the player bags.
    // The game picks the destination slot and merges into existing player stacks. Returns
    // false (safe no-op) unless the retainer item window is open and its agent is active.
    public static bool TryRetrieveSlot(InventoryType page, ushort slot)
    {
        try
        {
            if (!IsItemWindowOpen())
                return false;

            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return false;
            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            if (agent == null || !agent->IsAgentActive())
                return false;

            var fn = Resolve();
            if (fn == null)
                return false;

            // The command operates on the AgentRetainer item-command sub-module (agent + 0x28).
            var commandModule = (nint)agent + 40;
            fn(commandModule, slot, page, 0, (long)RetainerItemCommand.RetrieveFromRetainer);
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Retainer retrieve failed: {ex.Message}");
            return false;
        }
    }

    // Dismiss the currently-summoned retainer, returning to the summoning bell's retainer list, by
    // hiding the Retainer agent -- the exact call AutoRetainer uses to move between retainers
    // (RetainerHandlers.CloseAgentRetainer). Hiding the agent closes both the retainer's action menu
    // and its item window at once. Returns false (safe no-op) when no retainer is active.
    public static bool CloseRetainerAgent()
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
                return false;
            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            if (agent == null || !agent->IsAgentActive())
                return false;
            agent->Hide();
            return true;
        }
        catch (Exception ex)
        {
            DebugLog.Warn($"Retainer dismiss failed: {ex.Message}");
            return false;
        }
    }

    private static delegate* unmanaged<nint, uint, InventoryType, uint, long, void> Resolve()
    {
        if (_scanned)
            return _fn;
        _scanned = true;
        try
        {
            // ScanText throws when the pattern is absent, but do not rely on that alone:
            // a zero address would otherwise be called as a function pointer.
            var addr = Plugin.SigScanner.ScanText(RetainerItemCommandSig);
            if (addr == 0)
                _scanError = "pattern not found (scanner returned 0)";
            else
                _fn = (delegate* unmanaged<nint, uint, InventoryType, uint, long, void>)addr;
        }
        catch (Exception ex)
        {
            _scanError = ex.Message;
            _fn = null;
        }
        Report();
        return _fn;
    }

    // Said exactly once, on whichever path scans first (ProbeSignature at load, or a lazy
    // first use). Warn, not Verbose, because DebugLog.Warn is the only level that emits with
    // the debug toggle off -- a broken signature is a silent feature loss otherwise, and the
    // message has to name the consequence AND the fix, since the fix lives in another repo.
    private static void Report()
    {
        if (_fn != null)
        {
            DebugLog.Info("Retainer item-command signature resolved; retainer withdrawal available.");
            return;
        }
        DebugLog.Warn(
            "Retainer item-command signature did not resolve -- retainer withdrawal is DISABLED for this " +
            "session, so the Novus material restock will not pull from retainers (it falls back to buying). " +
            "This is the expected symptom of a game patch moving the function: re-copy the signature from " +
            "AutoRetainer's Internal/Memory.cs." +
            (_scanError is null ? string.Empty : $" Scanner said: {_scanError}"));
    }
}
