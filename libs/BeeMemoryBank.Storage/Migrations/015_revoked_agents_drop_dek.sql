-- A revoked agent must not keep a wrapped master DEK. AgentRepository.DeleteAsync now clears the
-- key material as part of flipping status to 'D', but that only helps agents revoked from here on:
-- every row revoked BEFORE that change still carries encrypted_dek/dek_iv/salt.
--
-- Migration 014 did not catch these. It cleared by the OWNER'S ROLE, which is the right rule for
-- the H6 hybrid model (only a superadmin's agents may hold key material) but says nothing about
-- revocation — a superadmin's own revoked keys passed the role test and kept their DEK. That is
-- precisely the wrong outcome: the old plaintext bee_... string plus a copy of the database file
-- (a backup, a decommissioned disk) re-derives the KEK from the stored salt and decrypts the whole
-- vault, long after the operator believed they had cut that key off. Observed on a live node right
-- after 014: four revoked superadmin agents still holding wrapped DEKs.
--
-- Nothing un-revokes an agent (DeleteAsync is the only writer of status = 'D'), so a revoked row
-- has no future use for key material. key_prefix/key_hash are left alone — they are what makes the
-- audit trail and the "this key was revoked" answer still readable.
--
-- Irreversible by design: re-wrapping would need the plaintext API key, which was shown once at
-- creation and is not recoverable from key_hash.
UPDATE tbl_agent
   SET encrypted_dek = NULL,
       dek_iv        = NULL,
       salt          = NULL,
       kdf_version   = 0
 WHERE status = 'D'
   AND encrypted_dek IS NOT NULL;
