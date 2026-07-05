namespace Voxel.Server;

/// <summary>Snapshot of a physics contraption/body for SQLite persistence (plan 03 M5).</summary>
public sealed record SavedEntity(
    uint Id,
    byte Kind,
    ushort DimX,
    ushort DimY,
    ushort DimZ,
    ushort[] Blocks,
    float PivotX,
    float PivotY,
    float PivotZ,
    double PosX,
    double PosY,
    double PosZ,
    float Qx,
    float Qy,
    float Qz,
    float Qw,
    float VelX,
    float VelY,
    float VelZ,
    float AngVelX,
    float AngVelY,
    float AngVelZ,
    bool Asleep);
