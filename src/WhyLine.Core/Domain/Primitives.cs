namespace WhyLine.Core.Domain;

/// <summary>Pitch coordinates in metres. X runs 0..105 (Home attacks towards +X), Y runs 0..68.</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);

    [System.Text.Json.Serialization.JsonIgnore]
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double DistanceTo(Vec2 other) => (this - other).Length;

    public Vec2 Normalized()
    {
        var len = Length;
        return len < 1e-9 ? new Vec2(0, 0) : new Vec2(X / len, Y / len);
    }

    public Vec2 MoveTowards(Vec2 target, double maxStep)
    {
        var delta = target - this;
        var dist = delta.Length;
        if (dist <= maxStep || dist < 1e-9) return target;
        return this + delta * (maxStep / dist);
    }

    public Vec2 Clamp() => new(Math.Clamp(X, 0, Pitch.Length), Math.Clamp(Y, 0, Pitch.Width));

    public override string ToString() => $"({X:0.0},{Y:0.0})";
}

public static class Pitch
{
    public const double Length = 105.0;
    public const double Width = 68.0;
    public static readonly Vec2 Centre = new(Length / 2, Width / 2);

    /// <summary>Goal centre that the given side attacks.</summary>
    public static Vec2 GoalAttackedBy(TeamSide side) => side == TeamSide.Home ? new Vec2(Length, Width / 2) : new Vec2(0, Width / 2);

    /// <summary>0 = own goal line, 1 = opponent goal line, from the perspective of <paramref name="side"/>.</summary>
    public static double Progress(TeamSide side, Vec2 p) => side == TeamSide.Home ? p.X / Length : 1 - p.X / Length;

    /// <summary>Third of the pitch from the attacking team's perspective: 1 defensive, 2 middle, 3 final.</summary>
    public static int Third(TeamSide side, Vec2 p) => Progress(side, p) switch
    {
        < 1.0 / 3 => 1,
        < 2.0 / 3 => 2,
        _ => 3
    };
}

public enum TeamSide { Home, Away }

public static class TeamSideExtensions
{
    public static TeamSide Opponent(this TeamSide side) => side == TeamSide.Home ? TeamSide.Away : TeamSide.Home;
    public static int Sign(this TeamSide side) => side == TeamSide.Home ? 1 : -1;
}
