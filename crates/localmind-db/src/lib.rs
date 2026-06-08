//! LocalMind database layer.
//!
//! Provides the SQLite connection pool, the OS-specific database path,
//! and the migration runner. All async functions expect a Tokio runtime
//! (provided by the Tauri application).

pub mod migrations;

use std::path::PathBuf;

use sqlx::{sqlite::SqliteConnectOptions, SqlitePool};

/// Returns the path to the LocalMind SQLite database file.
///
/// Windows:  `%APPDATA%\LocalMind\localmind.db`
/// macOS:    `~/Library/Application Support/LocalMind/localmind.db`
/// Linux:    `~/.local/share/LocalMind/localmind.db`
pub fn db_path() -> Option<PathBuf> {
    dirs::data_dir().map(|d| d.join("LocalMind").join("localmind.db"))
}

/// Opens (or creates) the SQLite database at the given path and returns a pool.
///
/// The directory is created if it does not exist.
/// `PRAGMA journal_mode=WAL` is enabled for better concurrent read performance.
pub async fn open(path: &std::path::Path) -> Result<SqlitePool, DbError> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent).map_err(DbError::Io)?;
    }

    let options = SqliteConnectOptions::new()
        .filename(path)
        .create_if_missing(true)
        .journal_mode(sqlx::sqlite::SqliteJournalMode::Wal);

    SqlitePool::connect_with(options)
        .await
        .map_err(DbError::Sqlx)
}

#[derive(Debug)]
pub enum DbError {
    Io(std::io::Error),
    Sqlx(sqlx::Error),
}

impl std::fmt::Display for DbError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DbError::Io(e) => write!(f, "filesystem error: {e}"),
            DbError::Sqlx(e) => write!(f, "database error: {e}"),
        }
    }
}

impl std::error::Error for DbError {}
