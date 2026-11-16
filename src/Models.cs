using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Arena1v1;

public class SpawnData
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }

    public Vector ToVector() => new(X, Y, Z);
    public QAngle ToAngle() => new(0, Yaw, 0);

    public float DistanceTo(SpawnData o)
    {
        float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"({X:0}, {Y:0}, {Z:0}) yaw {Yaw:0}";
}

public class ArenaDef
{
    public SpawnData? Spawn1 { get; set; }
    public SpawnData? Spawn2 { get; set; }

    public bool IsComplete => Spawn1 != null && Spawn2 != null;
}

public class ArenaPlayer
{
    public ArenaPlayer(int slot) => Slot = slot;

    public int Slot { get; }
    public Duel? Duel { get; set; }
    public ArenaPlayer? LastOpponent { get; set; }
    public bool OptedOut { get; set; }
    public bool Disconnected { get; set; }
    public int DuelsWon { get; set; }

    /// <summary>Index du dernier loadout joué (-1 = aucun), pour alterner les armes.</summary>
    public int LastLoadoutIndex { get; set; } = -1;

    public CCSPlayerController? Controller => Utilities.GetPlayerFromSlot(Slot);
    public string Name => Controller?.PlayerName ?? "?";
}

public class Duel
{
    public Duel(int arenaIndex, ArenaPlayer a, ArenaPlayer b)
    {
        ArenaIndex = arenaIndex;
        A = a;
        B = b;
    }

    public int ArenaIndex { get; }
    public ArenaPlayer A { get; }
    public ArenaPlayer B { get; }

    public int LoadoutIndex { get; private set; }
    public Loadout Loadout { get; private set; } = new();

    public void SetLoadout(int index, Loadout loadout)
    {
        LoadoutIndex = index;
        Loadout = loadout;
        A.LastLoadoutIndex = index;
        B.LastLoadoutIndex = index;
    }

    public int DeathsA { get; private set; }
    public int DeathsB { get; private set; }

    /// <summary>Le duel a atteint sa limite, on attend la rotation.</summary>
    public bool Ending { get; set; }

    /// <summary>Le duel est terminé et les joueurs ont été libérés.</summary>
    public bool Finished { get; set; }

    public ArenaPlayer Other(ArenaPlayer p) => p == A ? B : A;
    public int KillsOf(ArenaPlayer p) => p == A ? DeathsB : DeathsA;

    public void AddDeath(ArenaPlayer victim)
    {
        if (victim == A) DeathsA++;
        else DeathsB++;
    }

    public bool IsOver(string mode, int limit) =>
        mode.Equals("PerPlayer", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(DeathsA, DeathsB) >= limit
            : DeathsA + DeathsB >= limit;

    /// <summary>Le joueur avec le moins de morts gagne ; null en cas d'égalité.</summary>
    public ArenaPlayer? Winner() =>
        DeathsA < DeathsB ? A : DeathsB < DeathsA ? B : null;
}
