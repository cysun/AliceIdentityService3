-- Step 8: additive user-search objects for fresh AIS3 databases and AIS2 restores.
-- Run explicitly as the database owner/deployment role, never at application startup.
-- No Identity table/column changes. Existing AIS2 search objects are left intact.
BEGIN;
CREATE EXTENSION IF NOT EXISTS pg_trgm WITH SCHEMA public;

CREATE INDEX IF NOT EXISTS "IX_AspNetUsers_Search"
ON public."AspNetUsers" USING gin
(("FirstName" || ' ' || "LastName" || ' ' || "ScreenName" || ' ' || COALESCE("Email", '')) public.gin_trgm_ops);
COMMIT;
