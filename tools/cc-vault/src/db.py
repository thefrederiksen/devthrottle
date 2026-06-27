"""
Vault - Personal Life Organizer
SQLite database for contacts, tasks, goals, ideas, and memory management.

Domains:
- CONTACTS: People, relationships, network
- TASKS: Everything you need to do (unified)
- GOALS: What you want to achieve
- IDEAS: Unstructured thoughts to capture
"""

import logging
import sqlite3
import json
import hashlib
from pathlib import Path
from datetime import datetime, date
from typing import Optional, List, Dict, Any

logger = logging.getLogger(__name__)

try:
    from .config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        HEALTH_PATH, IMPORTS_PATH, BACKUPS_PATH,
        DOCUMENT_TYPES, ENTITY_TYPES, ensure_directories
    )
except ImportError:
    from config import (
        VAULT_PATH, DB_PATH, VECTORS_PATH, DOCUMENTS_PATH,
        HEALTH_PATH, IMPORTS_PATH, BACKUPS_PATH,
        DOCUMENT_TYPES, ENTITY_TYPES, ensure_directories
    )

# Track if DB has been initialized this session
_db_initialized = False

# Optional override for the database location, set by `cc-vault init <path>` so
# the database is created where the user asked rather than at the default path
# captured in DB_PATH at import time.
_db_path_override: Optional[Path] = None


def set_db_path(path) -> None:
    """Point the database layer at an explicit vault.db location.

    Used by `cc-vault init <path>` so initialization honors the requested path.
    Resets the per-session init flag so the new database gets its schema.
    """
    global _db_path_override, _db_initialized
    _db_path_override = Path(path)
    _db_initialized = False


def _current_db_path() -> Path:
    """Resolve the active database path (override beats the import-time default)."""
    return _db_path_override if _db_path_override is not None else DB_PATH


def get_db() -> sqlite3.Connection:
    """Get database connection with row factory for dict-like access.

    vault.db is shared with the cc-director desktop app, so the connection is
    opened in Write-Ahead Logging mode with a busy timeout to avoid
    "database is locked" errors when both processes access it concurrently.
    """
    db_path = _current_db_path()
    db_path.parent.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(db_path, timeout=30.0)
    conn.row_factory = sqlite3.Row
    # WAL allows one writer plus concurrent readers without immediate lock errors.
    conn.execute("PRAGMA journal_mode = WAL")
    # Wait up to 5000 ms for a competing writer before raising "database is locked".
    conn.execute("PRAGMA busy_timeout = 5000")
    conn.execute("PRAGMA foreign_keys = ON")
    return conn


def _migrate_schema(conn: sqlite3.Connection):
    """
    Add new columns to existing tables if they don't exist.
    This allows upgrading existing databases without losing data.
    """
    cursor = conn.cursor()

    # Get existing columns in contacts table
    cursor.execute("PRAGMA table_info(contacts)")
    existing_columns = {row[1] for row in cursor.fetchall()}

    # New columns to add (column_name, column_definition)
    new_columns = [
        # Social media / messaging
        ('whatsapp', 'TEXT'),
        ('instagram', 'TEXT'),
        ('facebook', 'TEXT'),
        ('github', 'TEXT'),
        ('skype', 'TEXT'),
        ('telegram', 'TEXT'),
        ('signal', 'TEXT'),
        # CRM fields
        ('lead_source', 'TEXT'),
        ('referred_by', 'INTEGER REFERENCES contacts(id)'),
        ('lead_status', 'TEXT'),
        ('client_since', 'DATE'),
        ('contract_value', 'REAL'),
        ('next_followup', 'DATE'),
        # Relationship strength (1=acquaintance, 2=contact, 3=colleague, 4=friend, 5=close)
        ('relationship_strength', 'INTEGER CHECK(relationship_strength BETWEEN 1 AND 5)'),
    ]

    for col_name, col_def in new_columns:
        if col_name not in existing_columns:
            try:
                cursor.execute(f"ALTER TABLE contacts ADD COLUMN {col_name} {col_def}")
            except sqlite3.OperationalError as e:
                logger.debug("Column %s already exists or migration skipped: %s", col_name, e)

    # Migrate interactions table: add account and source_url columns
    cursor.execute("PRAGMA table_info(interactions)")
    interactions_columns = {row[1] for row in cursor.fetchall()}

    interactions_new_columns = [
        ('account', 'TEXT'),
        ('source_url', 'TEXT'),
    ]

    for col_name, col_def in interactions_new_columns:
        if col_name not in interactions_columns:
            try:
                cursor.execute(f"ALTER TABLE interactions ADD COLUMN {col_name} {col_def}")
            except sqlite3.OperationalError as e:
                logger.debug("Column %s already exists or migration skipped: %s", col_name, e)

    # Add unique index on message_id for idempotent email scanning
    try:
        cursor.execute("""
            CREATE UNIQUE INDEX IF NOT EXISTS idx_interactions_message_id
            ON interactions(message_id) WHERE message_id IS NOT NULL
        """)
    except sqlite3.OperationalError as e:
        logger.debug("Index idx_interactions_message_id already exists or skipped: %s", e)

    # Populate contact_emails from existing contacts (idempotent)
    try:
        cursor.execute("""
            INSERT OR IGNORE INTO contact_emails (contact_id, email, label, is_primary)
            SELECT id, email, 'primary', 1 FROM contacts
            WHERE email IS NOT NULL AND email != ''
        """)
    except sqlite3.OperationalError as e:
        logger.debug("contact_emails migration skipped: %s", e)

    conn.commit()


def init_db(silent: bool = False):
    """Create database tables if they don't exist."""
    global _db_initialized
    if _db_initialized:
        return

    conn = get_db()
    cursor = conn.cursor()

    # ==========================================
    # CONTACTS DOMAIN
    # ==========================================

    # Contacts table (core identity)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS contacts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,

            -- Identity
            email TEXT UNIQUE NOT NULL,
            name TEXT NOT NULL,
            nickname TEXT,
            pronunciation TEXT,

            -- Classification
            account TEXT NOT NULL CHECK(account IN ('consulting', 'personal', 'both')),
            category TEXT DEFAULT 'whitelist' CHECK(category IN ('whitelist', 'blacklist', 'notifications', 'ignore', 'archive')),
            relationship TEXT,
            priority TEXT DEFAULT 'normal' CHECK(priority IN ('vip', 'high', 'normal', 'low')),

            -- Communication preferences
            style TEXT DEFAULT 'casual' CHECK(style IN ('formal', 'casual', 'friendly')),
            greeting TEXT,
            signoff TEXT,
            best_contact_method TEXT,
            best_time TEXT,
            timezone TEXT,
            response_speed TEXT,

            -- Context
            context TEXT,
            company TEXT,
            title TEXT,
            location TEXT,

            -- Personal details
            birthday DATE,
            spouse_name TEXT,
            children TEXT,
            pets TEXT,
            hobbies TEXT,

            -- Additional contact info
            phone TEXT,
            linkedin TEXT,
            twitter TEXT,
            website TEXT,
            address TEXT,

            -- Social media / messaging
            whatsapp TEXT,
            instagram TEXT,
            facebook TEXT,
            github TEXT,
            skype TEXT,
            telegram TEXT,
            signal TEXT,

            -- CRM fields
            lead_source TEXT,
            referred_by INTEGER REFERENCES contacts(id),
            lead_status TEXT CHECK(lead_status IN ('prospect', 'active', 'inactive', 'closed')),
            client_since DATE,
            contract_value REAL,
            next_followup DATE,

            -- Relationship strength (1=acquaintance, 2=contact, 3=colleague, 4=friend, 5=close)
            relationship_strength INTEGER CHECK(relationship_strength BETWEEN 1 AND 5),

            -- Meta
            first_contact DATE,
            last_contact DATE,
            contact_frequency TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Contact emails table (multiple emails per contact)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS contact_emails (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            email TEXT NOT NULL,
            label TEXT DEFAULT 'primary',
            is_primary INTEGER DEFAULT 0,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            UNIQUE(email)
        )
    """)

    # Interactions table (every touchpoint)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS interactions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,

            -- What happened
            type TEXT NOT NULL,
            direction TEXT,
            subject TEXT,
            summary TEXT,
            content TEXT,

            -- Context
            sentiment TEXT,
            action_required INTEGER DEFAULT 0,
            action_description TEXT,
            action_completed INTEGER DEFAULT 0,

            -- Reference
            message_id TEXT,

            -- Meta
            interaction_date TIMESTAMP NOT NULL,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Memories table (facts learned about contacts)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS memories (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,

            -- The memory
            category TEXT NOT NULL,
            fact TEXT NOT NULL,
            detail TEXT,

            -- Source tracking
            source TEXT,
            source_date DATE,
            source_ref TEXT,

            -- Validity
            confidence TEXT DEFAULT 'confirmed',
            still_valid INTEGER DEFAULT 1,

            -- Meta
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Notes table (free-form observations)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS notes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,

            note TEXT NOT NULL,
            context TEXT,

            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Contact tags table
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS contact_tags (
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            tag TEXT NOT NULL,
            PRIMARY KEY (contact_id, tag)
        )
    """)

    # Contact lists (named, reusable collections)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS lists (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT UNIQUE NOT NULL,
            description TEXT,
            list_type TEXT DEFAULT 'general',
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # List members (many-to-many: lists <-> contacts)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS list_members (
            list_id INTEGER NOT NULL REFERENCES lists(id) ON DELETE CASCADE,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            added_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            notes TEXT,
            PRIMARY KEY (list_id, contact_id)
        )
    """)

    # Lead scores table
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS lead_scores (
            contact_id INTEGER PRIMARY KEY REFERENCES contacts(id) ON DELETE CASCADE,
            total_score INTEGER DEFAULT 0,
            category TEXT CHECK(category IN ('prospect', 'warm', 'hot', 'client', 'partner')),
            engagement_score INTEGER DEFAULT 0,
            last_scored TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Legacy actions table (kept for compatibility during migration)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS actions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            action_type TEXT NOT NULL CHECK(action_type IN ('follow_up', 'call', 'email', 'introduce', 'meeting', 'other')),
            description TEXT NOT NULL,
            status TEXT DEFAULT 'pending' CHECK(status IN ('pending', 'completed', 'cancelled')),
            priority INTEGER DEFAULT 3 CHECK(priority BETWEEN 1 AND 5),
            due_date DATE,
            completed_at TIMESTAMP,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # TASKS DOMAIN (unified - replaces actions)
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS tasks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            description TEXT,
            status TEXT DEFAULT 'pending' CHECK(status IN ('pending', 'done', 'cancelled')),
            priority INTEGER DEFAULT 3 CHECK(priority BETWEEN 1 AND 5),
            due_date DATE,
            context TEXT,

            -- Optional links
            contact_id INTEGER REFERENCES contacts(id),
            goal_id INTEGER REFERENCES goals(id),

            -- Meta
            completed_at TIMESTAMP,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # GOALS DOMAIN
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS goals (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            description TEXT,
            category TEXT,
            timeframe TEXT CHECK(timeframe IN ('short', 'medium', 'long')),
            status TEXT DEFAULT 'active' CHECK(status IN ('active', 'achieved', 'abandoned', 'paused')),
            why TEXT,
            target_date DATE,
            achieved_at TIMESTAMP,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # IDEAS DOMAIN
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS ideas (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            content TEXT NOT NULL,
            tags TEXT,
            domain TEXT,
            status TEXT DEFAULT 'captured' CHECK(status IN ('captured', 'exploring', 'actionable', 'archived')),

            -- Optional links
            goal_id INTEGER REFERENCES goals(id),

            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # GENERAL DOMAIN
    # ==========================================

    # Facts table (general knowledge, not contact-specific)
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS facts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            domain TEXT NOT NULL,
            subdomain TEXT,
            fact TEXT NOT NULL,
            tags TEXT,
            source TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # VAULT 2.0 - DOCUMENTS & VECTORS
    # ==========================================

    # Documents table - metadata for files stored in vault/documents
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS documents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            title TEXT,
            doc_type TEXT CHECK(doc_type IN ('transcript', 'note', 'journal', 'research')),
            summary TEXT,
            tags TEXT,
            source TEXT,
            source_date DATE,

            -- Vector reference
            vector_id TEXT,

            -- Links
            contact_id INTEGER REFERENCES contacts(id),
            goal_id INTEGER REFERENCES goals(id),

            -- Meta
            file_hash TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            indexed_at TIMESTAMP
        )
    """)

    # Health data registry
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS health_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            category TEXT NOT NULL,
            entry_date DATE NOT NULL,
            data_file TEXT,
            summary TEXT,
            vector_id TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # PHOTOS DOMAIN
    # ==========================================

    # Photo sources - directories to scan for photos
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photo_sources (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            label TEXT UNIQUE NOT NULL,
            category TEXT NOT NULL CHECK(category IN ('private', 'work', 'other')),
            priority INTEGER DEFAULT 10,
            enabled INTEGER DEFAULT 1,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Photos table - the main photo registry
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photos (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_id INTEGER NOT NULL REFERENCES photo_sources(id) ON DELETE CASCADE,
            file_path TEXT UNIQUE NOT NULL,
            file_name TEXT NOT NULL,
            file_size INTEGER,
            sha256_hash TEXT,
            is_screenshot INTEGER DEFAULT 0,
            screenshot_confidence REAL,
            category TEXT NOT NULL CHECK(category IN ('private', 'work', 'other')),

            -- Links to other entities
            contact_id INTEGER REFERENCES contacts(id),
            goal_id INTEGER REFERENCES goals(id),

            -- Vector reference for AI descriptions
            vector_id TEXT,

            -- Meta
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            file_modified_at TIMESTAMP
        )
    """)

    # Photo metadata - EXIF and image properties
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photo_metadata (
            photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
            width INTEGER,
            height INTEGER,
            date_taken TIMESTAMP,
            camera_make TEXT,
            camera_model TEXT,
            gps_lat REAL,
            gps_lon REAL,
            orientation INTEGER,
            raw_exif TEXT
        )
    """)

    # Photo analysis - AI-generated descriptions
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photo_analysis (
            photo_id INTEGER PRIMARY KEY REFERENCES photos(id) ON DELETE CASCADE,
            description TEXT,
            keywords TEXT,
            analyzed_at TIMESTAMP,
            provider TEXT,
            model TEXT
        )
    """)

    # Photo exclusions - paths to skip during scanning
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photo_exclusions (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            reason TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # Photo scan state - tracks initialized drives
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS photo_scan_state (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            drive TEXT UNIQUE NOT NULL,
            last_scan TIMESTAMP,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # CHUNKS - Document chunks for hybrid search
    # ==========================================

    # Chunks table - stores document chunks with vector references
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS chunks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
            content TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            start_line INTEGER,
            end_line INTEGER,
            chunk_index INTEGER,
            vector_id TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # FTS5 full-text search index for chunks
    cursor.execute("""
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            content,
            content='chunks',
            content_rowid='id'
        )
    """)

    # Triggers to keep FTS5 in sync with chunks table
    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
            INSERT INTO chunks_fts(rowid, content) VALUES (new.id, new.content);
        END
    """)

    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
            INSERT INTO chunks_fts(chunks_fts, rowid, content) VALUES('delete', old.id, old.content);
        END
    """)

    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS chunks_au AFTER UPDATE ON chunks BEGIN
            INSERT INTO chunks_fts(chunks_fts, rowid, content) VALUES('delete', old.id, old.content);
            INSERT INTO chunks_fts(rowid, content) VALUES (new.id, new.content);
        END
    """)

    # ==========================================
    # SOCIAL POSTS DOMAIN
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS social_posts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            platform TEXT NOT NULL CHECK(platform IN ('linkedin', 'twitter', 'reddit', 'other')),
            content TEXT NOT NULL,
            status TEXT DEFAULT 'draft' CHECK(status IN ('draft', 'scheduled', 'posted')),
            audience TEXT,
            url TEXT,
            posted_at TIMESTAMP,
            tags TEXT,
            goal_id INTEGER REFERENCES goals(id),
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # EMAIL ACTIVITY DOMAIN
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS email_activity (
            contact_id INTEGER NOT NULL REFERENCES contacts(id) ON DELETE CASCADE,
            account TEXT NOT NULL,
            email_count INTEGER DEFAULT 0,
            sent_count INTEGER DEFAULT 0,
            received_count INTEGER DEFAULT 0,
            first_email_date TEXT,
            last_email_date TEXT,
            scanned_at TEXT,
            PRIMARY KEY (contact_id, account)
        )
    """)

    # Entity links - flexible graph-like relationships
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS entity_links (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source_type TEXT NOT NULL,
            source_id INTEGER NOT NULL,
            target_type TEXT NOT NULL,
            target_id INTEGER NOT NULL,
            relationship TEXT,
            strength INTEGER DEFAULT 1 CHECK(strength BETWEEN 1 AND 5),
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            UNIQUE(source_type, source_id, target_type, target_id, relationship)
        )
    """)

    # Search history for learning
    cursor.execute("""
        CREATE TABLE IF NOT EXISTS search_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            query TEXT NOT NULL,
            results_count INTEGER,
            clicked_type TEXT,
            clicked_id INTEGER,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
    """)

    # ==========================================
    # VECTOR EMBEDDINGS (replaces ChromaDB)
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS vec_embeddings (
            id TEXT NOT NULL,
            collection TEXT NOT NULL,
            embedding BLOB NOT NULL,
            document TEXT,
            metadata TEXT,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (id, collection)
        )
    """)

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_vec_collection ON vec_embeddings(collection)")

    # ==========================================
    # DOCUMENT CATALOG (Document Library)
    # ==========================================

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS libraries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT UNIQUE NOT NULL,
            label TEXT UNIQUE NOT NULL,
            category TEXT NOT NULL CHECK(category IN ('business','personal','other')),
            owner TEXT,
            recursive INTEGER DEFAULT 1,
            enabled INTEGER DEFAULT 1,
            last_scanned TEXT,
            created_at TEXT DEFAULT (datetime('now'))
        )
    """)

    cursor.execute("""
        CREATE TABLE IF NOT EXISTS catalog_entries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            library_id INTEGER NOT NULL REFERENCES libraries(id) ON DELETE CASCADE,
            file_path TEXT UNIQUE NOT NULL,
            file_name TEXT NOT NULL,
            file_ext TEXT NOT NULL,
            file_size INTEGER,
            file_hash TEXT,
            file_modified_at TEXT,
            title TEXT,
            summary TEXT,
            tags TEXT,
            department TEXT,
            status TEXT DEFAULT 'pending'
                CHECK(status IN ('pending','summarized','error','skipped','missing')),
            error_message TEXT,
            summarizable INTEGER DEFAULT 1,
            dedup_source_id INTEGER,
            created_at TEXT DEFAULT (datetime('now')),
            summarized_at TEXT
        )
    """)

    # FTS5 full-text search for catalog entries
    cursor.execute("""
        CREATE VIRTUAL TABLE IF NOT EXISTS catalog_fts USING fts5(
            title, summary, tags, file_name, department,
            content='catalog_entries',
            content_rowid='id'
        )
    """)

    # Triggers to keep catalog_fts in sync
    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS catalog_ai AFTER INSERT ON catalog_entries BEGIN
            INSERT INTO catalog_fts(rowid, title, summary, tags, file_name, department)
            VALUES (new.id, new.title, new.summary, new.tags, new.file_name, new.department);
        END
    """)

    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS catalog_ad AFTER DELETE ON catalog_entries BEGIN
            INSERT INTO catalog_fts(catalog_fts, rowid, title, summary, tags, file_name, department)
            VALUES('delete', old.id, old.title, old.summary, old.tags, old.file_name, old.department);
        END
    """)

    cursor.execute("""
        CREATE TRIGGER IF NOT EXISTS catalog_au AFTER UPDATE ON catalog_entries BEGIN
            INSERT INTO catalog_fts(catalog_fts, rowid, title, summary, tags, file_name, department)
            VALUES('delete', old.id, old.title, old.summary, old.tags, old.file_name, old.department);
            INSERT INTO catalog_fts(rowid, title, summary, tags, file_name, department)
            VALUES (new.id, new.title, new.summary, new.tags, new.file_name, new.department);
        END
    """)

    # ==========================================
    # INDEXES
    # ==========================================

    # Contacts indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_email ON contacts(email)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_name ON contacts(name)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_account ON contacts(account)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_category ON contacts(category)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_relationship ON contacts(relationship)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contacts_company ON contacts(company)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contact_emails_contact ON contact_emails(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contact_emails_email ON contact_emails(email)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_interactions_contact ON interactions(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_interactions_date ON interactions(interaction_date)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_interactions_type ON interactions(type)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_memories_contact ON memories(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_memories_category ON memories(category)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_notes_contact ON notes(contact_id)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_contact_tags_tag ON contact_tags(tag)")

    # Lists indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_lists_name ON lists(name)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_lists_type ON lists(list_type)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_list_members_list ON list_members(list_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_list_members_contact ON list_members(contact_id)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_actions_contact ON actions(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_actions_status ON actions(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_actions_due_date ON actions(due_date)")

    cursor.execute("CREATE INDEX IF NOT EXISTS idx_lead_scores_category ON lead_scores(category)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_lead_scores_total ON lead_scores(total_score)")

    # Tasks indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_status ON tasks(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_priority ON tasks(priority)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_due_date ON tasks(due_date)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_contact ON tasks(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_goal ON tasks(goal_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_tasks_context ON tasks(context)")

    # Goals indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_goals_status ON goals(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_goals_category ON goals(category)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_goals_timeframe ON goals(timeframe)")

    # Ideas indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_ideas_status ON ideas(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_ideas_domain ON ideas(domain)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_ideas_goal ON ideas(goal_id)")

    # Facts indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_facts_domain ON facts(domain)")

    # Documents indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_documents_path ON documents(path)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_documents_type ON documents(doc_type)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_documents_contact ON documents(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_documents_goal ON documents(goal_id)")

    # Health entries indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_health_category ON health_entries(category)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_health_date ON health_entries(entry_date)")

    # Photos indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_source ON photos(source_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_hash ON photos(sha256_hash)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_category ON photos(category)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_screenshot ON photos(is_screenshot)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_contact ON photos(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photos_goal ON photos(goal_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_photo_sources_label ON photo_sources(label)")

    # Chunks indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_chunks_document ON chunks(document_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_chunks_hash ON chunks(content_hash)")

    # Social posts indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_social_posts_platform ON social_posts(platform)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_social_posts_status ON social_posts(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_social_posts_posted_at ON social_posts(posted_at)")

    # Email activity indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_email_activity_contact ON email_activity(contact_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_email_activity_account ON email_activity(account)")

    # Catalog indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_catalog_library ON catalog_entries(library_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_catalog_hash ON catalog_entries(file_hash)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_catalog_status ON catalog_entries(status)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_catalog_ext ON catalog_entries(file_ext)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_catalog_dept ON catalog_entries(department)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_libraries_label ON libraries(label)")

    # Entity links indexes
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_links_source ON entity_links(source_type, source_id)")
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_links_target ON entity_links(target_type, target_id)")

    # Search log index
    cursor.execute("CREATE INDEX IF NOT EXISTS idx_search_log_query ON search_log(query)")

    # Run schema migrations for existing databases
    _migrate_schema(conn)

    conn.commit()
    conn.close()

    _db_initialized = True
    if not silent:
        print("[OK] Vault database initialized")


