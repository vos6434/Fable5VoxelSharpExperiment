using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Voxel.Shared;

namespace Voxel.Server;

/// <summary>
/// Server-side BepuPhysics v2 simulation, stepped once per world tick (plan
/// 03). M1 scope: a debug box body + a large static floor so spawned bodies
/// fall and rest; real voxel-terrain colliders arrive in M2. Each dynamic
/// body maps to a PhysicsEntity that the game server spawns/streams/despawns
/// over the wire.
/// </summary>
public sealed partial class PhysicsWorld : IDisposable
{
    public sealed class Entity
    {
        public required uint Id { get; init; }
        public required byte Kind { get; init; }
        public required BodyHandle Body { get; set; }
        public required ushort DimX { get; set; }
        public required ushort DimY { get; set; }
        public required ushort DimZ { get; set; }
        /// <summary>Grid blocks (x-fastest,z,y) for the spawn payload / client mesh.</summary>
        public required ushort[] Blocks { get; set; }
        /// <summary>Center-of-mass in grid-local coords (body origin = grid corner + pivot).</summary>
        public required Vector3 Pivot { get; set; }
        /// <summary>Awake on the previous poll (for sleep-transition persistence).</summary>
        public bool WasAwake { get; set; } = true;
    }

    private const int S = Constants.ChunkSize;
    private const int ColliderChunkRadius = 1; // chunks around each awake body to collide

    private readonly BufferPool _pool = new();
    private readonly Simulation _sim;
    private readonly Dictionary<uint, Entity> _entities = new();
    private uint _nextId = 1;

    // Lazy voxel-terrain colliders (M2): built for chunks near awake bodies,
    // cached, invalidated on edit, removed when no awake body is near.
    private Func<int, int, int, ushort[]?>? _getChunk;
    private byte[] _collides = [];
    private readonly Dictionary<(int, int, int), List<(StaticHandle Static, TypedIndex Shape)>> _chunkColliders = new();
    private readonly HashSet<(int, int, int)> _neededScratch = new();

