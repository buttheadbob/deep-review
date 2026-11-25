# Data access: EF Core, ORMs and databases

Passes P3, P8, P10 and P12, after the core files. SimpleORMDotNet is a library that generates this kind of code, so a review of it reads this file from the generator's side as well.

<a id="injection"></a>
## Injection (P10)

- **Raw SQL with input:** `FromSqlRaw`, `ExecuteSqlRaw` and `SqlQueryRaw` with concatenated or interpolated input are SQL injection. `FromSql`, `FromSqlInterpolated` and `ExecuteSql` turn an interpolated string into parameters only when it is passed directly as a `FormattableString`; a string built first and passed in loses that.
- **Dapper and ADO.NET:** parameters for every value. Identifiers (table, column and sort names) can't be parameters: allowlist them.
- **Generated SQL:** identifiers quoted for the dialect, values parameterized, and `LIKE` patterns with `%` and `_` escaped.

<a id="contexts-and-connections"></a>
## Contexts and connections (P3)

- **`DbContext` is not thread-safe:** one operation at a time ("A second operation was started on this context instance"). Parallel queries need separate contexts. In Blazor Server, use a context factory ([hosting-di.md](hosting-di.md#di-lifetimes)).
- **Short-lived contexts:** the change tracker grows with every entity loaded, so a long-lived context leaks memory and returns stale data.
- **Connections** disposed with `await using`. Connection strings from configuration, never from code.
- **SQLite:**
  - One writer at a time. Concurrent writers get `SQLITE_BUSY` unless the code sets a busy timeout, uses WAL mode, and retries.
  - The database and its `-wal` and `-shm` files stay together on a local disk, not a network share.
  - Back up through the backup API, never by copying a live file.

<a id="queries"></a>
## Queries (P12)

- **N+1:** a query per item in a loop, or lazy loading inside a loop. Use `Include`, a projection, or one batched query.
- **Unbounded results:** user-facing lists without paging or `Take`. `ToList()` or `AsEnumerable()` before `Where` loads the whole table into memory.
- **Tracking:** `AsNoTracking` for read-only queries. `AsSplitQuery` where several collection includes multiply rows.
- **Indexes** for the filters and sort orders the change adds, on tables that grow.
- **Sync calls** (`ToList`, `SaveChanges`, `First`) in async request or circuit paths block a thread; use the async versions.

## Writes and concurrency

- **Lost updates:** two users editing the same row. A concurrency token (`[Timestamp]`, `IsConcurrencyToken`) and handling of `DbUpdateConcurrencyException`.
- **Transactions:** a multi-step change is one `SaveChanges` or one explicit transaction. Outside side effects (emails, Discord posts, files) happen after the commit, never before.
- **Cascades:** a configured delete behaviour can remove more than intended. Read the model's delete rules for every entity the change deletes.
- **Bulk operations:** `ExecuteUpdate` and `ExecuteDelete` skip change tracking, interceptors and audit fields.

## Migrations (P8)

- **Read the SQL:** script each migration and read it. A rename EF detected as drop plus add loses data. Narrowed column types, and NOT NULL columns added to tables with rows and no default, fail or truncate.
- **Existing rows:** data migrations for rows already there. A migration that can't be reversed says so.
- **Applying at start-up** with more than one instance races. SQLite rebuilds tables for some changes, which is slow on large tables.
- **Snapshot in step:** the model snapshot matches the migrations, with no pending model changes.
- **Rollouts:** an old app version may run against the new schema for a while. Additive changes first, removals later.

## Stored values

- **Serialized data** in JSON columns or blobs follows core persistence: enums by name, tolerant readers, a version.
- **Times:** stored in UTC with the kind kept, or as `DateTimeOffset` where the offset matters. SQLite stores text, so formats are culture-invariant.
