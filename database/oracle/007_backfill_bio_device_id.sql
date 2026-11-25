-- Link legacy attendance rows to BIO_DEVICE by serial (or primary device if serial empty)

UPDATE biometric_attendance_log a
SET bio_device_id = (
  SELECT d.device_id FROM bio_device d
  WHERE d.deleted_at IS NULL AND TRIM(d.device_serial) = TRIM(a.device_serial)
  AND ROWNUM = 1
)
WHERE a.bio_device_id IS NULL
  AND a.device_serial IS NOT NULL;

UPDATE biometric_attendance_log a
SET bio_device_id = (
  SELECT d.device_id FROM bio_device d
  WHERE d.deleted_at IS NULL AND d.is_primary = 'Y' AND ROWNUM = 1
)
WHERE a.bio_device_id IS NULL;

COMMIT;