def get_vault_stats() -> Dict[str, Any]:
    """Get statistics about the vault contents."""
    conn = get_db()

    stats = {}

    # Count contacts
    result = conn.execute("SELECT COUNT(*) FROM contacts").fetchone()
    stats['contacts'] = result[0] if result else 0

    # Count tasks by status
    result = conn.execute("SELECT COUNT(*) FROM tasks WHERE status = 'pending'").fetchone()
    stats['tasks_pending'] = result[0] if result else 0

    # complete_task() stores 'done' (per the tasks CHECK constraint), so count that.
    result = conn.execute("SELECT COUNT(*) FROM tasks WHERE status = 'done'").fetchone()
    stats['tasks_completed'] = result[0] if result else 0

    # Count goals by status
    result = conn.execute("SELECT COUNT(*) FROM goals WHERE status = 'active'").fetchone()
    stats['goals_active'] = result[0] if result else 0

    # Count ideas
    result = conn.execute("SELECT COUNT(*) FROM ideas").fetchone()
    stats['ideas'] = result[0] if result else 0

    # Count documents
    result = conn.execute("SELECT COUNT(*) FROM documents").fetchone()
    stats['documents'] = result[0] if result else 0

    # Count health entries
    result = conn.execute("SELECT COUNT(*) FROM health_entries").fetchone()
    stats['health_entries'] = result[0] if result else 0

    # Count social posts
    try:
        result = conn.execute("SELECT COUNT(*) FROM social_posts").fetchone()
        stats['social_posts'] = result[0] if result else 0
        result = conn.execute("SELECT COUNT(*) FROM social_posts WHERE status = 'draft'").fetchone()
        stats['social_posts_draft'] = result[0] if result else 0
        result = conn.execute("SELECT COUNT(*) FROM social_posts WHERE status = 'posted'").fetchone()
        stats['social_posts_posted'] = result[0] if result else 0
    except sqlite3.OperationalError:
        stats['social_posts'] = 0
        stats['social_posts_draft'] = 0
        stats['social_posts_posted'] = 0

    return stats


# ===========================================
# VECTOR EMBEDDINGS HELPERS
# ===========================================

def vec_add(
    id: str,
    collection: str,
    embedding: bytes,
    document: Optional[str] = None,
    metadata: Optional[Dict[str, Any]] = None
) -> None:
    """Add or replace an embedding in the vec_embeddings table."""
    conn = get_db()
    meta_json = json.dumps(metadata) if metadata else None
    conn.execute(
        """INSERT OR REPLACE INTO vec_embeddings
           (id, collection, embedding, document, metadata)
           VALUES (?, ?, ?, ?, ?)""",
        (id, collection, embedding, document, meta_json)
    )
    conn.commit()
    conn.close()


def vec_add_batch(
    rows: List[Dict[str, Any]],
    collection: str
) -> None:
    """Add multiple embeddings in a single transaction.

    Each row dict must have: id, embedding (bytes).
    Optional: document (str), metadata (dict).
    """
    if not rows:
        return
    conn = get_db()
    data = []
    for row in rows:
        meta_json = json.dumps(row.get('metadata')) if row.get('metadata') else None
        data.append((
            row['id'], collection, row['embedding'],
            row.get('document'), meta_json
        ))
    conn.executemany(
        """INSERT OR REPLACE INTO vec_embeddings
           (id, collection, embedding, document, metadata)
           VALUES (?, ?, ?, ?, ?)""",
        data
    )
    conn.commit()
    conn.close()


def vec_get_all(collection: str) -> List[Dict[str, Any]]:
    """Return all rows for a collection."""
    conn = get_db()
    rows = conn.execute(
        "SELECT id, embedding, document, metadata FROM vec_embeddings WHERE collection = ?",
        (collection,)
    ).fetchall()
    conn.close()
    result = []
    for r in rows:
        meta = json.loads(r['metadata']) if r['metadata'] else {}
        result.append({
            'id': r['id'],
            'embedding': r['embedding'],
            'document': r['document'],
            'metadata': meta
        })
    return result


def vec_delete_by_id(id: str, collection: str) -> None:
    """Delete a single embedding by id and collection."""
    conn = get_db()
    conn.execute(
        "DELETE FROM vec_embeddings WHERE id = ? AND collection = ?",
        (id, collection)
    )
    conn.commit()
    conn.close()


def vec_delete_by_metadata(collection: str, key: str, value: Any) -> int:
    """Delete embeddings where metadata->>key = value. Returns count deleted."""
    conn = get_db()
    cursor = conn.execute(
        "DELETE FROM vec_embeddings WHERE collection = ? AND json_extract(metadata, '$.' || ?) = ?",
        (collection, key, value)
    )
    deleted = cursor.rowcount
    conn.commit()
    conn.close()
    return deleted


def vec_count(collection: str) -> int:
    """Count embeddings in a collection."""
    conn = get_db()
    row = conn.execute(
        "SELECT COUNT(*) FROM vec_embeddings WHERE collection = ?",
        (collection,)
    ).fetchone()
    conn.close()
    return row[0] if row else 0


def vec_get_embedding(id: str, collection: str) -> Optional[bytes]:
    """Get a single embedding blob by id."""
    conn = get_db()
    row = conn.execute(
        "SELECT embedding FROM vec_embeddings WHERE id = ? AND collection = ?",
        (id, collection)
    ).fetchone()
    conn.close()
    return row['embedding'] if row else None


# ===========================================
# CONTACT MANAGEMENT
# ===========================================

def add_contact(email: str, name: str, account: str, **kwargs) -> int:
    """
    Add a new contact.
    Returns the new contact's ID.
    """
    if account not in ('consulting', 'personal', 'both'):
        raise ValueError(f"Invalid account '{account}'. Must be: consulting, personal, both")

    init_db(silent=True)

    # The CLI exposes the job role as -r/--role, but the column is `title`
    # (which is what `contacts edit --title` writes). Accept `role` as an alias
    # so a role passed at creation is actually persisted instead of silently
    # dropped. An explicit `title` wins if somehow both are supplied.
    role_value = kwargs.pop('role', None)
    if role_value is not None and kwargs.get('title') is None:
        kwargs['title'] = role_value

    conn = get_db()
    try:
        cursor = conn.cursor()

        # Build insert with optional fields
        fields = ['email', 'name', 'account']
        values = [email, name, account]

        valid_fields = [
            'nickname', 'pronunciation', 'category', 'relationship', 'priority',
            'style', 'greeting', 'signoff', 'best_contact_method', 'best_time',
            'timezone', 'response_speed', 'context', 'company', 'title', 'location',
            'birthday', 'spouse_name', 'children', 'pets', 'hobbies',
            'phone', 'linkedin', 'twitter', 'website', 'address',
            'first_contact', 'last_contact', 'contact_frequency',
            # Social media / messaging
            'whatsapp', 'instagram', 'facebook', 'github', 'skype', 'telegram', 'signal',
            # CRM fields
            'lead_source', 'referred_by', 'lead_status', 'client_since', 'contract_value', 'next_followup',
            # Relationship
            'relationship_strength'
        ]

        for field in valid_fields:
            if field in kwargs and kwargs[field] is not None:
                fields.append(field)
                values.append(kwargs[field])

        placeholders = ', '.join(['?' for _ in fields])
        field_names = ', '.join(fields)

        cursor.execute(f"""
            INSERT INTO contacts ({field_names})
            VALUES ({placeholders})
        """, values)

        contact_id = cursor.lastrowid
        conn.commit()
        return contact_id
    finally:
        conn.close()


def get_contact(identifier) -> Optional[dict]:
    """Get a contact by email or partial name match."""
    if not identifier:
        return None
    identifier = str(identifier)

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    # Try exact email match on contacts table first
    cursor.execute("SELECT * FROM contacts WHERE email = ?", (identifier,))
    row = cursor.fetchone()

    if not row:
        # Try contact_emails table (secondary/merged emails)
        cursor.execute("""
            SELECT c.* FROM contacts c
            JOIN contact_emails ce ON ce.contact_id = c.id
            WHERE ce.email = ?
        """, (identifier,))
        row = cursor.fetchone()

    if not row:
        # Try partial name match (case-insensitive)
        search_term = f"%{identifier.lower()}%"
        cursor.execute(
            "SELECT * FROM contacts WHERE LOWER(name) LIKE ? OR LOWER(nickname) LIKE ?",
            (search_term, search_term)
        )
        row = cursor.fetchone()

    conn.close()
    return dict(row) if row else None


def get_contact_by_id(contact_id: int) -> Optional[dict]:
    """Get a contact by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("SELECT * FROM contacts WHERE id = ?", (contact_id,))
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None


def get_contact_by_linkedin(url: str) -> Optional[dict]:
    """Get a contact by LinkedIn URL (exact or partial match on the linkedin field)."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    # Normalize: strip trailing slashes for matching
    normalized = url.rstrip('/')

    cursor.execute(
        "SELECT * FROM contacts WHERE REPLACE(linkedin, '/', '') = REPLACE(?, '/', '') "
        "OR linkedin LIKE ?",
        (normalized, f"%{normalized}%")
    )
    row = cursor.fetchone()
    conn.close()
    return dict(row) if row else None


# ===========================================
# CONTACT EMAIL MANAGEMENT
# ===========================================

def get_contact_emails(contact_id: int) -> List[dict]:
    """Get all emails for a contact from contact_emails table."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(
        "SELECT * FROM contact_emails WHERE contact_id = ? ORDER BY is_primary DESC, label",
        (contact_id,)
    )
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()
    return results


def add_contact_email(contact_id: int, email: str, label: str = 'other', is_primary: bool = False) -> int:
    """Add an email address to a contact. Returns the new row ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    if is_primary:
        # Unset any existing primary
        cursor.execute(
            "UPDATE contact_emails SET is_primary = 0 WHERE contact_id = ?",
            (contact_id,)
        )
        # Also update contacts.email
        cursor.execute(
            "UPDATE contacts SET email = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
            (email, contact_id)
        )

    cursor.execute(
        "INSERT INTO contact_emails (contact_id, email, label, is_primary) VALUES (?, ?, ?, ?)",
        (contact_id, email, label, 1 if is_primary else 0)
    )
    row_id = cursor.lastrowid
    conn.commit()
    conn.close()
    return row_id


def remove_contact_email(contact_id: int, email: str) -> bool:
    """Remove an email from a contact. Cannot remove the primary email."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute(
        "SELECT is_primary FROM contact_emails WHERE contact_id = ? AND email = ?",
        (contact_id, email)
    )
    row = cursor.fetchone()
    if not row:
        conn.close()
        raise ValueError(f"Email {email} not found on contact #{contact_id}")
    if row[0] == 1:
        conn.close()
        raise ValueError("Cannot remove primary email. Set a different primary first.")

    cursor.execute(
        "DELETE FROM contact_emails WHERE contact_id = ? AND email = ?",
        (contact_id, email)
    )
    conn.commit()
    conn.close()
    return True


def set_primary_email(contact_id: int, email: str) -> bool:
    """Set an email as primary for a contact. Updates contacts.email too."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute(
        "SELECT id FROM contact_emails WHERE contact_id = ? AND email = ?",
        (contact_id, email)
    )
    if not cursor.fetchone():
        conn.close()
        raise ValueError(f"Email {email} not found on contact #{contact_id}")

    cursor.execute(
        "UPDATE contact_emails SET is_primary = 0 WHERE contact_id = ?",
        (contact_id,)
    )
    cursor.execute(
        "UPDATE contact_emails SET is_primary = 1 WHERE contact_id = ? AND email = ?",
        (contact_id, email)
    )
    cursor.execute(
        "UPDATE contacts SET email = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
        (email, contact_id)
    )
    conn.commit()
    conn.close()
    return True


