using HBack.Config;
using HBack.Engine;
using HBack.Scan;

namespace HBack.Tests;

public class ExcludeMatcherTests
{
    private static bool Excluded(string pattern, string relativePath, bool isFolder = false, string? fullPath = null)
    {
        var matcher = new ExcludeMatcher([pattern], ignoreCase: false);
        return matcher.IsExcluded(PathText.NameOf(relativePath), relativePath, fullPath ?? "C:/code/app/" + relativePath, isFolder);
    }

    [Theory]
    // A bare name matches at any depth, files and folders alike.
    [InlineData("node_modules", "node_modules", true, true)]
    [InlineData("node_modules", "web/node_modules", true, true)]
    [InlineData("node_modules", "web/node_modules_old", true, false)]
    [InlineData("notes.txt", "docs/notes.txt", false, true)]
    // A trailing slash means folders only.
    [InlineData("bin/", "src/bin", true, true)]
    [InlineData("bin/", "src/bin", false, false)]
    // Wildcards stay inside one name.
    [InlineData("*.tmp", "a/b/c.tmp", false, true)]
    [InlineData("*.tmp", "a/b/c.tmp.keep", false, false)]
    [InlineData("data?.bin", "data1.bin", false, true)]
    [InlineData("data?.bin", "data10.bin", false, false)]
    [InlineData("[Bb]in/", "Bin", true, true)]
    [InlineData("[Bb]in/", "bin", true, true)]
    [InlineData("[Bb]in/", "cin", true, false)]
    // A slash makes it a path from the top of the source.
    [InlineData("build/output", "build/output", true, true)]
    [InlineData("build/output", "sub/build/output", true, false)]
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "sub/build", true, false)]
    [InlineData("**/output", "output", true, true)]
    [InlineData("**/output", "a/b/output", true, true)]
    [InlineData("docs/**/*.pdf", "docs/a/b/c.pdf", false, true)]
    [InlineData("docs/**/*.pdf", "docs/c.pdf", false, true)]
    [InlineData("docs/*.pdf", "docs/a/c.pdf", false, false)]
    public void Pattern_matches_as_a_gitignore_line_would(string pattern, string path, bool isFolder, bool expected)
        => Assert.Equal(expected, Excluded(pattern, path, isFolder));

    [Fact]
    public void A_full_path_excludes_that_folder_only()
    {
        Assert.True(Excluded("C:/code/app/old/", "old", isFolder: true, fullPath: "C:/code/app/old"));
        Assert.False(Excluded("C:/code/app/old/", "old", isFolder: true, fullPath: "C:/code/other/old"));
        Assert.True(Excluded(@"C:\code\app\old", "old", isFolder: true, fullPath: "C:/code/app/old"));
    }

    [Fact]
    public void Blank_lines_and_comments_are_not_patterns()
    {
        var matcher = new ExcludeMatcher(["", "   ", "# node_modules"], ignoreCase: false);
        Assert.False(matcher.IsExcluded("node_modules", "node_modules", "C:/x/node_modules", true));
    }

    [Fact]
    public void Case_is_ignored_when_asked()
    {
        var matcher = new ExcludeMatcher(["Node_Modules/", "*.TMP"], ignoreCase: true);
        Assert.True(matcher.IsExcluded("node_modules", "node_modules", "C:/x/node_modules", true));
        Assert.True(matcher.IsExcluded("a.tmp", "a.tmp", "C:/x/a.tmp", false));
    }
}

