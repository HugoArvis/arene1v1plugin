using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Arena1v1;

[MinimumApiVersion(300)]
public class Arena1v1Plugin : BasePlugin, IPluginConfig<ArenaConfig>
{
    public override string ModuleName => "Arena 1v1";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "Hugo";
    public override string ModuleDescription => "Arènes 1v1 avec rotation des adversaires après N morts";

    public ArenaConfig Config { get; set; } = new();

    private readonly Dictionary<int, ArenaPlayer> _players = new();
    private readonly List<ArenaPlayer> _queue = new();
    private readonly Random _rng = new();

    private List<ArenaDef> _arenas = new();
    private Duel?[] _duels = Array.Empty<Duel?>();
    private bool _arenasLoaded;
    private string _mapName = "";
    private ArenaStore _store = null!;

    private static string Prefix => $" {ChatColors.Green}[Arena]{ChatColors.Default}";

    // ------------------------------------------------------------------ cycle de vie

    public void OnConfigParsed(ArenaConfig config)
    {
        if (config.DeathsBeforeRotation < 1) config.DeathsBeforeRotation = 1;
        if (config.Loadouts.Count == 0)
            config.Loadouts.Add(new Loadout { Name = "Deagle", Weapons = new() { "weapon_deagle" } });
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        _store = new ArenaStore(Path.Combine(ModuleDirectory, "arenas"));

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        AddCommandListener("jointeam", OnJoinTeam);

        AddTimer(1.0f, HudTick, TimerFlags.REPEAT);

        if (hotReload)
        {
            OnMapStart(Server.MapName);
            foreach (var c in Utilities.GetPlayers().Where(IsValidPlayer))
                Enqueue(GetOrCreate(c));
        }
    }

