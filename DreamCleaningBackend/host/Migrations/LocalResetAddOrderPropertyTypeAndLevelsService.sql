-- LOCAL DEV RESET for 20260818143000_AddOrderPropertyTypeAndLevelsService
--
-- Run against DreamCleaningDB_Dev ONLY. Never against production.
--
-- Why: the first version of the migration seeded Levels by hardcoded ServiceTypeId {1, 4}.
-- Service type IDs diverge between local and production, so locally id 4 is Custom Cleaning
-- rather than Move in/out. This unwinds the whole migration so it re-runs at next startup
-- with the corrected, data-driven seed.
--
-- Order matters:
--   ServiceThreshold.SourceServiceId is delete-restricted, so the self-referencing threshold
--   rows must go BEFORE the services they point at.
--
-- Step 3 is the one missing from the obvious "delete the history row" approach: the migration
-- also added two columns to Orders. EF replays the ENTIRE Up() method, so leaving the columns
-- in place makes AddColumn fail with "Duplicate column name" and Database.MigrateAsync() throws
-- at startup, which stops the API booting. Verified safe to drop: no order has a value in
-- either column and no OrderServices row references a levels service.

START TRANSACTION;

-- 1. Thresholds first (FK is delete-restricted).
DELETE `t`
FROM `ServiceThresholds` AS `t`
INNER JOIN `Services` AS `s` ON `s`.`Id` = `t`.`ServiceId`
WHERE `s`.`ServiceKey` = 'levels';

-- Also catch any row that merely POINTS at a levels service as its source, which would
-- otherwise block the delete below. Currently none, but the FK makes this cheap insurance.
DELETE `t`
FROM `ServiceThresholds` AS `t`
INNER JOIN `Services` AS `s` ON `s`.`Id` = `t`.`SourceServiceId`
WHERE `s`.`ServiceKey` = 'levels';

-- 2. The levels services themselves, on every service type they were seeded onto.
DELETE FROM `Services` WHERE `ServiceKey` = 'levels';

-- 3. The two Orders columns, so the migration's AddColumn calls can replay.
ALTER TABLE `Orders` DROP COLUMN `LevelsQuantity`;
ALTER TABLE `Orders` DROP COLUMN `PropertyType`;

-- 4. Forget the migration so Database.MigrateAsync() applies it again on next startup.
DELETE FROM `__EFMigrationsHistory`
WHERE `MigrationId` = '20260818143000_AddOrderPropertyTypeAndLevelsService';

COMMIT;

-- ── Verification: run this AFTER the reset, and again AFTER the app has restarted ──────────
--
-- After the reset  : all four result sets must be EMPTY / zero.
-- After the restart: LevelsByServiceType must list exactly Residential Cleaning and
--                    Move in/out Cleaning, each with exactly one self-referencing threshold.

SELECT 'LevelsByServiceType' AS Check_, st.`Id` AS ServiceTypeId, st.`Name` AS ServiceTypeName,
       s.`Id` AS LevelsServiceId, s.`Cost`, s.`TimeDuration`, s.`MinValue`, s.`MaxValue`,
       s.`DisplayOrder`, s.`ChargeAboveThreshold`, s.`ZeroQuantityCost`, s.`ZeroQuantityDuration`
FROM `Services` AS s
INNER JOIN `ServiceTypes` AS st ON st.`Id` = s.`ServiceTypeId`
WHERE s.`ServiceKey` = 'levels'
ORDER BY st.`Id`;

SELECT 'SelfReferencingThresholds' AS Check_, t.`Id`, t.`ServiceId`, t.`SourceServiceId`,
       t.`SourceQuantity`, t.`IncludedQuantity`,
       (t.`ServiceId` = t.`SourceServiceId`) AS IsSelfReferencing
FROM `ServiceThresholds` AS t
INNER JOIN `Services` AS s ON s.`Id` = t.`ServiceId`
WHERE s.`ServiceKey` = 'levels'
ORDER BY t.`ServiceId`;

-- The set the seed SHOULD have produced, computed independently of what it actually did.
-- These two result sets must contain the same service type IDs.
SELECT 'ExpectedTargets' AS Check_, DISTINCT_TYPES.`ServiceTypeId`, st.`Name`
FROM (
    SELECT DISTINCT `ServiceTypeId`
    FROM `Services`
    WHERE `ServiceKey` = 'bedrooms' AND `IsActive` = 1
) AS DISTINCT_TYPES
INNER JOIN `ServiceTypes` AS st ON st.`Id` = DISTINCT_TYPES.`ServiceTypeId`
ORDER BY DISTINCT_TYPES.`ServiceTypeId`;

-- Anything here is a levels service on a type with NO active bedrooms service, i.e. the exact
-- bug this reset exists to remove. Must return zero rows.
SELECT 'MisplacedLevels' AS Check_, st.`Id`, st.`Name`
FROM `Services` AS s
INNER JOIN `ServiceTypes` AS st ON st.`Id` = s.`ServiceTypeId`
WHERE s.`ServiceKey` = 'levels'
  AND NOT EXISTS (
      SELECT 1 FROM `Services` AS b
      WHERE b.`ServiceTypeId` = s.`ServiceTypeId`
        AND b.`ServiceKey` = 'bedrooms'
        AND b.`IsActive` = 1
  );

SELECT 'OrdersColumns' AS Check_, `COLUMN_NAME`
FROM `INFORMATION_SCHEMA`.`COLUMNS`
WHERE `TABLE_SCHEMA` = DATABASE()
  AND `TABLE_NAME` = 'Orders'
  AND `COLUMN_NAME` IN ('PropertyType', 'LevelsQuantity');
