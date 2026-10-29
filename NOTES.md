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

1. Forwarded values reach the animator, locally and on a remote client. Then check whether the unsynced declarations are needed at all.
2. Resting face with no tracker connected (eyelids should stay open at the default).
3. Remote smoothness: OSCmooth's remote branch smooths more heavily than the local one, as it always did.
4. Built-in eye look, blink and visemes behave as before when face tracking is enabled on the descriptor.
5. Performance stats: Face Tracking Operations and Forwarded Parameters (30).
