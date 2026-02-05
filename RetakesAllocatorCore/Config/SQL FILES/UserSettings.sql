-- MySQL schema dump for cs2-retakes-allocator
-- Contains the complete structure required by the plugin.
-- Updated: Flat columns instead of JSON WeaponPreferences

SET NAMES utf8mb4;

SET FOREIGN_KEY_CHECKS = 0;

DROP TABLE IF EXISTS `UserSettings`;

CREATE TABLE `UserSettings` (
    `UserId` BIGINT UNSIGNED NOT NULL,
    `T_PistolRound` INT NULL,
    `T_Secondary` INT NULL,
    `T_HalfBuyPrimary` INT NULL,
    `T_FullBuyPrimary` INT NULL,
    `T_Preferred` INT NULL,
    `CT_PistolRound` INT NULL,
    `CT_Secondary` INT NULL,
    `CT_HalfBuyPrimary` INT NULL,
    `CT_FullBuyPrimary` INT NULL,
    `CT_Preferred` INT NULL,
    `ZeusEnabled` TINYINT(1) NOT NULL DEFAULT 0,
    `EnemyStuffTeamPreference` INT NOT NULL DEFAULT 0,
    CONSTRAINT `PK_UserSettings` PRIMARY KEY (`UserId`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

DROP TABLE IF EXISTS `__EFMigrationsHistory`;

CREATE TABLE `__EFMigrationsHistory` (
    `MigrationId` VARCHAR(150) NOT NULL,
    `ProductVersion` VARCHAR(32) NOT NULL,
    CONSTRAINT `PK___EFMigrationsHistory` PRIMARY KEY (`MigrationId`)
) ENGINE = InnoDB DEFAULT CHARSET = utf8mb4 COLLATE = utf8mb4_unicode_ci;

INSERT INTO
    `__EFMigrationsHistory` (
        `MigrationId`,
        `ProductVersion`
    )
VALUES (
        '20240105045524_InitialCreate',
        '8.0.0'
    ),
    (
        '20240105050248_DontAutoIncrement',
        '8.0.0'
    ),
    (
        '20240116025022_BigIntTime',
        '8.0.0'
    ),
    (
        '20250927201835_AddZeusPreferenceToUserSettings',
        '8.0.0'
    ),
    (
        '20251007120000_AddEnemyStuffToUserSettings',
        '8.0.0'
    ),
    (
        '20251012021500_AddEnemyStuffTeamPreferenceToUserSettings',
        '8.0.0'
    ),
    (
        '20260205130000_FlatWeaponPreferences',
        '8.0.0'
    );

SET FOREIGN_KEY_CHECKS = 1;