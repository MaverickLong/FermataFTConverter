# Developer notes

Facts this tool relies on, where they were verified, and what is still open. Checked October 2026.

## Design

- Scope: a stopgap. FermataFT's face blend tree stays in the animator; compiling all of it to native face tracking was prototyped and abandoned (it does not fit the 512-instruction budget). Migrating (below) is opt-in, and always keeps forwarding as the base for what can't move.
- One NDMF pass, triggered by the `FermataNativeFTBridge` marker, patches the build copy after Modular Avatar has merged FermataFT:
  1. Remove the `_OSCmooth_Binary_Gen` FX layer (decodes the bit-packed sync).
  2. Keep `_OSCmooth_Smoothing_Gen` untouched. Its `v2/*` inputs are now written by native *Forward to Animator* behaviors, on every client.
  3. Expression parameters: drop parameters only the binary layer read (the sync bits); keep each forwarded `v2/*` input as an unsynced entry.
  4. Enable native face tracking (*Use All Tracking Keys*) and add one behavior per input.
  5. Self-check that nothing else in FX or the expression parameters changed.
  6. Fold zero overrides in FermataFT's face layers and prune what's left empty (below), with its own self-check.
- Zero folding (every build): a blendshape that a single-state Write Defaults blend-tree layer only ever sets to 0, and that rests at 0 on its renderer, always has the value 0 while the layer writes it.
  - Only when every state in FX has Write Defaults on. Unity blends unreliably in controllers that mix WD on and off. Otherwise nothing is folded, and the Unity console says why. Chocolat's and Plum's FX controllers and FermataFT's layers are WD on throughout (Plum's FermataFT controllers also contain a few WD-off states that no layer reaches).
  - Whether the layer writes it is the part that matters, measured in Unity 2022.3: a clip at weight 0 isn't evaluated, and a blendshape no active clip writes shows the layers below, while any active clip overrides them completely. A Direct child writing 30 over a lower layer's 100 gives 100 at weight 0, 7.5 at 0.25 and 30 at 1. So these zero curves are the switch that hides the hand layers' gestures (FermataFT merges those below its face layer) while tracking is on.
  - A curve therefore only moves up the tree as far as its activation stays the same: to a node where having any weight already guarantees an active clip writes that shape. 1D and 2D blends cover a shape when every child does, because one child always plays. A Direct child covers it under its own parameter, and under an always-on or already-implied parameter also for its parent. Curves moved to one place become one clip, weighted by the same parameter (or by `FNFT/One`, a float added with default 1 that nothing writes, inside a new Direct wrapper).
  - Subtrees left animating nothing are pruned. A dead Direct child goes; a dead 1D or 2D child becomes an empty clip, because its threshold still shapes its siblings' weights.
  - Self-checks: the layer is analysed again, and every other blendshape must keep exactly its terms. Both trees are also played at 512 sampled parameter values, and the same blendshapes must be written at each (2D blends count every child as active).
  - On Plum, folding takes FacialTracking from 2,321 to 1,673 curves. Moving everything to one always-on clip gave 759, but pinned 186 gesture shapes to 0 with tracking off.
  - Tested end to end in Unity with Plum's controller under a layer writing every shape to 37: built and original agree on all 267 shapes at 1,500 sampled states (max error 0), and the always-on version fails that test.
- Forward targets are discovered, not hard-coded: every `v2/*` parameter that a remaining layer reads.
- `PatchPlanner` holds every check and is shared by the inspector (on the original controllers) and the build (on NDMF virtual controllers).
- FermataFT is detected by its `ftSet` component (matched by type name) or by the OSCmooth layers in FX, which covers older installs that edited the avatar FX directly.
- Custom expressions (`FermataNativeFTBridge.customForwards`) replace a built-in formula by parameter name, add extra forwards, or fill in `v2/*` inputs without a built-in formula. The UI follows Pawlygon's Face Tracking Parameters design (override button, `+` input picker, `ƒ` function menu, inserting at the cursor). Pawlygon's VRC-Facetracking is MIT; see `THIRD_PARTY_NOTICES.md`.
- `NativeExpression.Check` parses expressions with the SDK's documented rules: syntax, known inputs, function arities, the installed SDK's limits on characters, tokens, instructions and depth, numbers up to ±10000. The inspector and the build share it; an invalid custom expression is a build error.

## Migrating to native face tracking

`migrateToNative` on the marker, off by default. It replaces zero folding and runs after forwarding. Anything it can't move keeps running in the animator on forwarded inputs, so a partial result is still exact.

