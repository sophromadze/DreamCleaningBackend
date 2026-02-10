-- Run this ONCE on your PRODUCTION database (host) if you see:
--   Unknown column 'u.LastEmailVerificationTokenHash' in 'field list'
-- (e.g. after deploying to dreamcleaningnearme.com)

-- 1. Add the column
ALTER TABLE `Users` ADD COLUMN `LastEmailVerificationTokenHash` longtext CHARACTER SET utf8mb4 NULL;

-- 2. Mark the migration as applied so future deployments don't try to add it again
INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20260210201808_AddLastEmailVerificationTokenHash', '8.0.0');