def update_contact_email_label(contact_id: int, email: str, label: str) -> bool:
    """Update the label for an existing email on a contact."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute(
        "SELECT id FROM contact_emails WHERE contact_id = ? AND email = ?",
        (contact_id, email)
    )
    if not cursor.fetchone():
        conn.close()
        raise ValueError(f"Email {email} not found on contact #{contact_id}")

    cursor.execute(
        "UPDATE contact_emails SET label = ? WHERE contact_id = ? AND email = ?",
        (label, contact_id, email)
    )
    conn.commit()
    conn.close()
    return True


# ===========================================
# CONTACT MERGE
# ===========================================

def merge_contacts(target_id: int, source_ids: List[int], email_labels: Optional[Dict[str, str]] = None) -> dict:
    """Merge source contacts into target contact.

    Reassigns all related records, moves emails, creates audit trail, deletes sources.
    Runs in a single transaction for atomicity.

    Args:
        target_id: The surviving contact ID
        source_ids: List of contact IDs to absorb into target
        email_labels: Optional dict mapping email -> label (work, personal, other)

    Returns:
        Summary dict with counts of reassigned records
    """
    init_db(silent=True)

    if not email_labels:
        email_labels = {}

    target = get_contact_by_id(target_id)
    if not target:
        raise ValueError(f"Target contact #{target_id} not found")

    sources = []
    for sid in source_ids:
        s = get_contact_by_id(sid)
        if not s:
            raise ValueError(f"Source contact #{sid} not found")
        if sid == target_id:
            raise ValueError(f"Cannot merge contact #{sid} into itself")
        sources.append(s)

    conn = get_db()
    cursor = conn.cursor()
    summary = {
        'target_id': target_id,
        'source_ids': source_ids,
        'interactions': 0,
        'memories': 0,
        'notes': 0,
        'tags': 0,
        'list_members': 0,
        'actions': 0,
        'tasks': 0,
        'documents': 0,
        'photos': 0,
        'email_activity': 0,
        'entity_links': 0,
        'emails_added': 0,
        'sources_deleted': 0,
    }

    try:
        placeholders = ','.join('?' * len(source_ids))

        # Reassign interactions
        cursor.execute(
            f"UPDATE interactions SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['interactions'] = cursor.rowcount

        # Reassign memories
        cursor.execute(
            f"UPDATE memories SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['memories'] = cursor.rowcount

        # Reassign notes
        cursor.execute(
            f"UPDATE notes SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['notes'] = cursor.rowcount

        # Reassign contact_tags (skip duplicates)
        for sid in source_ids:
            cursor.execute(
                "UPDATE OR IGNORE contact_tags SET contact_id = ? WHERE contact_id = ?",
                (target_id, sid)
            )
            summary['tags'] += cursor.rowcount
            # Delete any remaining (were duplicates)
            cursor.execute("DELETE FROM contact_tags WHERE contact_id = ?", (sid,))

        # Reassign list_members (skip duplicates)
        for sid in source_ids:
            cursor.execute(
                "UPDATE OR IGNORE list_members SET contact_id = ? WHERE contact_id = ?",
                (target_id, sid)
            )
            summary['list_members'] += cursor.rowcount
            cursor.execute("DELETE FROM list_members WHERE contact_id = ?", (sid,))

        # Reassign lead_scores (keep the best)
        cursor.execute(
            "SELECT contact_id, COALESCE(total_score, 0) FROM lead_scores WHERE contact_id = ?",
            (target_id,)
        )
        target_score_row = cursor.fetchone()
        target_score = target_score_row[1] if target_score_row else 0

        for sid in source_ids:
            cursor.execute(
                "SELECT COALESCE(total_score, 0) FROM lead_scores WHERE contact_id = ?", (sid,)
            )
            src_row = cursor.fetchone()
            if src_row and src_row[0] > target_score:
                if target_score_row:
                    cursor.execute(
                        "UPDATE lead_scores SET total_score = ? WHERE contact_id = ?",
                        (src_row[0], target_id)
                    )
                else:
                    cursor.execute(
                        "INSERT INTO lead_scores (contact_id, total_score) VALUES (?, ?)",
                        (target_id, src_row[0])
                    )
                target_score = src_row[0]
            cursor.execute("DELETE FROM lead_scores WHERE contact_id = ?", (sid,))

        # Reassign actions
        cursor.execute(
            f"UPDATE actions SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['actions'] = cursor.rowcount

        # Reassign tasks (nullable FK)
        cursor.execute(
            f"UPDATE tasks SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['tasks'] = cursor.rowcount

        # Reassign documents (nullable FK)
        cursor.execute(
            f"UPDATE documents SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['documents'] = cursor.rowcount

        # Reassign photos (nullable FK)
        cursor.execute(
            f"UPDATE photos SET contact_id = ? WHERE contact_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['photos'] = cursor.rowcount

        # Merge email_activity (update contact_id, merge counts for same account)
        for sid in source_ids:
            cursor.execute(
                "SELECT account, sent_count, received_count, first_email_date, last_email_date "
                "FROM email_activity WHERE contact_id = ?", (sid,)
            )
            for ea_row in cursor.fetchall():
                acct = ea_row[0]
                # Check if target already has this account
                cursor.execute(
                    "SELECT sent_count, received_count FROM email_activity "
                    "WHERE contact_id = ? AND account = ?", (target_id, acct)
                )
                existing = cursor.fetchone()
                if existing:
                    cursor.execute(
                        "UPDATE email_activity SET sent_count = sent_count + ?, "
                        "received_count = received_count + ? "
                        "WHERE contact_id = ? AND account = ?",
                        (ea_row[1] or 0, ea_row[2] or 0, target_id, acct)
                    )
                else:
                    cursor.execute(
                        "UPDATE email_activity SET contact_id = ? "
                        "WHERE contact_id = ? AND account = ?",
                        (target_id, sid, acct)
                    )
                summary['email_activity'] += 1
            # Clean up any remaining
            cursor.execute("DELETE FROM email_activity WHERE contact_id = ?", (sid,))

        # Reassign entity_links
        cursor.execute(
            f"UPDATE entity_links SET source_id = ? "
            f"WHERE source_type = 'contact' AND source_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['entity_links'] += cursor.rowcount
        cursor.execute(
            f"UPDATE entity_links SET target_id = ? "
            f"WHERE target_type = 'contact' AND target_id IN ({placeholders})",
            [target_id] + source_ids
        )
        summary['entity_links'] += cursor.rowcount

        # Update referred_by on any contacts pointing to sources
        cursor.execute(
            f"UPDATE contacts SET referred_by = ? WHERE referred_by IN ({placeholders})",
            [target_id] + source_ids
        )

        # Move emails from sources to target's contact_emails
        # Collect all emails to move, then delete source rows first
        # to avoid UNIQUE constraint blocking the inserts
        emails_to_move = []
        for source in sources:
            source_email = source['email']
            if source_email:
                label = email_labels.get(source_email, 'other')
                emails_to_move.append((source_email, label))
            # Collect contact_emails from source
            cursor.execute(
                "SELECT email, label FROM contact_emails WHERE contact_id = ?", (source['id'],)
            )
            for ce_row in cursor.fetchall():
                ce_email = ce_row[0]
                ce_label = email_labels.get(ce_email, ce_row[1])
                emails_to_move.append((ce_email, ce_label))
            # Delete source contact_emails FIRST
            cursor.execute(
                "DELETE FROM contact_emails WHERE contact_id = ?", (source['id'],)
            )
        # Now insert collected emails into target (skip duplicates already on target)
        for em, lbl in emails_to_move:
            cursor.execute(
                "INSERT OR IGNORE INTO contact_emails "
                "(contact_id, email, label, is_primary) VALUES (?, ?, ?, 0)",
                (target_id, em, lbl)
            )
            if cursor.rowcount > 0:
                summary['emails_added'] += 1

        # Create audit trail entity_links
        for source in sources:
            cursor.execute(
                "INSERT INTO entity_links (source_type, source_id, target_type, target_id, relationship, strength) "
                "VALUES ('contact', ?, 'contact', ?, 'merged_from', 1)",
                (target_id, source['id'])
            )

        # Add merge note
        merged_list = ', '.join(
            f"{s['email']} (#{s['id']})" for s in sources
        )
        cursor.execute(
            "INSERT INTO notes (contact_id, note, context) VALUES (?, ?, ?)",
            (target_id, f"Merged from: {merged_list}", "contact_merge")
        )

        # Delete source contacts (CASCADE handles remaining FKs)
        cursor.execute(
            f"DELETE FROM contacts WHERE id IN ({placeholders})",
            source_ids
        )
        summary['sources_deleted'] = cursor.rowcount

        # Update last_contact on target to the most recent interaction
        cursor.execute(
            "SELECT MAX(interaction_date) FROM interactions WHERE contact_id = ?",
            (target_id,)
        )
        max_date_row = cursor.fetchone()
        if max_date_row and max_date_row[0]:
            max_date = max_date_row[0][:10] if len(max_date_row[0]) > 10 else max_date_row[0]
            cursor.execute(
                "UPDATE contacts SET last_contact = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
                (max_date, target_id)
            )

        conn.commit()
        return summary

    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


def get_merge_preview(target_id: int, source_ids: List[int]) -> dict:
    """Preview what a merge would do without executing it.

    Returns counts of records that would be reassigned.
    """
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    target = get_contact_by_id(target_id)
    if not target:
        raise ValueError(f"Target contact #{target_id} not found")

    placeholders = ','.join('?' * len(source_ids))
    preview = {
        'target': target,
        'sources': [],
        'interactions': 0,
        'memories': 0,
        'notes': 0,
        'tags': 0,
        'list_members': 0,
        'actions': 0,
        'email_activity': 0,
        'emails_to_add': [],
    }

    for sid in source_ids:
        s = get_contact_by_id(sid)
        if s:
            # Count interactions for this source
            cursor.execute(
                "SELECT COUNT(*) FROM interactions WHERE contact_id = ?", (sid,)
            )
            s['interaction_count'] = cursor.fetchone()[0]
            preview['sources'].append(s)

    cursor.execute(
        f"SELECT COUNT(*) FROM interactions WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['interactions'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM memories WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['memories'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM notes WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['notes'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM contact_tags WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['tags'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM list_members WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['list_members'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM actions WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['actions'] = cursor.fetchone()[0]

    cursor.execute(
        f"SELECT COUNT(*) FROM email_activity WHERE contact_id IN ({placeholders})",
        source_ids
    )
    preview['email_activity'] = cursor.fetchone()[0]

    # Emails that would be added
    for sid in source_ids:
        s = get_contact_by_id(sid)
        if s and s.get('email'):
            preview['emails_to_add'].append(s['email'])

    conn.close()
    return preview


def get_last_communication(contact_id: int) -> dict:
    """Get last communication summary for a contact.

    Returns dict with:
        last_touch: most recent interaction (any direction)
        last_inbound: most recent inbound interaction
        last_outbound: most recent outbound interaction
        days_since_last: days since last interaction
        email_activity: list of email_activity records
    """
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    result = {
        'contact_id': contact_id,
        'last_touch': None,
        'last_inbound': None,
        'last_outbound': None,
        'days_since_last': None,
        'email_activity': [],
    }

    # Last touch overall
    cursor.execute("""
        SELECT * FROM interactions
        WHERE contact_id = ?
        ORDER BY interaction_date DESC
        LIMIT 1
    """, (contact_id,))
    row = cursor.fetchone()
    if row:
        result['last_touch'] = dict(row)
        # Calculate days since last
        last_date_str = row['interaction_date']
        if last_date_str:
            try:
                last_dt = datetime.fromisoformat(last_date_str.replace('Z', '+00:00'))
                delta = datetime.now() - last_dt.replace(tzinfo=None)
                result['days_since_last'] = delta.days
            except (ValueError, TypeError):
                pass

    # Last inbound
    cursor.execute("""
        SELECT * FROM interactions
        WHERE contact_id = ? AND direction = 'inbound'
        ORDER BY interaction_date DESC
        LIMIT 1
    """, (contact_id,))
    row = cursor.fetchone()
    if row:
        result['last_inbound'] = dict(row)

    # Last outbound
    cursor.execute("""
        SELECT * FROM interactions
        WHERE contact_id = ? AND direction = 'outbound'
        ORDER BY interaction_date DESC
        LIMIT 1
    """, (contact_id,))
    row = cursor.fetchone()
    if row:
        result['last_outbound'] = dict(row)

    # Email activity aggregates
    cursor.execute("""
        SELECT ea.*, c.name, c.email
        FROM email_activity ea
        JOIN contacts c ON c.id = ea.contact_id
        WHERE ea.contact_id = ?
    """, (contact_id,))
    result['email_activity'] = [dict(r) for r in cursor.fetchall()]

    conn.close()
    return result


def list_contacts(
    account: Optional[str] = None,
    category: Optional[str] = None,
    relationship: Optional[str] = None,
    has_fields: Optional[List[str]] = None,
    missing_fields: Optional[List[str]] = None,
    tags: Optional[List[str]] = None,
) -> List[dict]:
    """List contacts with optional filtering.

    Args:
        account: Filter by account type.
        category: Filter by category.
        relationship: Filter by relationship.
        has_fields: Only return contacts where these fields are non-empty.
        missing_fields: Only return contacts where these fields are empty/null.
        tags: Filter by tags (AND logic - contact must have ALL specified tags).
    """
    valid_fields = [
        'name', 'nickname', 'pronunciation', 'account', 'category', 'relationship',
        'priority', 'style', 'greeting', 'signoff', 'best_contact_method',
        'best_time', 'timezone', 'response_speed', 'context', 'company',
        'title', 'location', 'birthday', 'spouse_name', 'children', 'pets',
        'hobbies', 'phone', 'linkedin', 'twitter', 'website', 'address',
        'first_contact', 'last_contact', 'contact_frequency',
        'whatsapp', 'instagram', 'facebook', 'github', 'skype', 'telegram', 'signal',
        'lead_source', 'referred_by', 'lead_status', 'client_since', 'contract_value', 'next_followup',
        'relationship_strength', 'email',
    ]

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = "SELECT * FROM contacts WHERE 1=1"
    params = []

    if account:
        if account == 'both':
            sql += " AND account IN ('consulting', 'personal', 'both')"
        else:
            sql += " AND (account = ? OR account = 'both')"
            params.append(account)

    if category:
        sql += " AND category = ?"
        params.append(category)

    if relationship:
        sql += " AND relationship = ?"
        params.append(relationship)

    if has_fields:
        for field in has_fields:
            if field not in valid_fields:
                conn.close()
                raise ValueError(f"Invalid field: {field}")
            sql += f" AND ({field} IS NOT NULL AND {field} != '')"

    if missing_fields:
        for field in missing_fields:
            if field not in valid_fields:
                conn.close()
                raise ValueError(f"Invalid field: {field}")
            sql += f" AND ({field} IS NULL OR {field} = '')"

    if tags:
        for tag in tags:
            tag_normalized = tag.lower().strip()
            sql += " AND id IN (SELECT contact_id FROM contact_tags WHERE tag = ?)"
            params.append(tag_normalized)

    sql += " ORDER BY name"

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def delete_contact(contact_id: int) -> bool:
    """Delete a contact by ID. Returns True if deleted, False if not found."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()
    cursor.execute("DELETE FROM contacts WHERE id = ?", (contact_id,))
    deleted = cursor.rowcount > 0
    conn.commit()
    conn.close()
    return deleted


def update_contact(contact_id: int, **kwargs) -> bool:
    """
    Update a contact's fields by contact ID.
    Returns True if updated, False if contact not found.
    """
    contact = get_contact_by_id(contact_id)
    if not contact:
        return False

    valid_fields = [
        'name', 'nickname', 'pronunciation', 'account', 'category', 'relationship',
        'priority', 'style', 'greeting', 'signoff', 'best_contact_method',
        'best_time', 'timezone', 'response_speed', 'context', 'company',
        'title', 'location', 'birthday', 'spouse_name', 'children', 'pets',
        'hobbies', 'phone', 'linkedin', 'twitter', 'website', 'address',
        'first_contact', 'last_contact', 'contact_frequency',
        # Social media / messaging
        'whatsapp', 'instagram', 'facebook', 'github', 'skype', 'telegram', 'signal',
        # CRM fields
        'lead_source', 'referred_by', 'lead_status', 'client_since', 'contract_value', 'next_followup',
        # Relationship
        'relationship_strength',
        # Email
        'email',
    ]

    updates = []
    params = []

    for field in valid_fields:
        if field in kwargs and kwargs[field] is not None:
            updates.append(f"{field} = ?")
            params.append(kwargs[field])

    if not updates:
        return True  # Nothing to update

    updates.append("updated_at = CURRENT_TIMESTAMP")
    sql = f"UPDATE contacts SET {', '.join(updates)} WHERE id = ?"
    params.append(contact_id)

    conn = get_db()
    cursor = conn.cursor()
    cursor.execute(sql, params)
    conn.commit()
    conn.close()

    return True