    private void OnMapStart(string mapName)
    {
        _mapName = mapName;
        _arenasLoaded = false;
        _arenas = new();
        _duels = Array.Empty<Duel?>();
        _players.Clear();
        _queue.Clear();

        // On laisse le temps aux entités de la map d'apparaître avant de lire les spawns.
        AddTimer(1.0f, () =>
        {
            foreach (var cmd in Config.ServerCommands)
                Server.ExecuteCommand(cmd);
            EnsureArenasLoaded();
            TryMatch();
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void EnsureArenasLoaded()
    {
        if (_arenasLoaded) return;

        List<ArenaDef>? arenas = null;
        try
        {
            arenas = _store.Load(_mapName);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Impossible de lire le fichier d'arènes de {Map}", _mapName);
        }

        if (arenas is { Count: > 0 })
        {
            Logger.LogInformation("{Count} arène(s) chargée(s) pour {Map}", arenas.Count, _mapName);
        }
        else
        {
            arenas = ArenaStore.AutoDetect(Config.AutoDetectClusterRadius);
            Logger.LogInformation("{Count} arène(s) détectée(s) automatiquement sur {Map}", arenas.Count, _mapName);
        }

        _arenas = arenas;
        _duels = new Duel?[_arenas.Count];
        _arenasLoaded = true;
    }

    private bool ModeActive => _arenasLoaded && _arenas.Any(a => a.IsComplete);

    // ------------------------------------------------------------------ joueurs

    private static bool IsValidPlayer(CCSPlayerController? c) =>
        c is { IsValid: true, IsHLTV: false, Connected: PlayerConnectedState.PlayerConnected };

    private ArenaPlayer GetOrCreate(CCSPlayerController c)
    {
        if (!_players.TryGetValue(c.Slot, out var ap))
        {
            ap = new ArenaPlayer(c.Slot);
            _players[c.Slot] = ap;
        }
        return ap;
    }

    private void Enqueue(ArenaPlayer ap)
    {
        if (ap.OptedOut || ap.Disconnected || ap.Duel != null || _queue.Contains(ap)) return;
        _queue.Add(ap);
    }

    private static void MoveToSpectator(CCSPlayerController c)
    {
        if (c.Team != CsTeam.Spectator)
            c.ChangeTeam(CsTeam.Spectator);
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var c = @event.Userid;
        if (!IsValidPlayer(c)) return HookResult.Continue;

        var ap = GetOrCreate(c!);
        Enqueue(ap);

        AddTimer(0.5f, () =>
        {
            if (!ModeActive || ap.Disconnected) return;
            TryMatch();
            var ctrl = ap.Controller;
            if (ap.Duel == null && IsValidPlayer(ctrl))
            {
                MoveToSpectator(ctrl!);
                ctrl!.PrintToChat($"{Prefix} Bienvenue ! Tu es dans la file d'attente des duels. Tape {ChatColors.Yellow}!arena{ChatColors.Default} pour ton statut.");
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int slot)
    {
        if (!_players.Remove(slot, out var ap)) return;

        ap.Disconnected = true;
        _queue.Remove(ap);

        if (ap.Duel is { Finished: false } duel)
        {
            var opp = duel.Other(ap);
            opp.Controller?.PrintToChat($"{Prefix} Ton adversaire a quitté le serveur.");
            EndDuel(duel, opp);
        }
    }

    /// <summary>
    /// Les équipes sont gérées par le plugin : un joueur qui choisit T/CT rejoint la file,
    /// un joueur qui choisit Spectateur se met en pause.
    /// </summary>
    private HookResult OnJoinTeam(CCSPlayerController? c, CommandInfo info)
    {
        if (!ModeActive || !IsValidPlayer(c)) return HookResult.Continue;
        if (!int.TryParse(info.GetArg(1), out var team)) return HookResult.Continue;

        var ap = GetOrCreate(c!);

        if (team == (int)CsTeam.Spectator)
        {
            SetOptOut(ap, true);
            return HookResult.Continue; // on laisse le jeu le passer spectateur
        }

        if (ap.OptedOut)
            SetOptOut(ap, false);
        else if (ap.Duel == null)
            c!.PrintToChat($"{Prefix} Tu es déjà dans la file d'attente ({QueuePosition(ap)}).");

        return HookResult.Handled; // le plugin choisit l'équipe lui-même
    }

    private void SetOptOut(ArenaPlayer ap, bool optOut)
    {
        var c = ap.Controller;
        if (optOut)
        {
            if (ap.OptedOut) return;
            ap.OptedOut = true;
            _queue.Remove(ap);
            if (ap.Duel is { Finished: false } duel)
            {
                duel.Other(ap).Controller?.PrintToChat($"{Prefix} Ton adversaire a abandonné le duel.");
                EndDuel(duel, duel.Other(ap));
            }
            if (IsValidPlayer(c)) MoveToSpectator(c!);
            c?.PrintToChat($"{Prefix} Tu es en pause. Tape {ChatColors.Yellow}!afk{ChatColors.Default} pour revenir.");
        }
        else
        {
            if (!ap.OptedOut) return;
            ap.OptedOut = false;
            Enqueue(ap);
            TryMatch();
            if (ap.Duel == null)
                c?.PrintToChat($"{Prefix} Tu es de retour dans la file d'attente ({QueuePosition(ap)}).");
        }
    }

    private string QueuePosition(ArenaPlayer ap)
    {
        int i = _queue.IndexOf(ap);
        return i < 0 ? "hors file" : $"position {i + 1}/{_queue.Count}";
    }

    // ------------------------------------------------------------------ matchmaking

    private int FindFreeArena()
    {
        for (int i = 0; i < _arenas.Count; i++)
            if (_arenas[i].IsComplete && _duels[i] == null)
                return i;
        return -1;
    }

    private bool AnyDuelRunning() => _duels.Any(d => d is { Finished: false });

    private void TryMatch()
    {
        if (!ModeActive) return;

        _queue.RemoveAll(ap => ap.Disconnected || ap.OptedOut || !IsValidPlayer(ap.Controller));

        while (_queue.Count >= 2)
        {
            int arena = FindFreeArena();
            if (arena < 0) break;

            // On cherche la première paire (dans l'ordre de la file) qui ne vient pas de s'affronter.
            ArenaPlayer? a = null, b = null;
            for (int i = 0; i < _queue.Count && a == null; i++)
            {
                for (int j = i + 1; j < _queue.Count; j++)
                {
                    if (_queue[i].LastOpponent == _queue[j] || _queue[j].LastOpponent == _queue[i]) continue;
                    a = _queue[i];
                    b = _queue[j];
                    break;
                }
            }

            if (a == null || b == null)
            {
                // Seule une revanche est possible : on l'autorise si personne d'autre ne joue.
                if (!Config.AllowRematchIfAlone || _queue.Count != 2 || AnyDuelRunning()) break;
                a = _queue[0];
                b = _queue[1];
            }

            _queue.Remove(a);
            _queue.Remove(b);
            StartDuel(arena, a, b);
        }
    }

    /// <summary>
    /// Choisit le loadout d'un nouveau duel en évitant celui que chaque joueur vient de jouer.
    /// En mode "Cycle", on prend le suivant dans la liste après le dernier loadout du joueur A.
    /// </summary>
    private int PickLoadoutIndex(ArenaPlayer a, ArenaPlayer b)
    {
        int n = Config.Loadouts.Count;
        if (n == 1) return 0;

        var candidates = Enumerable.Range(0, n)
            .Where(i => i != a.LastLoadoutIndex && i != b.LastLoadoutIndex).ToList();
        if (candidates.Count == 0)
            candidates = Enumerable.Range(0, n).Where(i => i != a.LastLoadoutIndex).ToList();

        if (Config.LoadoutMode.Equals("Random", StringComparison.OrdinalIgnoreCase))
            return candidates[_rng.Next(candidates.Count)];

        int start = a.LastLoadoutIndex + 1;
        return candidates.OrderBy(i => ((i - start) % n + n) % n).First();
    }

    private bool ChangeLoadoutEachKill =>
        Config.LoadoutChange.Equals("Kill", StringComparison.OrdinalIgnoreCase) && Config.Loadouts.Count > 1;

    private string ModeText() =>
        Config.RotationMode.Equals("PerPlayer", StringComparison.OrdinalIgnoreCase)
            ? $"premier à {Config.DeathsBeforeRotation} kills"
            : $"{Config.DeathsBeforeRotation} morts au total";

    private void StartDuel(int arenaIndex, ArenaPlayer a, ArenaPlayer b)
    {
        var duel = new Duel(arenaIndex, a, b);
        int loadoutIndex = PickLoadoutIndex(a, b);
        duel.SetLoadout(loadoutIndex, Config.Loadouts[loadoutIndex]);
        _duels[arenaIndex] = duel;
        a.Duel = duel;
        b.Duel = duel;
        a.LastOpponent = b;
        b.LastOpponent = a;

        foreach (var p in new[] { a, b })
        {
            p.Controller?.PrintToChat(
                $"{Prefix} Arène {ChatColors.Yellow}#{arenaIndex + 1}{ChatColors.Default} : tu affrontes " +
                $"{ChatColors.LightRed}{duel.Other(p).Name}{ChatColors.Default} " +
                $"({duel.Loadout.Name}, {ModeText()}).");
        }

        BringIntoArena(a, duel, CsTeam.Terrorist);
        BringIntoArena(b, duel, CsTeam.CounterTerrorist);
    }

    private void BringIntoArena(ArenaPlayer ap, Duel duel, CsTeam team)
    {
        var c = ap.Controller;
        if (!IsValidPlayer(c)) return;

        if (c!.PawnIsAlive)
        {
            PreparePlayer(ap);
            return;
        }

        if (c.Team is CsTeam.Spectator or CsTeam.None)
            c.ChangeTeam(team);

        // Le Respawn déclenche EventPlayerSpawn -> PreparePlayer.
        AddTimer(0.1f, () => RespawnIfStillInDuel(ap, duel), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private void RespawnIfStillInDuel(ArenaPlayer ap, Duel duel)
    {
        if (duel.Finished || ap.Duel != duel) return;
        var c = ap.Controller;
        if (IsValidPlayer(c) && !c!.PawnIsAlive && c.Team is CsTeam.Terrorist or CsTeam.CounterTerrorist)
            c.Respawn();
    }

    /// <summary>
    /// Donne au joueur l'équipement du duel. Si fullReset, il est aussi téléporté à son spawn et soigné.
    /// </summary>
    private void PreparePlayer(ArenaPlayer ap, bool fullReset = true)
    {
        var duel = ap.Duel;
        var c = ap.Controller;
        if (duel == null || duel.Finished || !IsValidPlayer(c)) return;
        if (duel.ArenaIndex >= _arenas.Count) return;

        var pawn = c!.PlayerPawn.Value;
        if (pawn is not { IsValid: true }) return;

        var arena = _arenas[duel.ArenaIndex];
        var spawn = ap == duel.A ? arena.Spawn1 : arena.Spawn2;
        if (spawn == null) return;

        if (fullReset)
        {
            pawn.Teleport(spawn.ToVector(), spawn.ToAngle(), new Vector(0, 0, 0));
            pawn.Health = Config.Health;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }

        var loadout = duel.Loadout;
        c.RemoveWeapons();
        Server.NextFrame(() =>
        {
            if (!IsValidPlayer(c) || !c.PawnIsAlive) return;
            c.GiveNamedItem("weapon_knife");
            foreach (var weapon in loadout.Weapons)
                c.GiveNamedItem(weapon);

            if (fullReset && Config.GiveArmor)
            {
                c.GiveNamedItem("item_assaultsuit");
                var p = c.PlayerPawn.Value;
                if (p is { IsValid: true })
                {
                    p.ArmorValue = 100;
                    Utilities.SetStateChanged(p, "CCSPlayerPawn", "m_ArmorValue");
                }
            }
        });
    }

    // ------------------------------------------------------------------ événements de jeu

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var c = @event.Userid;
        if (!IsValidPlayer(c)) return HookResult.Continue;
        int slot = c!.Slot;

        Server.NextFrame(() =>
        {
            if (!ModeActive) return;
            var ctrl = Utilities.GetPlayerFromSlot(slot);
            if (!IsValidPlayer(ctrl) || !ctrl!.PawnIsAlive) return;

            var ap = GetOrCreate(ctrl);
            if (ap.Duel == null)
            {
                Enqueue(ap);
                TryMatch(); // si un duel démarre, BringIntoArena -> PreparePlayer
            }

            if (ap.Duel != null)
                PreparePlayer(ap);
            else
                MoveToSpectator(ctrl); // personne ne doit être en vie hors d'un duel
        });

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (!IsValidPlayer(victim)) return HookResult.Continue;
        if (!_players.TryGetValue(victim!.Slot, out var vap)) return HookResult.Continue;
        if (vap.Duel is not { Finished: false, Ending: false } duel) return HookResult.Continue;

        var opp = duel.Other(vap);
        duel.AddDeath(vap);

        string score = $"{duel.A.Name} {duel.KillsOf(duel.A)} - {duel.KillsOf(duel.B)} {duel.B.Name}";

        if (duel.IsOver(Config.RotationMode, Config.DeathsBeforeRotation))
        {
            duel.Ending = true;
            var winner = duel.Winner();
            foreach (var p in new[] { duel.A, duel.B })
            {
                var res = winner == null ? "Égalité !" : winner == p ? "Victoire !" : "Défaite.";
                p.Controller?.PrintToChat($"{Prefix} {res} {score} — changement d'adversaire...");
            }
            AddTimer(Config.RotationDelay, () => EndDuel(duel, winner), TimerFlags.STOP_ON_MAPCHANGE);
        }
        else
        {
            bool weaponsChanged = false;
            if (ChangeLoadoutEachKill)
            {
                int next = (duel.LoadoutIndex + 1) % Config.Loadouts.Count;
                duel.SetLoadout(next, Config.Loadouts[next]);
                weaponsChanged = true;
                foreach (var p in new[] { duel.A, duel.B })
                    p.Controller?.PrintToChat($"{Prefix} {score} — prochaine manche : {ChatColors.Yellow}{duel.Loadout.Name}");
            }

            AddTimer(Config.RespawnDelay, () =>
            {
                if (duel.Finished || duel.Ending) return;
                RespawnIfStillInDuel(vap, duel);
                // Le survivant est replacé/soigné si ResetBothOnKill, et rééquipé si les armes ont changé.
                if ((Config.ResetBothOnKill || weaponsChanged) && opp.Duel == duel && opp.Controller is { PawnIsAlive: true })
                    PreparePlayer(opp, fullReset: Config.ResetBothOnKill);
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }

        return HookResult.Continue;
    }

    /// <summary>Termine un duel, remet les joueurs dans la file (gagnant en premier) et relance le matchmaking.</summary>
    private void EndDuel(Duel duel, ArenaPlayer? winner)
    {
        if (duel.Finished) return;
        duel.Finished = true;

        if (duel.ArenaIndex < _duels.Length && _duels[duel.ArenaIndex] == duel)
            _duels[duel.ArenaIndex] = null;

        if (winner != null) winner.DuelsWon++;

        var order = winner == duel.B ? new[] { duel.B, duel.A } : new[] { duel.A, duel.B };
        ReleasePlayers(order);
    }

    private void ReleasePlayers(IEnumerable<ArenaPlayer> players)
    {
        var list = players.ToList();
        foreach (var p in list)
        {
            p.Duel = null;
            Enqueue(p);
        }

        TryMatch();

        foreach (var p in list)
        {
            if (p.Duel != null || p.Disconnected) continue;
            var c = p.Controller;
            if (!IsValidPlayer(c)) continue;
            MoveToSpectator(c!);
            if (!p.OptedOut)
                c!.PrintToChat($"{Prefix} En attente d'un adversaire ({QueuePosition(p)}).");
        }
    }

    /// <summary>Annule tous les duels sans résultat (utilisé quand la liste des arènes change).</summary>
    private void CancelAllDuels()
    {
        var players = new List<ArenaPlayer>();
        foreach (var d in _duels)
        {
            if (d == null || d.Finished) continue;
            d.Finished = true;
            players.Add(d.A);
            players.Add(d.B);
        }
        _duels = new Duel?[_arenas.Count];
        ReleasePlayers(players);
    }

    private void HudTick()
    {
        if (!Config.ShowHud || !ModeActive) return;

        foreach (var d in _duels)
        {
            if (d == null || d.Finished) continue;
            string html =
                $"<font color='#7CFC00'>Arène #{d.ArenaIndex + 1}</font> · {d.Loadout.Name}<br>" +
                $"{d.A.Name} <b>{d.KillsOf(d.A)} - {d.KillsOf(d.B)}</b> {d.B.Name}";
            d.A.Controller?.PrintToCenterHtml(html, 2);
            d.B.Controller?.PrintToCenterHtml(html, 2);
        }

        foreach (var ap in _queue)
            ap.Controller?.PrintToCenterHtml($"En attente d'un adversaire<br>{QueuePosition(ap)}", 2);
    }

    // ------------------------------------------------------------------ commandes joueurs

    [ConsoleCommand("css_arena", "Affiche ton statut dans les arènes")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdStatus(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null) return;
        if (!ModeActive)
        {
            info.ReplyToCommand($"{Prefix} Aucune arène sur cette map.");
            return;
        }

        var ap = GetOrCreate(caller);
        if (ap.Duel is { } d)
            info.ReplyToCommand($"{Prefix} Arène #{d.ArenaIndex + 1} contre {d.Other(ap).Name} : {d.KillsOf(ap)} - {d.KillsOf(d.Other(ap))} ({ModeText()}). Duels gagnés : {ap.DuelsWon}");
        else if (ap.OptedOut)
            info.ReplyToCommand($"{Prefix} Tu es en pause. Tape !afk pour revenir.");
        else
            info.ReplyToCommand($"{Prefix} En attente ({QueuePosition(ap)}). Duels gagnés : {ap.DuelsWon}");
    }

    [ConsoleCommand("css_afk", "Se mettre en pause / revenir dans la file")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    public void CmdAfk(CCSPlayerController? caller, CommandInfo info)
    {
        if (caller == null || !ModeActive) return;
        var ap = GetOrCreate(caller);
        SetOptOut(ap, !ap.OptedOut);
    }

    // ------------------------------------------------------------------ commandes admin (édition des arènes)

    private static SpawnData? SpawnFromPlayer(CCSPlayerController? c)
    {
        var pawn = c?.PlayerPawn.Value;
        if (pawn is not { IsValid: true } || pawn.AbsOrigin == null) return null;
        return new SpawnData
        {
            X = pawn.AbsOrigin.X,
            Y = pawn.AbsOrigin.Y,
            Z = pawn.AbsOrigin.Z,
            Yaw = pawn.EyeAngles.Y,
        };
    }

    private bool TryGetArenaId(CommandInfo info, int argIndex, out int index)
    {
        index = -1;
        if (!int.TryParse(info.GetArg(argIndex), out var id) || id < 1 || id > _arenas.Count)
        {
            info.ReplyToCommand($"{Prefix} Id d'arène invalide (1 à {_arenas.Count}).");
            return false;
        }
        index = id - 1;
        return true;
    }

    private void GrowDuelsArray()
    {
        if (_duels.Length >= _arenas.Count) return;
        var bigger = new Duel?[_arenas.Count];
        Array.Copy(_duels, bigger, _duels.Length);
        _duels = bigger;
    }

    [ConsoleCommand("css_arena_list", "Liste les arènes de la map")]
    [RequiresPermissions("@css/root")]
    public void CmdList(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        info.ReplyToCommand($"{Prefix} {_arenas.Count} arène(s) sur {_mapName} :");
        for (int i = 0; i < _arenas.Count; i++)
        {
            var a = _arenas[i];
            var state = _duels.Length > i && _duels[i] is { } d ? $"{d.A.Name} vs {d.B.Name}" : "libre";
            info.ReplyToCommand($"  #{i + 1} S1 {a.Spawn1?.ToString() ?? "—"} | S2 {a.Spawn2?.ToString() ?? "—"} | {state}");
        }
    }

    [ConsoleCommand("css_arena_add", "Crée une arène avec le spawn 1 à ta position")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void CmdAdd(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        var spawn = SpawnFromPlayer(caller);
        if (spawn == null)
        {
            info.ReplyToCommand($"{Prefix} Tu dois être en vie.");
            return;
        }
        _arenas.Add(new ArenaDef { Spawn1 = spawn });
        GrowDuelsArray();
        info.ReplyToCommand($"{Prefix} Arène #{_arenas.Count} créée. Va de l'autre côté et tape css_arena_setspawn {_arenas.Count} 2, puis css_arena_save.");
    }

    [ConsoleCommand("css_arena_setspawn", "Définit le spawn 1 ou 2 d'une arène à ta position")]
    [CommandHelper(minArgs: 2, usage: "<id> <1|2>", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void CmdSetSpawn(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        if (!TryGetArenaId(info, 1, out var idx)) return;
        var spawn = SpawnFromPlayer(caller);
        if (spawn == null)
        {
            info.ReplyToCommand($"{Prefix} Tu dois être en vie.");
            return;
        }

        switch (info.GetArg(2))
        {
            case "1": _arenas[idx].Spawn1 = spawn; break;
            case "2": _arenas[idx].Spawn2 = spawn; break;
            default:
                info.ReplyToCommand($"{Prefix} Le spawn doit être 1 ou 2.");
                return;
        }
        info.ReplyToCommand($"{Prefix} Spawn {info.GetArg(2)} de l'arène #{idx + 1} défini {spawn}.");
        TryMatch();
    }

    [ConsoleCommand("css_arena_del", "Supprime une arène")]
    [CommandHelper(minArgs: 1, usage: "<id>")]
    [RequiresPermissions("@css/root")]
    public void CmdDelete(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        if (!TryGetArenaId(info, 1, out var idx)) return;
        _arenas.RemoveAt(idx);
        CancelAllDuels();
        info.ReplyToCommand($"{Prefix} Arène #{idx + 1} supprimée (pense à css_arena_save).");
    }

    [ConsoleCommand("css_arena_tp", "Te téléporte au spawn d'une arène")]
    [CommandHelper(minArgs: 1, usage: "<id> [1|2]", whoCanExecute: CommandUsage.CLIENT_ONLY)]
    [RequiresPermissions("@css/root")]
    public void CmdTeleport(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        if (!TryGetArenaId(info, 1, out var idx)) return;
        var spawn = info.GetArg(2) == "2" ? _arenas[idx].Spawn2 : _arenas[idx].Spawn1;
        var pawn = caller?.PlayerPawn.Value;
        if (spawn == null || pawn is not { IsValid: true })
        {
            info.ReplyToCommand($"{Prefix} Spawn non défini ou tu n'es pas en vie.");
            return;
        }
        pawn.Teleport(spawn.ToVector(), spawn.ToAngle(), new Vector(0, 0, 0));
    }

    [ConsoleCommand("css_arena_save", "Sauvegarde les arènes de la map")]
    [RequiresPermissions("@css/root")]
    public void CmdSave(CCSPlayerController? caller, CommandInfo info)
    {
        EnsureArenasLoaded();
        var path = _store.Save(_mapName, _arenas);
        info.ReplyToCommand($"{Prefix} {_arenas.Count} arène(s) sauvegardée(s) dans {path}");
    }

    [ConsoleCommand("css_arena_auto", "Recrée les arènes à partir des spawns de la map")]
    [RequiresPermissions("@css/root")]
    public void CmdAuto(CCSPlayerController? caller, CommandInfo info)
    {
        _arenas = ArenaStore.AutoDetect(Config.AutoDetectClusterRadius);
        _arenasLoaded = true;
        CancelAllDuels();
        info.ReplyToCommand($"{Prefix} {_arenas.Count} arène(s) détectée(s) (non sauvegardées, utilise css_arena_save).");
    }

    [ConsoleCommand("css_arena_reload", "Recharge les arènes depuis le fichier")]
    [RequiresPermissions("@css/root")]
    public void CmdReload(CCSPlayerController? caller, CommandInfo info)
    {
        _arenasLoaded = false;
        EnsureArenasLoaded();
        CancelAllDuels();
        info.ReplyToCommand($"{Prefix} {_arenas.Count} arène(s) chargée(s).");
    }
}