    public PhysicsWorld()
    {
        _sim = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(new Vector3(0, -20f, 0)),
            new SolveDescription(velocityIterationCount: 6, substepCount: 1));
    }

    /// <summary>Cheap single-block world read (no chunk clone) for buoyancy's water scan.</summary>
    private Func<int, int, int, ushort>? _getBlock;

    /// <summary>Wires the terrain source: a thread-safe chunk-blocks fetch, a cheap single-block read, and the "is solid" table.</summary>
    public void SetTerrainSource(Func<int, int, int, ushort[]?> getChunk, Func<int, int, int, ushort> getBlock, byte[] collidesTable)
    {
        _getChunk = getChunk;
        _getBlock = getBlock;
        _collides = collidesTable;
    }

    /// <summary>Drop a chunk's colliders (rebuilt next time a body needs them).</summary>
    public void InvalidateChunk(int cx, int cy, int cz)
    {
        if (_chunkColliders.Remove((cx, cy, cz), out var list)) RemoveColliders(list);
    }

    public IReadOnlyDictionary<uint, Entity> Entities => _entities;

    /// <summary>Spawns a single-block dynamic body; returns it so the caller can broadcast EntitySpawn.</summary>
    public Entity SpawnDebugBox(Vector3 position, ushort blockId)
    {
        var shape = new Box(1, 1, 1);
        var shapeIndex = _sim.Shapes.Add(shape);
        var inertia = shape.ComputeInertia(1f);

        var body = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            // Continuous collision so a fast fall/throw can't tunnel thin terrain.
            new CollidableDescription(shapeIndex, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)),
            new BodyActivityDescription(0.01f)));

        var entity = new Entity
        {
            Id = _nextId++,
            Kind = 0,
            Body = body,
            DimX = 1, DimY = 1, DimZ = 1,
            Blocks = [blockId],
            Pivot = new Vector3(0.5f, 0.5f, 0.5f),
            WasAwake = true,
        };
        _entities[entity.Id] = entity;
        TrackBody(entity);
        return entity;
    }

    /// <summary>
    /// Spawns a contraption from a glued block grid: greedy-merged boxes form a
    /// dynamic Bepu compound, mass ∝ block count. Returns the entity (its Pivot
    /// is the center of mass in grid-local coords) for the caller to broadcast.
    /// </summary>
    public Entity SpawnContraption(int minX, int minY, int minZ, int dimX, int dimY, int dimZ, ushort[] blocks)
    {
        var (compound, inertia, center) = BuildContraptionCompound(dimX, dimY, dimZ, blocks);
        var position = new Vector3(minX, minY, minZ) + center;

        var body = _sim.Bodies.Add(BodyDescription.CreateDynamic(
            new RigidPose(position),
            inertia,
            new CollidableDescription(compound, 0.1f, ContinuousDetection.Continuous(1e-3f, 1e-3f)),
            new BodyActivityDescription(0.01f)));

        var entity = new Entity
        {
            Id = _nextId++,
            Kind = 1,
            Body = body,
            DimX = (ushort)dimX, DimY = (ushort)dimY, DimZ = (ushort)dimZ,
            Blocks = blocks,
            Pivot = center,
            WasAwake = true,
        };
        _entities[entity.Id] = entity;
        TrackBody(entity);
        return entity;
    }

    public void Remove(uint id)
    {
        if (_entities.Remove(id, out var e))
        {
            ReleaseGrabsOnEntity(id);
            UntrackBody(e);
            _sim.Bodies.Remove(e.Body);
        }
    }

    /// <summary>Advances the simulation by one tick's worth of time.</summary>
    public void Step(float dt)
    {
        UpdateTerrainColliders();
        // Buoyancy before the timestep: its gravity-cancelling lift and the integrator's
        // gravity resolve together, so a settled boat reads ~zero velocity and can sleep
        // (instead of a phantom vy that keeps it awake forever).
        ApplyBuoyancy(dt);
        _sim.Timestep(dt);
    }

    /// <summary>Ensures chunks near awake bodies have colliders; drops the rest.</summary>
    private void UpdateTerrainColliders()
    {
        if (_getChunk is null) return;

        _neededScratch.Clear();
        foreach (var e in _entities.Values)
        {
            var body = _sim.Bodies[e.Body];
            if (!body.Awake) continue;
            var p = body.Pose.Position;
            int bcx = Coords.WorldToChunk((int)MathF.Floor(p.X));
            int bcy = Coords.WorldToChunk((int)MathF.Floor(p.Y));
            int bcz = Coords.WorldToChunk((int)MathF.Floor(p.Z));
            for (int dx = -ColliderChunkRadius; dx <= ColliderChunkRadius; dx++)
            for (int dy = -ColliderChunkRadius; dy <= ColliderChunkRadius; dy++)
            for (int dz = -ColliderChunkRadius; dz <= ColliderChunkRadius; dz++)
                _neededScratch.Add((bcx + dx, bcy + dy, bcz + dz));
        }

        // Add missing.
        foreach (var key in _neededScratch)
        {
            if (!_chunkColliders.ContainsKey(key))
            {
                _chunkColliders[key] = BuildChunkColliders(key.Item1, key.Item2, key.Item3);
            }
        }
        // Remove no-longer-needed (collect first — can't mutate while iterating).
        List<(int, int, int)>? drop = null;
        foreach (var key in _chunkColliders.Keys)
        {
            if (!_neededScratch.Contains(key)) (drop ??= []).Add(key);
        }
        if (drop is not null)
        {
            foreach (var key in drop)
            {
                RemoveColliders(_chunkColliders[key]);
                _chunkColliders.Remove(key);
            }
        }
    }

    /// <summary>Greedy-merges a chunk's solid voxels into box statics.</summary>
    private List<(StaticHandle, TypedIndex)> BuildChunkColliders(int cx, int cy, int cz)
    {
        var result = new List<(StaticHandle, TypedIndex)>();
        ushort[]? blocks = _getChunk!(cx, cy, cz);
        if (blocks is null) return result;

        var consumed = new bool[Constants.ChunkVolume];
        bool Solid(int x, int y, int z) =>
            !consumed[ChunkData.Index(x, y, z)] && _collides[blocks[ChunkData.Index(x, y, z)]] != 0;

        int ox = cx * S, oy = cy * S, oz = cz * S;
        for (int y = 0; y < S; y++)
        for (int z = 0; z < S; z++)
        for (int x = 0; x < S; x++)
        {
            if (!Solid(x, y, z)) continue;

            // Extend a box: +X run, then +Y (rows), then +Z (slabs).
            int w = 1;
            while (x + w < S && Solid(x + w, y, z)) w++;
            int h = 1;
            bool RowSolid(int yy) { for (int i = 0; i < w; i++) if (!Solid(x + i, yy, z)) return false; return true; }
            while (y + h < S && RowSolid(y + h)) h++;
            int d = 1;
            bool SlabSolid(int zz) { for (int j = 0; j < h; j++) if (!RowSolid2(x, y + j, zz, w)) return false; return true; }
            bool RowSolid2(int xx, int yy, int zz, int ww) { for (int i = 0; i < ww; i++) if (!Solid(xx + i, yy, zz)) return false; return true; }
            while (z + d < S && SlabSolid(z + d)) d++;

            for (int j = 0; j < h; j++)
            for (int k = 0; k < d; k++)
            for (int i = 0; i < w; i++)
                consumed[ChunkData.Index(x + i, y + j, z + k)] = true;

            var shape = _sim.Shapes.Add(new Box(w, h, d));
            var handle = _sim.Statics.Add(new StaticDescription(
                new Vector3(ox + x + w / 2f, oy + y + h / 2f, oz + z + d / 2f), shape));
            result.Add((handle, shape));
        }
        return result;
    }

    private void RemoveColliders(List<(StaticHandle Static, TypedIndex Shape)> list)
    {
        foreach (var (s, shape) in list)
        {
            _sim.Statics.Remove(s);
            _sim.Shapes.Remove(shape);
        }
    }

    /// <summary>Current pose + velocity of an entity (for EntityState streaming).</summary>
    public (Vector3 Pos, Quaternion Rot, Vector3 Vel) GetState(Entity e)
    {
        var bodyRef = _sim.Bodies[e.Body];
        return (bodyRef.Pose.Position, bodyRef.Pose.Orientation, bodyRef.Velocity.Linear);
    }

    public bool IsAwake(Entity e) => _sim.Bodies[e.Body].Awake;

    /// <summary>Adds angular velocity to a body (debug: makes a test contraption topple).</summary>
    public void Nudge(Entity e, Vector3 angularVelocity)
    {
        var body = _sim.Bodies[e.Body];
        body.Velocity.Angular += angularVelocity;
        body.Awake = true;
    }

    public void Dispose()
    {
        _sim.Dispose();
        _pool.Clear();
    }
}

// ---- Bepu callback boilerplate (standard "collide with friction") ----------

internal struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public void Initialize(Simulation simulation) { }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties material) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        material = new PairMaterialProperties
        {
            FrictionCoefficient = 1f,
            MaximumRecoveryVelocity = 2f,
            SpringSettings = new SpringSettings(30, 1),
        };
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

internal struct PoseIntegratorCallbacks(Vector3 gravity) : IPoseIntegratorCallbacks
{
    private Vector3Wide _gravityWide;
    private Vector<float> _linearDampingDt;
    private Vector<float> _angularDampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        // Light global damping so bodies settle instead of jittering forever.
        _linearDampingDt = new Vector<float>(MathF.Pow(1 - 0.03f, dt));
        _angularDampingDt = new Vector<float>(MathF.Pow(1 - 0.03f, dt));
        _gravityWide = Vector3Wide.Broadcast(gravity * dt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
    {
        velocity.Linear = (velocity.Linear + _gravityWide) * _linearDampingDt;
        velocity.Angular *= _angularDampingDt;
    }
}
