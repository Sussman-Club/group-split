-- Aspire creates AddDatabase() databases through the AppHost orchestrator, which only runs
-- locally. A deployed Postgres container just runs everything in /docker-entrypoint-initdb.d
-- on first init, so the databases have to be created here instead.
--
-- \gexec keeps this idempotent: the SELECT yields the CREATE statement only when the database
-- is missing, and CREATE DATABASE has no IF NOT EXISTS form.

SELECT 'CREATE DATABASE groupsplit'
 WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'groupsplit')\gexec

SELECT 'CREATE DATABASE keycloak'
 WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'keycloak')\gexec