def search_contacts(query: str) -> List[dict]:
    """Search contacts by name, email, context, company, pets, hobbies, etc."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()

        search_term = f"%{query}%"
        cursor.execute("""
            SELECT * FROM contacts
            WHERE LOWER(name) LIKE LOWER(?)
               OR LOWER(email) LIKE LOWER(?)
               OR LOWER(context) LIKE LOWER(?)
               OR LOWER(company) LIKE LOWER(?)
               OR LOWER(nickname) LIKE LOWER(?)
               OR LOWER(pets) LIKE LOWER(?)
               OR LOWER(hobbies) LIKE LOWER(?)
               OR LOWER(relationship) LIKE LOWER(?)
            ORDER BY name
        """, (search_term,) * 8)

        results = [dict(row) for row in cursor.fetchall()]
        return results
    finally:
        conn.close()


def filter_contacts(
    company: Optional[str] = None,
    domain: Optional[str] = None,
    tag: Optional[str] = None,
    notes: Optional[str] = None,
    title: Optional[str] = None,
    location: Optional[str] = None,
    limit: int = 50,
) -> List[dict]:
    """Search contacts by field-level filters.

    All filters use case-insensitive LIKE matching. Multiple filters
    are combined with AND logic.

    Args:
        company: Match company field.
        domain: Match email domain (e.g. "bakertilly.ca").
        tag: Match contacts with this tag.
        notes: Full-text search in context/notes field.
        title: Match job title field.
        location: Match location field.
        limit: Max results.

    Returns:
        List of matching contact dicts.
    """
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()

        sql = "SELECT c.* FROM contacts c WHERE 1=1"
        params = []

        if company:
            sql += " AND LOWER(c.company) LIKE LOWER(?)"
            params.append(f"%{company}%")

        if domain:
            sql += " AND LOWER(c.email) LIKE LOWER(?)"
            params.append(f"%@{domain.lstrip('@')}%")

        if notes:
            sql += " AND LOWER(c.context) LIKE LOWER(?)"
            params.append(f"%{notes}%")

        if title:
            sql += " AND LOWER(c.title) LIKE LOWER(?)"
            params.append(f"%{title}%")

        if location:
            sql += " AND LOWER(c.location) LIKE LOWER(?)"
            params.append(f"%{location}%")

        if tag:
            sql += " AND c.id IN (SELECT contact_id FROM contact_tags WHERE tag = ?)"
            params.append(tag.lower().strip())

        sql += " ORDER BY c.name LIMIT ?"
        params.append(limit)

        cursor.execute(sql, params)
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()


def fuzzy_search_contacts(
    query: str,
    threshold: int = 50,
    limit: int = 10
) -> List[dict]:
    """
    Search contacts using fuzzy and phonetic matching.

    Args:
        query: Search query (name to find)
        threshold: Minimum score (0-100) to include in results (default 50)
        limit: Maximum number of results to return (default 10)

    Returns:
        List of contacts with match scores, sorted by score descending.
        Each contact dict has added 'match_score' and 'match_type' fields.
    """
    try:
        from .fuzzy_search import fuzzy_search_contacts as _fuzzy_search
    except ImportError:
        from fuzzy_search import fuzzy_search_contacts as _fuzzy_search

    # Get all contacts
    contacts = list_contacts()

    # Run fuzzy search
    return _fuzzy_search(query, contacts, threshold=threshold, limit=limit)


# ===========================================
# TAG MANAGEMENT
# ===========================================

def add_tags(email: str, *tags: str) -> int:
    """
    Add tags to a contact.
    Returns the number of tags added.
    """
    contact = get_contact(email)
    if not contact:
        raise ValueError(f"Contact not found: {email}")

    conn = get_db()
    cursor = conn.cursor()
    added = 0

    for tag_item in tags:
        tag_normalized = tag_item.lower().strip()
        if not tag_normalized:
            continue
        try:
            cursor.execute(
                "INSERT INTO contact_tags (contact_id, tag) VALUES (?, ?)",
                (contact['id'], tag_normalized)
            )
            added += 1
        except sqlite3.IntegrityError:
            pass  # Tag already exists

    conn.commit()
    conn.close()

    return added


def remove_tag(email: str, tag: str) -> bool:
    """
    Remove a tag from a contact.
    Returns True if removed, False if tag wasn't found.
    """
    contact = get_contact(email)
    if not contact:
        raise ValueError(f"Contact not found: {email}")

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute(
        "DELETE FROM contact_tags WHERE contact_id = ? AND tag = ?",
        (contact['id'], tag.lower().strip())
    )

    removed = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return removed


def get_tags(contact_id: int) -> List[str]:
    """Get all tags for a contact by contact ID."""
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute(
        "SELECT tag FROM contact_tags WHERE contact_id = ? ORDER BY tag",
        (contact_id,)
    )

    tags = [row[0] for row in cursor.fetchall()]
    conn.close()

    return tags


def list_contacts_by_tag(tag: str) -> List[dict]:
    """List all contacts with a specific tag."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT c.* FROM contacts c
        JOIN contact_tags t ON c.id = t.contact_id
        WHERE t.tag = ?
        ORDER BY c.name
    """, (tag.lower().strip(),))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def list_all_tags() -> List[dict]:
    """List all tags with their contact counts."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT tag, COUNT(*) as count
        FROM contact_tags
        GROUP BY tag
        ORDER BY count DESC, tag
    """)

    results = [{'tag': row[0], 'count': row[1]} for row in cursor.fetchall()]
    conn.close()

    return results


def add_tags_by_id(contact_id: int, *tags: str) -> int:
    """
    Add tags to a contact by contact ID.
    Returns the number of tags added.
    """
    conn = get_db()
    cursor = conn.cursor()

    # Verify contact exists
    cursor.execute("SELECT id FROM contacts WHERE id = ?", (contact_id,))
    if not cursor.fetchone():
        conn.close()
        raise ValueError(f"Contact #{contact_id} not found")

    added = 0
    for tag_item in tags:
        tag_normalized = tag_item.lower().strip()
        if not tag_normalized:
            continue
        try:
            cursor.execute(
                "INSERT INTO contact_tags (contact_id, tag) VALUES (?, ?)",
                (contact_id, tag_normalized)
            )
            added += 1
        except sqlite3.IntegrityError:
            pass  # Tag already exists

    conn.commit()
    conn.close()

    return added


def remove_tags_by_id(contact_id: int, *tags: str) -> int:
    """
    Remove tags from a contact by contact ID.
    Returns the number of tags removed.
    """
    conn = get_db()
    cursor = conn.cursor()

    # Verify contact exists
    cursor.execute("SELECT id FROM contacts WHERE id = ?", (contact_id,))
    if not cursor.fetchone():
        conn.close()
        raise ValueError(f"Contact #{contact_id} not found")

    removed = 0
    for tag_item in tags:
        tag_normalized = tag_item.lower().strip()
        if not tag_normalized:
            continue
        cursor.execute(
            "DELETE FROM contact_tags WHERE contact_id = ? AND tag = ?",
            (contact_id, tag_normalized)
        )
        removed += cursor.rowcount

    conn.commit()
    conn.close()

    return removed


# ===========================================
# LIST MANAGEMENT
# ===========================================

def create_list(name: str, description: Optional[str] = None, list_type: str = "general") -> int:
    """
    Create a named contact list.
    Returns the new list's ID.
    """
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            "INSERT INTO lists (name, description, list_type) VALUES (?, ?, ?)",
            (name, description, list_type)
        )
        list_id = cursor.lastrowid
        conn.commit()
        return list_id
    finally:
        conn.close()


def get_list(name: str) -> Optional[dict]:
    """Get a list by name."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM lists WHERE name = ?", (name,))
        row = cursor.fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def get_list_by_id(list_id: int) -> Optional[dict]:
    """Get a list by ID."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM lists WHERE id = ?", (list_id,))
        row = cursor.fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def list_lists() -> List[dict]:
    """List all lists with member counts."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("""
            SELECT l.*, COUNT(lm.contact_id) as member_count
            FROM lists l
            LEFT JOIN list_members lm ON l.id = lm.list_id
            GROUP BY l.id
            ORDER BY l.name
        """)
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()


def delete_list(name: str) -> bool:
    """Delete a list by name. Members are cascade-deleted."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("DELETE FROM lists WHERE name = ?", (name,))
        deleted = cursor.rowcount > 0
        conn.commit()
        return deleted
    finally:
        conn.close()


def add_list_member(list_name: str, contact_id: int, notes: Optional[str] = None) -> bool:
    """
    Add a contact to a list.
    Returns True if added, False if already a member.
    """
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            "INSERT INTO list_members (list_id, contact_id, notes) VALUES (?, ?, ?)",
            (lst['id'], contact_id, notes)
        )
        conn.commit()
        return True
    except sqlite3.IntegrityError:
        return False
    finally:
        conn.close()


def add_list_members_by_query(list_name: str, where_clause: str, params: Optional[list] = None) -> int:
    """
    Add contacts matching a SQL WHERE clause to a list.
    Returns the number of contacts added.
    """
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        # Find matching contacts not already in the list
        sql = f"""
            INSERT OR IGNORE INTO list_members (list_id, contact_id)
            SELECT ?, c.id FROM contacts c
            WHERE {where_clause}
            AND c.id NOT IN (SELECT contact_id FROM list_members WHERE list_id = ?)
        """
        query_params = [lst['id']] + (params or []) + [lst['id']]
        cursor.execute(sql, query_params)
        added = cursor.rowcount
        conn.commit()
        return added
    finally:
        conn.close()


def remove_list_member(list_name: str, contact_id: int) -> bool:
    """Remove a contact from a list. Returns True if removed."""
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            "DELETE FROM list_members WHERE list_id = ? AND contact_id = ?",
            (lst['id'], contact_id)
        )
        removed = cursor.rowcount > 0
        conn.commit()
        return removed
    finally:
        conn.close()


def get_list_members(list_name: str) -> List[dict]:
    """Get all contacts in a list, joined with contact details."""
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("""
            SELECT c.*, lm.added_at as list_added_at, lm.notes as list_notes
            FROM contacts c
            JOIN list_members lm ON c.id = lm.contact_id
            WHERE lm.list_id = ?
            ORDER BY c.name
        """, (lst['id'],))
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()


def export_list(list_name: str, format: str = "json") -> str:
    """
    Export list members as JSON or CSV string.
    """
    members = get_list_members(list_name)

    if format == "csv":
        import csv
        import io
        output = io.StringIO()
        if not members:
            return ""
        fields = ['id', 'name', 'email', 'company', 'title', 'location', 'phone', 'linkedin']
        writer = csv.DictWriter(output, fieldnames=fields, extrasaction='ignore')
        writer.writeheader()
        for m in members:
            writer.writerow(m)
        return output.getvalue()
    else:
        # JSON format
        export_fields = ['id', 'name', 'email', 'company', 'title', 'location',
                         'phone', 'linkedin', 'list_added_at', 'list_notes']
        export_data = []
        for m in members:
            export_data.append({k: m.get(k) for k in export_fields})
        return json.dumps(export_data, indent=2, default=str)


def rename_list(name: str, new_name: str) -> bool:
    """Rename a list. Returns True if renamed. Raises ValueError if not found."""
    init_db(silent=True)
    lst = get_list(name)
    if not lst:
        raise ValueError(f"List not found: {name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            "UPDATE lists SET name = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
            (new_name, lst['id'])
        )
        conn.commit()
        return cursor.rowcount > 0
    finally:
        conn.close()


def update_list(name: str, description: Optional[str] = None, list_type: Optional[str] = None) -> bool:
    """Update a list's description and/or type. Returns True if updated."""
    init_db(silent=True)
    lst = get_list(name)
    if not lst:
        raise ValueError(f"List not found: {name}")

    updates = []
    params = []
    if description is not None:
        updates.append("description = ?")
        params.append(description)
    if list_type is not None:
        updates.append("list_type = ?")
        params.append(list_type)

    if not updates:
        return False

    updates.append("updated_at = CURRENT_TIMESTAMP")
    params.append(lst['id'])

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            f"UPDATE lists SET {', '.join(updates)} WHERE id = ?",
            params
        )
        conn.commit()
        return cursor.rowcount > 0
    finally:
        conn.close()


def copy_list(source_name: str, dest_name: str) -> int:
    """
    Duplicate a list with all its members.
    Returns the number of members copied.
    """
    init_db(silent=True)
    src = get_list(source_name)
    if not src:
        raise ValueError(f"List not found: {source_name}")

    # create_list will raise IntegrityError if dest_name exists
    new_id = create_list(dest_name, description=src.get('description'), list_type=src.get('list_type', 'general'))

    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("""
            INSERT INTO list_members (list_id, contact_id, notes)
            SELECT ?, contact_id, notes FROM list_members WHERE list_id = ?
        """, (new_id, src['id']))
        copied = cursor.rowcount
        conn.commit()
        return copied
    finally:
        conn.close()


def validate_where_clause(where_clause: str) -> str:
    """Validate a raw expert WHERE clause: a single expression, no statement breaks.

    Blocks semicolons and SQL comment markers so the clause cannot smuggle in a
    second statement. Returns the trimmed clause. Raises ValueError if unsafe.
    """
    clause = (where_clause or "").strip()
    if not clause:
        raise ValueError("Empty WHERE clause")
    if ";" in clause:
        raise ValueError("WHERE clause must be a single expression (no ';')")
    if "--" in clause or "/*" in clause or "*/" in clause:
        raise ValueError("WHERE clause must not contain SQL comments ('--', '/*', '*/')")
    return clause


def _contact_ids_by_filters(
    company: Optional[str] = None,
    tag: Optional[List[str]] = None,
    account: Optional[str] = None,
    category: Optional[str] = None,
    relationship: Optional[str] = None,
) -> List[int]:
    """Resolve contact IDs matching structured filters using parameterized SQL.

    All filters combine with AND logic. Returns an empty list when no filter is
    given (callers should treat that as "no match", not "all contacts").
    """
    if not any([company, tag, account, category, relationship]):
        return []

    contacts = list_contacts(
        account=account,
        category=category,
        relationship=relationship,
        tags=tag,
    )
    if company:
        wanted = company.strip().lower()
        contacts = [c for c in contacts if (c.get("company") or "").strip().lower() == wanted]
    return [c["id"] for c in contacts]


def add_list_members_by_filters(
    list_name: str,
    company: Optional[str] = None,
    tag: Optional[List[str]] = None,
    account: Optional[str] = None,
    category: Optional[str] = None,
    relationship: Optional[str] = None,
) -> int:
    """Add contacts matching structured filters to a list. Returns count added."""
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    ids = _contact_ids_by_filters(company, tag, account, category, relationship)
    added = 0
    for contact_id in ids:
        if add_list_member(list_name, contact_id):
            added += 1
    return added


def remove_list_members_by_filters(
    list_name: str,
    company: Optional[str] = None,
    tag: Optional[List[str]] = None,
    account: Optional[str] = None,
    category: Optional[str] = None,
    relationship: Optional[str] = None,
) -> int:
    """Remove contacts matching structured filters from a list. Returns count removed."""
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    ids = _contact_ids_by_filters(company, tag, account, category, relationship)
    removed = 0
    for contact_id in ids:
        if remove_list_member(list_name, contact_id):
            removed += 1
    return removed


def count_contacts_by_filters(
    company: Optional[str] = None,
    tag: Optional[List[str]] = None,
    account: Optional[str] = None,
    category: Optional[str] = None,
    relationship: Optional[str] = None,
) -> int:
    """Count contacts matching structured filters (for a pre-action preview)."""
    return len(_contact_ids_by_filters(company, tag, account, category, relationship))


def count_contacts_by_query(where_clause: str, params: Optional[list] = None) -> int:
    """Count contacts matching a raw (validated) WHERE clause, for a preview."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute(
            f"SELECT COUNT(*) FROM contacts WHERE {where_clause}",
            params or [],
        )
        row = cursor.fetchone()
        return row[0] if row else 0
    finally:
        conn.close()


def remove_list_members_by_query(list_name: str, where_clause: str, params: Optional[list] = None) -> int:
    """
    Remove contacts matching a SQL WHERE clause from a list.
    Returns the number of contacts removed.
    """
    init_db(silent=True)
    lst = get_list(list_name)
    if not lst:
        raise ValueError(f"List not found: {list_name}")

    conn = get_db()
    try:
        cursor = conn.cursor()
        sql = f"""
            DELETE FROM list_members
            WHERE list_id = ?
            AND contact_id IN (SELECT id FROM contacts WHERE {where_clause})
        """
        query_params = [lst['id']] + (params or [])
        cursor.execute(sql, query_params)
        removed = cursor.rowcount
        conn.commit()
        return removed
    finally:
        conn.close()


# ===========================================
# MEMORY MANAGEMENT
# ===========================================

def add_memory(
    contact_id: int,
    category: str,
    fact: str,
    detail: Optional[str] = None,
    source: Optional[str] = None,
    source_date: Optional[str] = None,
    source_ref: Optional[str] = None,
    confidence: str = 'confirmed'
) -> int:
    """
    Add a memory about a contact by contact ID.
    Returns the memory ID.
    """
    contact = get_contact_by_id(contact_id)
    if not contact:
        raise ValueError(f"Contact not found: #{contact_id}")

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO memories (contact_id, category, fact, detail, source, source_date, source_ref, confidence)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (contact_id, category, fact, detail, source, source_date, source_ref, confidence))

    memory_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return memory_id


def get_memories(contact_id: int, category: Optional[str] = None) -> List[dict]:
    """Get memories for a contact by contact ID, optionally filtered by category."""
    conn = get_db()
    cursor = conn.cursor()

    sql = "SELECT * FROM memories WHERE contact_id = ? AND still_valid = 1"
    params = [contact_id]

    if category:
        sql += " AND category = ?"
        params.append(category)

    sql += " ORDER BY created_at DESC"

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def search_memories(query: str) -> List[dict]:
    """Search across all memories."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT m.*, c.name, c.email
        FROM memories m
        JOIN contacts c ON m.contact_id = c.id
        WHERE m.still_valid = 1
          AND (LOWER(m.fact) LIKE LOWER(?) OR LOWER(m.detail) LIKE LOWER(?))
        ORDER BY m.created_at DESC
    """, (search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# INTERACTION MANAGEMENT
# ===========================================

def add_interaction(
    email: str,
    interaction_type: str,
    interaction_date: str,
    direction: Optional[str] = None,
    subject: Optional[str] = None,
    summary: Optional[str] = None,
    content: Optional[str] = None,
    sentiment: Optional[str] = None,
    action_required: bool = False,
    action_description: Optional[str] = None,
    message_id: Optional[str] = None,
    account: Optional[str] = None,
    source_url: Optional[str] = None,
) -> int:
    """
    Log an interaction with a contact.
    Returns the interaction ID.
    If message_id is provided and already exists, returns the existing interaction ID (dedup).
    """
    contact = get_contact(email)
    if not contact:
        raise ValueError(f"Contact not found: {email}")

    conn = get_db()
    cursor = conn.cursor()

    # Dedup: if message_id provided, check if it already exists.
    # Returns negative ID if deduped (existing record found).
    if message_id:
        cursor.execute(
            "SELECT id FROM interactions WHERE message_id = ?", (message_id,)
        )
        existing = cursor.fetchone()
        if existing:
            conn.close()
            return -existing[0]

    cursor.execute("""
        INSERT INTO interactions
        (contact_id, type, direction, subject, summary, content, sentiment,
         action_required, action_description, message_id, interaction_date,
         account, source_url)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        contact['id'], interaction_type, direction, subject, summary, content,
        sentiment, 1 if action_required else 0, action_description, message_id,
        interaction_date, account, source_url
    ))

    interaction_id = cursor.lastrowid

    # Update last_contact on the contact
    cursor.execute(
        "UPDATE contacts SET last_contact = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
        (interaction_date.split('T')[0] if 'T' in interaction_date else interaction_date, contact['id'])
    )

    conn.commit()
    conn.close()

    return interaction_id


def add_interaction_direct(
    contact_id: int,
    interaction_type: str,
    interaction_date: str,
    direction: Optional[str] = None,
    subject: Optional[str] = None,
    summary: Optional[str] = None,
    content: Optional[str] = None,
    sentiment: Optional[str] = None,
    action_required: bool = False,
    action_description: Optional[str] = None,
    message_id: Optional[str] = None,
    account: Optional[str] = None,
    source_url: Optional[str] = None,
) -> int:
    """Log an interaction using contact_id directly (no re-lookup)."""
    conn = get_db()
    cursor = conn.cursor()

    if message_id:
        cursor.execute(
            "SELECT id FROM interactions WHERE message_id = ?", (message_id,)
        )
        existing = cursor.fetchone()
        if existing:
            conn.close()
            return -existing[0]

    cursor.execute("""
        INSERT INTO interactions
        (contact_id, type, direction, subject, summary, content, sentiment,
         action_required, action_description, message_id, interaction_date,
         account, source_url)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        contact_id, interaction_type, direction, subject, summary, content,
        sentiment, 1 if action_required else 0, action_description, message_id,
        interaction_date, account, source_url
    ))

    interaction_id = cursor.lastrowid

    cursor.execute(
        "UPDATE contacts SET last_contact = ?, updated_at = CURRENT_TIMESTAMP WHERE id = ?",
        (interaction_date.split('T')[0] if 'T' in interaction_date else interaction_date, contact_id)
    )

    conn.commit()
    conn.close()
    return interaction_id


def get_interactions(email: str, limit: int = 20) -> List[dict]:
    """Get recent interactions with a contact."""
    contact = get_contact(email)
    if not contact:
        return []

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT * FROM interactions
        WHERE contact_id = ?
        ORDER BY interaction_date DESC
        LIMIT ?
    """, (contact['id'], limit))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# NOTES MANAGEMENT
# ===========================================

def add_note(email: str, note: str, context: Optional[str] = None) -> int:
    """Add a free-form note about a contact."""
    contact = get_contact(email)
    if not contact:
        raise ValueError(f"Contact not found: {email}")

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO notes (contact_id, note, context)
        VALUES (?, ?, ?)
    """, (contact['id'], note, context))

    note_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return note_id


def get_notes(email: str) -> List[dict]:
    """Get all notes for a contact."""
    contact = get_contact(email)
    if not contact:
        return []

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT * FROM notes
        WHERE contact_id = ?
        ORDER BY created_at DESC
    """, (contact['id'],))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# GENERAL FACTS
