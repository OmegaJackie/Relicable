# Relicable — copyright and third-party notices

Relicable
Copyright (C) 2026 OmegaJackie

This program is free software: you can redistribute it and/or modify it under the
terms of the GNU Affero General Public License as published by the Free Software
Foundation, either version 3 of the License, or (at your option) any later version.
See [LICENSE](LICENSE) for the full text.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU Affero General Public License for more details.

## Third-party components

### ECommons — MIT
<https://github.com/NightmareXIV/ECommons>
Copyright (c) 2023 NightmareXIV

ECommons is **not** vendored in this repository. It is cloned into the repository
root at build time (see BUILDING.md) and is gitignored. `ECommons.dll` ships inside
the release zip, so its MIT notice must travel with that binary.

### RotationSolverReborn — LGPL-3.0
<https://github.com/FFXIV-CombatReborn/RotationSolverReborn>
Copyright (c) the RotationSolverReborn contributors

`RelicBurstRotations/Rotations/NIN_IfritEX.cs` contains a trimmed copy of RSR's
shipped `NIN_Reborn` mudra state machine, and a verbatim copy of its `DoTenChiJin`
AdjustId probes — both are marked as such in that file's own comments. Those
portions remain licensed under LGPL-3.0 by their authors. Relicable as a whole is
conveyed under AGPL-3.0; the incorporated LGPL-licensed portions remain governed by
their own license.

### XIVAPI-GUI — MIT
<https://github.com/OmegaJackie/XIVAPI-GUI>
Copyright (c) 2026 OmegaJackie

`tools/xivapi_client.py` is trimmed from that project (same author).
