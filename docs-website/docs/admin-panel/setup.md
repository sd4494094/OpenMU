---
title: Setup
sidebar_position: 2
description: Create, reinstall and update the OpenMU database from the admin panel.
---

# Setup

**Navigation:** *Setup* — route `/setup`

The setup page is where the database is created, reinstalled and updated. It is
the first page you need on a fresh installation, and it is the only page shown in
the navigation menu when no usable configuration exists yet.

## Database status

The page starts by telling you the state of the database:

| Status | Meaning | What to do |
|---|---|---|
| *Can't connect to the database, probably not created yet* | The server can reach the database server, but not the OpenMU database | Click **Create** |
| *Not created* | The database exists but has no schema yet | Click **Create** |
| *Update required* | The schema is older than the running OpenMU version | Click **Update** |
| *Up to date* | Everything is fine | Nothing |

When the database is up to date, the page also shows the **initialized game
version**, or a warning that no initialized data was found.

:::note[Connection settings]
The database connection itself is not configured here — it comes from
`ConnectionSettings.xml` and, in the docker deployments, from the
`DB_HOST`/`DB_ADMIN_USER`/`DB_ADMIN_PW`
[environment variables](../deployment/startup-parameters.md#environment-variables).
:::

## Installing

Clicking **Create** (or **Reinstall**) opens the installation dialog with three
choices:

### 1. Game version

Every supported version has its own data initialization, which creates the
matching items, monsters, maps, skills and servers. The list contains the
versions which are available in your OpenMU build — Season 6 Episode 3 is the
main one, plus older versions such as 0.75 and 0.95d.

### 2. Number of game servers

A slider from 1 to 10. This only creates the **configuration** of that many game
servers; whether they are actually started is decided on the
[Servers page](servers.md). Each game server gets its own network port, starting
at 45901.

### 3. Test accounts

A checkbox which creates a set of [test accounts](../getting-started/test-accounts.md)
with well-known passwords.

:::danger[Not for public servers]
Do not create test accounts on a server that players can reach — some of them
have game master rights and their password is identical to their user name.
:::

Then click **Install** and wait. When the installation is finished, the page says
so.

If installation fails, the page shows an error instead. Check the server logs,
correct the problem, and retry; the selected version and test account option stay
selected.

:::warning[All players are disconnected]
The installation button is disabled while players are connected — the page shows
*"First close all connections to the server"*. An installation **drops the
existing data**, so every account, character and configuration change is lost.
:::

:::note[Distributed deployment]
In a [distributed deployment](../deployment/distributed.md) you have to restart
the central server and game server containers after the installation has
finished. The panel reminds you of that.
:::

## Reinstalling

When a database is already installed, the button is labelled **Reinstall**. It
runs the same dialog and lets you switch to another game version, another number
of game servers, or add/remove test accounts.

This is the admin panel equivalent of the `-reinit` start parameter — and it has
the same consequence: **the existing data is gone.** Take a backup of your
PostgreSQL database first if you care about it.

## Updating

If the running OpenMU version needs a newer database schema, the page offers
**Update**. This migrates the schema; it does not touch your accounts.

If a schema update fails, the page shows an error. Check the server logs, restore
the database connection if needed, reload the page, and retry **Update**.

Schema updates are separate from **configuration** updates — new drop tables,
fixed monster stats or new plugin configurations are applied on the
[Configuration updates](configuration-updates.md) page.

In the all-in-one deployment, the schema update can also happen automatically at
startup if *Auto update schema* is enabled in the
[System configuration](game-configuration.md#system). In a distributed
deployment, the update always has to be started here manually.

## Database snapshots

A database snapshot replaces the OpenMU database with the saved accounts,
characters, configuration and admin data. Keep a separate backup and stop game
activity before restoring a snapshot.

Before replacing the database, OpenMU checks the snapshot format, migration
compatibility, manifest and archive entries. Missing or duplicate table files,
invalid manifests and incomplete PostgreSQL COPY data are rejected. The restore
service performs these checks even when it is called without the setup page.

OpenMU imports the snapshot into a separate temporary database, checks that all
tables are present, validates imported foreign keys, and applies remaining
migrations there. Import or migration failures leave the original database in
place. Game and admin data must use the same PostgreSQL database. Restoration
requires enough disk space for both databases, ownership of the target database,
`CREATEDB`, permission to terminate its sessions, and permission to set
`session_replication_role` while importing rows. On PostgreSQL 15 or later, an
administrator can grant `SET ON PARAMETER session_replication_role` to the restore
role without making it a superuser, and revoke the grant after restoration.
An explicitly selected custom tablespace also requires `CREATE` on that
tablespace. No additional tablespace grant is needed when the target uses
the same tablespace as `template0`.

Restoration applies migrations after importing the snapshot's original schema.
If a snapshot was taken before the admin schema was initialized, restoration
can therefore add empty admin tables and their migration history. Compare
existing data and account for these expected schema changes when validating
the restored database.

After the import succeeds, OpenMU closes existing database sessions and switches
the database names in a transaction. The original database remains under an
`openmu_previous_` name with connections disabled. Verify the restored database
and keep a separate backup before an administrator removes this retained copy.
A failed attempt may leave a temporary database if cleanup also fails; the error
reports both failures. Never delete a database based only on its name prefix.

This does not protect against every interruption. If the process or host stops
during switching, the original database may need its connections re-enabled by
an administrator. A connection failure during commit can leave the outcome
uncertain: inspect database identities before retrying.

Before restoring, record the target database's name, OID and owner. For an
interrupted restore, stop game and admin services and confirm the restore
process has ended. Connect to the maintenance database (`postgres`) and inspect
`pg_database` for the recorded target OID, the target name, and any temporary or
retained database identities.

If the original OID still has the original name but `datallowconn` is false,
an administrator can re-enable connections for that verified database with
`ALTER DATABASE ... ALLOW_CONNECTIONS true`. Validate its data and test a new
connection before restarting services or retrying restoration. Do not re-enable
a database merely because its name starts with `openmu_restore_` or
`openmu_previous_`. If the target name points to a different OID, first determine
whether the name switch committed; do not assume the original database is
still the active one.

Process termination can leave an imported temporary database with connections
disabled because in-process cleanup did not run. Retrying restoration creates a
new temporary database; it does not establish that an older temporary database
is safe to delete. Keep these identities and a separate backup until recovery
has been verified.

Snapshot creation reads the table list, migration histories and all table data
through one read-only, repeatable-read transaction. Normal concurrent database
writes can continue; all exported rows reflect the same committed database
snapshot. This includes persisted data only: changes still held in game memory
are not captured. Stop game activity and complete saving when a backup must
include every player's latest actions.

Do not run schema migrations, table truncation or other DDL while creating a
snapshot. Long backups keep an older database snapshot open and can delay
cleanup of obsolete row versions. If writing the archive fails or is canceled,
discard the incomplete file. Verify completed snapshots in an independent
database before relying on them for recovery.
