using Voxel.Shared;

namespace Voxel.Client;

/// <summary>Unified crosshair target: world chunk block or contraption voxel.</summary>
public abstract record AimTarget
{
    public sealed record World(RaycastHit Hit) : AimTarget;
    public sealed record Entity(EntityRenderer.EntityRaycastHit Hit) : AimTarget;
}
