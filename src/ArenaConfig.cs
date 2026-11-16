using CounterStrikeSharp.API.Core;

namespace Arena1v1;

public class Loadout
{
    public string Name { get; set; } = "";
    public List<string> Weapons { get; set; } = new();
}

/// <summary>
/// Généré automatiquement dans addons/counterstrikesharp/configs/plugins/Arena1v1/Arena1v1.json
/// </summary>
public class ArenaConfig : BasePluginConfig
{
    /// <summary>
    /// "Total"     : le duel s'arrête quand la somme des morts des deux joueurs atteint DeathsBeforeRotation.
    /// "PerPlayer" : le duel s'arrête quand UN joueur atteint DeathsBeforeRotation morts (premier à N kills).
    /// </summary>
    public string RotationMode { get; set; } = "Total";

    /// <summary>Nombre de morts avant de changer d'adversaire.</summary>
    public int DeathsBeforeRotation { get; set; } = 5;

    /// <summary>Délai (s) avant de faire réapparaître le joueur mort pendant un duel.</summary>
    public float RespawnDelay { get; set; } = 1.5f;

    /// <summary>Délai (s) entre la fin d'un duel et l'attribution d'un nouvel adversaire.</summary>
    public float RotationDelay { get; set; } = 2.0f;

    /// <summary>Après chaque kill, le survivant est aussi soigné, rééquipé et replacé à son spawn.</summary>
    public bool ResetBothOnKill { get; set; } = true;

    /// <summary>
    /// Si seuls deux joueurs attendent et qu'ils viennent de s'affronter, autoriser la revanche
    /// uniquement si aucun autre duel n'est en cours (sinon ils attendent un nouvel adversaire).
    /// </summary>
    public bool AllowRematchIfAlone { get; set; } = true;

    public int Health { get; set; } = 100;
    public bool GiveArmor { get; set; } = true;

    /// <summary>Affiche le score du duel au centre de l'écran.</summary>
    public bool ShowHud { get; set; } = true;

    /// <summary>
    /// "Cycle"  : les loadouts s'enchaînent dans l'ordre de la liste (Pistolets -> Fusils -> AWP -> Pistolets...).
    /// "Random" : loadout tiré au hasard.
    /// Dans les deux cas, un joueur ne retombe jamais deux fois de suite sur le même loadout (s'il y en a plusieurs).
    /// </summary>
    public string LoadoutMode { get; set; } = "Cycle";

    /// <summary>
    /// "Duel" : les armes changent à chaque nouveau duel.
    /// "Kill" : les armes changent après chaque mort, à l'intérieur d'un même duel.
    /// </summary>
    public string LoadoutChange { get; set; } = "Duel";

    /// <summary>Ordre du cycle = ordre de cette liste.</summary>
    public List<Loadout> Loadouts { get; set; } = new()
    {
        new Loadout { Name = "Pistolets", Weapons = new() { "weapon_deagle" } },
        new Loadout { Name = "Fusils d'assaut", Weapons = new() { "weapon_ak47", "weapon_deagle" } },
        new Loadout { Name = "AWP", Weapons = new() { "weapon_awp", "weapon_deagle" } },
    };

    /// <summary>
    /// Détection automatique (quand aucune arène n'est configurée pour la map) :
    /// distance max entre deux spawns d'un même camp pour qu'ils soient dans la même arène.
    /// </summary>
    public float AutoDetectClusterRadius { get; set; } = 400f;

    /// <summary>Commandes exécutées à chaque chargement de map.</summary>
    public List<string> ServerCommands { get; set; } = new()
    {
        "mp_teammates_are_enemies 1",
        "mp_ignore_round_win_conditions 1",
        "mp_respawn_on_death_t 0",
        "mp_respawn_on_death_ct 0",
        "mp_roundtime 60",
        "mp_roundtime_defuse 60",
        "mp_roundtime_hostage 60",
        "mp_timelimit 30",
        "mp_freezetime 0",
        "mp_autoteambalance 0",
        "mp_limitteams 0",
        "mp_buytime 0",
        "mp_buy_anywhere 0",
        "mp_give_player_c4 0",
        "mp_death_drop_gun 0",
        "mp_weapons_allow_map_placed 0",
        "mp_t_default_secondary \"\"",
        "mp_ct_default_secondary \"\"",
        "mp_warmup_end",
    };
}