public class PathTextTests
{
    [SkippableFact]
    public void A_windows_path_keeps_its_drive_letter_as_a_folder()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.Equal("C/code/app/a.cs", PathText.ToBackupPath(@"C:\code\app\a.cs"));
        Assert.Equal("D", PathText.ToBackupPath(@"D:\"));
        Assert.Equal("UNC/nas/share/a.cs", PathText.ToBackupPath(@"\\nas\share\a.cs"));
    }

    [SkippableFact]
    public void A_unix_path_drops_its_leading_slash()
    {
        Skip.If(OperatingSystem.IsWindows());
        Assert.Equal("home/me/a.cs", PathText.ToBackupPath("/home/me/a.cs"));
        Assert.Equal(@"home/me/odd\name", PathText.ToBackupPath(@"/home/me/odd\name"));
    }

    [Fact]
    public void Lines_drops_blanks_and_comments()
        => Assert.Equal(["a", "b c"], PathText.Lines("\n  a  \n\n # no\n b c \n"));

    [Fact]
    public void A_partial_file_name_is_flat_and_stable()
    {
        var name = BackupRunner.PartialName("C/code/app/a.cs");
        Assert.Equal(name, BackupRunner.PartialName("C/code/app/a.cs"));
        Assert.NotEqual(name, BackupRunner.PartialName("C/code/app/b.cs"));
        Assert.DoesNotContain('/', name);
    }

    [Theory]
    [InlineData("2026-11-11_103000", true)]
    [InlineData("2026-11-11_103000-2", true)]
    [InlineData("notes", false)]
    [InlineData("2026-13-45_990000", false)]
    public void A_history_folder_name_is_a_time(string name, bool expected)
        => Assert.Equal(expected, BackupLayout.TryParseStamp(name, out _));
}

public class DestinationSpecTests
{
    private static DestinationSpec Parse(string destination, params (string Key, string Value)[] settings)
        => DestinationSpec.Parse(destination, key => settings.FirstOrDefault(s => s.Key == key).Value);

    [Fact]
    public void An_sftp_address_is_taken_apart()
    {
        var d = Parse("sftp://me@backup.example.com:2222/backups/laptop", ("password", "pw"));
        Assert.True(d.IsSftp);
        Assert.Equal("me", d.User);
        Assert.Equal("backup.example.com", d.Host);
        Assert.Equal(2222, d.Port);
        Assert.Equal("/backups/laptop", d.Root);
        Assert.Equal("pw", d.Password);
    }

    [Theory]
    [InlineData("sftp://me@host/backups/", "/backups")]
    [InlineData("sftp://me@host/", "/")]
    [InlineData("sftp://me@host", ".")]
    [InlineData("sftp://me@host/~", ".")]
    [InlineData("sftp://me@host/~/backups/laptop", "backups/laptop")]
    [InlineData("sftp://me@host/my%20backups", "/my backups")]
    public void The_remote_folder_is_absolute_or_under_the_login_folder(string destination, string root)
    {
        var d = Parse(destination, ("password", "pw"));
        Assert.Equal(root, d.Root);
        Assert.Equal(22, d.Port);
    }

    [Fact]
    public void A_private_key_wins_over_a_password()
    {
        var key = Path.Combine(Path.GetTempPath(), "id_test");
        var d = Parse("sftp://me@host/x", ("password", "pw"), ("private_key", key));
        Assert.Equal(key, d.PrivateKey);
        Assert.Null(d.Password);
    }

    [Fact]
    public void An_sftp_destination_needs_a_way_to_log_in()
        => Assert.Contains("private_key", Assert.Throws<ConfigException>(() => Parse("sftp://me@host/x")).Message);

    [Fact]
    public void The_id_holds_no_password()
    {
        var d = Parse("sftp://me:hunter2@Host/x");
        Assert.Equal("hunter2", d.Password);
        Assert.DoesNotContain("hunter2", d.Id);
        Assert.DoesNotContain("hunter2", d.Display);
        Assert.Equal("sftp://me@host:22/x", d.Id);
    }

    [Fact]
    public void A_local_destination_must_be_a_full_path()
    {
        Assert.Throws<ConfigException>(() => Parse("backups"));
        var full = Path.Combine(Path.GetTempPath(), "backups");
        Assert.Equal(full, Parse(full + Path.DirectorySeparatorChar).Root);
    }
}

public class ConfigTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hback-config-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private List<ConfigEntry> Load(string xml)
    {
        var path = Path.Combine(_folder, "hback.xml");
        File.WriteAllText(path, xml);
        return ConfigLoader.Load(path);
    }

