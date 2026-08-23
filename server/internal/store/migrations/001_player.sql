CREATE TABLE IF NOT EXISTS player (
  token       TEXT    PRIMARY KEY,
  name        TEXT    NOT NULL,
  credits     BIGINT  NOT NULL,
  inventory   TEXT    NOT NULL,
  equipped    TEXT    NOT NULL,
  pos_x       REAL    NOT NULL,
  pos_y       REAL    NOT NULL,
  pos_z       REAL    NOT NULL,
  created_ms  BIGINT  NOT NULL,
  updated_ms  BIGINT  NOT NULL
);
