-- Skrypt SQL do nadania uprawnień użytkownikowi ms w bazie danych smb
-- Uruchom to jako superuser (np. postgres) w PostgreSQL

-- Nadaj uprawnienia do schematu public
GRANT ALL PRIVILEGES ON SCHEMA public TO ms;
GRANT CREATE ON SCHEMA public TO ms;

-- Nadaj uprawnienia do wszystkich istniejących tabel (jeśli jakieś są)
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO ms;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO ms;

-- Nadaj uprawnienia do przyszłych tabel i sekwencji
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON TABLES TO ms;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT ALL ON SEQUENCES TO ms;


