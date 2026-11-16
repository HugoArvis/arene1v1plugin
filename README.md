# Arena 1v1 — plugin CounterStrikeSharp (CS2)

Mode de jeu en arènes : les joueurs s'affrontent en 1v1, chacun dans son arène.
Au bout de N morts (configurable), le duel se termine et chaque joueur reçoit un nouvel adversaire.

## Fonctionnement

- Chaque joueur entre dans une **file d'attente** (en spectateur).
- Dès que 2 joueurs et une arène sont libres, un **duel** démarre (loadout aléatoire, configurable).
- Chaque mort : le mort réapparaît après `RespawnDelay` s ; le survivant est soigné et replacé (`ResetBothOnKill`).
- Quand la limite est atteinte (`DeathsBeforeRotation`, voir `RotationMode`), les deux joueurs retournent
  dans la file (le gagnant passe en premier) et le matchmaking **évite de remettre le même adversaire**.
- Avec un nombre impair de joueurs, un joueur attend en spectateur et prend la place au prochain duel terminé.

## Compiler

Il faut le **SDK .NET 10** (CounterStrikeSharp 1.0.380 cible `net10.0`) : https://dotnet.microsoft.com/download

```
dotnet build -c Release
```

La DLL se trouve dans `bin/Release/net10.0/Arena1v1.dll`.

## Installer sur le serveur

1. Le serveur CS2 doit avoir Metamod:Source + CounterStrikeSharp **v380 ou plus récent** (même version que le paquet NuGet).
2. Copier `Arena1v1.dll` dans `game/csgo/addons/counterstrikesharp/plugins/Arena1v1/`.
3. Redémarrer ou taper `css_plugins load Arena1v1` dans la console serveur.
4. La config est générée dans `addons/counterstrikesharp/configs/plugins/Arena1v1/Arena1v1.json`.

## Configuration (`Arena1v1.json`)

| Option | Défaut | Description |
|---|---|---|
| `RotationMode` | `"Total"` | `"Total"` : fin quand la somme des morts des 2 joueurs = N. `"PerPlayer"` : fin quand un joueur a N morts (premier à N kills). |
| `DeathsBeforeRotation` | `5` | Le N ci-dessus. |
| `RespawnDelay` | `1.5` | Délai de réapparition pendant un duel (s). |
| `RotationDelay` | `2.0` | Délai avant le changement d'adversaire (s). |
| `ResetBothOnKill` | `true` | Le survivant est aussi soigné/rééquipé/replacé à chaque kill. |
| `AllowRematchIfAlone` | `true` | Autorise une revanche si personne d'autre n'est disponible. |
| `Health` / `GiveArmor` | `100` / `true` | Vie et kevlar+casque. |
| `ShowHud` | `true` | Score affiché au centre de l'écran. |
| `LoadoutMode` | `"Cycle"` | `"Cycle"` : les armes s'enchaînent dans l'ordre de `Loadouts` (Pistolets → Fusils → AWP → ...). `"Random"` : au hasard. Un joueur ne rejoue jamais deux fois de suite le même loadout. |
| `LoadoutChange` | `"Duel"` | `"Duel"` : changement d'armes à chaque nouveau duel. `"Kill"` : changement après chaque mort dans le duel. |
| `Loadouts` | Pistolets, Fusils d'assaut, AWP | Liste ordonnée `{ "Name", "Weapons": [...] }` (ex. `"weapon_ak47"`, `"weapon_awp"`). |
| `AutoDetectClusterRadius` | `400` | Détection auto : distance max entre spawns d'une même arène. |
| `ServerCommands` | voir fichier | Cvars appliquées à chaque map (respawn désactivé, rounds sans fin, etc.). |

## Arènes

Au chargement d'une map, le plugin lit `plugins/Arena1v1/arenas/<map>.json`.
S'il n'existe pas, il **détecte automatiquement** les arènes en regroupant les spawns T et CT proches.
Ça marche bien sur les maps d'arènes (`am_*`, workshop « multi 1v1 »), sur une map classique ça donne une seule arène.

Pour créer les arènes à la main (admin `@css/root`) :

| Commande | Effet |
|---|---|
| `css_arena_add` | Crée une arène, spawn 1 = ta position et ta direction |
| `css_arena_setspawn <id> <1\|2>` | Définit le spawn 1 ou 2 à ta position |
| `css_arena_list` | Liste les arènes |
| `css_arena_tp <id> [1\|2]` | Te téléporte à un spawn |
| `css_arena_del <id>` | Supprime une arène |
| `css_arena_auto` | Recrée les arènes à partir des spawns de la map |
| `css_arena_save` | Sauvegarde dans `arenas/<map>.json` |
| `css_arena_reload` | Recharge depuis le fichier |

Toutes les commandes marchent aussi dans le chat avec `!` (ex. `!arena_add`).

## Commandes joueurs

- `!arena` : ton statut (arène, adversaire, score, position dans la file).
- `!afk` : se mettre en pause / revenir dans la file. Choisir Spectateur dans le menu des équipes met aussi en pause.
