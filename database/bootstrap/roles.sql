-- Run once per environment as a superuser (local: postgres; production: the managed-service admin).
-- Creates the two least-privilege roles from BRD §9 and the database they work in.
-- Passwords are set out of band (secrets manager); local dev uses trust auth on localhost only.

-- Roles are cluster-wide, so tolerate a cluster where they already exist.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rwbr_migrator') THEN
    CREATE ROLE rwbr_migrator LOGIN;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'rwbr_app') THEN
    CREATE ROLE rwbr_app LOGIN;
  END IF;
END $$;

CREATE DATABASE rwbr_dev OWNER rwbr_migrator;
\connect rwbr_dev

REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO rwbr_migrator;
GRANT USAGE ON SCHEMA public TO rwbr_app;

-- Every table the migrator creates is readable by the app; write grants are given per table in migrations.
ALTER DEFAULT PRIVILEGES FOR ROLE rwbr_migrator IN SCHEMA public GRANT SELECT ON TABLES TO rwbr_app;
ALTER DEFAULT PRIVILEGES FOR ROLE rwbr_migrator IN SCHEMA public GRANT USAGE ON SEQUENCES TO rwbr_app;
