-- Comment encryption: store ciphertext + IV instead of plaintext
-- Uses the article's DEK for encryption (same key hierarchy as article body)

ALTER TABLE tbl_comment ADD COLUMN ciphertext BLOB;
ALTER TABLE tbl_comment ADD COLUMN iv BLOB;
ALTER TABLE tbl_comment ADD COLUMN encrypted INTEGER NOT NULL DEFAULT 0;

-- Existing comments remain plaintext (encrypted=0) until manually re-encrypted.
-- New comments will be created with encrypted=1, ciphertext, iv filled, and text=''.
