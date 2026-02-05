using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.EntityFrameworkCore;
using RetakesAllocatorCore.Config;

namespace RetakesAllocatorCore.Db;

public class UserSetting
{
    [Key]
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column(TypeName = "bigint")]
    public ulong UserId { get; set; }

    // Terrorist Team Preferences
    public CsItem? T_PistolRound { get; set; }
    public CsItem? T_Secondary { get; set; }
    public CsItem? T_HalfBuyPrimary { get; set; }
    public CsItem? T_FullBuyPrimary { get; set; }
    public CsItem? T_Preferred { get; set; }

    // Counter-Terrorist Team Preferences
    public CsItem? CT_PistolRound { get; set; }
    public CsItem? CT_Secondary { get; set; }
    public CsItem? CT_HalfBuyPrimary { get; set; }
    public CsItem? CT_FullBuyPrimary { get; set; }
    public CsItem? CT_Preferred { get; set; }

    public bool ZeusEnabled { get; set; } = false;

    public EnemyStuffTeamPreference EnemyStuffTeamPreference { get; set; } = EnemyStuffTeamPreference.None;

    public static void Configure(ModelConfigurationBuilder configurationBuilder)
    {
        // CsItem conversion configured in Db.cs
    }

    public bool IsEnemyStuffEnabledForTeam(CsTeam team)
    {
        return EnemyStuffTeamPreference switch
        {
            EnemyStuffTeamPreference.Terrorist => team == CsTeam.Terrorist,
            EnemyStuffTeamPreference.CounterTerrorist => team == CsTeam.CounterTerrorist,
            EnemyStuffTeamPreference.Both =>
                team is CsTeam.Terrorist or CsTeam.CounterTerrorist,
            _ => false,
        };
    }

    public void SetWeaponPreference(CsTeam team, WeaponAllocationType weaponAllocationType, CsItem? weapon)
    {
        switch (team)
        {
            case CsTeam.Terrorist:
                switch (weaponAllocationType)
                {
                    case WeaponAllocationType.PistolRound: T_PistolRound = weapon; break;
                    case WeaponAllocationType.Secondary: T_Secondary = weapon; break;
                    case WeaponAllocationType.HalfBuyPrimary: T_HalfBuyPrimary = weapon; break;
                    case WeaponAllocationType.FullBuyPrimary: T_FullBuyPrimary = weapon; break;
                    case WeaponAllocationType.Preferred: T_Preferred = weapon; break;
                }
                break;
            case CsTeam.CounterTerrorist:
                switch (weaponAllocationType)
                {
                    case WeaponAllocationType.PistolRound: CT_PistolRound = weapon; break;
                    case WeaponAllocationType.Secondary: CT_Secondary = weapon; break;
                    case WeaponAllocationType.HalfBuyPrimary: CT_HalfBuyPrimary = weapon; break;
                    case WeaponAllocationType.FullBuyPrimary: CT_FullBuyPrimary = weapon; break;
                    case WeaponAllocationType.Preferred: CT_Preferred = weapon; break;
                }
                break;
        }
    }

    public CsItem? GetWeaponPreference(CsTeam team, WeaponAllocationType weaponAllocationType)
    {
        return team switch
        {
            CsTeam.Terrorist => weaponAllocationType switch
            {
                WeaponAllocationType.PistolRound => T_PistolRound,
                WeaponAllocationType.Secondary => T_Secondary,
                WeaponAllocationType.HalfBuyPrimary => T_HalfBuyPrimary,
                WeaponAllocationType.FullBuyPrimary => T_FullBuyPrimary,
                WeaponAllocationType.Preferred => T_Preferred,
                _ => null
            },
            CsTeam.CounterTerrorist => weaponAllocationType switch
            {
                WeaponAllocationType.PistolRound => CT_PistolRound,
                WeaponAllocationType.Secondary => CT_Secondary,
                WeaponAllocationType.HalfBuyPrimary => CT_HalfBuyPrimary,
                WeaponAllocationType.FullBuyPrimary => CT_FullBuyPrimary,
                WeaponAllocationType.Preferred => CT_Preferred,
                _ => null
            },
            _ => null
        };
    }
}

public class CsItemConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<CsItem?, int?>
{
    public CsItemConverter() : base(
        v => v.HasValue ? (int?)v.Value : null,
        v => v.HasValue ? (CsItem?)v.Value : null
    )
    {
    }
}
