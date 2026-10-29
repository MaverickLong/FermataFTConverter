using UnityEngine;
using VRC.SDKBase;

namespace Maberikku.FermataNativeFT
{
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
    }
}