# ===========================================

def add_fact(domain: str, fact: str, subdomain: Optional[str] = None,
             tags: Optional[str] = None, source: Optional[str] = None) -> int:
    """Add a general fact (not contact-specific)."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO facts (domain, subdomain, fact, tags, source)
        VALUES (?, ?, ?, ?, ?)
    """, (domain, subdomain, fact, tags, source))

    fact_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return fact_id


def get_facts(domain: Optional[str] = None) -> List[dict]:
    """Get general facts, optionally filtered by domain."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    if domain:
        cursor.execute(
            "SELECT * FROM facts WHERE domain = ? ORDER BY created_at DESC",
            (domain,)
        )
    else:
        cursor.execute("SELECT * FROM facts ORDER BY domain, created_at DESC")

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def search_facts(query: str) -> List[dict]:
    """Search general facts."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT * FROM facts
        WHERE LOWER(fact) LIKE LOWER(?)
           OR LOWER(tags) LIKE LOWER(?)
           OR LOWER(domain) LIKE LOWER(?)
        ORDER BY created_at DESC
    """, (search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# TASKS DOMAIN
# ===========================================

def add_task(
    title: str,
    description: Optional[str] = None,
    priority: int = 3,
    due_date: Optional[str] = None,
    context: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None
) -> int:
    """
    Add a new task.
    Returns the task ID.
    """
    if priority < 1 or priority > 5:
        raise ValueError(f"Priority must be between 1 (highest) and 5 (lowest)")

    # Validate due_date format if provided
    if due_date:
        try:
            datetime.strptime(due_date, '%Y-%m-%d')
        except ValueError:
            raise ValueError(f"Invalid date format '{due_date}'. Use YYYY-MM-DD")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO tasks (title, description, priority, due_date, context, contact_id, goal_id)
        VALUES (?, ?, ?, ?, ?, ?, ?)
    """, (title, description, priority, due_date, context, contact_id, goal_id))

    task_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return task_id


