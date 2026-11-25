-- If 009 ran before role grant fix: give device_users.view to every role that already has devices.view.
-- Re-login after this so the sidebar link appears.

INSERT INTO SEC_ROLE_PERMISSION (ROLE_ID, PERMISSION_ID, CREATED_BY)
SELECT DISTINCT rp.ROLE_ID, p_new.PERMISSION_ID, 1
FROM SEC_ROLE_PERMISSION rp
JOIN SEC_PERMISSION p_dev ON p_dev.PERMISSION_ID = rp.PERMISSION_ID AND p_dev.PERMISSION_CODE = 'devices.view' AND p_dev.DELETED_AT IS NULL
CROSS JOIN SEC_PERMISSION p_new
WHERE p_new.PERMISSION_CODE = 'device_users.view' AND p_new.DELETED_AT IS NULL
  AND rp.DELETED_AT IS NULL
  AND NOT EXISTS (
    SELECT 1 FROM SEC_ROLE_PERMISSION x
    WHERE x.ROLE_ID = rp.ROLE_ID AND x.PERMISSION_ID = p_new.PERMISSION_ID AND x.DELETED_AT IS NULL
  );

COMMIT;
