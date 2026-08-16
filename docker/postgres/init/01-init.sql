-- Runs once, on first initialisation of the Postgres data volume.
--
-- Two jobs:
--   1. Create Keycloak's own database. Keycloak gets a separate database in the same
--      instance rather than sharing the application's schema — it owns its data, and the
--      application never sees its tables.
--   2. Enable pgvector.
--
-- The `vector` extension is created here so the database is usable immediately, and again
-- (idempotently) in the EF migration so a fresh database created by migrations alone —
-- as TestContainers does — is also correct. Neither path depends on the other.

\set keycloak_db `echo "${KEYCLOAK_DB:-keycloak}"`

SELECT format('CREATE DATABASE %I', :'keycloak_db')
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = :'keycloak_db')
\gexec

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Sanity check surfaced in the container logs on first boot.
DO $$
BEGIN
    RAISE NOTICE 'pgvector version: %', (SELECT extversion FROM pg_extension WHERE extname = 'vector');
END
$$;
