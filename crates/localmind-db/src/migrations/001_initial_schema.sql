-- Phase 0 initial schema
-- Spec reference: Section 10 (SQLite Database Schema)
-- Tables included: conversations, messages, app_settings

CREATE TABLE IF NOT EXISTS conversations (
  id            TEXT PRIMARY KEY,
  title         TEXT NOT NULL DEFAULT 'New conversation',
  project_id    TEXT,
  memory_enabled INTEGER NOT NULL DEFAULT 1,
  model_mode    TEXT NOT NULL DEFAULT 'balanced',
  privacy_mode  TEXT NOT NULL DEFAULT 'local',
  created_at    TEXT NOT NULL,
  updated_at    TEXT NOT NULL,
  archived_at   TEXT
);

CREATE TABLE IF NOT EXISTS messages (
  id              TEXT PRIMARY KEY,
  conversation_id TEXT NOT NULL REFERENCES conversations(id),
  role            TEXT NOT NULL CHECK(role IN ('user', 'assistant', 'system')),
  content         TEXT NOT NULL,
  model_used      TEXT,
  model_mode      TEXT,
  tool_calls      TEXT,
  citation_ids    TEXT,
  created_at      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS app_settings (
  key        TEXT PRIMARY KEY,
  value      TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
