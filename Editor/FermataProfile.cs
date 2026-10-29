using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.Editor
{
    /// <summary>
    /// The only FermataFT-specific knowledge in this tool: names, never data.
    /// </summary>
    internal static class FermataProfile
    {
        /// <summary>FermataFT's settings component (global namespace). Matched by name so this tool never links against FermataFT.</summary>
        public const string SettingsComponentTypeName = "ftSet";

        /// <summary>FermataFT's state behaviour that controls an FX layer by name; resolved by FermataFT after this tool runs.</summary>
        public const string NamedLayerControlTypeName = "FermataFT.FTNamedFxLayerControl";

        public const string NamedLayerControlTargetField = "targetLayerName";

        /// <summary>OSCmooth layer that decodes the bit-packed remote sync into v2 values. Replaced by native forwarding.</summary>
        public const string BinaryDecodeLayer = "_OSCmooth_Binary_Gen";

        /// <summary>OSCmooth smoothing layer. Kept untouched; it now smooths the natively forwarded values.</summary>
        public const string SmoothingLayer = "_OSCmooth_Smoothing_Gen";

        /// <summary>VRCFaceTracking parameter namespace that the smoothing layer reads.</summary>
        public const string InputPrefix = "v2/";

        /// <summary>
        /// FermataFT is present when its settings component is on the avatar, or, for older installs that wrote their
        /// layers straight into the avatar's FX, when the FX has its OSCmooth layers.
        /// </summary>
        public static bool IsPresent(VRCAvatarDescriptor descriptor, IEnumerable<LayerInfo> fxLayers)
        {
            return FindSettings(descriptor).Count > 0 || HasOscmoothLayers(fxLayers);
        }

        public static bool HasOscmoothLayers(IEnumerable<LayerInfo> fxLayers)
        {
            return fxLayers.Any(l => l.Name == BinaryDecodeLayer || l.Name == SmoothingLayer);
        }

        /// <summary>FermataFT settings components that belong to <paramref name="descriptor"/>'s avatar.</summary>
        public static List<Component> FindSettings(VRCAvatarDescriptor descriptor)
        {
            if (!descriptor) return new List<Component>();
            return descriptor.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(c => c && c.GetType().FullName == SettingsComponentTypeName
                            && c.GetComponentInParent<VRCAvatarDescriptor>(true) == descriptor)
                .Cast<Component>()
                .ToList();
        }
    }
}
