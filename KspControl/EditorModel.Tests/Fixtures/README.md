# Twin fixtures

Craft files saved by KSP 1.12.5 on the live LunaMP modpack (about 60 modules per part, including MechJeb, TweakScale,
B9PartSwitch, KSP-Recall AttachedOnEditor, Waterfall, KIS and ModuleCryoTank). They are our own creations from stock parts.

| File | Origin |
|---|---|
| `S0a-manual.craft` | T1 twin (mk1pod.v2 / fuelTankSmall / liquidEngine.v2) built by hand in the VAB and saved with the KSP Save button. Source of the T1 staging rules. |
| `S0a-saved1.craft` | KSP `SaveShip()` output after loading our hand-written structural file through the craft browser (browser-path, provisional evidence). |
| `S0a2-saved.craft` | Same, after the structural file carried the `_modVersions` header (33-log 18:51). |

Tests treat module sets, module values and `_modVersions` as data: they assert planner and staging-rule reproduction and
comparator equality against these files, never a specific module inventory. When the modpack changes, regenerate the
fixtures (P2.0/P2.5); the content is not portable between modpacks.

Stock Squad craft (Orbiter One, GDLV3) are never committed; tests that want them read the installed game and call
`Assert.Inconclusive` when it is absent.