- Analysis (`BlendTreeTerms`): a single-state, Write Defaults, override layer at weight 1 with no mask, no transitions and no layer control is rewritten per blendshape as a sum of terms, coefficient × product of factors.
  - A Direct child multiplies by its parameter.
  - A 1D tree interpolates its children's coefficients over the thresholds. When every child gives the same result, the parameter drops out; FermataFT repeats subtrees like this.
  - 2D blends, measured against Unity 2022.3 on a grid over -1..1 (error under 0.00001):
    - Freeform Cartesian with all points on x = y is exactly a 1D blend on (x + y) / 2.
    - Simple Directional with a center and a convex ring: in the wedge between neighbours A and B the point is u·A + v·B. A ring child weighs u or v, the center 1 - u - v, and past the ring u and v are divided by u + v. That's `max(0, min(u_next, v_prev)) / max(1, gauge)` per child. Values on one plane cut off at 0 reduce to `max(0, a·x + b·y) / max(1, gauge)`.
    - Freeform Directional also depends on the angle off the points' line, even with all points on x = y. Expressions have no sqrt or atan, so it stays in the animator. Treating it as 1D on the average is off by up to 0.018 in weight for |L-R| ≤ 0.1, and 0.6 at worst.
  - Clips that change over time and normalised Direct trees also stay in the animator.
