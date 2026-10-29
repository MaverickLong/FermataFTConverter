using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Maberikku.FermataNativeFT
{
    /// <summary>A native face tracking expression forwarded to an animator parameter in place of the built-in one.</summary>
    [Serializable]
    public sealed class CustomForward
    {
        [Tooltip("Animator parameter that receives the value, for example v2/JawOpen.")]
        public string parameter = "";

        [Tooltip("Tracking keys, Voice and Viseme_* inputs combined with + - * / abs() min() max() clamp(), " +
                 "for example JawRight - JawLeft. The result is clamped to -1..1.")]
        public string expression = "";
    }

    /// <summary>
    /// Marks an avatar whose FermataFT setup should run on VRChat native face tracking.
    /// At upload, native tracking values are forwarded into the inputs of Fermata's OSCmooth smoothing layer,
    /// and Fermata's bit-packed OSC sync is dropped. Everything else Fermata configures is kept as is.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Maberikku/Fermata Native FT Bridge")]
    [HelpURL("https://github.com/MaverickLong/FermataFTConverter")]
    public sealed class FermataNativeFTBridge : MonoBehaviour, IEditorOnly
    {
        [Tooltip("Replaces the expression of a forwarded FermataFT input with the same name, or forwards another animator parameter.")]
        public List<CustomForward> customForwards = new List<CustomForward>();

        [Tooltip("Experimental. Moves the parts of FermataFT's face that native face tracking can reproduce exactly onto " +
                 "baked blendshapes it drives directly. Everything else keeps using the forwarded inputs.")]
        public bool bakeExactShapes;
    }
}
