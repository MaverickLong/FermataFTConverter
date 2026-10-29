using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace Maberikku.FermataNativeFT
{
    /// <summary>
    /// Added during the build when shapes are baked, and removed again before upload. Lists the blendshapes that only
    /// native face tracking drives, so mesh optimisers that only look at animators keep them.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class FermataNativeFTBakedShapes : MonoBehaviour, IEditorOnly
    {
        [Serializable]
        public sealed class Entry
        {
            public SkinnedMeshRenderer renderer;
            public List<string> blendshapes = new List<string>();
        }

        public List<Entry> entries = new List<Entry>();
    }
}