def get_task(task_id: int) -> Optional[dict]:
    """Get a task by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT t.*, c.name as contact_name, c.email as contact_email, g.title as goal_title
        FROM tasks t
        LEFT JOIN contacts c ON t.contact_id = c.id
        LEFT JOIN goals g ON t.goal_id = g.id
        WHERE t.id = ?
    """, (task_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_tasks(
    status: Optional[str] = None,
    context: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None,
    include_done: bool = False,
    today_only: bool = False,
    overdue_only: bool = False,
    limit: int = 100,
    sort: str = "priority"
) -> List[dict]:
    """List tasks with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT t.*, c.name as contact_name, c.email as contact_email, g.title as goal_title
        FROM tasks t
        LEFT JOIN contacts c ON t.contact_id = c.id
        LEFT JOIN goals g ON t.goal_id = g.id
        WHERE 1=1
    """
    params = []

    if status:
        # Accept the documented-but-legacy 'completed' as an alias for the stored
        # 'done' value so `tasks list --status completed` matches real rows.
        if status == "completed":
            status = "done"
        sql += " AND t.status = ?"
        params.append(status)
    elif not include_done:
        sql += " AND t.status != 'done'"

    if context:
        sql += " AND t.context = ?"
        params.append(context)

    if contact_id:
        sql += " AND t.contact_id = ?"
        params.append(contact_id)

    if goal_id:
        sql += " AND t.goal_id = ?"
        params.append(goal_id)

    if today_only:
        sql += " AND t.due_date = DATE('now')"

    if overdue_only:
        sql += " AND t.due_date < DATE('now') AND t.status = 'pending'"

    sort_clauses = {
        "priority": "t.priority ASC, t.due_date ASC NULLS LAST, t.created_at ASC",
        "newest": "t.created_at DESC",
        "due": "t.due_date ASC NULLS LAST, t.priority ASC",
    }
    order = sort_clauses.get(sort, sort_clauses["priority"])
    sql += f" ORDER BY {order} LIMIT ?"
    params.append(limit)

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def complete_task(task_id: int) -> bool:
    """Mark a task as done."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE tasks
        SET status = 'done', completed_at = CURRENT_TIMESTAMP
        WHERE id = ? AND status = 'pending'
    """, (task_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def cancel_task(task_id: int) -> bool:
    """Cancel a task."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE tasks
        SET status = 'cancelled'
        WHERE id = ? AND status = 'pending'
    """, (task_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def update_task(
    task_id: int,
    title: Optional[str] = None,
    description: Optional[str] = None,
    priority: Optional[int] = None,
    due_date: Optional[str] = None,
    context: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None
) -> bool:
    """Update a task's fields."""
    task = get_task(task_id)
    if not task:
        return False

    if priority is not None and (priority < 1 or priority > 5):
        raise ValueError(f"Priority must be between 1 and 5")

    if due_date:
        try:
            datetime.strptime(due_date, '%Y-%m-%d')
        except ValueError:
            raise ValueError(f"Invalid date format '{due_date}'. Use YYYY-MM-DD")

    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if title is not None:
        updates.append("title = ?")
        params.append(title)
    if description is not None:
        updates.append("description = ?")
        params.append(description)
    if priority is not None:
        updates.append("priority = ?")
        params.append(priority)
    if due_date is not None:
        updates.append("due_date = ?")
        params.append(due_date)
    if context is not None:
        updates.append("context = ?")
        params.append(context)
    if contact_id is not None:
        updates.append("contact_id = ?")
        params.append(contact_id if contact_id != 0 else None)
    if goal_id is not None:
        updates.append("goal_id = ?")
        params.append(goal_id if goal_id != 0 else None)

    if updates:
        sql = f"UPDATE tasks SET {', '.join(updates)} WHERE id = ?"
        params.append(task_id)
        cursor.execute(sql, params)

    conn.commit()
    conn.close()

    return True


def search_tasks(query: str) -> List[dict]:
    """Search tasks by title or description."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT t.*, c.name as contact_name, c.email as contact_email, g.title as goal_title
        FROM tasks t
        LEFT JOIN contacts c ON t.contact_id = c.id
        LEFT JOIN goals g ON t.goal_id = g.id
        WHERE LOWER(t.title) LIKE LOWER(?)
           OR LOWER(t.description) LIKE LOWER(?)
           OR LOWER(t.context) LIKE LOWER(?)
        ORDER BY t.status = 'done', t.priority ASC, t.created_at DESC
    """, (search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# GOALS DOMAIN
# ===========================================

def add_goal(
    title: str,
    description: Optional[str] = None,
    category: Optional[str] = None,
    timeframe: Optional[str] = None,
    why: Optional[str] = None,
    target_date: Optional[str] = None
) -> int:
    """
    Add a new goal.
    Returns the goal ID.
    """
    if timeframe and timeframe not in ('short', 'medium', 'long'):
        raise ValueError(f"Invalid timeframe '{timeframe}'. Must be: short, medium, long")

    if target_date:
        try:
            datetime.strptime(target_date, '%Y-%m-%d')
        except ValueError:
            raise ValueError(f"Invalid date format '{target_date}'. Use YYYY-MM-DD")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO goals (title, description, category, timeframe, why, target_date)
        VALUES (?, ?, ?, ?, ?, ?)
    """, (title, description, category, timeframe, why, target_date))

    goal_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return goal_id


def get_goal(goal_id: int) -> Optional[dict]:
    """Get a goal by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM goals WHERE id = ?", (goal_id,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_goals(
    status: Optional[str] = None,
    category: Optional[str] = None,
    timeframe: Optional[str] = None,
    include_achieved: bool = False
) -> List[dict]:
    """List goals with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = "SELECT * FROM goals WHERE 1=1"
    params = []

    if status:
        sql += " AND status = ?"
        params.append(status)
    elif not include_achieved:
        sql += " AND status = 'active'"

    if category:
        sql += " AND category = ?"
        params.append(category)

    if timeframe:
        sql += " AND timeframe = ?"
        params.append(timeframe)

    sql += " ORDER BY target_date ASC NULLS LAST, created_at DESC"

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def achieve_goal(goal_id: int) -> bool:
    """Mark a goal as achieved."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE goals
        SET status = 'achieved', achieved_at = CURRENT_TIMESTAMP
        WHERE id = ? AND status = 'active'
    """, (goal_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def pause_goal(goal_id: int) -> bool:
    """Pause a goal."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE goals
        SET status = 'paused'
        WHERE id = ? AND status = 'active'
    """, (goal_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def resume_goal(goal_id: int) -> bool:
    """Resume a paused goal."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE goals
        SET status = 'active'
        WHERE id = ? AND status = 'paused'
    """, (goal_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def abandon_goal(goal_id: int) -> bool:
    """Abandon a goal."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE goals
        SET status = 'abandoned'
        WHERE id = ? AND status IN ('active', 'paused')
    """, (goal_id,))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def update_goal(
    goal_id: int,
    title: Optional[str] = None,
    description: Optional[str] = None,
    category: Optional[str] = None,
    timeframe: Optional[str] = None,
    why: Optional[str] = None,
    target_date: Optional[str] = None
) -> bool:
    """Update a goal's fields."""
    goal = get_goal(goal_id)
    if not goal:
        return False

    if timeframe and timeframe not in ('short', 'medium', 'long'):
        raise ValueError(f"Invalid timeframe '{timeframe}'. Must be: short, medium, long")

    if target_date:
        try:
            datetime.strptime(target_date, '%Y-%m-%d')
        except ValueError:
            raise ValueError(f"Invalid date format '{target_date}'. Use YYYY-MM-DD")

    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if title is not None:
        updates.append("title = ?")
        params.append(title)
    if description is not None:
        updates.append("description = ?")
        params.append(description)
    if category is not None:
        updates.append("category = ?")
        params.append(category)
    if timeframe is not None:
        updates.append("timeframe = ?")
        params.append(timeframe)
    if why is not None:
        updates.append("why = ?")
        params.append(why)
    if target_date is not None:
        updates.append("target_date = ?")
        params.append(target_date)

    if updates:
        sql = f"UPDATE goals SET {', '.join(updates)} WHERE id = ?"
        params.append(goal_id)
        cursor.execute(sql, params)

    conn.commit()
    conn.close()

    return True


def search_goals(query: str) -> List[dict]:
    """Search goals by title or description."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT * FROM goals
        WHERE LOWER(title) LIKE LOWER(?)
           OR LOWER(description) LIKE LOWER(?)
           OR LOWER(why) LIKE LOWER(?)
        ORDER BY status = 'active' DESC, created_at DESC
    """, (search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def get_goal_tasks(goal_id: int) -> List[dict]:
    """Get all tasks linked to a goal."""
    return list_tasks(goal_id=goal_id, include_done=True)


def get_goal_ideas(goal_id: int) -> List[dict]:
    """Get all ideas linked to a goal."""
    return list_ideas(goal_id=goal_id, include_archived=True)


# ===========================================
# IDEAS DOMAIN
# ===========================================

def add_idea(
    content: str,
    tags: Optional[str] = None,
    domain: Optional[str] = None,
    goal_id: Optional[int] = None
) -> int:
    """
    Add a new idea.
    Returns the idea ID.
    """
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO ideas (content, tags, domain, goal_id)
        VALUES (?, ?, ?, ?)
    """, (content, tags, domain, goal_id))

    idea_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return idea_id


def get_idea(idea_id: int) -> Optional[dict]:
    """Get an idea by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT i.*, g.title as goal_title
        FROM ideas i
        LEFT JOIN goals g ON i.goal_id = g.id
        WHERE i.id = ?
    """, (idea_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_ideas(
    status: Optional[str] = None,
    domain: Optional[str] = None,
    goal_id: Optional[int] = None,
    tag: Optional[str] = None,
    include_archived: bool = False,
    limit: int = 100
) -> List[dict]:
    """List ideas with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT i.*, g.title as goal_title
        FROM ideas i
        LEFT JOIN goals g ON i.goal_id = g.id
        WHERE 1=1
    """
    params = []

    if status:
        sql += " AND i.status = ?"
        params.append(status)
    elif not include_archived:
        sql += " AND i.status != 'archived'"

    if domain:
        sql += " AND i.domain = ?"
        params.append(domain)

    if goal_id:
        sql += " AND i.goal_id = ?"
        params.append(goal_id)

    if tag:
        sql += " AND LOWER(i.tags) LIKE LOWER(?)"
        params.append(f"%{tag}%")

    sql += " ORDER BY i.created_at DESC LIMIT ?"
    params.append(limit)

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def update_idea_status(idea_id: int, status: str) -> bool:
    """Update an idea's status."""
    if status not in ('captured', 'exploring', 'actionable', 'archived'):
        raise ValueError(f"Invalid status '{status}'. Must be: captured, exploring, actionable, archived")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE ideas SET status = ? WHERE id = ?
    """, (status, idea_id))

    updated = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return updated


def archive_idea(idea_id: int) -> bool:
    """Archive an idea."""
    return update_idea_status(idea_id, 'archived')


def update_idea(
    idea_id: int,
    content: Optional[str] = None,
    tags: Optional[str] = None,
    domain: Optional[str] = None,
    goal_id: Optional[int] = None,
    status: Optional[str] = None
) -> bool:
    """Update an idea's fields."""
    idea = get_idea(idea_id)
    if not idea:
        return False

    if status and status not in ('captured', 'exploring', 'actionable', 'archived'):
        raise ValueError(f"Invalid status '{status}'. Must be: captured, exploring, actionable, archived")

    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if content is not None:
        updates.append("content = ?")
        params.append(content)
    if tags is not None:
        updates.append("tags = ?")
        params.append(tags)
    if domain is not None:
        updates.append("domain = ?")
        params.append(domain)
    if goal_id is not None:
        updates.append("goal_id = ?")
        params.append(goal_id if goal_id != 0 else None)
    if status is not None:
        updates.append("status = ?")
        params.append(status)

    if updates:
        sql = f"UPDATE ideas SET {', '.join(updates)} WHERE id = ?"
        params.append(idea_id)
        cursor.execute(sql, params)

    conn.commit()
    conn.close()

    return True


def search_ideas(query: str) -> List[dict]:
    """Search ideas by content or tags."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT i.*, g.title as goal_title
        FROM ideas i
        LEFT JOIN goals g ON i.goal_id = g.id
        WHERE LOWER(i.content) LIKE LOWER(?)
           OR LOWER(i.tags) LIKE LOWER(?)
           OR LOWER(i.domain) LIKE LOWER(?)
        ORDER BY i.created_at DESC
    """, (search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# SOCIAL POSTS DOMAIN
# ===========================================

def add_social_post(
    platform: str,
    content: str,
    status: str = 'draft',
    audience: Optional[str] = None,
    url: Optional[str] = None,
    tags: Optional[str] = None,
    goal_id: Optional[int] = None
) -> int:
    """
    Add a new social media post.
    Returns the post ID.
    """
    if platform not in ('linkedin', 'twitter', 'reddit', 'other'):
        raise ValueError(f"Invalid platform '{platform}'. Must be: linkedin, twitter, reddit, other")

    if status not in ('draft', 'scheduled', 'posted'):
        raise ValueError(f"Invalid status '{status}'. Must be: draft, scheduled, posted")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    posted_at = datetime.now().isoformat() if status == 'posted' else None

    cursor.execute("""
        INSERT INTO social_posts (platform, content, status, audience, url, posted_at, tags, goal_id)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
    """, (platform, content, status, audience, url, posted_at, tags, goal_id))

    post_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return post_id


def get_social_post(post_id: int) -> Optional[dict]:
    """Get a social post by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT sp.*, g.title as goal_title
        FROM social_posts sp
        LEFT JOIN goals g ON sp.goal_id = g.id
        WHERE sp.id = ?
    """, (post_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_social_posts(
    platform: Optional[str] = None,
    status: Optional[str] = None,
    limit: int = 50
) -> list[dict]:
    """List social posts with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT sp.*, g.title as goal_title
        FROM social_posts sp
        LEFT JOIN goals g ON sp.goal_id = g.id
        WHERE 1=1
    """
    params = []

    if platform:
        sql += " AND sp.platform = ?"
        params.append(platform)

    if status:
        sql += " AND sp.status = ?"
        params.append(status)

    sql += " ORDER BY sp.created_at DESC LIMIT ?"
    params.append(limit)

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def update_social_post(
    post_id: int,
    content: Optional[str] = None,
    status: Optional[str] = None,
    audience: Optional[str] = None,
    url: Optional[str] = None,
    tags: Optional[str] = None,
    goal_id: Optional[int] = None
) -> bool:
    """Update a social post's fields."""
    post = get_social_post(post_id)
    if not post:
        return False

    if status and status not in ('draft', 'scheduled', 'posted'):
        raise ValueError(f"Invalid status '{status}'. Must be: draft, scheduled, posted")

    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if content is not None:
        updates.append("content = ?")
        params.append(content)
    if status is not None:
        updates.append("status = ?")
        params.append(status)
    if audience is not None:
        updates.append("audience = ?")
        params.append(audience)
    if url is not None:
        updates.append("url = ?")
        params.append(url)
    if tags is not None:
        updates.append("tags = ?")
        params.append(tags)
    if goal_id is not None:
        updates.append("goal_id = ?")
        params.append(goal_id)

    if not updates:
        conn.close()
        return True  # Nothing to update

    sql = f"UPDATE social_posts SET {', '.join(updates)} WHERE id = ?"
    params.append(post_id)

    cursor.execute(sql, params)
    conn.commit()
    conn.close()

    return True


def mark_social_post_posted(post_id: int, url: Optional[str] = None) -> bool:
    """Mark a social post as posted."""
    post = get_social_post(post_id)
    if not post:
        return False

    conn = get_db()
    cursor = conn.cursor()

    if url:
        cursor.execute("""
            UPDATE social_posts
            SET status = 'posted', posted_at = ?, url = ?
            WHERE id = ?
        """, (datetime.now().isoformat(), url, post_id))
    else:
        cursor.execute("""
            UPDATE social_posts
            SET status = 'posted', posted_at = ?
            WHERE id = ?
        """, (datetime.now().isoformat(), post_id))

    conn.commit()
    conn.close()

    return True


def search_social_posts(query: str) -> list[dict]:
    """Search social posts by content, tags, or audience."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT sp.*, g.title as goal_title
        FROM social_posts sp
        LEFT JOIN goals g ON sp.goal_id = g.id
        WHERE LOWER(sp.content) LIKE LOWER(?)
           OR LOWER(sp.tags) LIKE LOWER(?)
           OR LOWER(sp.audience) LIKE LOWER(?)
        ORDER BY sp.created_at DESC
    """, (search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# DOCUMENTS DOMAIN (Vault 2.0)
# ===========================================

def add_document(
    path: str,
    title: Optional[str] = None,
    doc_type: Optional[str] = None,
    summary: Optional[str] = None,
    tags: Optional[str] = None,
    source: Optional[str] = None,
    source_date: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None
) -> int:
    """
    Add a document to the registry.
    Path is relative to vault/documents.
    Returns the document ID.
    """
    if doc_type and doc_type not in DOCUMENT_TYPES:
        raise ValueError(f"Invalid doc_type '{doc_type}'. Must be: {', '.join(DOCUMENT_TYPES)}")

    init_db(silent=True)

    # Calculate file hash if file exists
    file_hash = None
    full_path = DOCUMENTS_PATH / path
    if full_path.exists():
        with open(full_path, 'rb') as f:
            file_hash = hashlib.md5(f.read()).hexdigest()

    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO documents (path, title, doc_type, summary, tags, source, source_date,
                               contact_id, goal_id, file_hash)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (path, title, doc_type, summary, tags, source, source_date,
          contact_id, goal_id, file_hash))

    doc_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return doc_id


def get_document(doc_id: int) -> Optional[dict]:
    """Get a document by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT d.*, c.name as contact_name, g.title as goal_title
        FROM documents d
        LEFT JOIN contacts c ON d.contact_id = c.id
        LEFT JOIN goals g ON d.goal_id = g.id
        WHERE d.id = ?
    """, (doc_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def get_document_by_path(path: str) -> Optional[dict]:
    """Get a document by path."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM documents WHERE path = ?", (path,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_documents(
    doc_type: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None,
    limit: int = 50
) -> List[dict]:
    """List documents with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT d.*, c.name as contact_name, g.title as goal_title
        FROM documents d
        LEFT JOIN contacts c ON d.contact_id = c.id
        LEFT JOIN goals g ON d.goal_id = g.id
        WHERE 1=1
    """
    params = []

    if doc_type:
        sql += " AND d.doc_type = ?"
        params.append(doc_type)

    if contact_id:
        sql += " AND d.contact_id = ?"
        params.append(contact_id)

    if goal_id:
        sql += " AND d.goal_id = ?"
        params.append(goal_id)

    sql += " ORDER BY d.created_at DESC LIMIT ?"
    params.append(limit)

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def update_document(
    doc_id: int,
    title: Optional[str] = None,
    summary: Optional[str] = None,
    tags: Optional[str] = None,
    vector_id: Optional[str] = None
) -> bool:
    """Update document metadata."""
    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if title is not None:
        updates.append("title = ?")
        params.append(title)
    if summary is not None:
        updates.append("summary = ?")
        params.append(summary)
    if tags is not None:
        updates.append("tags = ?")
        params.append(tags)
    if vector_id is not None:
        updates.append("vector_id = ?")
        updates.append("indexed_at = CURRENT_TIMESTAMP")
        params.append(vector_id)

    if updates:
        sql = f"UPDATE documents SET {', '.join(updates)} WHERE id = ?"
        params.append(doc_id)
        cursor.execute(sql, params)

    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()

    return updated


def search_documents(query: str) -> List[dict]:
    """Search documents by title, summary, or tags."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT d.*, c.name as contact_name, g.title as goal_title
        FROM documents d
        LEFT JOIN contacts c ON d.contact_id = c.id
        LEFT JOIN goals g ON d.goal_id = g.id
        WHERE LOWER(d.title) LIKE LOWER(?)
           OR LOWER(d.summary) LIKE LOWER(?)
           OR LOWER(d.tags) LIKE LOWER(?)
           OR LOWER(d.path) LIKE LOWER(?)
        ORDER BY d.created_at DESC
    """, (search_term, search_term, search_term, search_term))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# ===========================================
# CHUNKS DOMAIN (Vault 2.0 - Hybrid Search)
# ===========================================

def add_chunk(
    document_id: int,
    content: str,
    content_hash: str,
    start_line: Optional[int] = None,
    end_line: Optional[int] = None,
    chunk_index: Optional[int] = None,
    vector_id: Optional[str] = None
) -> int:
    """Add a chunk for a document."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO chunks (document_id, content, content_hash, start_line, end_line, chunk_index, vector_id)
        VALUES (?, ?, ?, ?, ?, ?, ?)
    """, (document_id, content, content_hash, start_line, end_line, chunk_index, vector_id))

    chunk_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return chunk_id


def get_chunks_for_document(document_id: int) -> List[dict]:
    """Get all chunks for a document."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT * FROM chunks
        WHERE document_id = ?
        ORDER BY chunk_index
    """, (document_id,))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def delete_chunks_for_document(document_id: int) -> int:
    """Delete all chunks for a document. Returns count deleted."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("DELETE FROM chunks WHERE document_id = ?", (document_id,))

    count = cursor.rowcount
    conn.commit()
    conn.close()

    return count


def update_chunk_vector_id(chunk_id: int, vector_id: str) -> bool:
    """Update the vector_id for a chunk."""
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        UPDATE chunks SET vector_id = ? WHERE id = ?
    """, (vector_id, chunk_id))

    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()

    return updated


def search_chunks_fts(query: str, limit: int = 20) -> List[dict]:
    """
    Search chunks using FTS5 full-text search with BM25 ranking.

    Returns chunks with their BM25 scores (lower is better in SQLite FTS5).
    """
    init_db(silent=True)

    # Sanitize query for FTS5
    safe_query = _sanitize_fts_query(query)
    if not safe_query:
        return []

    conn = get_db()
    cursor = conn.cursor()

    # FTS5 match query with BM25 ranking
    try:
        cursor.execute("""
            SELECT
                c.*,
                d.title as doc_title,
                d.path as doc_path,
                d.doc_type,
                bm25(chunks_fts) as bm25_score
            FROM chunks_fts
            JOIN chunks c ON chunks_fts.rowid = c.id
            JOIN documents d ON c.document_id = d.id
            WHERE chunks_fts MATCH ?
            ORDER BY bm25(chunks_fts)
            LIMIT ?
        """, (safe_query, limit))

        results = [dict(row) for row in cursor.fetchall()]
    except sqlite3.OperationalError as exc:
        # The query was sanitized but FTS5 still rejected it. Do not hide this
        # behind an empty result set -- surface a clear error naming the query
        # so the caller can see what failed instead of "no results".
        conn.close()
        raise ValueError(
            f"Full-text search could not run for query {query!r} "
            f"(sanitized as {safe_query!r}): {exc}"
        ) from exc

    conn.close()

    return results


def get_chunk_by_id(chunk_id: int) -> Optional[dict]:
    """Get a chunk by ID with document info."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT c.*, d.title as doc_title, d.path as doc_path, d.doc_type
        FROM chunks c
        JOIN documents d ON c.document_id = d.id
        WHERE c.id = ?
    """, (chunk_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def get_chunk_stats() -> dict:
    """Get statistics about chunks."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT COUNT(*) FROM chunks")
    total_chunks = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(DISTINCT document_id) FROM chunks")
    chunked_docs = cursor.fetchone()[0]

    cursor.execute("SELECT AVG(LENGTH(content)) FROM chunks")
    avg_chunk_size = cursor.fetchone()[0] or 0

    conn.close()

    return {
        'total_chunks': total_chunks,
        'chunked_documents': chunked_docs,
        'avg_chunk_size': int(avg_chunk_size)
    }


# ===========================================
# HEALTH ENTRIES DOMAIN (Vault 2.0)
# ===========================================

def add_health_entry(
    category: str,
    entry_date: str,
    data_file: Optional[str] = None,
    summary: Optional[str] = None
) -> int:
    """Add a health data entry."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO health_entries (category, entry_date, data_file, summary)
        VALUES (?, ?, ?, ?)
    """, (category, entry_date, data_file, summary))

    entry_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return entry_id


def list_health_entries(
    category: Optional[str] = None,
    days: int = 30
) -> List[dict]:
    """List health entries for the past N days."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT * FROM health_entries
        WHERE entry_date >= DATE('now', ?)
    """
    params = [f'-{days} days']

    if category:
        sql += " AND category = ?"
        params.append(category)

    sql += " ORDER BY entry_date DESC, category"

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def get_health_summary(days: int = 7) -> dict:
    """Get health summary for the past N days."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT category, COUNT(*) as count
        FROM health_entries
        WHERE entry_date >= DATE('now', ?)
        GROUP BY category
    """, (f'-{days} days',))

    summary = {row[0]: row[1] for row in cursor.fetchall()}
    conn.close()

    return summary


# ===========================================
# PHOTOS DOMAIN
# ===========================================

def add_photo_source(
    path: str,
    label: str,
    category: str,
    priority: int = 10
) -> int:
    """Add a photo source directory. Returns the source ID."""
    if category not in ('private', 'work', 'other'):
        raise ValueError(f"Invalid category '{category}'. Must be: private, work, other")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO photo_sources (path, label, category, priority)
        VALUES (?, ?, ?, ?)
    """, (path, label, category, priority))

    source_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return source_id


def get_photo_source(label: str) -> Optional[dict]:
    """Get a photo source by label."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_sources WHERE label = ?", (label,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def get_photo_source_by_id(source_id: int) -> Optional[dict]:
    """Get a photo source by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_sources WHERE id = ?", (source_id,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def list_photo_sources(enabled_only: bool = True) -> List[dict]:
    """List all photo sources."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = "SELECT * FROM photo_sources"
    if enabled_only:
        sql += " WHERE enabled = 1"
    sql += " ORDER BY priority ASC, label"

    cursor.execute(sql)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def remove_photo_source(label: str) -> bool:
    """Remove a photo source by label. Returns True if removed."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("DELETE FROM photo_sources WHERE label = ?", (label,))
    removed = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return removed


def update_photo_source(
    label: str,
    path: Optional[str] = None,
    category: Optional[str] = None,
    priority: Optional[int] = None,
    enabled: Optional[bool] = None
) -> bool:
    """Update a photo source."""
    source = get_photo_source(label)
    if not source:
        return False

    if category and category not in ('private', 'work', 'other'):
        raise ValueError(f"Invalid category '{category}'. Must be: private, work, other")

    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if path is not None:
        updates.append("path = ?")
        params.append(path)
    if category is not None:
        updates.append("category = ?")
        params.append(category)
    if priority is not None:
        updates.append("priority = ?")
        params.append(priority)
    if enabled is not None:
        updates.append("enabled = ?")
        params.append(1 if enabled else 0)

    if updates:
        sql = f"UPDATE photo_sources SET {', '.join(updates)} WHERE label = ?"
        params.append(label)
        cursor.execute(sql, params)

    conn.commit()
    conn.close()

    return True


def add_photo(
    source_id: int,
    file_path: str,
    file_name: str,
    category: str,
    file_size: Optional[int] = None,
    sha256_hash: Optional[str] = None,
    is_screenshot: bool = False,
    screenshot_confidence: Optional[float] = None,
    file_modified_at: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None
) -> int:
    """Add a photo to the registry. Returns the photo ID."""
    if category not in ('private', 'work', 'other'):
        raise ValueError(f"Invalid category '{category}'. Must be: private, work, other")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO photos (
            source_id, file_path, file_name, category, file_size,
            sha256_hash, is_screenshot, screenshot_confidence,
            file_modified_at, contact_id, goal_id
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        source_id, file_path, file_name, category, file_size,
        sha256_hash, 1 if is_screenshot else 0, screenshot_confidence,
        file_modified_at, contact_id, goal_id
    ))

    photo_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return photo_id


def get_photo(photo_id: int) -> Optional[dict]:
    """Get a photo by ID."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT p.*, s.label as source_label, s.path as source_path
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        WHERE p.id = ?
    """, (photo_id,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def get_photo_by_path(file_path: str) -> Optional[dict]:
    """Get a photo by file path."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        SELECT p.*, s.label as source_label, s.path as source_path
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        WHERE p.file_path = ?
    """, (file_path,))

    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def update_photo(
    photo_id: int,
    sha256_hash: Optional[str] = None,
    is_screenshot: Optional[bool] = None,
    screenshot_confidence: Optional[float] = None,
    file_modified_at: Optional[str] = None,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None,
    vector_id: Optional[str] = None
) -> bool:
    """Update a photo."""
    conn = get_db()
    cursor = conn.cursor()

    updates = []
    params = []

    if sha256_hash is not None:
        updates.append("sha256_hash = ?")
        params.append(sha256_hash)
    if is_screenshot is not None:
        updates.append("is_screenshot = ?")
        params.append(1 if is_screenshot else 0)
    if screenshot_confidence is not None:
        updates.append("screenshot_confidence = ?")
        params.append(screenshot_confidence)
    if file_modified_at is not None:
        updates.append("file_modified_at = ?")
        params.append(file_modified_at)
    if contact_id is not None:
        updates.append("contact_id = ?")
        params.append(contact_id if contact_id != 0 else None)
    if goal_id is not None:
        updates.append("goal_id = ?")
        params.append(goal_id if goal_id != 0 else None)
    if vector_id is not None:
        updates.append("vector_id = ?")
        params.append(vector_id)

    if updates:
        sql = f"UPDATE photos SET {', '.join(updates)} WHERE id = ?"
        params.append(photo_id)
        cursor.execute(sql, params)

    conn.commit()
    updated = cursor.rowcount > 0
    conn.close()

    return updated


def delete_photo(photo_id: int) -> bool:
    """Delete a photo and its metadata/analysis."""
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("DELETE FROM photos WHERE id = ?", (photo_id,))
    deleted = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return deleted


def list_photos(
    source_id: Optional[int] = None,
    category: Optional[str] = None,
    screenshots_only: bool = False,
    contact_id: Optional[int] = None,
    goal_id: Optional[int] = None,
    limit: int = 100
) -> List[dict]:
    """List photos with optional filtering."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT p.*, s.label as source_label, s.path as source_path
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        WHERE 1=1
    """
    params = []

    if source_id:
        sql += " AND p.source_id = ?"
        params.append(source_id)

    if category:
        sql += " AND p.category = ?"
        params.append(category)

    if screenshots_only:
        sql += " AND p.is_screenshot = 1"

    if contact_id:
        sql += " AND p.contact_id = ?"
        params.append(contact_id)

    if goal_id:
        sql += " AND p.goal_id = ?"
        params.append(goal_id)

    sql += " ORDER BY p.file_name LIMIT ?"
    params.append(limit)

    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def get_photos_by_source(source_id: int) -> List[str]:
    """Get all file paths for photos from a source."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT file_path FROM photos WHERE source_id = ?", (source_id,))
    paths = [row[0] for row in cursor.fetchall()]
    conn.close()

    return paths


def delete_photos_not_in_paths(source_id: int, valid_paths: List[str]) -> int:
    """Delete photos from a source that are not in the valid paths list."""
    if not valid_paths:
        # Delete all photos for this source
        conn = get_db()
        cursor = conn.cursor()
        cursor.execute("DELETE FROM photos WHERE source_id = ?", (source_id,))
        count = cursor.rowcount
        conn.commit()
        conn.close()
        return count

    conn = get_db()
    cursor = conn.cursor()

    # Get photos to delete
    placeholders = ",".join("?" * len(valid_paths))
    cursor.execute(f"""
        DELETE FROM photos
        WHERE source_id = ? AND file_path NOT IN ({placeholders})
    """, [source_id] + valid_paths)

    count = cursor.rowcount
    conn.commit()
    conn.close()

    return count


# Photo metadata functions

def add_photo_metadata(
    photo_id: int,
    width: Optional[int] = None,
    height: Optional[int] = None,
    date_taken: Optional[str] = None,
    camera_make: Optional[str] = None,
    camera_model: Optional[str] = None,
    gps_lat: Optional[float] = None,
    gps_lon: Optional[float] = None,
    orientation: Optional[int] = None,
    raw_exif: Optional[str] = None
) -> None:
    """Add or replace metadata for a photo."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT OR REPLACE INTO photo_metadata (
            photo_id, width, height, date_taken, camera_make,
            camera_model, gps_lat, gps_lon, orientation, raw_exif
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    """, (
        photo_id, width, height, date_taken, camera_make,
        camera_model, gps_lat, gps_lon, orientation, raw_exif
    ))

    conn.commit()
    conn.close()


def get_photo_metadata(photo_id: int) -> Optional[dict]:
    """Get metadata for a photo."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_metadata WHERE photo_id = ?", (photo_id,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


# Photo analysis functions

def add_photo_analysis(
    photo_id: int,
    description: Optional[str] = None,
    keywords: Optional[str] = None,
    provider: Optional[str] = None,
    model: Optional[str] = None
) -> None:
    """Add or replace AI analysis for a photo."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT OR REPLACE INTO photo_analysis (
            photo_id, description, keywords, analyzed_at, provider, model
        ) VALUES (?, ?, ?, CURRENT_TIMESTAMP, ?, ?)
    """, (photo_id, description, keywords, provider, model))

    conn.commit()
    conn.close()


def get_photo_analysis(photo_id: int) -> Optional[dict]:
    """Get AI analysis for a photo."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_analysis WHERE photo_id = ?", (photo_id,))
    row = cursor.fetchone()
    conn.close()

    return dict(row) if row else None


def get_unanalyzed_photos(limit: Optional[int] = None) -> List[dict]:
    """Get photos that haven't been analyzed yet."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    sql = """
        SELECT p.*, s.label as source_label, s.path as source_path
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        LEFT JOIN photo_analysis a ON p.id = a.photo_id
        WHERE a.photo_id IS NULL
        ORDER BY p.created_at DESC
    """
    if limit:
        sql += f" LIMIT {limit}"

    cursor.execute(sql)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def search_photos(query: str) -> List[dict]:
    """Search photos by description."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    search_term = f"%{query}%"
    cursor.execute("""
        SELECT p.*, s.label as source_label, a.description
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        JOIN photo_analysis a ON p.id = a.photo_id
        WHERE LOWER(a.description) LIKE LOWER(?)
        ORDER BY p.file_name
    """, (search_term,))

    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


# Duplicate detection

def get_photo_duplicate_groups() -> List[List[dict]]:
    """Get groups of photos with the same SHA-256 hash."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    # Find hashes with multiple photos
    hashes = cursor.execute("""
        SELECT sha256_hash, COUNT(*) as count
        FROM photos
        WHERE sha256_hash IS NOT NULL
        GROUP BY sha256_hash
        HAVING count > 1
        ORDER BY count DESC
    """).fetchall()

    groups = []
    for row in hashes:
        hash_val = row[0]
        photos = cursor.execute("""
            SELECT p.*, s.label as source_label, s.priority as source_priority
            FROM photos p
            JOIN photo_sources s ON p.source_id = s.id
            WHERE p.sha256_hash = ?
            ORDER BY s.priority, p.file_path
        """, (hash_val,)).fetchall()
        groups.append([dict(p) for p in photos])

    conn.close()
    return groups


def get_photo_stats() -> dict:
    """Get photo statistics."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    stats = {
        'total': 0,
        'total_size_bytes': 0,
        'by_category': {},
        'by_source': {},
        'screenshots': 0,
        'analyzed': 0,
        'duplicate_groups': 0,
        'duplicate_files': 0
    }

    cursor.execute("SELECT COUNT(*), SUM(file_size) FROM photos")
    row = cursor.fetchone()
    stats['total'] = row[0] or 0
    stats['total_size_bytes'] = row[1] or 0

    cursor.execute("SELECT category, COUNT(*), SUM(file_size) FROM photos GROUP BY category")
    for row in cursor.fetchall():
        stats['by_category'][row[0]] = {'count': row[1], 'size': row[2] or 0}

    cursor.execute("""
        SELECT s.label, COUNT(*), SUM(p.file_size)
        FROM photos p
        JOIN photo_sources s ON p.source_id = s.id
        GROUP BY s.label
    """)
    for row in cursor.fetchall():
        stats['by_source'][row[0]] = {'count': row[1], 'size': row[2] or 0}

    cursor.execute("SELECT COUNT(*) FROM photos WHERE is_screenshot = 1")
    stats['screenshots'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM photo_analysis")
    stats['analyzed'] = cursor.fetchone()[0]

    cursor.execute("""
        SELECT COUNT(*) FROM (
            SELECT sha256_hash FROM photos
            WHERE sha256_hash IS NOT NULL
            GROUP BY sha256_hash HAVING COUNT(*) > 1
        )
    """)
    stats['duplicate_groups'] = cursor.fetchone()[0]

    cursor.execute("""
        SELECT COUNT(*) FROM photos
        WHERE sha256_hash IN (
            SELECT sha256_hash FROM photos
            WHERE sha256_hash IS NOT NULL
            GROUP BY sha256_hash HAVING COUNT(*) > 1
        )
    """)
    stats['duplicate_files'] = cursor.fetchone()[0]

    conn.close()
    return stats


# Photo Exclusions

def add_photo_exclusion(path: str, reason: Optional[str] = None) -> int:
    """Add a path to the exclusion list."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT OR REPLACE INTO photo_exclusions (path, reason, created_at)
        VALUES (?, ?, CURRENT_TIMESTAMP)
    """, (path, reason))

    exclusion_id = cursor.lastrowid
    conn.commit()
    conn.close()
    return exclusion_id


