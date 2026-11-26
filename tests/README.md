# Running the tests

Build on the dev machine with the repository's required .NET SDK, then use the
same configuration for tests without rebuilding:

```bash
dotnet build src/MUnique.OpenMU.sln -c Release
dotnet test src/MUnique.OpenMU.sln -c Release --no-build
```

The socket tests use loopback and operating-system-assigned ports. They do not
need a running game server.

## Optional PostgreSQL initialization and persistence tests

The initialization, backup, snapshot, configuration loader, aggregate deletion,
leaderboard and persisted item price tests are marked `Explicit`. To run one,
set `OPENMU_INTEGRATION_TEST_CONNECTION` to the `postgres` maintenance database
of a disposable local PostgreSQL cluster, then select its method explicitly:

```bash
dotnet test tests/MUnique.OpenMU.Persistence.Initialization.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~BackupServiceEfCoreTests.ExportAndRestoreRoundTripAsync'
dotnet test tests/MUnique.OpenMU.Tests -c Release --no-build \
  --filter 'FullyQualifiedName~ItemPriceCalculatorGoldenMasterTest.PricesAreUnchangedWhenLoadedFromDatabaseAsync'
```

Supply the connection through the environment; do not put credentials in the
command line or tracked files. A Unix socket, `localhost`, or a loopback address
is required. The maintenance user must be able to create roles and databases,
and terminate sessions for snapshot restore. Each test creates a unique login
role with `CREATEDB` and a database owned by that role. Cleanup checks role and
database OIDs and ownership before removing that test's databases, including
previous databases retained by snapshot restore, and then removes its role.
Application databases configured in `ConnectionSettings.xml` are not used.

The full binary snapshot roundtrip additionally grants its temporary role `SET`
on the `session_replication_role` parameter, so it can suspend foreign key checks
during import without becoming a superuser. This grant is revoked during cleanup.
That test requires PostgreSQL 15 or newer and a maintenance user permitted to
grant this parameter privilege. Other tests receive no such grant.

`DefaultTablespaceRestoreTests` also uses `OPENMU_INTEGRATION_TEST_TABLESPACE`,
the name of an existing custom tablespace in the disposable cluster. The
maintenance user must be permitted to move the test database there and grant
`CREATE` on that tablespace. The tests check both a granted restore and a denied
restore, and revoke the temporary role's grant before deleting the role.

These tests run without parallel execution because the persistence connection
provider and JSON converters are process-wide. The scope restores both after
each test. Tests which load an existing configuration seed their own Season 6
data first. `LoadConfigByJsonAsync` checks the loaded graph and reports elapsed
time as diagnostics; elapsed time is not a pass/fail threshold.

`DatabaseSnapshotServiceTests.OtherFilesAreRejectedAsync` needs no database and
runs with the normal suite. The other optional integration test fixtures retain
their documented environment variables and explicit selection requirements.
