# Developer notes

Facts this tool relies on, where they were verified, and what is still open. Checked October 2026.

## Design

- Scope: a stopgap. FermataFT's face blend tree stays in the animator; compiling it to native Blendshapes Sets was prototyped and abandoned (it does not fit the 512-instruction budget).
- One NDMF pass, triggered by the `FermataNativeFTBridge` marker, patches the build copy after Modular Avatar has merged FermataFT:
  1. Remove the `_OSCmooth_Binary_Gen` FX layer (decodes the bit-packed sync).
  2. Keep `_OSCmooth_Smoothing_Gen` untouched. Its `v2/*` inputs are now written by native *Forward to Animator* behaviors, on every client.
  3. Expression parameters: drop parameters only the binary layer read (the sync bits); keep each forwarded `v2/*` input as an unsynced entry.
  4. Enable native face tracking (*Use All Tracking Keys*) and add one behavior per input.
  5. Self-check that nothing else in FX or the expression parameters changed.
- Forward targets are discovered, not hard-coded: every `v2/*` parameter that a remaining layer reads.
- `PatchPlanner` holds every check and is shared by the inspector (on the original controllers) and the build (on NDMF virtual controllers).
- FermataFT is detected by its `ftSet` component (matched by type name) or by the OSCmooth layers in FX, which covers older installs that edited the avatar FX directly.
- Custom expressions (`FermataNativeFTBridge.customForwards`) replace a built-in formula by parameter name, add extra forwards, or fill in `v2/*` inputs without a built-in formula. The UI follows Pawlygon's Face Tracking Parameters design (override button, `+` input picker, `ƒ` function menu, inserting at the cursor). Pawlygon's VRC-Facetracking is MIT; see `THIRD_PARTY_NOTICES.md`.
- `NativeExpression.Check` parses expressions with the SDK's documented rules: syntax, known inputs, function arities, 512 characters, 128 tokens, 64 instructions, nesting and stack depth 16, numbers up to ±10000. The inspector and the build share it; an invalid custom expression is a build error.

## VRChat SDK (Avatars 3.10.6-beta.4)

Read from the SDK editor sources and `VRCSDK3A.dll` metadata.

- `VRCAvatarDescriptor`: `enableFaceTracking`, `useAllTrackingKeys`, `faceTrackingMeshes`, `faceTrackingBlendshapesSetList`, `faceTrackingParameterBehaviors` (a list).
- `FaceTrackingParameterBehavior` fields: `targetMode`, `outputParameterName`, `targetBlendSetName`, `targetBlendshapeName`, `weightBlendSetName`, `inputTrackingKeys` (int[]), `inputTrackingKeyNames`, `inputInverted`, `combineMode`, `customExpression`.
- `ParameterBehaviorTargetMode`: ForwardToAnimator=0, ApplyBlendSetWeight, ApplyToBlendshape, LimitBlendshapeMax, LimitBlendshapeMin.
- `ParameterBehaviorCombineMode`: Max=0, Min, Add, Multiply, Custom=4. Custom output is clamped to -1..1; the others to 0..1.
- `FaceTrackingKey`: 102 Unified Expressions keys (EyeLookOutRight=0 … NeckFlexLeft=101, Max=102).
- Limits: 128 behaviors, 32 inputs per behavior, 64-character parameter names; per expression 512 characters, 128 tokens, 64 instructions; 512 instructions per avatar.
- Instruction estimate (`FaceTrackingExpressionValidator`): every number, input, function call and operator, unary minus included, counts 1; parentheses and commas are free. Non-Custom behaviors cost nothing.
- The SDK only warns, and only for the scene avatar, so the build pass runs `FaceTrackingExpressionValidator.CollectWarnings` on the built descriptor itself.
- Nothing in the SDK requires a forwarded parameter to be declared in Expression Parameters. The docs only mention declaring it to sync it. The tool declares forwarded inputs as unsynced (0 bits), which works either way.

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
