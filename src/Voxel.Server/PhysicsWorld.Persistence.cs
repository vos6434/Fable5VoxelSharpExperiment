using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace Voxel.Server;

public sealed partial class PhysicsWorld
{
    /// <summary>Restores a saved entity into the simulation (asleep unless the save says otherwise).</summary>
    public Entity LoadEntity(SavedEntity saved)
    {
        var entity = saved.Kind switch
        {
            0 => LoadDebugBox(saved),
            1 => LoadContraption(saved),
            _ => throw new InvalidDataException($"unknown entity kind {saved.Kind}"),
        };
        TrackBody(entity);
        return entity;
    }

    public void NoteEntityId(uint id)
    {
        if (id >= _nextId) _nextId = id + 1;
    }

    public SavedEntity ToSavedEntity(Entity e)
    {
        var body = _sim.Bodies[e.Body];
        return new SavedEntity(
            e.Id, e.Kind, e.DimX, e.DimY, e.DimZ, e.Blocks,
            e.Pivot.X, e.Pivot.Y, e.Pivot.Z,
            body.Pose.Position.X, body.Pose.Position.Y, body.Pose.Position.Z,
            body.Pose.Orientation.X, body.Pose.Orientation.Y, body.Pose.Orientation.Z, body.Pose.Orientation.W,
            body.Velocity.Linear.X, body.Velocity.Linear.Y, body.Velocity.Linear.Z,
            body.Velocity.Angular.X, body.Velocity.Angular.Y, body.Velocity.Angular.Z,
            !body.Awake);
    }

    /// <summary>Entities that just transitioned awake → asleep since the last poll.</summary>
    public IReadOnlyList<Entity> PollSleepTransitions()
    {
        var slept = new List<Entity>();
        foreach (var e in _entities.Values)
        {
            bool awake = IsAwake(e);
            if (e.WasAwake && !awake) slept.Add(e);
            e.WasAwake = awake;
        }
        return slept;
    }

    private Entity LoadDebugBox(SavedEntity saved)
    {
        if (saved.Blocks.Length != 1) throw new InvalidDataException("debug box must have one block");
        var shape = new Box(1, 1, 1);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(1f);
        var pose = new RigidPose(
            new Vector3((float)saved.PosX, (float)saved.PosY, (float)saved.PosZ),
            new Quaternion(saved.Qx, saved.Qy, saved.Qz, saved.Qw));
        var body = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            pose, inertia,
            new CollidableDescription(shapeIndex, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)),
            new BodyActivityDescription(0.01f)));
        ApplySavedMotion(_sim.Bodies[body], saved);
        return CreateEntity(saved.Id, 0, 1, 1, 1, saved.Blocks, new Vector3(0.5f, 0.5f, 0.5f), body, asleep: saved.Asleep);
    }

    private Entity LoadContraption(SavedEntity saved)
    {
        var (compound, inertia, center) = BuildContraptionCompound(saved.DimX, saved.DimY, saved.DimZ, saved.Blocks);
        var pose = new RigidPose(
            new Vector3((float)saved.PosX, (float)saved.PosY, (float)saved.PosZ),
            new Quaternion(saved.Qx, saved.Qy, saved.Qz, saved.Qw));
        var body = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            pose, inertia,
            new CollidableDescription(compound, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)),
            new BodyActivityDescription(0.01f)));
        ApplySavedMotion(_sim.Bodies[body], saved);
        return CreateEntity(saved.Id, 1, saved.DimX, saved.DimY, saved.DimZ, saved.Blocks, center, body, asleep: saved.Asleep);
    }

    private static void ApplySavedMotion(BodyReference body, SavedEntity saved)
    {
        body.Velocity = new BodyVelocity
        {
            Linear = new Vector3(saved.VelX, saved.VelY, saved.VelZ),
            Angular = new Vector3(saved.AngVelX, saved.AngVelY, saved.AngVelZ),
        };
        body.Awake = !saved.Asleep;
    }

    private Entity CreateEntity(
        uint id, byte kind, ushort dimX, ushort dimY, ushort dimZ, ushort[] blocks,
        Vector3 pivot, BodyHandle body, bool asleep)
    {
        NoteEntityId(id);
        var entity = new Entity
        {
            Id = id,
            Kind = kind,
            Body = body,
            DimX = dimX, DimY = dimY, DimZ = dimZ,
            Blocks = blocks,
            Pivot = pivot,
            WasAwake = !asleep,
        };
        _entities[id] = entity;
        return entity;
    }

    private (TypedIndex Compound, BodyInertia Inertia, Vector3 Center) BuildContraptionCompound(
        int dimX, int dimY, int dimZ, ushort[] blocks)
    {
        int Index(int x, int y, int z) => (y * dimZ + z) * dimX + x;
        var consumed = new bool[dimX * dimY * dimZ];
        bool Solid(int x, int y, int z) => !consumed[Index(x, y, z)] && blocks[Index(x, y, z)] != 0;

        using var builder = new CompoundBuilder(_pool, _sim.Shapes, 8);
        for (int y = 0; y < dimY; y++)
        for (int z = 0; z < dimZ; z++)
        for (int x = 0; x < dimX; x++)
        {
            if (!Solid(x, y, z)) continue;
            int w = 1; while (x + w < dimX && Solid(x + w, y, z)) w++;
            int h = 1;
            bool Row(int yy) { for (int i = 0; i < w; i++) if (!Solid(x + i, yy, z)) return false; return true; }
            while (y + h < dimY && Row(y + h)) h++;
            int d = 1;
            bool Slab(int zz) { for (int j = 0; j < h; j++) for (int i = 0; i < w; i++) if (!Solid(x + i, y + j, zz)) return false; return true; }
            while (z + d < dimZ && Slab(z + d)) d++;
            for (int j = 0; j < h; j++) for (int k = 0; k < d; k++) for (int i = 0; i < w; i++)
                consumed[Index(x + i, y + j, z + k)] = true;

            var local = new RigidPose(new Vector3(x + w / 2f, y + h / 2f, z + d / 2f));
            builder.Add(new Box(w, h, d), local, w * h * d);
        }

        builder.BuildDynamicCompound(out var children, out var inertia, out var center);
        return (_sim.Shapes.Add(new Compound(children)), inertia, center);
    }
}
