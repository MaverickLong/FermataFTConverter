# Fermata Native FT Bridge

Runs avatars set up with **FermataFT** face tracking on **VRChat native face tracking**, without changing FermataFT's look or settings.

At upload, native tracking values are fed into FermataFT's OSCmooth smoothing layer, and FermataFT's OSC parameter sync (about 140–150 synced bits) is removed. VRChat syncs native face tracking itself, outside the synced-parameter budget.

**By default, this is a temporary solution that only reduces synced parameters.** It still uses the original blendtree / OSCmooth, which means the animator / CPU load remains almost the same. Plan you gimmicks / synced parameters wisely -- you may end up with a very bad performing avatar if you carelessly add more. Fermata said they are working on a Native Face Tracking solution themselves -- you may soon no longer need this utility.

This is an unofficial stopgap. It is not affiliated with FermataShop / CPMEDIA, and it contains no FermataFT files: it reads your own installed copy while the avatar is being built.

We implemented blendtree pruning that removes FermataFT's "Write Defaults off" compatibility animations, to reduce animator load on "Write Defaults on" avatars. This is a best-effort change and is not a conversion to Native Face Tracking; for that, see [Migrate to Native Face Tracking](#migrate-to-native-face-tracking-experimental).

## Requirements

- Unity 2022.3, PC (Windows) avatars
- VRChat Avatars SDK with native face tracking (3.10.6-beta.1 or later)
- NDMF 1.15 or later and Modular Avatar (tested with 1.19)
- FermataFT installed on the avatar with FermataFT's installer (the prefab with the *FT Setting* component).
  Older installs that wrote FermataFT's layers straight into the avatar's FX controller are also recognized, on a best-effort basis.
- A face tracking app that sends VRChat native face tracking, such as a recent VRCFaceTracking

## Usage

1. Install FermataFT on your avatar as usual.
2. Drag `FermataNativeFT.prefab` onto the avatar.
3. Select it and read the inspector. It shows any problem it can find before you upload, and what it will change.
4. Upload as usual.

If the avatar has no FermataFT face tracking, the bridge warns and does nothing. If FermataFT is there but cannot be patched safely, the build stops with an error that explains why.

## What changes at upload

- FermataFT's `_OSCmooth_Binary_Gen` layer, which decoded the bit-packed sync, is removed.
- FermataFT's `v2/*` sync parameters are removed from the expression parameters. The `v2/*` inputs that OSCmooth reads stay as local (unsynced) parameters.
- Face tracking is enabled on the avatar descriptor (*Use All Tracking Keys*), with one *Forward to Animator* parameter behavior per input. Each behavior computes the same value VRCFaceTracking would have sent, from VRChat's tracking keys. Every client, local and remote, computes these values itself.
- Eyes: VRChat derives the gaze and blink tracking keys from its own eye look, which any *Tracking Control* that sets eyes to *Animation* turns off. FermataFT does that whenever its face tracking drives the eyes, and during gestures. Those states set eyes to *Tracking* instead, and the descriptor's *Eye Gaze Mode* and *Eyelids* are set to *Face Tracking*, so VRChat's eye look feeds FermataFT without moving the eye bones or blinking on its own.
- Less animator work, same result: FermataFT's face blend tree sets many of the avatar's own expression shapes to 0 in dozens of clips at once. Curves that always play together are merged into one clip, and the parts of the tree left with nothing to animate are removed. Those zeros are what hide your gestures while face tracking drives the face, so they stay active exactly when they were before. This takes Plum's face layer from 2,321 curves to 1,673. It only happens when every state in the FX controller has Write Defaults on; otherwise the face layer is left as it is.

Everything else stays as FermataFT built it: OSCmooth smoothing (including its separate local and remote smoothing), the face blend trees' results, menus, toggles, gestures and their sync. The build checks this and stops if anything else would change.

The avatar's own files are never modified; all changes happen on NDMF's build copy.

## Custom expressions

Each forwarded input is a native face tracking expression that VRChat evaluates on every client. To change one, open **Forwarded inputs** in the bridge's inspector and click **Override**. To forward another animator parameter, click **Add** under **Custom Expressions**. An entry with the same name as a forwarded input replaces its built-in formula. It can also supply the formula for a `v2/*` input that has no built-in formula.

Expressions can use:

- tracking key names such as `JawOpen` or `EyeLookInLeft` (the **+** button lists them), `Voice`, and `Viseme_*` (needs LipSync Mode *Viseme BlendShape Face Tracking*)
- numbers, `+ - * /`, `abs(x)`, `min(a, b)`, `max(a, b)` and `clamp(x, low, high)` (the **ƒ** button inserts them)

The result is clamped to −1..1. A single key on its own is forwarded directly, clamped to 0..1, and costs no instructions. The limits come from your VRChat SDK. Avatars SDK 3.10.6 allows 1024 characters, 256 tokens and 256 instructions per expression, and 2048 instructions per avatar. The inspector checks each expression as you type. An invalid expression stops the upload with an error.

## Migrate to Native Face Tracking (experimental)

Off by default. With this option on, the bridge moves FermataFT's face onto native face tracking instead of forwarding values into it:

- Each movement that native face tracking can reproduce exactly becomes a new blendshape it drives directly, with the same mesh movement.
- FermataFT's OSCmooth smoothing is removed. Native sync does its own. A part of it stays only where another layer needs it, like the QPro tongue toggle's smoothing on Plum.
- FermataFT's face blend tree shrinks to what native face tracking can't do, and forwarded inputs nothing reads any more are dropped.
- Menu toggles, gesture fades, Eye Lid LR Sync, QPro and Type A/B keep working. The animator computes their effect and passes it to native face tracking.

**What stays in the animator:** ear and other bone movement, the zeros that hide gesture shapes while tracking is on, FermataFT's state layers, the LR-synced eyelid blend (which depends on an angle that expressions can't compute), and anything past VRChat's limits. These limits matter: on the older 3.10.6 betas, with 512 instructions, far less fits. Update your SDK.

**What changes:**

- No OSCmooth smoothing on the face.
- Toggles reach native shapes a frame or two later.
- While tracking is on and no gesture has control, gesture shapes stay hidden. FermataFT let some of them show through at some tracking values.

The NDMF console shows a summary, and the Unity console lists every native shape and what was left behind. With Avatar Optimizer installed, the bridge tells it that native face tracking drives the new shapes, so they are kept.

## Known limits

- With FermataFT's *Eye Tracking* menu toggle off, the eyes stay still and don't blink. Leave it on: without an eye tracker, VRChat's simulated eye movement and blinking drive FermataFT's eyes instead.
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

This project follows many designs of [Pawlygon VRC Face Tracking](https://github.com/PawlygonStudio/VRC-Facetracking) (MIT), including the custom expression editor. The `v2/*` formulas restate VRCFaceTracking's parameter definitions ([VRCFaceTracking](https://github.com/benaclejames/VRCFaceTracking), Apache-2.0). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
FermataFT is a product of FermataShop / CPMEDIA and is not included or redistributed here.
