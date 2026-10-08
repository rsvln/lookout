using Microsoft.Data.Sqlite;

namespace Lookout
{
    // Sent events (captions, AI text, faces) in lookout.db next to the YAML config. Off until Open() is called.
    public static class LocalStore
    {
        static string path;
        static readonly object sync = new object();

        public static bool IsOpen { get { lock (sync) return path != null; } }
        public static string Path { get { lock (sync) return path; } }

        public static void Open(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath)) return;
            string dir = System.IO.Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            lock (sync) path = dbPath;
            using var db = Connect();
            Migrate(db);
        }

        public static void Close()
        {
            lock (sync) path = null;
        }

        static SqliteConnection Connect()
        {
            string p;
            lock (sync) p = path;
            if (p == null) throw new InvalidOperationException("LocalStore is not open");
            var db = new SqliteConnection("Data Source=" + p);
            db.Open();
            using (var pragma = db.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=3000; PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            return db;
        }

        static void Migrate(SqliteConnection db)
        {
            Exec(db, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY)");
            int current = 0;
            using (var cmd = new SqliteCommand("SELECT COALESCE(MAX(version), 0) FROM schema_migrations", db))
                current = Convert.ToInt32(cmd.ExecuteScalar());
            if (current < 1)
            {
                Exec(db, @"
                    CREATE TABLE events (
                        id TEXT PRIMARY KEY,
                        kind TEXT NOT NULL,
                        camera TEXT NOT NULL,
                        label TEXT,
                        zones TEXT,
                        score REAL,
                        start_time REAL NOT NULL,
                        end_time REAL,
                        caption TEXT,
                        ai_text TEXT,
                        faces TEXT,
                        incident_id TEXT,
                        sent_at REAL NOT NULL
                    );
                    CREATE INDEX idx_events_start ON events(start_time);
                    CREATE INDEX idx_events_camera_start ON events(camera, start_time);
                    CREATE INDEX idx_events_incident ON events(incident_id);
                    CREATE TABLE incidents (
                        id TEXT PRIMARY KEY,
                        start_time REAL NOT NULL,
                        cameras TEXT,
                        summary TEXT
                    );");
                Exec(db, "INSERT INTO schema_migrations (version) VALUES (1)");
            }
        }

        static void Exec(SqliteConnection db, string sql)
        {
            using var cmd = new SqliteCommand(sql, db);
            cmd.ExecuteNonQuery();
        }

        // When options.correlate > 0, events of other cameras in that window (seconds) share an incident id.
        public static string LinkIncident(string id, string camera, double start)
        {
            int window = Program.settings?.options?.correlate ?? 0;
            if (window <= 0 || !IsOpen || string.IsNullOrEmpty(camera)) return null;
            using var db = Connect();
            using var find = new SqliteCommand(
                "SELECT id, incident_id FROM events WHERE camera != $camera AND start_time BETWEEN $from AND $to ORDER BY ABS(start_time - $start) LIMIT 1", db);
            find.Parameters.AddWithValue("$camera", camera);
            find.Parameters.AddWithValue("$from", start - window);
            find.Parameters.AddWithValue("$to", start + window);
            find.Parameters.AddWithValue("$start", start);
            using var r = find.ExecuteReader();
            if (!r.Read()) return null;
            string otherId = r.GetString(0);
            string incident = r.IsDBNull(1) ? null : r.GetString(1);
            r.Close();
            if (string.IsNullOrEmpty(incident))
            {
                incident = Guid.NewGuid().ToString("N");
                using var up = new SqliteCommand("UPDATE events SET incident_id = $i WHERE id = $id", db);
                up.Parameters.AddWithValue("$i", incident);
                up.Parameters.AddWithValue("$id", otherId);
                up.ExecuteNonQuery();
                using var ins = new SqliteCommand(
                    "INSERT OR IGNORE INTO incidents (id, start_time, cameras) VALUES ($id, $t, $c)", db);
                ins.Parameters.AddWithValue("$id", incident);
                ins.Parameters.AddWithValue("$t", start);
                ins.Parameters.AddWithValue("$c", camera);
                ins.ExecuteNonQuery();
            }
            return incident;
        }

        public static string OtherCameras(string incidentId, string camera)
        {
            if (string.IsNullOrEmpty(incidentId) || !IsOpen) return null;
            using var db = Connect();
            using var cmd = new SqliteCommand(
                "SELECT DISTINCT camera FROM events WHERE incident_id = $i AND camera != $c ORDER BY camera", db);
            cmd.Parameters.AddWithValue("$i", incidentId);
            cmd.Parameters.AddWithValue("$c", camera ?? "");
            var names = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
            return names.Count == 0 ? null : string.Join(", ", names);
        }

        public static void Record(string id, string kind, string camera, string label, double start, double? end,
            IEnumerable<string> zones, double score, string caption, string incidentId)
        {
            if (!IsOpen || string.IsNullOrEmpty(id)) return;
            using var db = Connect();
            using var cmd = new SqliteCommand(@"
                INSERT INTO events (id, kind, camera, label, zones, score, start_time, end_time, caption, incident_id, sent_at)
                VALUES ($id, $kind, $camera, $label, $zones, $score, $start, $end, $caption, $incident, $sent)
                ON CONFLICT(id) DO UPDATE SET
                    kind = excluded.kind,
                    camera = excluded.camera,
                    label = COALESCE(excluded.label, events.label),
                    zones = COALESCE(excluded.zones, events.zones),
                    score = excluded.score,
                    start_time = excluded.start_time,
                    end_time = COALESCE(excluded.end_time, events.end_time),
                    caption = COALESCE(excluded.caption, events.caption),
                    incident_id = COALESCE(excluded.incident_id, events.incident_id)", db);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$kind", kind ?? "event");
            cmd.Parameters.AddWithValue("$camera", camera ?? "");
            cmd.Parameters.AddWithValue("$label", (object)label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$zones", ZonesJson(zones));
            cmd.Parameters.AddWithValue("$score", score);
            cmd.Parameters.AddWithValue("$start", start);
            cmd.Parameters.AddWithValue("$end", (object)end ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$caption", (object)caption ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$incident", (object)incidentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sent", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }

        public static void SetAi(string id, string text)
        {
            if (!IsOpen || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(text)) return;
            using var db = Connect();
            using (var cmd = new SqliteCommand("UPDATE events SET ai_text = $t WHERE id = $id", db))
            {
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var inc = new SqliteCommand(@"
                UPDATE incidents SET summary = COALESCE(NULLIF(summary, ''), $t)
                WHERE id = (SELECT incident_id FROM events WHERE id = $id)", db))
            {
                inc.Parameters.AddWithValue("$t", text);
                inc.Parameters.AddWithValue("$id", id);
                inc.ExecuteNonQuery();
            }
        }

        public static void SetFaces(string id, string names)
        {
            if (!IsOpen || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(names)) return;
            using var db = Connect();
            using var cmd = new SqliteCommand("UPDATE events SET faces = $t WHERE id = $id", db);
            cmd.Parameters.AddWithValue("$t", names);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }

        public static string IncidentContext(string id)
        {
            if (!IsOpen || string.IsNullOrEmpty(id)) return null;
            using var db = Connect();
            using var cmd = new SqliteCommand(@"
                SELECT camera, IFNULL(label, '') FROM events
                WHERE incident_id IS NOT NULL
                  AND incident_id = (SELECT incident_id FROM events WHERE id = $id)
                  AND id != $id
                ORDER BY start_time", db);
            cmd.Parameters.AddWithValue("$id", id);
            var parts = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string cam = r.GetString(0);
                string label = r.GetString(1);
                parts.Add(string.IsNullOrEmpty(label) ? cam : cam + " (" + label + ")");
            }
            return parts.Count == 0 ? null : L10n.Ai.T("ai.prompt.related", string.Join(", ", parts));
        }

        public static List<EventRow> Search(string q, string camera, string label, double? from, double? to, int limit)
        {
            if (!IsOpen) return new List<EventRow>();
            limit = Math.Clamp(limit, 1, 500);
            string sql = " FROM events WHERE 1=1";
            if (camera != null) sql += " AND camera = $camera";
            if (label != null) sql += " AND label = $label";
            if (from != null) sql += " AND start_time >= $from";
            if (to != null) sql += " AND start_time <= $to";
            if (!string.IsNullOrWhiteSpace(q))
                sql += " AND (id LIKE $q OR camera LIKE $q OR IFNULL(label,'') LIKE $q OR IFNULL(zones,'') LIKE $q OR IFNULL(ai_text,'') LIKE $q OR IFNULL(faces,'') LIKE $q OR IFNULL(caption,'') LIKE $q)";
            using var db = Connect();
            using var cmd = new SqliteCommand(
                "SELECT id, camera, label, faces, score, start_time, end_time, zones, ai_text, faces, incident_id " +
                sql + " ORDER BY start_time DESC LIMIT $limit", db);
            BindSearch(cmd, q, camera, label, from, to);
            cmd.Parameters.AddWithValue("$limit", limit);
            return ReadRows(cmd);
        }

        public static void Fill(IEnumerable<EventRow> rows)
        {
            if (!IsOpen) return;
            var list = rows?.Where(r => r != null && !string.IsNullOrEmpty(r.id)).ToList();
            if (list == null || list.Count == 0) return;
            using var db = Connect();
            var names = new List<string>();
            using var cmd = db.CreateCommand();
            for (int i = 0; i < list.Count; i++)
            {
                string n = "$id" + i;
                names.Add(n);
                cmd.Parameters.AddWithValue(n, list[i].id);
            }
            cmd.CommandText = "SELECT id, ai_text, faces, incident_id FROM events WHERE id IN (" + string.Join(",", names) + ")";
            var byId = list.ToDictionary(r => r.id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!byId.TryGetValue(r.GetString(0), out var row)) continue;
                if (!r.IsDBNull(1)) row.ai_text = r.GetString(1);
                if (!r.IsDBNull(2))
                {
                    row.faces = r.GetString(2);
                    if (string.IsNullOrEmpty(row.sub_label)) row.sub_label = row.faces;
                }
                if (!r.IsDBNull(3)) row.incident_id = r.GetString(3);
            }
        }

        static void BindSearch(SqliteCommand cmd, string q, string camera, string label, double? from, double? to)
        {
            if (camera != null) cmd.Parameters.AddWithValue("$camera", camera);
            if (label != null) cmd.Parameters.AddWithValue("$label", label);
            if (from != null) cmd.Parameters.AddWithValue("$from", from.Value);
            if (to != null) cmd.Parameters.AddWithValue("$to", to.Value);
            if (!string.IsNullOrWhiteSpace(q)) cmd.Parameters.AddWithValue("$q", "%" + q.Trim() + "%");
        }

        static List<EventRow> ReadRows(SqliteCommand cmd)
        {
            var rows = new List<EventRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new EventRow
                {
                    id = r.GetString(0),
                    camera = r.GetString(1),
                    label = r.IsDBNull(2) ? null : r.GetString(2),
                    sub_label = r.IsDBNull(3) ? null : r.GetString(3),
                    score = r.IsDBNull(4) ? 0 : r.GetDouble(4),
                    start_time = r.GetDouble(5),
                    end_time = r.IsDBNull(6) ? null : r.GetDouble(6),
                    zones = ParseZones(r.IsDBNull(7) ? null : r.GetString(7)),
                    has_snapshot = true,
                    has_clip = true,
                    ai_text = r.IsDBNull(8) ? null : r.GetString(8),
                    faces = r.IsDBNull(9) ? null : r.GetString(9),
                    incident_id = r.IsDBNull(10) ? null : r.GetString(10)
                });
            }
            return rows;
        }

        static object ZonesJson(IEnumerable<string> zones)
        {
            var list = zones?.Where(z => !string.IsNullOrEmpty(z)).ToList() ?? new List<string>();
            return list.Count == 0 ? DBNull.Value : (object)("[\"" + string.Join("\",\"", list.Select(z => z.Replace("\"", ""))) + "\"]");
        }

        static List<string> ParseZones(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return new List<string>();
            try
            {
                var arr = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(raw);
                return arr ?? new List<string>();
            }
            catch { return new List<string>(); }
        }
    }
}