    [Fact]
    public void A_backup_is_read_through_the_schedulers_own_reader()
    {
        var source = Path.Combine(_folder, "code");
        var destination = Path.Combine(_folder, "dest");
        var entries = Load($"""
            <?xml version="1.0" encoding="utf-8"?>
            <hback>
              <backup>
                <name>projects</name>
                <sys><interval>1800000</interval></sys>
                <sources>
                  {source}
                  # a comment
                  {source}{Path.DirectorySeparatorChar}
                </sources>
                <exclude>
                  node_modules/
                  *.tmp
                </exclude>
                <destination>{destination}</destination>
                <keep_history_days>7</keep_history_days>
              </backup>
              <backup>
                <name>by hand</name>
                <sources>{source}</sources>
                <destination>sftp://me@host/~/b</destination>
                <password>pw</password>
              </backup>
            </hback>
            """);

        Assert.All(entries, e => Assert.Null(e.Error));
        var projects = entries[0].Job!;
        Assert.Equal("projects", projects.Name);
        Assert.Equal([source], projects.Sources);
        Assert.Equal(["node_modules/", "*.tmp"], projects.Excludes);
        Assert.Equal(destination, projects.Destination.Root);
        Assert.Equal(7, projects.KeepHistoryDays);
        Assert.True(projects.Scheduled);
        Assert.Equal("every 30 min", projects.ScheduleText);

        var byHand = entries[1].Job!;
        Assert.False(byHand.Scheduled);
        Assert.Equal(30, byHand.KeepHistoryDays);
        Assert.Equal("b", byHand.Destination.Root);
    }

    [SkippableFact]
    public void The_sample_configuration_that_ships_with_hback_is_valid()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The sample is written with Windows paths.");
        var entry = Assert.Single(ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, AppPaths.SampleFileName)));

        Assert.Null(entry.Error);
        var job = entry.Job!;
        Assert.Equal("projects", job.Name);
        Assert.Equal(2, job.Sources.Count);
        Assert.Equal(@"C:\code", job.Sources[0]);
        Assert.Contains("node_modules/", job.Excludes);
        Assert.True(job.Destination.IsSftp);
        Assert.Equal("/backups/laptop", job.Destination.Root);
        Assert.NotNull(job.Destination.PrivateKey);
        Assert.Equal("every 60 min", job.ScheduleText);
    }

    [Fact]
    public void A_mistake_is_reported_for_its_backup_and_the_others_still_load()
    {
        var source = Path.Combine(_folder, "code");
        var entries = Load($"""
            <hback>
              <backup><name>good</name><sources>{source}</sources><destination>{Path.Combine(_folder, "d")}</destination></backup>
              <backup><name>no sources</name><destination>{Path.Combine(_folder, "e")}</destination></backup>
              <backup><name>relative</name><sources>code</sources><destination>{Path.Combine(_folder, "f")}</destination></backup>
            </hback>
            """);

        Assert.NotNull(entries[0].Job);
        Assert.Contains("no <sources>", entries[1].Error);
        Assert.Contains("full path", entries[2].Error);
    }

    [Fact]
    public void Two_backups_may_not_share_a_destination()
    {
        var source = Path.Combine(_folder, "code");
        var destination = Path.Combine(_folder, "d");
        var jobs = Load($"""
            <hback>
              <backup><name>a</name><sources>{source}</sources><destination>{destination}</destination></backup>
              <backup><name>b</name><sources>{source}</sources><destination>{destination}</destination></backup>
              <backup><name>c</name><sources>{source}</sources><destination>{destination}2</destination></backup>
            </hback>
            """).Select(e => e.Job!).ToList();

        Assert.Contains("same destination", BackupService.FindConflict(jobs[0], jobs));
        Assert.Null(BackupService.FindConflict(jobs[2], jobs));
    }
}
