-- Phase C part 6c: Duo and Squad. Parties (a code to share, a leader who queues for everyone), the DUO and SQUAD
-- modes, and teams in matches. Teammates share a placement, so placements are no longer unique per match: the
-- results service checks them per team instead (same within a team, different between teams).

CREATE TABLE parties (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  -- Six characters to read out or type: no 0/O or 1/I.
  code text NOT NULL UNIQUE CHECK (code ~ '^[A-HJ-NP-Z2-9]{6}$'),
  leader_id uuid NOT NULL REFERENCES users (id) ON DELETE CASCADE,
  mode text NOT NULL DEFAULT 'DUO' CHECK (mode IN ('SOLO', 'DUO', 'SQUAD')),
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE party_members (
  party_id uuid NOT NULL REFERENCES parties (id) ON DELETE CASCADE,
  -- A player is in at most one party.
  user_id uuid NOT NULL UNIQUE REFERENCES users (id) ON DELETE CASCADE,
  joined_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (party_id, user_id)
);

ALTER TABLE matches DROP CONSTRAINT matches_mode_check;
ALTER TABLE matches ADD CONSTRAINT matches_mode_check CHECK (mode IN ('SOLO', 'DUO', 'SQUAD'));

ALTER TABLE matchmaking_queue DROP CONSTRAINT matchmaking_queue_mode_check;
ALTER TABLE matchmaking_queue ADD CONSTRAINT matchmaking_queue_mode_check CHECK (mode IN ('SOLO', 'DUO', 'SQUAD'));
-- Members of a party queue together and are placed in the same match and team.
ALTER TABLE matchmaking_queue ADD COLUMN party_id uuid REFERENCES parties (id) ON DELETE SET NULL;

-- Team number within the match (players in Solo each get their own).
ALTER TABLE match_participants ADD COLUMN team smallint CHECK (team BETWEEN 0 AND 99);
ALTER TABLE match_participants ADD COLUMN party_id uuid;
ALTER TABLE match_participants DROP CONSTRAINT match_participants_match_id_placement_key;

-- Parties are short-lived: players create, join and leave them, and an empty party is deleted.
GRANT SELECT, INSERT, UPDATE, DELETE ON parties, party_members TO rwbr_app;
