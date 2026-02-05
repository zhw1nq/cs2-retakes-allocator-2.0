using System;
using System.Threading.Tasks;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Utils;
using Menu;
using Menu.Enums;
using RetakesAllocator;
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;
using RetakesAllocatorCore.Managers;

namespace RetakesAllocator.AdvancedMenus;

public class AdvancedGunMenu
{
    public HookResult OnEventPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        if (@event == null)
        {
            return HookResult.Continue;
        }

        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (!Helpers.PlayerIsValid(player))
        {
            return HookResult.Continue;
        }

        var message = (@event.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(message))
        {
            return HookResult.Continue;
        }

        var commands = Configs.GetConfigData().InGameGunMenuCenterCommands.Split(',');
        if (commands.Any(cmd => cmd.Equals(message, StringComparison.OrdinalIgnoreCase)))
        {
            _ = OpenMenuForPlayerAsync(player!);
        }

        return HookResult.Continue;
    }

    public void OnTick()
    {
        // Menu updates are handled by the Kitsune menu framework.
    }

    public HookResult OnEventPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event?.Userid == null)
        {
            return HookResult.Continue;
        }

        var player = @event.Userid;
        if (!Helpers.PlayerIsValid(player))
        {
            return HookResult.Continue;
        }

        return HookResult.Continue;
    }

    private async Task OpenMenuForPlayerAsync(CCSPlayerController player)
    {
        if (!Helpers.PlayerIsValid(player))
        {
            return;
        }

        if (!Configs.GetConfigData().CanPlayersSelectWeapons())
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.cannot_choose"], player.PrintToChat);
            return;
        }

        var team = Helpers.GetTeam(player);
        if (team is not CsTeam.Terrorist and not CsTeam.CounterTerrorist)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.join_team"], player.PrintToChat);
            return;
        }

        var steamId = Helpers.GetSteamId(player);
        if (steamId == 0)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["guns_menu.invalid_steam_id"], player.PrintToChat);
            return;
        }

        var data = BuildMenuData(team, steamId);
        if (data == null)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.not_saved"], player.PrintToChat);
            return;
        }

        Server.NextFrame(() =>
        {
            if (!Helpers.PlayerIsValid(player))
            {
                return;
            }

            ShowMainMenu(player, data);
        });
    }

    private GunMenuData? BuildMenuData(CsTeam team, ulong steamId)
    {
        var userSettings = PlayerSettingsCache.GetSettings(steamId);
        var primaryOptions = WeaponHelpers
            .GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, team)
            .ToList();
        var secondaryOptions = WeaponHelpers
            .GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, team)
            .ToList();
        var pistolOptions = WeaponHelpers
            .GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, team)
            .ToList();

        var currentPrimary = userSettings?.GetWeaponPreference(team, WeaponAllocationType.FullBuyPrimary) ??
                             GetDefaultWeapon(team, WeaponAllocationType.FullBuyPrimary, primaryOptions);
        var currentSecondary = userSettings?.GetWeaponPreference(team, WeaponAllocationType.Secondary) ??
                               GetDefaultWeapon(team, WeaponAllocationType.Secondary, secondaryOptions);
        var currentPistol = userSettings?.GetWeaponPreference(team, WeaponAllocationType.PistolRound) ??
                            GetDefaultWeapon(team, WeaponAllocationType.PistolRound, pistolOptions);

        var preferredSniper = userSettings?.GetWeaponPreference(team, WeaponAllocationType.Preferred);

        return new GunMenuData
        {
            SteamId = steamId,
            Team = team,
            PrimaryOptions = primaryOptions,
            SecondaryOptions = secondaryOptions,
            PistolOptions = pistolOptions,
            CurrentPrimary = currentPrimary,
            CurrentSecondary = currentSecondary,
            CurrentPistol = currentPistol,
            PreferredSniper = preferredSniper,
            ZeusEnabled = userSettings?.ZeusEnabled ?? false,
            EnemyStuffPreference = NormalizeEnemyStuffPreference(userSettings?.EnemyStuffTeamPreference)
        };
    }

    private void ShowMainMenu(CCSPlayerController player, GunMenuData data)
    {
        var teamDisplayName = GetTeamDisplayName(data.Team);
        var menuTitle = Translator.Instance["guns_menu.title", teamDisplayName];

        var config = Configs.GetConfigData();
        var awpMode = config.GetAwpMode();
        var ssgMode = config.GetSsgMode();
        data.AwpOptionAvailable = awpMode != AccessMode.Disabled;
        data.SsgOptionAvailable = ssgMode != AccessMode.Disabled;
        data.CanUseAwpPreference = awpMode switch
        {
            AccessMode.Disabled => false,
            AccessMode.Everyone => true,
            AccessMode.VipOnly => Helpers.HasAwpPermission(player),
            _ => false,
        };
        data.CanUseSsgPreference = ssgMode switch
        {
            AccessMode.Disabled => false,
            AccessMode.Everyone => true,
            AccessMode.VipOnly => Helpers.HasSsgPermission(player),
            _ => false,
        };
        var canUseSniperPreferences = data.CanUseAwpPreference || data.CanUseSsgPreference;
        var canUseEnemyStuff = Helpers.HasEnemyStuffPermission(player);

        List<MenuItem> items = [];
        var optionMap = new Dictionary<int, Action<CCSPlayerController>>();
        int i = 0;

        // Primary weapon submenu
        if (data.PrimaryOptions.Count > 0)
        {
            var currentName = data.CurrentPrimary?.GetName() ?? "---";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["weapon_type.primary"]}: {currentName}")]));
            optionMap[i++] = p => ShowWeaponSubmenu(p, data, WeaponAllocationType.FullBuyPrimary, data.PrimaryOptions);
        }

        // Secondary weapon submenu
        if (data.SecondaryOptions.Count > 0)
        {
            var currentName = data.CurrentSecondary?.GetName() ?? "---";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["weapon_type.secondary"]}: {currentName}")]));
            optionMap[i++] = p => ShowWeaponSubmenu(p, data, WeaponAllocationType.Secondary, data.SecondaryOptions);
        }

        // Pistol weapon submenu
        if (data.PistolOptions.Count > 0)
        {
            var currentName = data.CurrentPistol?.GetName() ?? "---";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["weapon_type.pistol"]}: {currentName}")]));
            optionMap[i++] = p => ShowWeaponSubmenu(p, data, WeaponAllocationType.PistolRound, data.PistolOptions);
        }

        // Sniper preference submenu
        if (canUseSniperPreferences)
        {
            var sniperText = GetSniperPreferenceText(data.PreferredSniper);
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["guns_menu.sniper_label"]}: {sniperText}")]));
            optionMap[i++] = p => ShowSniperSubmenu(p, data);
        }

        // Enemy stuff submenu
        if (canUseEnemyStuff)
        {
            var stuffText = GetEnemyStuffText(data.EnemyStuffPreference);
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["guns_menu.enemy_stuff_label"]}: {stuffText}")]));
            optionMap[i++] = p => ShowEnemyStuffSubmenu(p, data);
        }

        // Zeus option
        if (config.IsZeusEnabled())
        {
            var zeusText = data.ZeusEnabled ? Translator.Instance["guns_menu.zeus_choice_enable"] : Translator.Instance["guns_menu.zeus_choice_disable"];
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue($"{Translator.Instance["guns_menu.zeus_label"]}: {zeusText}")]));
            optionMap[i++] = p => ToggleZeus(p, data);
        }

        if (items.Count == 0) return;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, menuTitle, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select && optionMap.TryGetValue(menu.Option, out var action))
            {
                action.Invoke(player);
            }
        }, false, freezePlayer: Configs.GetConfigData().MenuFreezePlayer, disableDeveloper: true);
    }

    private void ShowWeaponSubmenu(CCSPlayerController player, GunMenuData data, WeaponAllocationType allocType, List<CsItem> weapons)
    {
        var title = allocType switch
        {
            WeaponAllocationType.FullBuyPrimary => Translator.Instance["weapon_type.primary"],
            WeaponAllocationType.Secondary => Translator.Instance["weapon_type.secondary"],
            WeaponAllocationType.PistolRound => Translator.Instance["weapon_type.pistol"],
            _ => "Weapons"
        };

        List<MenuItem> items = [];
        var weaponMap = new Dictionary<int, CsItem>();
        int i = 0;

        foreach (var weapon in weapons)
        {
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(weapon.GetName())]));
            weaponMap[i++] = weapon;
        }

        if (items.Count == 0) return;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, title, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select && weaponMap.TryGetValue(menu.Option, out var weapon))
            {
                ApplyWeaponSelection(player, data, allocType, weapon);
                ShowMainMenu(player, data);
            }
        }, true, freezePlayer: Configs.GetConfigData().MenuFreezePlayer, disableDeveloper: true);
    }

    private void ShowSniperSubmenu(CCSPlayerController player, GunMenuData data)
    {
        var title = Translator.Instance["guns_menu.sniper_label"];

        var choices = new[]
        {
            (Translator.Instance["guns_menu.sniper_awp"], (CsItem?)CsItem.AWP),
            (Translator.Instance["guns_menu.sniper_ssg"], (CsItem?)CsItem.Scout),
            (Translator.Instance["guns_menu.sniper_random"], (CsItem?)WeaponHelpers.RandomSniperPreference),
            (Translator.Instance["guns_menu.sniper_disabled"], (CsItem?)null)
        };

        List<MenuItem> items = [];
        var choiceMap = new Dictionary<int, (string Label, CsItem? Value)>();
        int i = 0;

        foreach (var (label, value) in choices)
        {
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(label)]));
            choiceMap[i++] = (label, value);
        }

        RetakesAllocator.Menu?.ShowScrollableMenu(player, title, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select && choiceMap.TryGetValue(menu.Option, out var choice))
            {
                ApplySniperPreference(player, data, choice.Value);
                ShowMainMenu(player, data);
            }
        }, true, freezePlayer: Configs.GetConfigData().MenuFreezePlayer, disableDeveloper: true);
    }

    private void ShowEnemyStuffSubmenu(CCSPlayerController player, GunMenuData data)
    {
        var title = Translator.Instance["guns_menu.enemy_stuff_label"];

        var choices = new[]
        {
            (Translator.Instance["guns_menu.enemy_stuff_choice_disable"], EnemyStuffTeamPreference.None),
            (Translator.Instance["guns_menu.enemy_stuff_choice_t_only"], EnemyStuffTeamPreference.Terrorist),
            (Translator.Instance["guns_menu.enemy_stuff_choice_ct_only"], EnemyStuffTeamPreference.CounterTerrorist),
            (Translator.Instance["guns_menu.enemy_stuff_choice_both"], EnemyStuffTeamPreference.Both)
        };

        List<MenuItem> items = [];
        var choiceMap = new Dictionary<int, EnemyStuffTeamPreference>();
        int i = 0;

        foreach (var (label, value) in choices)
        {
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(label)]));
            choiceMap[i++] = value;
        }

        RetakesAllocator.Menu?.ShowScrollableMenu(player, title, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select && choiceMap.TryGetValue(menu.Option, out var preference))
            {
                ApplyEnemyStuffPreference(player, data, preference);
                ShowMainMenu(player, data);
            }
        }, true, freezePlayer: Configs.GetConfigData().MenuFreezePlayer, disableDeveloper: true);
    }

    private void ApplyWeaponSelection(CCSPlayerController player, GunMenuData data, WeaponAllocationType allocType, CsItem weapon)
    {
        switch (allocType)
        {
            case WeaponAllocationType.FullBuyPrimary:
                data.CurrentPrimary = weapon;
                break;
            case WeaponAllocationType.Secondary:
                data.CurrentSecondary = weapon;
                break;
            case WeaponAllocationType.PistolRound:
                data.CurrentPistol = weapon;
                break;
        }

        var roundType = allocType == WeaponAllocationType.PistolRound ? RoundType.Pistol : RoundType.FullBuy;
        var message = OnWeaponCommandHelper.HandleFromCache(
            new[] { weapon.GetName() }, data.SteamId, roundType, data.Team, false, out _);

        if (!string.IsNullOrWhiteSpace(message))
        {
            Helpers.WriteNewlineDelimited(message, player.PrintToChat);
        }
    }

    private void ApplySniperPreference(CCSPlayerController player, GunMenuData data, CsItem? preference)
    {
        var previousPreference = data.PreferredSniper;
        data.PreferredSniper = preference;
        PlayerSettingsCache.SetPreferredWeapon(data.SteamId, preference);

        string message;
        if (preference.HasValue)
        {
            message = WeaponHelpers.IsRandomSniperPreference(preference.Value)
                ? Translator.Instance["weapon_preference.set_preference_preferred_random"]
                : Translator.Instance["weapon_preference.set_preference_preferred", preference.Value];
        }
        else
        {
            message = previousPreference.HasValue && WeaponHelpers.IsRandomSniperPreference(previousPreference.Value)
                ? Translator.Instance["weapon_preference.unset_preference_preferred_random"]
                : Translator.Instance["weapon_preference.unset_preference_preferred", previousPreference ?? CsItem.AWP];
        }
        Helpers.WriteNewlineDelimited(message, player.PrintToChat);
    }

    private void ApplyEnemyStuffPreference(CCSPlayerController player, GunMenuData data, EnemyStuffTeamPreference preference)
    {
        data.EnemyStuffPreference = preference;
        PlayerSettingsCache.SetEnemyStuffPreference(data.SteamId, preference);

        var messageKey = preference switch
        {
            EnemyStuffTeamPreference.None => "guns_menu.enemy_stuff_disabled_message",
            EnemyStuffTeamPreference.Terrorist => "guns_menu.enemy_stuff_enabled_t_message",
            EnemyStuffTeamPreference.CounterTerrorist => "guns_menu.enemy_stuff_enabled_ct_message",
            _ => "guns_menu.enemy_stuff_enabled_both_message"
        };
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);
    }

    private void ToggleZeus(CCSPlayerController player, GunMenuData data)
    {
        data.ZeusEnabled = !data.ZeusEnabled;
        PlayerSettingsCache.SetZeusPreference(data.SteamId, data.ZeusEnabled);

        var messageKey = data.ZeusEnabled ? "guns_menu.zeus_enabled_message" : "guns_menu.zeus_disabled_message";
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);
        ShowMainMenu(player, data);
    }

    private static string GetSniperPreferenceText(CsItem? preference)
    {
        if (!preference.HasValue)
            return Translator.Instance["guns_menu.sniper_disabled"];
        if (WeaponHelpers.IsRandomSniperPreference(preference.Value))
            return Translator.Instance["guns_menu.sniper_random"];
        return preference.Value switch
        {
            CsItem.Scout => Translator.Instance["guns_menu.sniper_ssg"],
            _ => Translator.Instance["guns_menu.sniper_awp"]
        };
    }

    private static string GetEnemyStuffText(EnemyStuffTeamPreference preference)
    {
        return preference switch
        {
            EnemyStuffTeamPreference.Terrorist => Translator.Instance["guns_menu.enemy_stuff_choice_t_only"],
            EnemyStuffTeamPreference.CounterTerrorist => Translator.Instance["guns_menu.enemy_stuff_choice_ct_only"],
            EnemyStuffTeamPreference.Both => Translator.Instance["guns_menu.enemy_stuff_choice_both"],
            _ => Translator.Instance["guns_menu.enemy_stuff_choice_disable"]
        };
    }

    private static EnemyStuffTeamPreference NormalizeEnemyStuffPreference(EnemyStuffTeamPreference? preference)
    {
        if (preference is null)
            return EnemyStuffTeamPreference.None;

        var value = preference.Value;
        var includesT = value.HasFlag(EnemyStuffTeamPreference.Terrorist);
        var includesCt = value.HasFlag(EnemyStuffTeamPreference.CounterTerrorist);

        return (includesT, includesCt) switch
        {
            (true, true) => EnemyStuffTeamPreference.Both,
            (true, false) => EnemyStuffTeamPreference.Terrorist,
            (false, true) => EnemyStuffTeamPreference.CounterTerrorist,
            _ => EnemyStuffTeamPreference.None,
        };
    }

    private static CsItem? GetDefaultWeapon(CsTeam team, WeaponAllocationType type, IReadOnlyList<CsItem> fallback)
    {
        if (Configs.GetConfigData().DefaultWeapons.TryGetValue(team, out var defaults) &&
            defaults.TryGetValue(type, out var configured))
        {
            return configured;
        }

        return fallback.Count > 0 ? fallback[0] : null;
    }

    private static string GetTeamDisplayName(CsTeam team)
    {
        return team == CsTeam.Terrorist
            ? Translator.Instance["teams.terrorist"]
            : Translator.Instance["teams.counter_terrorist"];
    }

    private sealed class GunMenuData
    {
        public required ulong SteamId { get; init; }
        public required CsTeam Team { get; init; }
        public required List<CsItem> PrimaryOptions { get; init; }
        public required List<CsItem> SecondaryOptions { get; init; }
        public required List<CsItem> PistolOptions { get; init; }
        public CsItem? CurrentPrimary { get; set; }
        public CsItem? CurrentSecondary { get; set; }
        public CsItem? CurrentPistol { get; set; }
        public CsItem? PreferredSniper { get; set; }
        public bool ZeusEnabled { get; set; }
        public EnemyStuffTeamPreference EnemyStuffPreference { get; set; }
        public bool AwpOptionAvailable { get; set; }
        public bool SsgOptionAvailable { get; set; }
        public bool CanUseAwpPreference { get; set; }
        public bool CanUseSsgPreference { get; set; }
    }
}
