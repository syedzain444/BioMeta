-- Run on Gate Entry DB (entrygate_surv) if login ADMIN / Admin@123 fails.
-- Hash is BCrypt for password: Admin@123

UPDATE sec_user
SET password_hash = '$2b$11$nIPQVn.S9dmHghpx8srX8OaI5vy7/L59HtGaiuvweeNzfVFIxjuee',
    is_active = 'Y',
    updated_at = SYSTIMESTAMP
WHERE UPPER(employee_id) = 'ADMIN';

COMMIT;

-- Verify:
-- SELECT employee_id, is_active, SUBSTR(password_hash,1,7) FROM sec_user WHERE employee_id = 'ADMIN';
