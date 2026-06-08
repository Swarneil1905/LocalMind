//! Migration runner for the LocalMind SQLite database.
//!
//! Migrations are numbered SQL files embedded in the binary at compile time.
//! Applied migrations are recorded in the `schema_migrations` table.
//! The runner skips any migration that already has a row in that table.

use sqlx::SqlitePool;

/// Each entry is (migration_name, sql_text).
/// Add new migrations by appending to this list.
/// Never reorder or remove existing entries.
const MIGRATIONS: &[(&str, &str)] = &[(
    "001_initial_schema",
    include_str!("migrations/001_initial_schema.sql"),
)];

/// Creates `schema_migrations` if it does not exist, then applies every
/// migration in `MIGRATIONS` that has not been applied yet, in order.
pub async fn run(pool: &SqlitePool) -> Result<(), sqlx::Error> {
    sqlx::query(
        "CREATE TABLE IF NOT EXISTS schema_migrations (
            name       TEXT PRIMARY KEY,
            applied_at TEXT NOT NULL
        )",
    )
    .execute(pool)
    .await?;

    for (name, sql) in MIGRATIONS {
        let already_applied: Option<(String,)> =
            sqlx::query_as("SELECT name FROM schema_migrations WHERE name = ?")
                .bind(name)
                .fetch_optional(pool)
                .await?;

        if already_applied.is_none() {
            sqlx::query(sql).execute(pool).await?;
            sqlx::query(
                "INSERT INTO schema_migrations (name, applied_at)
                 VALUES (?, datetime('now'))",
            )
            .bind(name)
            .execute(pool)
            .await?;
        }
    }

    Ok(())
}
