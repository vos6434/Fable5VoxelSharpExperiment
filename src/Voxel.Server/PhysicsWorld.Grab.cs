using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities;

namespace Voxel.Server;

public sealed partial class PhysicsWorld
{
    public const float GunReach = 6f;
    public const float GunHoldMin = 2f;
    public const float GunHoldMax = 8f;
    public const float GunThrowSpeed = 14f;

    /// <summary>Fired when a grab ends (release, throw, disconnect, entity removed).</summary>
    public Action<int>? OnGrabReleased;

    private sealed class Grab
    {
        public required uint EntityId;
        public required BodyHandle Body;
        public Vector3 LocalGrabPoint;
        public Quaternion TargetOrientation;
        public ConstraintHandle LinearMotor;
        public ConstraintHandle AngularMotor;
        public float HoldDistance;
        public bool HasAngularMotor;
    }

    private readonly Dictionary<BodyHandle, uint> _bodyToEntity = new();
    private readonly Dictionary<int, Grab> _grabsByClient = new();
    private readonly Dictionary<uint, int> _holderByEntity = new();

    private void TrackBody(Entity entity)
    {
        _bodyToEntity[entity.Body] = entity.Id;
    }

    private void UntrackBody(Entity entity)
    {
        _bodyToEntity.Remove(entity.Body);
    }

    /// <summary>Raycast from the eye; attaches a servo if a free contraption is hit within reach.</summary>
    public bool TryGrab(int clientId, Vector3 eye, Vector3 rayDir, out uint entityId)
    {
        entityId = 0;
        if (_grabsByClient.ContainsKey(clientId)) return false;

        var handler = new GunRayHitHandler { BodyToEntity = _bodyToEntity, T = float.MaxValue };
        _sim.RayCast(eye, rayDir, GunReach, ref handler);
        if (handler.T >= GunReach || handler.EntityId == 0 || _holderByEntity.ContainsKey(handler.EntityId)) return false;
        if (!_entities.TryGetValue(handler.EntityId, out var entity)) return false;

        var body = _sim.Bodies[entity.Body];
        var hitLocation = eye + rayDir * handler.T;
        RigidPose.TransformByInverse(hitLocation, body.Pose, out var localGrabPoint);

        float holdDistance = Math.Clamp(handler.T, GunHoldMin, GunHoldMax);
        CreateMotorDescription(localGrabPoint, hitLocation, body, out var linear, out var angular, out bool hasAngular);

        var grab = new Grab
        {
            EntityId = entity.Id,
            Body = entity.Body,
            LocalGrabPoint = localGrabPoint,
            TargetOrientation = body.Pose.Orientation,
            LinearMotor = _sim.Solver.Add(body.Handle, linear),
            HoldDistance = holdDistance,
        };
        if (hasAngular)
        {
            grab.AngularMotor = _sim.Solver.Add(body.Handle, angular);
            grab.HasAngularMotor = true;
        }

        _grabsByClient[clientId] = grab;
        _holderByEntity[entity.Id] = clientId;
        entityId = entity.Id;
        body.Awake = true;
        return true;
    }

    public bool ReleaseGrab(int clientId, bool throwImpulse, Vector3 rayDir, bool notifyClient = false)
    {
        if (!_grabsByClient.Remove(clientId, out var grab)) return false;
        _holderByEntity.Remove(grab.EntityId);
        RemoveMotors(grab);

        if (_entities.TryGetValue(grab.EntityId, out var entity))
        {
            var body = _sim.Bodies[entity.Body];
            if (throwImpulse)
            {
                body.Velocity.Linear += rayDir * GunThrowSpeed;
                body.Awake = true;
            }
        }

        if (notifyClient) OnGrabReleased?.Invoke(clientId);
        return true;
    }

    public void SetGrabDistance(int clientId, float distance)
    {
        if (_grabsByClient.TryGetValue(clientId, out var grab))
            grab.HoldDistance = Math.Clamp(distance, GunHoldMin, GunHoldMax);
    }

    public void UpdateGrab(int clientId, Vector3 eye, Vector3 rayDir)
    {
        if (!_grabsByClient.TryGetValue(clientId, out var grab)) return;
        if (!_entities.TryGetValue(grab.EntityId, out var entity))
        {
            EndGrab(clientId, grab, notify: true);
            return;
        }

        var body = _sim.Bodies[entity.Body];
        if (!body.Exists)
        {
            EndGrab(clientId, grab, notify: true);
            return;
        }

        var target = eye + rayDir * grab.HoldDistance;
        CreateMotorDescription(grab.LocalGrabPoint, target, body, out var linear, out var angular, out bool hasAngular);
        _sim.Solver.ApplyDescription(grab.LinearMotor, linear);
        if (grab.HasAngularMotor && hasAngular)
            _sim.Solver.ApplyDescription(grab.AngularMotor, angular);
        body.Activity.TimestepsUnderThresholdCount = 0;
        body.Awake = true;
    }

    public void ReleaseAllGrabsForClient(int clientId) => ReleaseGrab(clientId, throwImpulse: false, Vector3.UnitZ);

    private void ReleaseGrabsOnEntity(uint entityId)
    {
        if (!_holderByEntity.Remove(entityId, out int clientId)) return;
        if (!_grabsByClient.Remove(clientId, out var grab)) return;
        RemoveMotors(grab);
        OnGrabReleased?.Invoke(clientId);
    }

    private void EndGrab(int clientId, Grab grab, bool notify)
    {
        _grabsByClient.Remove(clientId);
        _holderByEntity.Remove(grab.EntityId);
        RemoveMotors(grab);
        if (notify) OnGrabReleased?.Invoke(clientId);
    }

    private void RemoveMotors(Grab grab)
    {
        _sim.Solver.Remove(grab.LinearMotor);
        if (grab.HasAngularMotor)
            _sim.Solver.Remove(grab.AngularMotor);
    }

    private void CreateMotorDescription(
        Vector3 localGrabPoint, Vector3 target, BodyReference body,
        out OneBodyLinearServo linear, out OneBodyAngularServo angular, out bool hasAngular)
    {
        float inverseMass = body.LocalInertia.InverseMass;
        linear = new OneBodyLinearServo
        {
            LocalOffset = localGrabPoint,
            Target = target,
            ServoSettings = new ServoSettings(float.MaxValue, 0, 360 / inverseMass),
            SpringSettings = new SpringSettings(5, 2),
        };
        hasAngular = !Bodies.HasLockedInertia(body.LocalInertia.InverseInertiaTensor);
        angular = new OneBodyAngularServo
        {
            TargetOrientation = body.Pose.Orientation,
            ServoSettings = new ServoSettings(float.MaxValue, 0, localGrabPoint.Length() * 180 / inverseMass),
            SpringSettings = new SpringSettings(5, 2),
        };
    }

    private struct GunRayHitHandler : IRayHitHandler
    {
        public Dictionary<BodyHandle, uint> BodyToEntity;
        public float T;
        public uint EntityId;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable)
            => collidable.Mobility == CollidableMobility.Dynamic;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal,
            CollidableReference collidable, int childIndex)
        {
            if (!BodyToEntity.ContainsKey(collidable.BodyHandle)) return;
            maximumT = t;
            T = t;
            EntityId = BodyToEntity[collidable.BodyHandle];
        }
    }
}
