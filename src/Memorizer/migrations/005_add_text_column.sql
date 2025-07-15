-- 005_add_text_column.sql: Add plain text column for memories and backfill existing rows

-- Add new column if it doesn't exist
ALTER TABLE memories
    ADD COLUMN IF NOT EXISTS text TEXT;

-- Back-fill existing rows by extracting common fields from the existing JSON content
UPDATE memories
SET    text = COALESCE(
                content->>'text',
                content->>'fact',
                content->>'observation',
                content->>'content',
                text,
                '')
WHERE  (text IS NULL OR text = '');

-- Optional: make sure the column is not null going forward
ALTER TABLE memories
    ALTER COLUMN text SET NOT NULL; 