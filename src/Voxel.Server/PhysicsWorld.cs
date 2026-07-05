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
public sealed class PhysicsWorld : IDisposable
{
    public sealed class Entity
    {
        public required uint Id { get; init; }
        public required byte Kind { get; init; }
        public required BodyHandle Body { get; init; }
        public required ushort DimX { get; init; }
        public required ushort DimY { get; init; }
        public required ushort DimZ { get; init; }
        /// <summary>Grid blocks (x-fastest,z,y) for the spawn payload / client mesh.</summary>
        public required ushort[] Blocks { get; init; }
    }

    private readonly BufferPool _pool = new();
    private readonly Simulation _sim;
    private readonly Dictionary<uint, Entity> _entities = new();
    private uint _nextId = 1;

    public PhysicsWorld()
    {
        _sim = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(new Vector3(0, -20f, 0)),
            new SolveDescription(velocityIterationCount: 6, substepCount: 1));

        // M1 test floor: a wide, thin static slab held a few blocks *above*
        // spawn terrain, so a dropped body rests visibly in the air (terrain
        // has no colliders until M2). M2 replaces this with voxel colliders.
        var floorShape = _sim.Shapes.Add(new Box(512, 1, 512));
        _sim.Statics.Add(new StaticDescription(new Vector3(0, WorldGen.SeaLevel + 12f, 0), floorShape));
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
            new CollidableDescription(shapeIndex, 0.1f),
            new BodyActivityDescription(0.01f)));

        var entity = new Entity
        {
            Id = _nextId++,
            Kind = 0,
            Body = body,
            DimX = 1, DimY = 1, DimZ = 1,
            Blocks = [blockId],
        };
        _entities[entity.Id] = entity;
        return entity;
    }

    public void Remove(uint id)
    {
        if (_entities.Remove(id, out var e)) _sim.Bodies.Remove(e.Body);
    }

    /// <summary>Advances the simulation by one tick's worth of time.</summary>
    public void Step(float dt) => _sim.Timestep(dt);

    /// <summary>Current pose + velocity of an entity (for EntityState streaming).</summary>
    public (Vector3 Pos, Quaternion Rot, Vector3 Vel) GetState(Entity e)
    {
        var bodyRef = _sim.Bodies[e.Body];
        return (bodyRef.Pose.Position, bodyRef.Pose.Orientation, bodyRef.Velocity.Linear);
    }

    public bool IsAwake(Entity e) => _sim.Bodies[e.Body].Awake;

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
