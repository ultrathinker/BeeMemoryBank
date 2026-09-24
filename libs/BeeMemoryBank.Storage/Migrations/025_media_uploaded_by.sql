-- Who uploaded a media row that is not (yet) linked to an article. The "new article" page
-- uploads file attachments before the article exists and links them on save; this column is
-- what lets only the uploader link, download or delete such an unlinked file (a non-superadmin
-- can't see unlinked media at all through the normal ACL, which is keyed on the owning
-- article's folder). Node-local: it is never synced, and it only matters until the row is
-- linked. NULL for rows created before this column and for rows uploaded straight onto an
-- article.
ALTER TABLE tbl_media ADD COLUMN uploaded_by TEXT NULL;