- `ExpressionCompiler`: an input is parsed to a constant plus scaled atoms (tracking keys, and `max` of keys) with the range it can take.
  - Spends nothing on clamps its range can't reach, spells one segment over the whole range as a plain linear function, and factors a common coefficient: `1.111111 - 0.555556 * (a + b)`.
  - A forward clamps to -1..1, so an input that can leave that range stays one clamped atom. Combining two inputs (a 2D blend's arithmetic) must not clamp.
- Encoding: one input, zero at rest: one *Apply to Blendshape* behavior per linear segment, relying on the 0..1 output clamp. Otherwise the whole product as one custom expression, split by sign. Blendshapes that share a driver and gate share one behavior and one native shape.
  - In-between frames could hold a whole curve on one behavior, but save under 5% of instructions, and whether a set scales the curve's input or output isn't settled, so every native shape stays linear.
- Gates: a term's other factors are rebuilt as a blend tree that writes their product to `FNFT/Gate/<n>`. A second layer re-enters a state every frame, so a *VRC Animator Face Tracking Blendshapes Set* behaviour copies that parameter into the set `FNFT/Gate/<n>`, which the term's behaviors name as `weightBlendSetName`.
- Binary parameters: a parameter is binary when every value the avatar can give it is 0 or 1 (controller defaults, parameter driver Sets, menu toggles and buttons) and nothing else writes it. Every factor on it becomes a two-point function and repeated ones multiply into one, so branches that differed only in how often a toggle appeared merge. Radial puppets stay continuous: Plum's Type A/B is a gate, and shapes where the types differ carry both branches.
- Which shapes can move: one frame, weight 0 on its renderer, not owned by the descriptor (visemes, eyelids, existing face tracking), and no later FX layer animates it. Earlier layers are fine: see holds.
- Holds, what freezes gestures: a slot whose term moved keeps writing 0, since an active clip writing a shape is what hides the layers below (see zero folding).
  - A shape nothing below animates loses those zeros: writing 0 and writing nothing then look the same.
  - The rest are taken out from under forwarded inputs, then folded. They follow the gates (tracking on, no gesture in control) and not the tracking values. This is the one deliberate change: FermataFT holds some shapes only over part of an input's range, and keeping that would keep most of the tree and its forwards.
- Forward trimming: a FermataFT input that no layer outside the smoothing layer still reads loses its forward. "Reads" counts transition conditions, state behaviours and state parameters, and blend tree parameters only where the subtree still animates something. Extra custom forwards are never dropped. Freed budget goes to the next round, up to 8 rounds.
- OSCmooth: once any term is native, forwards write the copies the blend trees read (`OSCm/Proxy/<input>`), and the smoothing layer goes. What other layers read from it and no forward could write keeps its branch: its root is a Direct tree with one child per input. If that can't be separated, the migration is skipped.
- Budget: terms are ranked by the curves they take out of the tree per instruction, and when a term is the last reader of an input, the forward's instructions count back. Terms are taken until the next would pass the SDK's limits.
- Self-checks: the layer is analysed again and must keep exactly its unmoved terms. Both trees are played at sampled parameter values, and the new one must write at least what the old one wrote among shapes the layers below animate. No dropped forward may still be read.
- Avatar Optimizer ignores the descriptor's face tracking mappings and would freeze the native shapes. `FermataNativeFTBakedShapes` plus a `ComponentInformation` declares them modified and follows renames and mesh merges into the descriptor. That assembly only compiles with AAO 1.7 or later, and the component is removed after AAO.
- NDMF quirks that bit: `RemoveLayer` does nothing to layers cloned with a controller when nothing has merged into it since (use `RemoveLayers`). `VirtualClip.Clone` shares curve entries, so setting a curve on a clone changes the original too; remove the binding first.

### Results

Tested end to end in Unity with Avatars SDK 3.10.6: the real NDMF pipeline on a synthetic avatar around the real controllers, with each source shape moving its own vertex. Native behaviors are evaluated with `NativeExpression.Evaluate`, and the resulting vertices compared with the original animator at 1,200 sampled states, under a layer writing what the avatar's lower layers really write. The native client itself isn't in this test.

| | Plum | Chocolat |
|---|---|---|
| Max error (of 100), besides holds | 0.0001 | 0.0001 |
| Face layer curves | 2,321 → 284 | 1,049 → 83 |
| Terms left in the animator | 40 | 11 |
| Forwards | 30 → 20 | 30 → 14 |
| Instructions, behaviors, sets | 2045, 87, 22 | 2022, 73, 7 |

The instruction budget, not the behaviors, is what stops Plum. Computing each shared factor once as a set weight (*Apply Blend Set Weight*, then `weightBlendSetName`) would roughly halve the products' cost, since the same lip suck pair appears in most of them. It's not used until chaining is verified in the client.

## VRChat SDK (Avatars 3.10.6)

Read from the SDK editor sources and `VRCSDK3A.dll` metadata. Between 3.10.6-beta.4 and 3.10.6 only the expression limits changed, so `NativeExpression` reads them from the installed validator, falling back to the older values.

- `VRCAvatarDescriptor`: `enableFaceTracking`, `useAllTrackingKeys`, `faceTrackingMeshes`, `faceTrackingBlendshapesSetList`, `faceTrackingParameterBehaviors` (a list).
- `FaceTrackingParameterBehavior` fields: `targetMode`, `outputParameterName`, `targetBlendSetName`, `targetBlendshapeName`, `weightBlendSetName`, `inputTrackingKeys` (int[]), `inputTrackingKeyNames`, `inputInverted`, `combineMode`, `customExpression`.
- `ParameterBehaviorTargetMode`: ForwardToAnimator=0, ApplyBlendSetWeight, ApplyToBlendshape, LimitBlendshapeMax, LimitBlendshapeMin.
- `ParameterBehaviorCombineMode`: Max=0, Min, Add, Multiply, Custom=4. Custom output is clamped to -1..1; the others to 0..1.
- `FaceTrackingKey`: 102 Unified Expressions keys (EyeLookOutRight=0 … NeckFlexLeft=101, Max=102).
- Limits: 128 behaviors, 16 meshes, 32 inputs per behavior, 64-character parameter names; per expression 1024 characters, 256 tokens, 256 instructions, nesting and stack depth 32; 2048 instructions per avatar. In 3.10.6-beta.4: 512 characters, 128 tokens, 64 instructions, depth 16, and 512 per avatar.
- Instruction estimate (`FaceTrackingExpressionValidator`): every number, input, function call and operator, unary minus included, counts 1; parentheses and commas are free. Non-Custom behaviors cost nothing.
- The SDK only warns, and only for the scene avatar, so the build pass runs `FaceTrackingExpressionValidator.CollectWarnings` on the built descriptor itself.
- Nothing in the SDK requires a forwarded parameter to be declared in Expression Parameters. The docs only mention declaring it to sync it. The tool declares forwarded inputs as unsynced (0 bits), which works either way.
- `FaceTrackingBlendshapesSet`: `setName`, `setWeight` (initial), and per key and viseme a blendshape name and a multiplier (0..10). Per the docs, when several sets map the same blendshape the highest value wins; they don't add. The docs don't say how behaviors that write the same blendshape combine, so each native shape gets exactly one behavior.
- *Apply to Blendshape*: 0..1 is weight 0..100, clamped to 0..1. `weightBlendSetName`: "the behavior then scales with that Blendshapes Set's weight".
- `VRCAnimatorFaceTrackingBlendshapesSet` (state behaviour): up to 64 entries, each a constant or an animator parameter (optionally range-converted), with a lerp speed (0 = instant). Weights are clamped to 0..1. It applies on state entry only; the docs suggest re-entering a state to update continuously.

## VRCFaceTracking (master, October 2026)

- The native encoder (`UnifiedEyeData.CopyTo`) splits gaze into complementary pairs: EyeLookOutRight = max(R.x, 0), EyeLookInRight = max(-R.x, 0), EyeLookOutLeft = max(-L.x, 0), EyeLookInLeft = max(L.x, 0), Up/Down = ±y. EyeClosed = 1 - openness.
- `BaseParam.ResetParam`: when the avatar reports full face tracking, legacy `/avatar/parameters/v2/*` messages are not sent. Forwarding into FermataFT's `v2/*` names therefore does not fight VRCFT.
- Parameter matching: `(?<!(v\d+))(/v2/<Name>)$|^(v2/<Name>)$`, so prefixed names such as `FT/v2/JawOpen` also match.
- `V2Formulas.cs` restates `UnifiedExpressionsParameters.cs` and `UnifiedSimpleExpressions.cs`. Pupil diameter parameters have no native equivalent.

## Eyes (tested in VRChat, October 2026)

- `EyeLook*` and `EyeClosed*` are the "basic" keys (`IsBasicFaceTrackingKey`; "Gaze/Blink only" in the SDK). VRChat fills them from its own eye look and blink: real eye tracking when available, simulated otherwise. That's why they work without *Use All Tracking Keys*.
- A Tracking Control with eyes set to *Animation* stops them: with FermataFT's eye tracking on (eyes: Animation), every forwarded `v2/Eye*` value stayed at its default. With it off (eyes: Tracking), they moved. Squint, wide and all mouth keys were unaffected. Changing the Eye Gaze Mode alone did not help.
- FermataFT locks the eyes in its eye-tracking state, its blink lock, AFK/station, and every gesture expression, including gestures that keep tracked eyes on (`EyeTracking_GestureControl` 0). Under OSC input none of that stopped the tracking data.
- Fix: when FermataFT's eye-tracking state locks the eyes (it sets `EyeTrackingActive`, or is only entered while it's on), every eye lock becomes *Tracking*, and the descriptor's Eye Gaze Mode and Eyelids become *Face Tracking*. With no Blendshapes Set mapping, VRChat's eye look then has no visible effect; it only feeds the keys. Mouth locks stay, since they also control visemes.
- Cost: with FermataFT's eye tracking toggle off, eyes no longer fall back to VRChat's bone gaze and descriptor blink; they stay still.
- Pawlygon's native setup takes a different route: it maps the eye keys to blendshapes natively, sets Eye Gaze Mode Both/Face Tracking and Eyelids Face Tracking, and uses no Tracking Control. Its `v2/*` forwarding formulas match `V2Formulas.cs`.

