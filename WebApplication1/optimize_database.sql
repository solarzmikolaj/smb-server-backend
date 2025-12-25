-- ============================================
-- Optymalizacja bazy danych dla Dashboard
-- ============================================
-- Ten skrypt tworzy indeksy poprawiające wydajność zapytań w Dashboard
-- Uruchom jako superuser PostgreSQL

-- ============================================
-- INDEKSY DLA TABELI Users
-- ============================================

-- Indeks dla zapytań filtrujących po IsActive (używane w system-stats)
CREATE INDEX IF NOT EXISTS idx_users_isactive 
ON "Users"("IsActive") 
WHERE "IsActive" = true;

-- Indeks dla zapytań z LastLoginAt (aktywni użytkownicy)
CREATE INDEX IF NOT EXISTS idx_users_isactive_lastlogin 
ON "Users"("IsActive", "LastLoginAt") 
WHERE "IsActive" = true AND "LastLoginAt" IS NOT NULL;

-- Indeks dla zapytań z IsApproved (oczekujący użytkownicy)
CREATE INDEX IF NOT EXISTS idx_users_approval 
ON "Users"("IsApproved", "IsActive") 
WHERE "IsApproved" = false AND "IsActive" = true;

-- Indeks dla zapytań z StorageQuota (użytkownicy z quota)
CREATE INDEX IF NOT EXISTS idx_users_quota 
ON "Users"("StorageQuota") 
WHERE "StorageQuota" IS NOT NULL;

-- Indeks dla zapytań z SmbFolderPath (fallback w system-stats)
CREATE INDEX IF NOT EXISTS idx_users_active_folderpath 
ON "Users"("IsActive", "SmbFolderPath") 
WHERE "IsActive" = true AND "SmbFolderPath" IS NOT NULL AND "SmbFolderPath" != '';

-- Indeks dla wyszukiwania po Email (dla logowania)
CREATE INDEX IF NOT EXISTS idx_users_email 
ON "Users"("Email");

-- Indeks dla wyszukiwania po Username (dla logowania)
CREATE INDEX IF NOT EXISTS idx_users_username 
ON "Users"("Username");

-- ============================================
-- INDEKSY DLA TABELI AuditLogs
-- ============================================

-- Główny indeks dla zapytań Dashboard - najczęściej używany
-- Dla: GetRecentFiles, GetActivityHistory, GetSpaceUsage
CREATE INDEX IF NOT EXISTS idx_auditlogs_userid_timestamp 
ON "AuditLogs"("UserId", "Timestamp" DESC) 
WHERE "UserId" IS NOT NULL;

-- Indeks dla zapytań z filtrem Action (GetRecentFiles)
CREATE INDEX IF NOT EXISTS idx_auditlogs_userid_action_timestamp 
ON "AuditLogs"("UserId", "Action", "Timestamp" DESC) 
WHERE "UserId" IS NOT NULL AND "Resource" IS NOT NULL;

-- Indeks dla zapytań z filtrem Action i zakresem dat (GetSpaceUsage)
CREATE INDEX IF NOT EXISTS idx_auditlogs_userid_action_timestamp_range 
ON "AuditLogs"("UserId", "Action", "Timestamp") 
WHERE "UserId" IS NOT NULL AND "Action" = 'FileUpload';

-- Indeks dla zapytań z filtrem Action (Contains) - GetActivityHistory
-- Uwaga: Contains nie może użyć indeksu, ale możemy stworzyć indeks dla częstych wartości
CREATE INDEX IF NOT EXISTS idx_auditlogs_userid_action 
ON "AuditLogs"("UserId", "Action") 
WHERE "UserId" IS NOT NULL;

-- Indeks dla zapytań po dacie (dla filtrowania dziennego w GetSpaceUsage)
-- Używamy date_trunc zamiast ::date, ponieważ jest IMMUTABLE
CREATE INDEX IF NOT EXISTS idx_auditlogs_timestamp_date 
ON "AuditLogs"(date_trunc('day', "Timestamp"), "UserId", "Action") 
WHERE "UserId" IS NOT NULL;

-- ============================================
-- INDEKSY DLA TABELI TrashItems
-- ============================================

-- Indeks dla zapytań GetTrashItems (ORDER BY DeletedAt DESC)
CREATE INDEX IF NOT EXISTS idx_trashitems_userid_deletedat 
ON "TrashItems"("UserId", "DeletedAt" DESC);

-- Indeks dla zapytań z ExpiresAt (automatyczne czyszczenie)
CREATE INDEX IF NOT EXISTS idx_trashitems_expiresat 
ON "TrashItems"("ExpiresAt") 
WHERE "ExpiresAt" IS NOT NULL;

-- ============================================
-- INDEKSY DLA TABELI UserSessions (jeśli istnieje)
-- ============================================

-- Sprawdź czy tabela istnieje przed utworzeniem indeksu
DO $$
BEGIN
    IF EXISTS (SELECT FROM information_schema.tables WHERE table_name = 'UserSessions') THEN
        CREATE INDEX IF NOT EXISTS idx_usersessions_userid_expiresat 
        ON "UserSessions"("UserId", "ExpiresAt") 
        WHERE "ExpiresAt" IS NOT NULL;
    END IF;
END $$;

-- ============================================
-- ANALIZA TABEL (aktualizacja statystyk)
-- ============================================

-- Zaktualizuj statystyki dla optymalizatora zapytań
ANALYZE "Users";
ANALYZE "AuditLogs";
ANALYZE "TrashItems";

-- ============================================
-- KOMENTARZE DO INDEKSÓW
-- ============================================

COMMENT ON INDEX idx_users_isactive IS 'Szybkie wyszukiwanie aktywnych użytkowników';
COMMENT ON INDEX idx_users_isactive_lastlogin IS 'Szybkie wyszukiwanie aktywnych użytkowników z ostatnim logowaniem';
COMMENT ON INDEX idx_users_approval IS 'Szybkie wyszukiwanie oczekujących użytkowników';
COMMENT ON INDEX idx_auditlogs_userid_timestamp IS 'Główny indeks dla zapytań Dashboard - najczęściej używany';
COMMENT ON INDEX idx_auditlogs_userid_action_timestamp IS 'Optymalizacja dla GetRecentFiles';
COMMENT ON INDEX idx_auditlogs_userid_action_timestamp_range IS 'Optymalizacja dla GetSpaceUsage z zakresem dat';
COMMENT ON INDEX idx_trashitems_userid_deletedat IS 'Optymalizacja dla GetTrashItems';

