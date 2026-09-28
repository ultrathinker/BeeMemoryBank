-- A blind node is never a superadmin and never auto-accepted (plan 3.2, BMB-42). The code refuses to
-- write such a row now, but one may already sit here: recorded by an older build, taken from a peer's
-- whitelist at join, or promoted before the guards existed. Clear the authority of every row whose
-- NodeId carries the blind mark (BlindNodeId: canonical form b11dxxxx-xxxx-8xxx-{8,9,a,b}xxx-...),
-- in any letter case. Reads normalize the same way (WhitelistRepository), for rows arriving later.
UPDATE tbl_whitelist
SET is_superadmin = 0,
    auto_accept_dek_rotation = 0,
    auto_accept_restore = 0
WHERE lower(substr(node_id, 1, 4)) = 'b11d'
  AND substr(node_id, 15, 1) = '8'
  AND lower(substr(node_id, 20, 1)) IN ('8', '9', 'a', 'b')
  AND length(node_id) = 36;
