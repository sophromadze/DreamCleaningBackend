-- Run this ONCE if you get "Duplicate column name 'LastEmailVerificationTokenHash'"
-- when running: dotnet ef database update
-- (The column was already added in a previous run; this marks the migration as applied.)

INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion)
VALUES ('20260210201808_AddLastEmailVerificationTokenHash', '8.0.0');
