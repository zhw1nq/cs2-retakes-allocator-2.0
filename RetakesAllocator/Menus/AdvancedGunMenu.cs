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
using RetakesAllocatorCore;
using RetakesAllocatorCore.Config;
using RetakesAllocatorCore.Db;

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

        var steamId = Helpers.GetSteamId(player);
        var team = player.Team;

        if (team == CsTeam.Spectator || team == CsTeam.None)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.join_team"], player.PrintToChat);
            return;
        }

        // Fetch user settings from cache or DB
        var userSettings = await Queries.GetUserSettings(steamId);

        // Show menu on main thread
        Server.NextFrame(() =>
        {
            if (!Helpers.PlayerIsValid(player)) return;
            ShowMainMenu(player, steamId, team, userSettings);
        });
    }

    private void ShowMainMenu(CCSPlayerController player, ulong steamId, CsTeam team, UserSetting? userSettings)
    {
        var teamDisplayName = GetTeamDisplayName(team);
        var menuTitle = Translator.Instance["guns_menu.title", teamDisplayName];

        List<MenuItem> items = [];
        var optionMap = new Dictionary<int, Action>();
        int i = 0;

        // Primary Weapons option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["weapon_type.primary"])]));
        optionMap[i++] = () => ShowWeaponCategoryMenu(player, steamId, team, userSettings, WeaponCategory.Primary);

        // Secondary Weapons option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["weapon_type.secondary"])]));
        optionMap[i++] = () => ShowWeaponCategoryMenu(player, steamId, team, userSettings, WeaponCategory.Secondary);

        // Pistol Round option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["weapon_type.pistol"])]));
        optionMap[i++] = () => ShowWeaponCategoryMenu(player, steamId, team, userSettings, WeaponCategory.Pistol);

        // Sniper preference
        var config = Configs.GetConfigData();
        var awpMode = config.GetAwpMode();
        var ssgMode = config.GetSsgMode();
        if (awpMode != AccessMode.Disabled || ssgMode != AccessMode.Disabled)
        {
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["guns_menu.sniper_label"])]));
            optionMap[i++] = () => ShowSniperMenu(player, steamId, userSettings);
        }

        // Zeus option
        if (config.IsZeusEnabled())
        {
            var zeusEnabled = userSettings?.ZeusEnabled ?? false;
            var zeusText = zeusEnabled
                ? $"{Translator.Instance["guns_menu.zeus_label"]}: {Translator.Instance["guns_menu.zeus_choice_enable"]}"
                : $"{Translator.Instance["guns_menu.zeus_label"]}: {Translator.Instance["guns_menu.zeus_choice_disable"]}";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(zeusText)]));
            optionMap[i++] = () => ToggleZeus(player, steamId, userSettings);
        }

        // Enemy Stuff option
        if (Helpers.HasEnemyStuffPermission(player) && config.GetEnemyStuffMode() != AccessMode.Disabled)
        {
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["guns_menu.enemy_stuff_label"])]));
            optionMap[i++] = () => ShowEnemyStuffMenu(player, steamId, userSettings);
        }

        if (i == 0) return;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, menuTitle, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select && optionMap.TryGetValue(menu.Option, out var action))
            {
                action.Invoke();
            }
        }, false, freezePlayer: false, disableDeveloper: true);
    }

    private void ShowWeaponCategoryMenu(CCSPlayerController player, ulong steamId, CsTeam team, UserSetting? userSettings, WeaponCategory category)
    {
        var (allocationType, weapons) = category switch
        {
            WeaponCategory.Primary => (WeaponAllocationType.FullBuyPrimary, WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.FullBuyPrimary, team)),
            WeaponCategory.Secondary => (WeaponAllocationType.Secondary, WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.Secondary, team)),
            WeaponCategory.Pistol => (WeaponAllocationType.PistolRound, WeaponHelpers.GetPossibleWeaponsForAllocationType(WeaponAllocationType.PistolRound, team)),
            _ => (WeaponAllocationType.FullBuyPrimary, new List<CsItem>())
        };

        if (weapons.Count == 0)
        {
            Helpers.WriteNewlineDelimited(Translator.Instance["guns_menu.unavailable"], player.PrintToChat);
            return;
        }

        var currentWeapon = userSettings?.GetWeaponPreference(team, allocationType);
        var menuTitle = Translator.Instance[$"weapon_type.{category.ToString().ToLower()}"];

        List<MenuItem> items = [];
        var weaponMap = new Dictionary<int, CsItem>();
        int i = 0;

        foreach (var weapon in weapons)
        {
            var weaponName = weapon.GetName();
            var isSelected = currentWeapon == weapon;
            var displayText = isSelected ? $"► {weaponName} ◄" : weaponName;
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(displayText)]));
            weaponMap[i++] = weapon;
        }

        // Back option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["menu.back"])]));
        int backIndex = i;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, menuTitle, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select)
            {
                if (menu.Option == backIndex)
                {
                    ShowMainMenu(player, steamId, team, userSettings);
                    return;
                }

                if (weaponMap.TryGetValue(menu.Option, out var weapon))
                {
                    ApplyWeaponSelection(player, steamId, team, allocationType, weapon);
                    // Selection applied, user will see confirmation message
                }
            }
        }, true, freezePlayer: false, disableDeveloper: true);
    }

    private void ShowSniperMenu(CCSPlayerController player, ulong steamId, UserSetting? userSettings)
    {
        var menuTitle = Translator.Instance["guns_menu.sniper_label"];
        var config = Configs.GetConfigData();
        var canUseAwp = config.GetAwpMode() == AccessMode.Everyone ||
                        (config.GetAwpMode() == AccessMode.VipOnly && Helpers.HasAwpPermission(player));
        var canUseSsg = config.GetSsgMode() == AccessMode.Everyone ||
                        (config.GetSsgMode() == AccessMode.VipOnly && Helpers.HasSsgPermission(player));

        var currentPreference = userSettings?.GetWeaponPreference(CsTeam.None, WeaponAllocationType.Preferred);

        List<MenuItem> items = [];
        var optionMap = new Dictionary<int, CsItem?>();
        int i = 0;

        // AWP option
        if (config.GetAwpMode() != AccessMode.Disabled)
        {
            var isSelected = currentPreference == CsItem.AWP;
            var text = isSelected ? $"► AWP ◄" : "AWP";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(text)]));
            optionMap[i++] = CsItem.AWP;
        }

        // SSG option
        if (config.GetSsgMode() != AccessMode.Disabled)
        {
            var isSelected = currentPreference == CsItem.Scout;
            var text = isSelected ? $"► SSG 08 ◄" : "SSG 08";
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(text)]));
            optionMap[i++] = CsItem.Scout;
        }

        // Random option
        var isRandomSelected = currentPreference.HasValue && WeaponHelpers.IsRandomSniperPreference(currentPreference.Value);
        var randomText = isRandomSelected ? $"► {Translator.Instance["guns_menu.sniper_random"]} ◄" : Translator.Instance["guns_menu.sniper_random"];
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(randomText)]));
        optionMap[i++] = WeaponHelpers.RandomSniperPreference;

        // Disable option
        var isDisabled = !currentPreference.HasValue;
        var disableText = isDisabled ? $"► {Translator.Instance["guns_menu.sniper_disabled"]} ◄" : Translator.Instance["guns_menu.sniper_disabled"];
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(disableText)]));
        optionMap[i++] = null;

        // Back option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["menu.back"])]));
        int backIndex = i;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, menuTitle, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select)
            {
                if (menu.Option == backIndex)
                {
                    _ = OpenMenuForPlayerAsync(player);
                    return;
                }

                if (optionMap.TryGetValue(menu.Option, out var preference))
                {
                    // Check permissions
                    if (preference == CsItem.AWP && !canUseAwp)
                    {
                        Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.only_vip_can_use"], player.PrintToChat);
                        return;
                    }
                    if (preference == CsItem.Scout && !canUseSsg)
                    {
                        Helpers.WriteNewlineDelimited(Translator.Instance["weapon_preference.only_vip_can_use"], player.PrintToChat);
                        return;
                    }

                    ApplySniperPreference(player, steamId, preference);
                    // Selection applied, user will see confirmation message
                }
            }
        }, true, freezePlayer: false, disableDeveloper: true);
    }

    private void ShowEnemyStuffMenu(CCSPlayerController player, ulong steamId, UserSetting? userSettings)
    {
        var menuTitle = Translator.Instance["guns_menu.enemy_stuff_label"];
        var currentPref = userSettings?.EnemyStuffTeamPreference ?? EnemyStuffTeamPreference.None;

        List<MenuItem> items = [];
        var prefMap = new Dictionary<int, EnemyStuffTeamPreference>();
        int i = 0;

        var options = new[]
        {
            (Translator.Instance["guns_menu.enemy_stuff_choice_disable"], EnemyStuffTeamPreference.None),
            ("T Only", EnemyStuffTeamPreference.Terrorist),
            ("CT Only", EnemyStuffTeamPreference.CounterTerrorist),
            (Translator.Instance["guns_menu.enemy_stuff_choice_both"], EnemyStuffTeamPreference.Both)
        };

        foreach (var (text, pref) in options)
        {
            var isSelected = currentPref == pref;
            var displayText = isSelected ? $"► {text} ◄" : text;
            items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(displayText)]));
            prefMap[i++] = pref;
        }

        // Back option
        items.Add(new MenuItem(MenuItemType.Button, [new MenuValue(Translator.Instance["menu.back"])]));
        int backIndex = i;

        RetakesAllocator.Menu?.ShowScrollableMenu(player, menuTitle, items, (buttons, menu, selected) =>
        {
            if (selected == null) return;
            if (buttons == MenuButtons.Select)
            {
                if (menu.Option == backIndex)
                {
                    _ = OpenMenuForPlayerAsync(player);
                    return;
                }

                if (prefMap.TryGetValue(menu.Option, out var pref))
                {
                    Queries.SetEnemyStuffPreference(steamId, pref);
                    var messageKey = pref switch
                    {
                        EnemyStuffTeamPreference.None => "guns_menu.enemy_stuff_disabled_message",
                        EnemyStuffTeamPreference.Terrorist => "guns_menu.enemy_stuff_enabled_t_message",
                        EnemyStuffTeamPreference.CounterTerrorist => "guns_menu.enemy_stuff_enabled_ct_message",
                        _ => "guns_menu.enemy_stuff_enabled_both_message"
                    };
                    Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);
                    // Selection applied, menu will close
                }
            }
        }, true, freezePlayer: false, disableDeveloper: true);
    }

    private void ToggleZeus(CCSPlayerController player, ulong steamId, UserSetting? userSettings)
    {
        var currentlyEnabled = userSettings?.ZeusEnabled ?? false;
        var newValue = !currentlyEnabled;
        Queries.SetZeusPreference(steamId, newValue);

        var messageKey = newValue ? "guns_menu.zeus_enabled_message" : "guns_menu.zeus_disabled_message";
        Helpers.WriteNewlineDelimited(Translator.Instance[messageKey], player.PrintToChat);

        // Refresh main menu
        _ = OpenMenuForPlayerAsync(player);
    }

    private void ApplyWeaponSelection(CCSPlayerController player, ulong steamId, CsTeam team, WeaponAllocationType allocationType, CsItem weapon)
    {
        var weaponName = weapon.GetName();
        _ = Task.Run(async () =>
        {
            var roundType = allocationType switch
            {
                WeaponAllocationType.PistolRound => RoundType.Pistol,
                WeaponAllocationType.HalfBuyPrimary => RoundType.HalfBuy,
                _ => RoundType.FullBuy
            };

            var result = await OnWeaponCommandHelper.HandleAsync(new[] { weaponName }, steamId, roundType, team, false);
            if (string.IsNullOrWhiteSpace(result.Item1))
            {
                return;
            }

            Server.NextFrame(() =>
            {
                if (!Helpers.PlayerIsValid(player))
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(result.Item1, player.PrintToChat);
            });
        });
    }

    private void ApplySniperPreference(CCSPlayerController player, ulong steamId, CsItem? preference)
    {
        _ = Task.Run(async () =>
        {
            await Queries.SetAwpWeaponPreferenceAsync(steamId, preference);

            string message;
            if (preference.HasValue)
            {
                message = WeaponHelpers.IsRandomSniperPreference(preference.Value)
                    ? Translator.Instance["weapon_preference.set_preference_preferred_random"]
                    : Translator.Instance["weapon_preference.set_preference_preferred", preference.Value];
            }
            else
            {
                message = Translator.Instance["weapon_preference.unset_preference_preferred", CsItem.AWP];
            }

            Server.NextFrame(() =>
            {
                if (!Helpers.PlayerIsValid(player))
                {
                    return;
                }

                Helpers.WriteNewlineDelimited(message, player.PrintToChat);
            });
        });
    }

    private static string GetTeamDisplayName(CsTeam team)
    {
        return team == CsTeam.Terrorist
            ? Translator.Instance["teams.terrorist"]
            : Translator.Instance["teams.counter_terrorist"];
    }

    private enum WeaponCategory
    {
        Primary,
        Secondary,
        Pistol
    }
}
