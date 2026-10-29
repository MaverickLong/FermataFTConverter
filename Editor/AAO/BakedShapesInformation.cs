using System.Linq;
using Anatawa12.AvatarOptimizer.API;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace Maberikku.FermataNativeFT.AAO
{
    /// <summary>
    /// Avatar Optimizer doesn't read the descriptor's face tracking mappings, so without this it would treat the baked
    /// blendshapes as never animated and freeze them. Declaring them modified keeps them, and the mapping step follows
    /// any rename or mesh merge into the descriptor.
    /// </summary>
    [ComponentInformation(typeof(FermataNativeFTBakedShapes))]
    internal sealed class BakedShapesInformation : ComponentInformation<FermataNativeFTBakedShapes>
    {
        const string Prefix = "blendShape.";

        protected override void CollectDependency(FermataNativeFTBakedShapes component, ComponentDependencyCollector collector)
        {
            collector.MarkEntrypoint();
            foreach (var entry in component.entries)
                if (entry.renderer) collector.AddDependency(entry.renderer);
        }

        protected override void CollectMutations(FermataNativeFTBakedShapes component, ComponentMutationsCollector collector)
        {
            foreach (var entry in component.entries)
                if (entry.renderer) collector.ModifyProperties(entry.renderer, entry.blendshapes.Select(name => Prefix + name));
        }

        protected override void ApplySpecialMapping(FermataNativeFTBakedShapes component, MappingSource mappingSource)
        {
            var descriptor = component.GetComponent<VRCAvatarDescriptor>();
            if (!descriptor || descriptor.faceTrackingMeshes == null) return;
            var meshes = descriptor.faceTrackingMeshes;
            foreach (var entry in component.entries)
            {
                if (!entry.renderer) continue;
                var info = mappingSource.GetMappedComponent(entry.renderer);
                foreach (var name in entry.blendshapes)
                {
                    if (!info.TryMapProperty(Prefix + name, out var mapped) || !(mapped.Component is SkinnedMeshRenderer target)
                        || !mapped.Property.StartsWith(Prefix)) continue;
                    var renamed = mapped.Property.Substring(Prefix.Length);
                    if (renamed != name && descriptor.faceTrackingParameterBehaviors != null)
                        foreach (var behavior in descriptor.faceTrackingParameterBehaviors)
                            if (behavior != null && behavior.targetBlendshapeName == name)
                                behavior.targetBlendshapeName = renamed;
                    if (!meshes.Contains(target)) meshes.Add(target);
                }
                if (info.MappedComponent != entry.renderer) meshes.Remove(entry.renderer);
            }
        }
    }
}
