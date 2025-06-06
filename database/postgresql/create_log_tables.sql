CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE IF NOT EXISTS public.logs
(
    id serial NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL,
    process_id UUID NULL,
    log_level TEXT NULL,
    message TEXT NULL,
    exception TEXT NULL,
    scope TEXT NULL,
    PRIMARY KEY (id)
);

CREATE TABLE IF NOT EXISTS public.app_events
(
    id serial NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL,
    process_id UUID NULL,
    event_id INTEGER NOT NULL,
    event_name TEXT NULL,
    PRIMARY KEY (id)
);

CREATE INDEX IF NOT EXISTS logs_timestamp_idx ON public.logs (created_at);
CREATE INDEX IF NOT EXISTS logs_process_id_idx ON public.logs (process_id);

CREATE INDEX IF NOT EXISTS events_timestamp_idx ON public.app_events (created_at);
CREATE INDEX IF NOT EXISTS events_process_id_idx ON public.app_events (process_id);