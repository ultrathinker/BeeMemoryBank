# Fixtures

`plain-2.5.1.sqlite` is a vault database written by the 2.5.1 tree (`release-2.5.1-candidate`, native SQLite 3.53.3 from
`SQLitePCLRaw.lib.e_sqlite3` 2.1.13, the engine before SQLite3 Multiple Ciphers). It is a plain SQLite file in WAL mode, checkpointed:
all 35 migrations applied (versions 1 to 36; 65 tables including the FTS5 shadow tables of `fts_article`, `fts_folder`, `fts_tag`, with their triggers), two folders, three
articles, two concept tags and the links between them. Used by `Engine/PlainDatabaseCompatibilityTests`.

Do not regenerate it with a newer engine: its value is that an older engine wrote it. It was made by a throwaway console program
(`DbConnectionFactory` + `MigrationRunner.RunMigrationsAsync()` on a new file, then `INSERT`s, then `PRAGMA wal_checkpoint(TRUNCATE)`)
built against the 2.5.1 tree before the engine swap.