## NDMF 1.15 / Modular Avatar 1.19

- MA's `nadena.dev.modular-avatar.late-transform-stages` plugin destroys every `AvatarTagComponent`. FermataFT's `ftSet` is one, so the pass must run before that plugin.
- `BeforePlugin`/`AfterPlugin` constrain against the named plugin's start/end markers *in the same phase*, so naming Avatar Optimizer (Resolving + Optimizing only) from Transforming is safe.
- While `AnimatorServicesContext` is active, `VRCAnimatorLayerControl` indices are virtualized and re-resolved on commit. `VirtualAnimatorController.RemoveLayer` is therefore safe; there is no need to leave an empty layer behind.
- MA leaves `AnimatorServicesContext` active through the end of its Transforming passes; running inside it avoids an extra commit.

## FermataFT layouts surveyed

Installer packages for 15 avatars (Chocolat, Airi, Chiffon, Eku, Kaguya, Karin, MANUKA, Mayo, Milfy, Milltina, Plum, RIRU, Shinano, Sio, rurune) were read without installing them. Only names are recorded here.

- All use the installer: an `ftSet` prefab with two MA Merge Animators (FX) and MA Parameters.
- All have exactly one `_OSCmooth_Binary_Gen` and one `_OSCmooth_Smoothing_Gen` FX layer; other FT layer names vary.
- All read 30 `v2/*` inputs in the smoothing layer. All 30 have formulas. Karin reads `MouthRaiserUpper`; the others read `LipFunnel` in its place.
- The planner rules pass for all 23 FX variants (normal, GXR, Pico4Pro). The only parameters dropped are `v2/<Name>1/2/4/8/Negative` sync bits.
- Synced face data per prefab variant: about 136–149 bits.

## Still to verify in VRChat

Verified so far: forwarded values reach the animator locally (mouth, tongue; eyes with FermataFT's eye tracking toggle off).

1. Eyes with the eye-lock fix: gaze, blink, squint and JellyEye with the toggle on; gaze during gestures; no extra bone gaze or descriptor blink.
2. A remote client. Then check whether the unsynced declarations are needed at all.
3. Resting face with no tracker connected; simulated gaze and blink should drive FermataFT's eyes.
4. Remote smoothness: OSCmooth's remote branch smooths more heavily than the local one, as it always did.
5. Visemes behave as before.
6. Performance stats: Face Tracking Operations and Forwarded Parameters (30).
7. Zero folding: gestures and expressions look the same as without the bridge, especially mouth gestures with the mouth open.
8. Migrating, in the client:
   - the rest face and every expression look as they do with OSCmooth, apart from smoothing;
   - set weights follow the eye and lip toggles, gestures, LR sync, QPro and Type A/B, on a remote client too;
   - gesture shapes are hidden while tracking is on, and show when a gesture takes control;
   - AAO keeps the native shapes;
   - frame cost compared with mode 1.
9. Chaining: whether an *Apply Blend Set Weight* behavior can feed another behavior's `weightBlendSetName` in the same frame, and be scaled by a set itself.
