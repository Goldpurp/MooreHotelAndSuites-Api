START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260912174325_AddEmailOutboxDeliveredMarker') THEN
    ALTER TABLE email_outbox ADD "DeliveredAtUtc" timestamp with time zone;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260912174325_AddEmailOutboxDeliveredMarker') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20260912174325_AddEmailOutboxDeliveredMarker', '10.0.11');
    END IF;
END $EF$;
COMMIT;