def remove_photo_exclusion(path: str) -> bool:
    """Remove a path from the exclusion list."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("DELETE FROM photo_exclusions WHERE path = ?", (path,))
    deleted = cursor.rowcount > 0
    conn.commit()
    conn.close()
    return deleted


def list_photo_exclusions() -> list:
    """List all exclusion paths."""
    init_db(silent=True)
    conn = get_db()
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_exclusions ORDER BY path")
    exclusions = [dict(row) for row in cursor.fetchall()]
    conn.close()
    return exclusions


def is_path_excluded(path: str) -> bool:
    """Check if a path is excluded (or under an excluded path)."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    # Normalize path
    path = path.replace('/', '\\').rstrip('\\').lower()

    cursor.execute("SELECT path FROM photo_exclusions")
    for row in cursor.fetchall():
        excluded = row[0].replace('/', '\\').rstrip('\\').lower()
        if path == excluded or path.startswith(excluded + '\\'):
            conn.close()
            return True

    conn.close()
    return False


def get_default_exclusions() -> list:
    """Get list of default paths to exclude."""
    import os
    exclusions = []

    # Windows system directories
    if os.name == 'nt':
        exclusions.extend([
            'C:\\Windows',
            'C:\\Program Files',
            'C:\\Program Files (x86)',
            'C:\\ProgramData',
            'C:\\$Recycle.Bin',
            os.path.expandvars('%APPDATA%'),
            os.path.expandvars('%LOCALAPPDATA%'),
        ])

    # Common development/cache directories (will be skipped anywhere)
    # These are handled in the scanner, not as exclusions

    return exclusions


def add_default_exclusions() -> int:
    """Add all default exclusions to the database."""
    count = 0
    for path in get_default_exclusions():
        try:
            add_photo_exclusion(path, "Default system exclusion")
            count += 1
        except sqlite3.IntegrityError:
            pass  # Already exists
    return count


# Photo Scan State

def set_drive_scanned(drive: str) -> None:
    """Mark a drive as scanned."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT OR REPLACE INTO photo_scan_state (drive, last_scan, created_at)
        VALUES (?, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
    """, (drive.upper(),))

    conn.commit()
    conn.close()


def get_scanned_drives() -> list:
    """Get list of drives that have been scanned."""
    init_db(silent=True)
    conn = get_db()
    conn.row_factory = sqlite3.Row
    cursor = conn.cursor()

    cursor.execute("SELECT * FROM photo_scan_state ORDER BY drive")
    drives = [dict(row) for row in cursor.fetchall()]
    conn.close()
    return drives


def is_drive_scanned(drive: str) -> bool:
    """Check if a drive has been scanned."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("SELECT 1 FROM photo_scan_state WHERE drive = ?", (drive.upper(),))
    result = cursor.fetchone() is not None
    conn.close()
    return result


# ===========================================
# ENTITY LINKS DOMAIN (Vault 2.0)
# ===========================================

def add_entity_link(
    source_type: str,
    source_id: int,
    target_type: str,
    target_id: int,
    relationship: Optional[str] = None,
    strength: int = 1
) -> int:
    """Create a link between two entities."""
    if source_type not in ENTITY_TYPES:
        raise ValueError(f"Invalid source_type '{source_type}'. Must be: {', '.join(ENTITY_TYPES)}")
    if target_type not in ENTITY_TYPES:
        raise ValueError(f"Invalid target_type '{target_type}'. Must be: {', '.join(ENTITY_TYPES)}")
    if strength < 1 or strength > 5:
        raise ValueError("Strength must be between 1 and 5")

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT OR REPLACE INTO entity_links
        (source_type, source_id, target_type, target_id, relationship, strength)
        VALUES (?, ?, ?, ?, ?, ?)
    """, (source_type, source_id, target_type, target_id, relationship, strength))

    link_id = cursor.lastrowid
    conn.commit()
    conn.close()

    return link_id


def get_entity_links(entity_type: str, entity_id: int) -> dict:
    """Get all links for an entity (both as source and target)."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    # Links where this entity is the source
    cursor.execute("""
        SELECT * FROM entity_links
        WHERE source_type = ? AND source_id = ?
        ORDER BY strength DESC
    """, (entity_type, entity_id))

    outgoing = [dict(row) for row in cursor.fetchall()]

    # Links where this entity is the target
    cursor.execute("""
        SELECT * FROM entity_links
        WHERE target_type = ? AND target_id = ?
        ORDER BY strength DESC
    """, (entity_type, entity_id))

    incoming = [dict(row) for row in cursor.fetchall()]

    conn.close()

    return {
        'outgoing': outgoing,
        'incoming': incoming
    }


def remove_entity_link(
    source_type: str,
    source_id: int,
    target_type: str,
    target_id: int,
    relationship: Optional[str] = None
) -> bool:
    """Remove a link between entities."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    if relationship:
        cursor.execute("""
            DELETE FROM entity_links
            WHERE source_type = ? AND source_id = ?
              AND target_type = ? AND target_id = ?
              AND relationship = ?
        """, (source_type, source_id, target_type, target_id, relationship))
    else:
        cursor.execute("""
            DELETE FROM entity_links
            WHERE source_type = ? AND source_id = ?
              AND target_type = ? AND target_id = ?
        """, (source_type, source_id, target_type, target_id))

    removed = cursor.rowcount > 0
    conn.commit()
    conn.close()

    return removed


def get_graph_stats() -> dict:
    """
    Get graph statistics - entity counts and total links.

    Returns:
        Dict with entities by type, total links, and most connected entities
    """
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    stats = {
        "entities": {},
        "total_links": 0,
        "most_connected": []
    }

    # Count entities by type
    entity_tables = {
        "contact": "contacts",
        "task": "tasks",
        "goal": "goals",
        "idea": "ideas",
        "document": "documents",
    }

    for entity_type, table_name in entity_tables.items():
        try:
            cursor.execute(f"SELECT COUNT(*) FROM {table_name}")
            stats["entities"][entity_type] = cursor.fetchone()[0]
        except sqlite3.Error:
            stats["entities"][entity_type] = 0

    # Total links
    try:
        cursor.execute("SELECT COUNT(*) FROM entity_links")
        stats["total_links"] = cursor.fetchone()[0]
    except sqlite3.Error:
        stats["total_links"] = 0

    # Most connected entities (by link count)
    try:
        cursor.execute("""
            SELECT entity_type, entity_id, link_count FROM (
                SELECT source_type as entity_type, source_id as entity_id, COUNT(*) as link_count
                FROM entity_links
                GROUP BY source_type, source_id
                UNION ALL
                SELECT target_type, target_id, COUNT(*)
                FROM entity_links
                GROUP BY target_type, target_id
            )
            GROUP BY entity_type, entity_id
            ORDER BY SUM(link_count) DESC
            LIMIT 10
        """)

        most_connected = []
        for row in cursor.fetchall():
            entity_type, entity_id, link_count = row
            # Try to get entity name
            name = None
            if entity_type == "contact":
                cursor.execute("SELECT name FROM contacts WHERE id = ?", (entity_id,))
                result = cursor.fetchone()
                if result:
                    name = result[0]
            elif entity_type == "task":
                cursor.execute("SELECT title FROM tasks WHERE id = ?", (entity_id,))
                result = cursor.fetchone()
                if result:
                    name = result[0]
            elif entity_type == "goal":
                cursor.execute("SELECT title FROM goals WHERE id = ?", (entity_id,))
                result = cursor.fetchone()
                if result:
                    name = result[0]

            most_connected.append({
                "type": entity_type,
                "id": entity_id,
                "name": name or f"#{entity_id}",
                "links": link_count
            })

        stats["most_connected"] = most_connected
    except sqlite3.Error:
        pass

    conn.close()
    return stats


def populate_links_from_fk(dry_run: bool = False) -> Dict[str, Any]:
    """
    Populate entity_links from foreign key relationships in the schema.

    Discovers implicit relationships from FK columns and creates explicit
    links in the entity_links table.

    Args:
        dry_run: If True, only report what would be created without making changes

    Returns:
        Dict with statistics: created counts by relationship type, total, errors
    """
    # FK mappings: (source_table, fk_column, source_type, target_type, relationship, strength)
    fk_mappings = [
        ("tasks", "contact_id", "task", "contact", "assigned_to", 3),
        ("tasks", "goal_id", "task", "goal", "supports", 3),
        ("ideas", "goal_id", "idea", "goal", "supports", 2),
        ("documents", "contact_id", "document", "contact", "about", 2),
        ("documents", "goal_id", "document", "goal", "supports", 2),
        ("photos", "contact_id", "photo", "contact", "features", 2),
        ("photos", "goal_id", "photo", "goal", "documents", 2),
        ("contacts", "referred_by", "contact", "contact", "referred_by", 3),
    ]

    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    stats = {
        "dry_run": dry_run,
        "relationships": {},
        "total_created": 0,
        "total_skipped": 0,
        "errors": []
    }

    for table, fk_col, source_type, target_type, relationship, strength in fk_mappings:
        rel_stats = {"found": 0, "created": 0, "skipped": 0}

        try:
            # Query rows where FK is not null
            cursor.execute(f"""
                SELECT id, {fk_col} FROM {table}
                WHERE {fk_col} IS NOT NULL
            """)
            rows = cursor.fetchall()
            rel_stats["found"] = len(rows)

            for source_id, target_id in rows:
                if dry_run:
                    # In dry-run, count what would be created
                    rel_stats["created"] += 1
                else:
                    try:
                        cursor.execute("""
                            INSERT OR REPLACE INTO entity_links
                            (source_type, source_id, target_type, target_id, relationship, strength)
                            VALUES (?, ?, ?, ?, ?, ?)
                        """, (source_type, source_id, target_type, target_id, relationship, strength))
                        rel_stats["created"] += 1
                    except sqlite3.Error as e:
                        rel_stats["skipped"] += 1
                        stats["errors"].append(f"{source_type}:{source_id}->{target_type}:{target_id}: {str(e)}")

        except sqlite3.Error as e:
            stats["errors"].append(f"{table}.{fk_col}: {str(e)}")

        stats["relationships"][f"{source_type}->{target_type} ({relationship})"] = rel_stats
        stats["total_created"] += rel_stats["created"]
        stats["total_skipped"] += rel_stats["skipped"]

    if not dry_run:
        conn.commit()

    conn.close()
    return stats


# ===========================================
# SEARCH LOG (Vault 2.0)
# ===========================================

