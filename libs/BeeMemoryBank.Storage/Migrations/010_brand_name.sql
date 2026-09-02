-- Admin-configurable product name shown in the web header and the browser tab title.
-- NULL or empty means "use the built-in default" (BrandingDefaults.Name), so every
-- existing node keeps the current wording until a superadmin changes it.
--
-- Node-local by design: it is never put in a sync event payload, so one network of
-- synced nodes can still carry a different name per installation (a company running
-- its own node brands it with its own name without affecting anyone else's).
ALTER TABLE tbl_node_identity ADD COLUMN brand_name TEXT;
