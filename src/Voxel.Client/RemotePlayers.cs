using Voxel.Shared;

namespace Voxel.Client;

/// <summary>
/// Other players, rendered as colored boxes and smoothed toward their last
/// reported position (10 Hz updates, rendered at frame rate) — parity with
/// the web client. Nametags arrive with the GUI/text pass (P6).
/// </summary>
public sealed class RemotePlayers
{
    public sealed class Player
    {
        public required string Name { get; init; }
        public required (float R, float G, float B) Color { get; init; }
        public float X, Y, Z;
        public float TargetX, TargetY, TargetZ;
        public bool Seen;
    }

    private readonly Dictionary<int, Player> _players = new();

    public int Count => _players.Count;

    public IEnumerable<Player> All => _players.Values;

    public void Handle(ServerEvent evt, int selfId)
    {
        switch (evt)
        {
            case ServerEvent.PlayerJoined joined when joined.Id != selfId:
            {
                // Golden-angle hue spacing keeps colors distinct (same as web).
                float hue = joined.Id * 137.508f % 360f;
                _players.TryAdd(joined.Id, new Player
                {
                    Name = joined.Name,
                    Color = HslToRgb(hue / 360f, 0.7f, 0.55f),
                });
                break;
            }
            case ServerEvent.PlayerLeft left:
                _players.Remove(left.Id);
                break;
            case ServerEvent.PlayerMoved moved:
            {
                foreach (var m in moved.Moves)
                {
                    if (m.Id == selfId || !_players.TryGetValue(m.Id, out var p)) continue;
                    // Camera position is the player's eyes; the box is centered lower.
                    p.TargetX = m.X;
                    p.TargetY = m.Y - 0.7f;
                    p.TargetZ = m.Z;
                    if (!p.Seen)
                    {
                        p.Seen = true;
                        (p.X, p.Y, p.Z) = (p.TargetX, p.TargetY, p.TargetZ);
                    }
                }
                break;
            }
            default:
                break;
        }
    }

    public void Update(float dt)
    {
        float alpha = 1f - MathF.Exp(-dt * 12f);
        foreach (var p in _players.Values)
        {
            p.X += (p.TargetX - p.X) * alpha;
            p.Y += (p.TargetY - p.Y) * alpha;
            p.Z += (p.TargetZ - p.Z) * alpha;
        }
    }

    private static (float, float, float) HslToRgb(float h, float s, float l)
    {
        float Channel(float n)
        {
            float k = (n + h * 12f) % 12f;
            float a = s * MathF.Min(l, 1 - l);
            return l - a * MathF.Max(-1, MathF.Min(MathF.Min(k - 3, 9 - k), 1));
        }
        return (Channel(0), Channel(8), Channel(4));
    }
}