def log_search(query: str, results_count: int = 0, clicked_type: Optional[str] = None, clicked_id: Optional[int] = None):
    """Log a search query for learning."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    cursor.execute("""
        INSERT INTO search_log (query, results_count, clicked_type, clicked_id)
        VALUES (?, ?, ?, ?)
    """, (query, results_count, clicked_type, clicked_id))

    conn.commit()
    conn.close()


# ===========================================
# UNIFIED SEARCH
# ===========================================

def search_all(query: str) -> dict:
    """Search across all domains: contacts, tasks, goals, ideas, memories, facts, documents."""
    results = {
        'contacts': search_contacts(query),
        'tasks': search_tasks(query),
        'goals': search_goals(query),
        'ideas': search_ideas(query),
        'memories': search_memories(query),
        'facts': search_facts(query),
        'documents': search_documents(query)
    }

    # Log the search
    total_results = sum(len(v) for v in results.values())
    log_search(query, total_results)

    return results


# ===========================================
# STATISTICS
# ===========================================

def get_stats() -> dict:
    """Get database statistics."""
    init_db(silent=True)
    conn = get_db()
    cursor = conn.cursor()

    stats = {
        'contacts': {'total': 0, 'by_account': {}, 'by_category': {}, 'by_relationship': {}, 'by_lead_status': {}},
        'memories': 0,
        'interactions': 0,
        'notes': 0,
        'facts': 0,
        'tags': 0,
        'tasks': {'total': 0, 'pending': 0, 'done': 0, 'overdue': 0},
        'goals': {'total': 0, 'active': 0, 'achieved': 0},
        'ideas': {'total': 0, 'captured': 0, 'actionable': 0},
        'lead_scores': {'total': 0, 'by_category': {}},
        'social_posts': {'total': 0, 'draft': 0, 'posted': 0, 'by_platform': {}},
        # Vault 2.0 tables
        'documents': {'total': 0, 'by_type': {}},
        'health_entries': {'total': 0, 'by_category': {}},
        'photos': {'total': 0, 'screenshots': 0, 'analyzed': 0, 'by_category': {}},
        'entity_links': 0,
        'searches': 0
    }

    # Contact counts
    cursor.execute("SELECT COUNT(*) FROM contacts")
    stats['contacts']['total'] = cursor.fetchone()[0]

    cursor.execute("SELECT account, COUNT(*) FROM contacts GROUP BY account")
    for row in cursor.fetchall():
        stats['contacts']['by_account'][row[0]] = row[1]

    cursor.execute("SELECT category, COUNT(*) FROM contacts GROUP BY category")
    for row in cursor.fetchall():
        stats['contacts']['by_category'][row[0]] = row[1]

    cursor.execute("SELECT relationship, COUNT(*) FROM contacts WHERE relationship IS NOT NULL GROUP BY relationship")
    for row in cursor.fetchall():
        stats['contacts']['by_relationship'][row[0]] = row[1]

    cursor.execute("SELECT lead_status, COUNT(*) FROM contacts WHERE lead_status IS NOT NULL GROUP BY lead_status")
    for row in cursor.fetchall():
        stats['contacts']['by_lead_status'][row[0]] = row[1]

    # Other counts
    cursor.execute("SELECT COUNT(*) FROM memories WHERE still_valid = 1")
    stats['memories'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM interactions")
    stats['interactions'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM notes")
    stats['notes'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM facts")
    stats['facts'] = cursor.fetchone()[0]

    # Tag counts
    cursor.execute("SELECT COUNT(DISTINCT tag) FROM contact_tags")
    stats['tags'] = cursor.fetchone()[0]

    # Task counts
    cursor.execute("SELECT COUNT(*) FROM tasks")
    stats['tasks']['total'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM tasks WHERE status = 'pending'")
    stats['tasks']['pending'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM tasks WHERE status = 'done'")
    stats['tasks']['done'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM tasks WHERE status = 'pending' AND due_date < DATE('now')")
    stats['tasks']['overdue'] = cursor.fetchone()[0]

    # Goal counts
    cursor.execute("SELECT COUNT(*) FROM goals")
    stats['goals']['total'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM goals WHERE status = 'active'")
    stats['goals']['active'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM goals WHERE status = 'achieved'")
    stats['goals']['achieved'] = cursor.fetchone()[0]

    # Idea counts
    cursor.execute("SELECT COUNT(*) FROM ideas")
    stats['ideas']['total'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM ideas WHERE status = 'captured'")
    stats['ideas']['captured'] = cursor.fetchone()[0]

    cursor.execute("SELECT COUNT(*) FROM ideas WHERE status = 'actionable'")
    stats['ideas']['actionable'] = cursor.fetchone()[0]

    # Lead score counts
    cursor.execute("SELECT COUNT(*) FROM lead_scores")
    stats['lead_scores']['total'] = cursor.fetchone()[0]

    cursor.execute("SELECT category, COUNT(*) FROM lead_scores WHERE category IS NOT NULL GROUP BY category")
    for row in cursor.fetchall():
        stats['lead_scores']['by_category'][row[0]] = row[1]

    # Social posts counts
    try:
        cursor.execute("SELECT COUNT(*) FROM social_posts")
        stats['social_posts']['total'] = cursor.fetchone()[0]

        cursor.execute("SELECT COUNT(*) FROM social_posts WHERE status = 'draft'")
        stats['social_posts']['draft'] = cursor.fetchone()[0]

        cursor.execute("SELECT COUNT(*) FROM social_posts WHERE status = 'posted'")
        stats['social_posts']['posted'] = cursor.fetchone()[0]

        cursor.execute("SELECT platform, COUNT(*) FROM social_posts GROUP BY platform")
        for row in cursor.fetchall():
            stats['social_posts']['by_platform'][row[0]] = row[1]
    except sqlite3.OperationalError:
        pass  # Table may not exist in older databases

    # Vault 2.0: Document counts
    try:
        cursor.execute("SELECT COUNT(*) FROM documents")
        stats['documents']['total'] = cursor.fetchone()[0]

        cursor.execute("SELECT doc_type, COUNT(*) FROM documents WHERE doc_type IS NOT NULL GROUP BY doc_type")
        for row in cursor.fetchall():
            stats['documents']['by_type'][row[0]] = row[1]
    except sqlite3.OperationalError:
        pass  # Table may not exist in older databases

    # Vault 2.0: Health entry counts
    try:
        cursor.execute("SELECT COUNT(*) FROM health_entries")
        stats['health_entries']['total'] = cursor.fetchone()[0]

        cursor.execute("SELECT category, COUNT(*) FROM health_entries GROUP BY category")
        for row in cursor.fetchall():
            stats['health_entries']['by_category'][row[0]] = row[1]
    except sqlite3.OperationalError:
        pass

    # Vault 2.0: Entity links count
    try:
        cursor.execute("SELECT COUNT(*) FROM entity_links")
        stats['entity_links'] = cursor.fetchone()[0]
    except sqlite3.OperationalError:
        pass

    # Vault 2.0: Search log count
    try:
        cursor.execute("SELECT COUNT(*) FROM search_log")
        stats['searches'] = cursor.fetchone()[0]
    except sqlite3.OperationalError:
        pass

    # Photos stats
    try:
        cursor.execute("SELECT COUNT(*) FROM photos")
        stats['photos']['total'] = cursor.fetchone()[0]

        cursor.execute("SELECT COUNT(*) FROM photos WHERE is_screenshot = 1")
        stats['photos']['screenshots'] = cursor.fetchone()[0]

        cursor.execute("SELECT COUNT(*) FROM photo_analysis")
        stats['photos']['analyzed'] = cursor.fetchone()[0]

        cursor.execute("SELECT category, COUNT(*) FROM photos GROUP BY category")
        for row in cursor.fetchall():
            stats['photos']['by_category'][row[0]] = row[1]
    except sqlite3.OperationalError:
        pass

    conn.close()

    return stats


# ===========================================
# EMAIL ACTIVITY
# ===========================================

def upsert_email_activity(
    contact_id: int,
    account: str,
    sent_count: int = 0,
    received_count: int = 0,
    first_email_date: Optional[str] = None,
    last_email_date: Optional[str] = None,
) -> None:
    """
    Insert or update email activity for a contact + account pair.

    Args:
        contact_id: The contact ID.
        account: Account name (e.g. 'personal', 'consulting', 'outlook').
        sent_count: Number of emails sent to this contact.
        received_count: Number of emails received from this contact.
        first_email_date: ISO date string of first email.
        last_email_date: ISO date string of most recent email.
    """
    init_db(silent=True)
    conn = get_db()
    now = datetime.now().isoformat()
    email_count = sent_count + received_count

    conn.execute("""
        INSERT INTO email_activity
            (contact_id, account, email_count, sent_count, received_count,
             first_email_date, last_email_date, scanned_at)
        VALUES (?, ?, ?, ?, ?, ?, ?, ?)
        ON CONFLICT(contact_id, account) DO UPDATE SET
            email_count = excluded.email_count,
            sent_count = excluded.sent_count,
            received_count = excluded.received_count,
            first_email_date = COALESCE(excluded.first_email_date, email_activity.first_email_date),
            last_email_date = COALESCE(excluded.last_email_date, email_activity.last_email_date),
            scanned_at = excluded.scanned_at
    """, (contact_id, account, email_count, sent_count, received_count,
          first_email_date, last_email_date, now))

    conn.commit()
    conn.close()


def get_email_activity(
    contact_id: Optional[int] = None,
    account: Optional[str] = None,
) -> List[dict]:
    """
    Get email activity records.

    Args:
        contact_id: Filter by contact ID (optional).
        account: Filter by account name (optional).

    Returns:
        List of email activity dicts.
    """
    init_db(silent=True)
    conn = get_db()

    sql = """
        SELECT ea.*, c.name, c.email
        FROM email_activity ea
        JOIN contacts c ON c.id = ea.contact_id
        WHERE 1=1
    """
    params = []

    if contact_id is not None:
        sql += " AND ea.contact_id = ?"
        params.append(contact_id)

    if account is not None:
        sql += " AND ea.account = ?"
        params.append(account)

    sql += " ORDER BY ea.sent_count DESC"

    cursor = conn.cursor()
    cursor.execute(sql, params)
    results = [dict(row) for row in cursor.fetchall()]
    conn.close()

    return results


def sync_recipients(
    recipients: List[Dict[str, Any]],
    account: str,
    dry_run: bool = False,
) -> Dict[str, Any]:
    """
    Bulk-import recipients into the vault.

    Takes the JSON array from cc-gmail/cc-outlook recipients --format json.
    Creates missing contacts and upserts email_activity in one transaction.

    Args:
        recipients: List of dicts with keys: email, name, sent_count.
        account: Account name (e.g. 'consulting', 'personal').
        dry_run: If True, count new vs existing without writing.

    Returns:
        Dict with keys: new_contacts, existing_contacts, activities_upserted, errors.
    """
    if account not in ('consulting', 'personal', 'both'):
        raise ValueError(f"Invalid account '{account}'. Must be: consulting, personal, both")

    init_db(silent=True)
    conn = get_db()

    new_contacts = 0
    existing_contacts = 0
    activities_upserted = 0
    errors = []

    try:
        cursor = conn.cursor()

        # Collect all emails for batch lookup
        all_emails = []
        recipients_by_email = {}
        for r in recipients:
            email = (r.get("email") or "").strip().lower()
            if not email:
                errors.append("Skipped entry with missing email")
                continue
            all_emails.append(email)
            recipients_by_email[email] = r

        # Batch lookup existing contacts by email (chunk at 900 for SQLite limit)
        existing_map = {}  # email -> contact_id
        chunk_size = 900
        for i in range(0, len(all_emails), chunk_size):
            chunk = all_emails[i:i + chunk_size]
            placeholders = ", ".join(["?" for _ in chunk])
            cursor.execute(
                f"SELECT id, email FROM contacts WHERE LOWER(email) IN ({placeholders})",
                [e.lower() for e in chunk],
            )
            for row in cursor.fetchall():
                existing_map[row["email"].lower()] = row["id"]

        if dry_run:
            for email in all_emails:
                if email in existing_map:
                    existing_contacts += 1
                else:
                    new_contacts += 1
            conn.close()
            return {
                "new_contacts": new_contacts,
                "existing_contacts": existing_contacts,
                "activities_upserted": 0,
                "errors": errors,
            }

        # Process each recipient in one transaction
        now = datetime.now().isoformat()
        for email in all_emails:
            r = recipients_by_email[email]
            sent_count = r.get("sent_count", 0)
            name = (r.get("name") or "").strip()

            if email in existing_map:
                contact_id = existing_map[email]
                existing_contacts += 1
            else:
                # Insert new contact
                cursor.execute(
                    "INSERT INTO contacts (email, name, account, category) VALUES (?, ?, ?, ?)",
                    (r.get("email", "").strip(), name, account, "whitelist"),
                )
                contact_id = cursor.lastrowid
                existing_map[email] = contact_id
                new_contacts += 1

            # Upsert email activity
            email_count = sent_count
            cursor.execute("""
                INSERT INTO email_activity
                    (contact_id, account, email_count, sent_count, received_count,
                     first_email_date, last_email_date, scanned_at)
                VALUES (?, ?, ?, ?, 0, NULL, NULL, ?)
                ON CONFLICT(contact_id, account) DO UPDATE SET
                    email_count = excluded.email_count,
                    sent_count = excluded.sent_count,
                    scanned_at = excluded.scanned_at
            """, (contact_id, account, email_count, sent_count, now))
            activities_upserted += 1

        conn.commit()
    finally:
        conn.close()

    return {
        "new_contacts": new_contacts,
        "existing_contacts": existing_contacts,
        "activities_upserted": activities_upserted,
        "errors": errors,
    }


# ==========================================
# LIBRARY / CATALOG CRUD
# ==========================================

def add_library(path: str, label: str, category: str,
                owner: Optional[str] = None, recursive: bool = True) -> Dict[str, Any]:
    """Register a document library folder."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("""
            INSERT INTO libraries (path, label, category, owner, recursive)
            VALUES (?, ?, ?, ?, ?)
        """, (str(path), label, category, owner, int(recursive)))
        conn.commit()
        lib_id = cursor.lastrowid
        row = conn.execute("SELECT * FROM libraries WHERE id = ?", (lib_id,)).fetchone()
        return dict(row)
    finally:
        conn.close()


def get_library(label: str) -> Optional[Dict[str, Any]]:
    """Get a library by label."""
    init_db(silent=True)
    conn = get_db()
    try:
        row = conn.execute("SELECT * FROM libraries WHERE label = ?", (label,)).fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def get_library_by_id(library_id: int) -> Optional[Dict[str, Any]]:
    """Get a library by ID."""
    init_db(silent=True)
    conn = get_db()
    try:
        row = conn.execute("SELECT * FROM libraries WHERE id = ?", (library_id,)).fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def list_libraries() -> List[Dict[str, Any]]:
    """List all registered libraries."""
    init_db(silent=True)
    conn = get_db()
    try:
        rows = conn.execute("SELECT * FROM libraries ORDER BY label").fetchall()
        return [dict(r) for r in rows]
    finally:
        conn.close()


def delete_library(label: str) -> bool:
    """Delete a library and all its catalog entries (CASCADE)."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("DELETE FROM libraries WHERE label = ?", (label,))
        conn.commit()
        return cursor.rowcount > 0
    finally:
        conn.close()


def update_library_last_scanned(library_id: int) -> None:
    """Update the last_scanned timestamp for a library."""
    init_db(silent=True)
    conn = get_db()
    try:
        conn.execute(
            "UPDATE libraries SET last_scanned = ? WHERE id = ?",
            (datetime.now().isoformat(), library_id)
        )
        conn.commit()
    finally:
        conn.close()


def upsert_catalog_entry(library_id: int, file_path: str, file_name: str,
                         file_ext: str, file_size: int, file_hash: str,
                         file_modified_at: str, department: Optional[str],
                         summarizable: bool, status: str = 'pending') -> Dict[str, Any]:
    """Insert or update a catalog entry by file_path."""
    init_db(silent=True)
    conn = get_db()
    try:
        cursor = conn.cursor()
        cursor.execute("""
            INSERT INTO catalog_entries
                (library_id, file_path, file_name, file_ext, file_size,
                 file_hash, file_modified_at, department, summarizable, status)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(file_path) DO UPDATE SET
                file_size = excluded.file_size,
                file_hash = excluded.file_hash,
                file_modified_at = excluded.file_modified_at,
                department = excluded.department,
                summarizable = excluded.summarizable,
                status = CASE
                    WHEN catalog_entries.file_hash = excluded.file_hash
                        AND catalog_entries.status IN ('summarized', 'skipped')
                    THEN catalog_entries.status
                    ELSE excluded.status
                END
        """, (library_id, file_path, file_name, file_ext, file_size,
              file_hash, file_modified_at, department, int(summarizable), status))
        conn.commit()

        row = conn.execute(
            "SELECT * FROM catalog_entries WHERE file_path = ?", (file_path,)
        ).fetchone()
        return dict(row) if row else {}
    finally:
        conn.close()


def get_catalog_entry_by_path(file_path: str) -> Optional[Dict[str, Any]]:
    """Get a catalog entry by file path."""
    init_db(silent=True)
    conn = get_db()
    try:
        row = conn.execute(
            "SELECT * FROM catalog_entries WHERE file_path = ?", (file_path,)
        ).fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def get_catalog_entry_by_hash(file_hash: str) -> Optional[Dict[str, Any]]:
    """Get the first catalog entry with a given hash that has a summary."""
    init_db(silent=True)
    conn = get_db()
    try:
        row = conn.execute(
            "SELECT * FROM catalog_entries WHERE file_hash = ? AND status = 'summarized' LIMIT 1",
            (file_hash,)
        ).fetchone()
        return dict(row) if row else None
    finally:
        conn.close()


def update_catalog_entry_summary(entry_id: int, title: str, summary: str,
                                 tags: str, status: str = 'summarized',
                                 dedup_source_id: Optional[int] = None) -> None:
    """Update a catalog entry with summary data."""
    init_db(silent=True)
    conn = get_db()
    try:
        conn.execute("""
            UPDATE catalog_entries
            SET title = ?, summary = ?, tags = ?, status = ?,
                dedup_source_id = ?, summarized_at = ?
            WHERE id = ?
        """, (title, summary, tags, status, dedup_source_id,
              datetime.now().isoformat(), entry_id))
        conn.commit()
    finally:
        conn.close()


def update_catalog_entry_status(entry_id: int, status: str,
                                error_message: Optional[str] = None) -> None:
    """Update the status (and optionally error_message) of a catalog entry."""
    init_db(silent=True)
    conn = get_db()
    try:
        conn.execute(
            "UPDATE catalog_entries SET status = ?, error_message = ? WHERE id = ?",
            (status, error_message, entry_id)
        )
        conn.commit()
    finally:
        conn.close()


def mark_missing_catalog_entries(library_id: int, existing_paths: set) -> int:
    """Mark entries whose files no longer exist as 'missing'. Returns count."""
    init_db(silent=True)
    conn = get_db()
    try:
        rows = conn.execute(
            "SELECT id, file_path FROM catalog_entries WHERE library_id = ? AND status != 'missing'",
            (library_id,)
        ).fetchall()
        missing_count = 0
        for row in rows:
            if row['file_path'] not in existing_paths:
                conn.execute(
                    "UPDATE catalog_entries SET status = 'missing' WHERE id = ?",
                    (row['id'],)
                )
                missing_count += 1
        conn.commit()
        return missing_count
    finally:
        conn.close()


def list_catalog_entries(library_id: Optional[int] = None,
                         ext: Optional[str] = None,
                         department: Optional[str] = None,
                         status: Optional[str] = None,
                         limit: int = 100) -> List[Dict[str, Any]]:
    """List catalog entries with optional filters."""
    init_db(silent=True)
    conn = get_db()
    try:
        sql = "SELECT * FROM catalog_entries WHERE 1=1"
        params: list = []

        if library_id is not None:
            sql += " AND library_id = ?"
            params.append(library_id)
        if ext is not None:
            sql += " AND file_ext = ?"
            params.append(ext)
        if department is not None:
            sql += " AND department = ?"
            params.append(department)
        if status is not None:
            sql += " AND status = ?"
            params.append(status)

        sql += " ORDER BY file_name LIMIT ?"
        params.append(limit)

        rows = conn.execute(sql, params).fetchall()
        return [dict(r) for r in rows]
    finally:
        conn.close()


def _sanitize_fts_query(query: str) -> str:
    """Sanitize a query string for FTS5 MATCH.

    Each whitespace-separated term that contains any non-word character is
    wrapped in double quotes so FTS5 treats it as a literal rather than as
    operator syntax. This covers hyphens, '+', '*', '/', '^', '~',
    parentheses, '&' (e.g. "SR&ED"), a column-filter colon (e.g. "term:value"),
    and a stray double quote -- all of which would otherwise change the query's
    meaning or raise a syntax error. Any double quote inside a term is doubled
    per FTS5 string escaping so an unbalanced quote cannot break the query.
    """
    import re
    terms = query.split()
    quoted = []
    for term in terms:
        # A term that is purely word characters (letters, digits, underscore,
        # including Unicode letters) is already a safe bareword token.
        if re.search(r'\W', term):
            quoted.append('"' + term.replace('"', '""') + '"')
        else:
            quoted.append(term)
    return " ".join(quoted)


def search_catalog_fts(query: str, limit: int = 50) -> List[Dict[str, Any]]:
    """Full-text search across catalog entries."""
    init_db(silent=True)
    conn = get_db()
    try:
        safe_query = _sanitize_fts_query(query)
        rows = conn.execute("""
            SELECT ce.*, bm25(catalog_fts) AS rank
            FROM catalog_fts
            JOIN catalog_entries ce ON ce.id = catalog_fts.rowid
            WHERE catalog_fts MATCH ?
            ORDER BY bm25(catalog_fts)
            LIMIT ?
        """, (safe_query, limit)).fetchall()
        return [dict(r) for r in rows]
    finally:
        conn.close()


def get_catalog_stats(library_id: Optional[int] = None) -> Dict[str, Any]:
    """Get catalog statistics."""
    init_db(silent=True)
    conn = get_db()
    try:
        where = ""
        params: list = []
        if library_id is not None:
            where = " WHERE library_id = ?"
            params = [library_id]

        total = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}", params
        ).fetchone()[0]

        summarized = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}"
            + (" AND" if where else " WHERE") + " status = 'summarized'",
            params
        ).fetchone()[0]

        pending = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}"
            + (" AND" if where else " WHERE") + " status = 'pending'",
            params
        ).fetchone()[0]

        errors = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}"
            + (" AND" if where else " WHERE") + " status = 'error'",
            params
        ).fetchone()[0]

        skipped = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}"
            + (" AND" if where else " WHERE") + " status = 'skipped'",
            params
        ).fetchone()[0]

        missing = conn.execute(
            f"SELECT COUNT(*) FROM catalog_entries{where}"
            + (" AND" if where else " WHERE") + " status = 'missing'",
            params
        ).fetchone()[0]

        return {
            "total": total,
            "summarized": summarized,
            "pending": pending,
            "errors": errors,
            "skipped": skipped,
            "missing": missing,
        }
    finally:
        conn.close()


def get_pending_catalog_entries(library_id: Optional[int] = None,
                                limit: int = 10) -> List[Dict[str, Any]]:
    """Get catalog entries with status='pending' and summarizable=1."""
    init_db(silent=True)
    conn = get_db()
    try:
        sql = "SELECT * FROM catalog_entries WHERE status = 'pending' AND summarizable = 1"
        params: list = []
        if library_id is not None:
            sql += " AND library_id = ?"
            params.append(library_id)
        sql += " ORDER BY id LIMIT ?"
        params.append(limit)
        rows = conn.execute(sql, params).fetchall()
        return [dict(r) for r in rows]
    finally:
        conn.close()
