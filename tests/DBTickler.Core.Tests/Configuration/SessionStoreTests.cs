using DBTickler.Core.Configuration;

namespace DBTickler.Core.Tests.Configuration;

public class SessionStoreTests
{
    /// <summary>Pure function, no filesystem needed.</summary>
    public class ToFileNameSanitization
    {
        [Theory]
        [InlineData("../../evil")]
        [InlineData("..\\..\\evil")]
        [InlineData("C:\\Windows\\system32\\x")]
        [InlineData("../../../etc/passwd")]
        [InlineData("a/b/c")]
        [InlineData("weird:name")]
        [InlineData("has\0null")]
        [InlineData("  leading and trailing spaces  ")]
        [InlineData("emoji-🎉-name")]
        public void Neutralizes_path_traversal_and_unsafe_characters(string maliciousName)
        {
            var fileName = SessionStore.ToFileName(maliciousName);

            Assert.DoesNotContain('/', fileName);
            Assert.DoesNotContain('\\', fileName);
            Assert.DoesNotContain(':', fileName);
            Assert.DoesNotContain('\0', fileName);
            Assert.DoesNotContain("..", fileName, StringComparison.Ordinal);
            Assert.EndsWith(".json", fileName, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Throws_for_null_or_whitespace_name(string? name) =>
            Assert.ThrowsAny<ArgumentException>(() => SessionStore.ToFileName(name!));

        [Theory]
        [InlineData("...")]
        [InlineData("///")]
        [InlineData(":::")]
        public void A_name_that_sanitizes_to_nothing_falls_back_to_session(string name) =>
            Assert.Equal("session.json", SessionStore.ToFileName(name));

        [Fact]
        public void Ordinary_names_survive_mostly_intact() =>
            Assert.Equal("My-Session_1.json", SessionStore.ToFileName("My-Session_1"));

        [Fact]
        public void Leading_and_trailing_underscores_from_sanitized_characters_are_trimmed() =>
            Assert.Equal("evil.json", SessionStore.ToFileName("../../evil"));

        [Fact]
        public void Names_longer_than_100_characters_are_truncated()
        {
            var fileName = SessionStore.ToFileName(new string('a', 500));
            Assert.Equal(100 + ".json".Length, fileName.Length);
            Assert.Equal(new string('a', 100) + ".json", fileName);
        }
    }

    public class PathSafety : IDisposable
    {
        private readonly string _directory;
        private readonly SessionStore _store;

        public PathSafety()
        {
            _directory = Path.Combine(Path.GetTempPath(), "dbtickler-tests-" + Guid.NewGuid().ToString("N"));
            _store = new SessionStore(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        [Theory]
        [InlineData("../../evil")]
        [InlineData("..\\..\\evil")]
        [InlineData("C:\\Windows\\system32\\x")]
        [InlineData("../../../etc/passwd")]
        [InlineData("a/b/c")]
        [InlineData("weird:name")]
        [InlineData("....")]
        public void GetFilePath_always_resolves_to_a_path_under_the_store_directory(string maliciousName)
        {
            var path = _store.GetFilePath(maliciousName);
            var root = Path.GetFullPath(_store.Directory) + Path.DirectorySeparatorChar;

            Assert.StartsWith(root, path, StringComparison.Ordinal);
            // Fully resolved already: re-resolving it must be a no-op.
            Assert.Equal(Path.GetFullPath(path), path);
        }

        [Fact]
        public void GetFilePath_for_a_benign_name_lands_directly_in_the_store_directory()
        {
            var path = _store.GetFilePath("MySession");
            Assert.Equal(Path.Combine(Path.GetFullPath(_store.Directory), "MySession.json"), path);
        }
    }

    public class SaveAndLoad : IDisposable
    {
        private readonly string _directory;
        private readonly SessionStore _store;

        public SaveAndLoad()
        {
            _directory = Path.Combine(Path.GetTempPath(), "dbtickler-tests-" + Guid.NewGuid().ToString("N"));
            _store = new SessionStore(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }

        private static ConnectionProfile SqlAuthConnection(string password) => new()
        {
            Server = "sql01",
            Database = "db1",
            IntegratedSecurity = false,
            Username = "loadtest",
            Password = password,
        };

        [Fact]
        public void Saved_file_never_contains_the_plaintext_password_anywhere_in_its_raw_text()
        {
            const string Secret = "TopSecretPassword!123";
            var config = SessionConfig.From("MySession", SqlAuthConnection(Secret), WorkloadProfile.ReadOnly());

            _store.Save(config);

            var raw = File.ReadAllText(_store.GetFilePath("MySession"));
            Assert.DoesNotContain(Secret, raw, StringComparison.Ordinal);
        }

        [Fact]
        public void Round_trips_a_saved_session_except_for_the_password()
        {
            var connection = SqlAuthConnection("TopSecretPassword!123");
            var workload = WorkloadProfile.Oltp();
            var config = SessionConfig.From("MySession", connection, workload);

            _store.Save(config);
            var loaded = _store.Load(_store.GetFilePath("MySession"));

            Assert.Equal("MySession", loaded.SessionName);
            Assert.Equal("sql01", loaded.Server);
            Assert.Equal("db1", loaded.Database);
            Assert.Equal("loadtest", loaded.Username);
            Assert.Equal(workload.VirtualUsers, loaded.VirtualUsers);
            Assert.Equal(workload.ReadPercent, loaded.ReadPercent);
            Assert.Equal(workload.InsertPercent, loaded.InsertPercent);
            Assert.Equal(SessionConfig.CurrentVersion, loaded.Version);

            // DPAPI is Windows-only; on Linux/macOS the password is deliberately not
            // persisted at all, so it comes back empty rather than round-tripping.
            Assert.Equal("", loaded.Password);
        }

        [Fact]
        public void LoadAll_returns_sessions_most_recently_used_first()
        {
            var older = SessionConfig.From("Older", new ConnectionProfile(), WorkloadProfile.ReadOnly());
            _store.Save(older); // stamps Version/ProtectedPassword/LastUsed onto `older`

            var newer = SessionConfig.From("Newer", new ConnectionProfile(), WorkloadProfile.ReadOnly());
            _store.Save(newer);

            // Save() always stamps LastUsed to "now", so back-dating one file directly on disk
            // is the only way to produce an unambiguous ordering for the test.
            older.LastUsed = DateTimeOffset.UtcNow.AddDays(-5);
            File.WriteAllText(_store.GetFilePath("Older"), System.Text.Json.JsonSerializer.Serialize(
                older, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

            var all = _store.LoadAll();

            Assert.Equal(2, all.Count);
            Assert.Equal("Newer", all[0].SessionName);
            Assert.Equal("Older", all[1].SessionName);
        }

        [Fact]
        public void LoadAll_skips_a_corrupt_file_rather_than_throwing()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "corrupt.json"), "{ this is not valid json");

            var good = SessionConfig.From("Good", new ConnectionProfile(), WorkloadProfile.ReadOnly());
            _store.Save(good);

            var all = _store.LoadAll();

            Assert.Single(all);
            Assert.Equal("Good", all[0].SessionName);
        }

        [Fact]
        public void LoadAll_on_a_nonexistent_directory_returns_an_empty_list_rather_than_throwing() =>
            Assert.Empty(_store.LoadAll());

        [Fact]
        public void Delete_removes_a_saved_session_and_reports_true()
        {
            _store.Save(SessionConfig.From("ToDelete", new ConnectionProfile(), WorkloadProfile.ReadOnly()));

            Assert.True(_store.Delete("ToDelete"));
            Assert.False(File.Exists(_store.GetFilePath("ToDelete")));
        }

        [Fact]
        public void Delete_of_a_session_that_was_never_saved_reports_false() =>
            Assert.False(_store.Delete("NeverSaved"));

        [Fact]
        public void Save_stamps_the_current_version_and_bumps_last_used()
        {
            var config = SessionConfig.From("Versioned", new ConnectionProfile(), WorkloadProfile.ReadOnly());
            config.Version = 1;
            config.LastUsed = DateTimeOffset.UnixEpoch;

            var before = DateTimeOffset.UtcNow;
            _store.Save(config);

            Assert.Equal(SessionConfig.CurrentVersion, config.Version);
            Assert.True(config.LastUsed >= before);
        }
    }
}
