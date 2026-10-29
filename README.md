# Fermata Native FT Bridge

Runs avatars set up with **FermataFT** face tracking on **VRChat native face tracking**, without changing FermataFT's look or settings.

At upload, native tracking values are fed into FermataFT's OSCmooth smoothing layer, and FermataFT's OSC parameter sync (about 140–150 synced bits) is removed. VRChat syncs native face tracking itself, outside the synced-parameter budget.

This is an unofficial stopgap. It is not affiliated with FermataShop / CPMEDIA, and it contains no FermataFT files: it reads your own installed copy while the avatar is being built.

## Requirements

- Unity 2022.3, PC (Windows) avatars
- VRChat Avatars SDK with native face tracking (3.10.6-beta.1 or later)
- NDMF 1.15 or later and Modular Avatar (tested with 1.19)
- FermataFT installed on the avatar with FermataFT's installer (the prefab with the *FT Setting* component).
  Older installs that wrote FermataFT's layers straight into the avatar's FX controller are also recognized, on a best-effort basis.
- A face tracking app that sends VRChat native face tracking, such as a recent VRCFaceTracking

## Usage

1. Install FermataFT on your avatar as usual.
2. Drag `FermataNativeFT.prefab` onto the avatar, next to FermataFT's prefab.
3. Select it and read the inspector. It shows any problem it can find before you upload, and what it will change.
4. Upload as usual.

If the avatar has no FermataFT face tracking, the bridge warns and does nothing. If FermataFT is there but cannot be patched safely, the build stops with an error that explains why.

## What changes at upload

- FermataFT's `_OSCmooth_Binary_Gen` layer, which decoded the bit-packed sync, is removed.
- FermataFT's `v2/*` sync parameters are removed from the expression parameters. The `v2/*` inputs that OSCmooth reads stay as local (unsynced) parameters.
- Face tracking is enabled on the avatar descriptor (*Use All Tracking Keys*), with one *Forward to Animator* parameter behavior per input. Each behavior computes the same value VRCFaceTracking would have sent, from VRChat's tracking keys. Every client, local and remote, computes these values itself.

Everything else stays as FermataFT built it: OSCmooth smoothing (including its separate local and remote smoothing), the face blend trees, menus, toggles, gestures and their sync. The build checks this and stops if anything else would change.

The avatar's own files are never modified; all changes happen on NDMF's build copy.

## Known limits

- The remote copy of the face comes from VRChat's native sync, which loses detail with distance.
- FermataFT reads 30 of VRCFaceTracking's `v2/*` parameters. The formulas for those are checked; the rest of the table is transcribed from VRCFaceTracking and not tested in-game.

## Building

CI builds `FermataNativeFT-<version>.unitypackage` on every push. For `v*` tags it also attaches the package to a GitHub release. The package contains everything in this folder except this README, `NOTES.md` and the repository plumbing.

To build locally:

```
python .github/scripts/build_unitypackage.py --out <outside Assets>/FermataNativeFT.unitypackage
```

The repository does not track Unity `.meta` files. Each asset's GUID is the MD5 of its Unity path, so a file keeps its GUID across releases and an update replaces it in place. Renaming or moving a file gives it a new GUID. In a Unity checkout, run the script with `--sync-metas` after adding, renaming or moving files, so the project uses the same GUIDs as the package.

## License

MIT, see [LICENSE](LICENSE).

The `v2/*` formulas restate VRCFaceTracking's parameter definitions ([VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking), Apache-2.0).
FermataFT is a product of FermataShop / CPMEDIA and is not included or redistributed here.
