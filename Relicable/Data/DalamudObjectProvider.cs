using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace Relicable.Data;

// Live implementation of IObjectProvider backed by Dalamud's object table and
// targeting. The interface is the seam that keeps the targeting logic testable
// against a synthetic table (see Data/Targeting.cs).
public sealed class DalamudObjectProvider : IObjectProvider
{
    public IEnumerable<IGameObject> Objects => Plugin.ObjectTable;

    public Vector3 PlayerPosition => Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;

    public void SetTarget(IGameObject obj)
    {
        Plugin.TargetManager.Target = obj;
    }
}
