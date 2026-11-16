using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Arena1v1;

/// <summary>Chargement / sauvegarde des arènes par map + détection automatique.</summary>
public class ArenaStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;

    public ArenaStore(string directory) => _directory = directory;

    private string PathFor(string map) => Path.Combine(_directory, $"{map}.json");

    public List<ArenaDef>? Load(string map)
    {
        var path = PathFor(map);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<List<ArenaDef>>(File.ReadAllText(path), JsonOptions);
    }

    public string Save(string map, List<ArenaDef> arenas)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(map);
        File.WriteAllText(path, JsonSerializer.Serialize(arenas, JsonOptions));
        return path;
    }

    /// <summary>
    /// Regroupe les spawns T et CT proches en "clusters", puis associe chaque cluster T
    /// au cluster CT le plus proche. Fonctionne bien sur les maps d'arènes (am_*),
    /// sur une map classique cela ne donnera qu'une seule grande arène.
    /// </summary>
    public static List<ArenaDef> AutoDetect(float clusterRadius)
    {
        var tClusters = Cluster(GetSpawns("info_player_terrorist"), clusterRadius);
        var ctClusters = Cluster(GetSpawns("info_player_counterterrorist"), clusterRadius);

        var pairs = new List<(int t, int ct, float dist)>();
        for (int i = 0; i < tClusters.Count; i++)
            for (int j = 0; j < ctClusters.Count; j++)
                pairs.Add((i, j, tClusters[i][0].DistanceTo(ctClusters[j][0])));

        var usedT = new HashSet<int>();
        var usedCt = new HashSet<int>();
        var arenas = new List<ArenaDef>();

        foreach (var (t, ct, _) in pairs.OrderBy(p => p.dist))
        {
            if (usedT.Contains(t) || usedCt.Contains(ct)) continue;
            usedT.Add(t);
            usedCt.Add(ct);
            arenas.Add(new ArenaDef { Spawn1 = tClusters[t][0], Spawn2 = ctClusters[ct][0] });
        }

        return arenas;
    }

    private static List<SpawnData> GetSpawns(string designerName)
    {
        var result = new List<SpawnData>();
        foreach (var sp in Utilities.FindAllEntitiesByDesignerName<SpawnPoint>(designerName))
        {
            if (sp is not { IsValid: true } || sp.AbsOrigin == null) continue;
            result.Add(new SpawnData
            {
                X = sp.AbsOrigin.X,
                Y = sp.AbsOrigin.Y,
                Z = sp.AbsOrigin.Z,
                Yaw = sp.AbsRotation?.Y ?? 0,
            });
        }
        return result;
    }

    private static List<List<SpawnData>> Cluster(List<SpawnData> spawns, float radius)
    {
        var clusters = new List<List<SpawnData>>();
        foreach (var s in spawns)
        {
            var target = clusters.FirstOrDefault(c => c.Any(o => o.DistanceTo(s) <= radius));
            if (target != null) target.Add(s);
            else clusters.Add(new List<SpawnData> { s });
        }
        return clusters;
    }
}
