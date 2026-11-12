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

    [SkippableFact]
    public void A_windows_source_keeps_as_much_of_its_path_as_asked()
    {
        Skip.IfNot(OperatingSystem.IsWindows());
        Assert.Equal("C/code/app", PathText.FolderPrefix(SourcePathStyle.Full, @"C:\code\app"));
        Assert.Equal("app", PathText.FolderPrefix(SourcePathStyle.Name, @"C:\code\app"));
        Assert.Equal("", PathText.FolderPrefix(SourcePathStyle.None, @"C:\code\app"));
        // A whole drive has no name of its own but its letter.
        Assert.Equal("D", PathText.FolderPrefix(SourcePathStyle.Name, @"D:\"));
        Assert.Equal("share", PathText.FolderPrefix(SourcePathStyle.Name, @"\\nas\share"));

        // A source that is one file keeps its own name whatever is asked.
        Assert.Equal("C/notes/todo.txt", PathText.FilePath(SourcePathStyle.Full, @"C:\notes\todo.txt"));
        Assert.Equal("todo.txt", PathText.FilePath(SourcePathStyle.Name, @"C:\notes\todo.txt"));
        Assert.Equal("todo.txt", PathText.FilePath(SourcePathStyle.None, @"C:\notes\todo.txt"));
    }

    [SkippableFact]
    public void A_unix_source_keeps_as_much_of_its_path_as_asked()
    {
        Skip.If(OperatingSystem.IsWindows());
        Assert.Equal("home/me/app", PathText.FolderPrefix(SourcePathStyle.Full, "/home/me/app"));
        Assert.Equal("app", PathText.FolderPrefix(SourcePathStyle.Name, "/home/me/app"));
        Assert.Equal("", PathText.FolderPrefix(SourcePathStyle.None, "/home/me/app"));
        Assert.Equal("", PathText.FolderPrefix(SourcePathStyle.Name, "/"));
        Assert.Equal("todo.txt", PathText.FilePath(SourcePathStyle.None, "/home/me/todo.txt"));
    }

    [Theory]
    [InlineData("kept/history", "kept/history", true)]
    [InlineData("kept/history/2026/a.txt", "kept/history", true)]
    [InlineData("Kept/History/a.txt", "kept/history", true)] // a destination may ignore letter case
    [InlineData("kept/history-old/a.txt", "kept/history", false)]
    [InlineData("kept", "kept/history", false)]
    [InlineData("anything/at/all", "", true)] // the top holds everything
    public void A_path_is_inside_a_folder_or_it_is_not(string path, string folder, bool expected)
        => Assert.Equal(expected, PathText.IsSameOrInside(path, folder));

    [Fact]
    public void The_place_of_a_file_at_a_destination_can_be_worked_out_from_its_original_path()
    {
        var top = Path.Combine(Path.GetTempPath(), "hback-paths");
        var code = Path.Combine(top, "code");
        var app = Path.Combine(code, "app");
        BackupJob Job(SourcePathStyle style) => new()
        {
            Name = "test",
            Sources = [code, app],
            Destinations = [new DestinationSpec { Root = Path.Combine(top, "dest"), SourcePath = style }],
        };

        var byName = Job(SourcePathStyle.Name);
        // A file under both sources is looked for where the nearer one put it.
        Assert.Equal("app/sub/a.cs", byName.BackupPathOf(byName.Destinations[0], Path.Combine(app, "sub", "a.cs")));
        Assert.Equal("code/other/b.cs", byName.BackupPathOf(byName.Destinations[0], Path.Combine(code, "other", "b.cs")));
        Assert.Equal("code", byName.BackupPathOf(byName.Destinations[0], code + Path.DirectorySeparatorChar));
        Assert.Null(byName.BackupPathOf(byName.Destinations[0], Path.Combine(top, "elsewhere", "c.cs")));
        Assert.Null(byName.BackupPathOf(byName.Destinations[0], code + "-old"));

        var straight = Job(SourcePathStyle.None);
        Assert.Equal("sub/a.cs", straight.BackupPathOf(straight.Destinations[0], Path.Combine(app, "sub", "a.cs")));
        Assert.Equal("", straight.BackupPathOf(straight.Destinations[0], code));

        var full = Job(SourcePathStyle.Full);
        Assert.Equal(PathText.ToBackupPath(Path.Combine(app, "a.cs")), full.BackupPathOf(full.Destinations[0], Path.Combine(app, "a.cs")));
    }

    [Fact]
    public void Only_a_name_hback_makes_itself_counts_as_a_leftover_of_its_own()
    {
        Assert.True(BackupRunner.IsPartialName(BackupRunner.PartialName("C/code/app/a.cs")));
        Assert.False(BackupRunner.IsPartialName("notes.txt"));
        Assert.False(BackupRunner.IsPartialName("upload.part"));
        Assert.False(BackupRunner.IsPartialName("0123456789ABCDEF0123456789ABCDEF.part"));
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

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "backups");

    private static (string, string, string) Layout(DestinationSpec d) => (d.CurrentFolder, d.HistoryFolder, d.PartialFolder);

    [Fact]
    public void The_three_folders_are_side_by_side_unless_told_otherwise()
    {
        var usual = Parse(Folder);

        Assert.Equal(("current", "history", "partial"), Layout(usual));
        Assert.Empty(usual.OwnFoldersInCurrent);
        Assert.Equal(SourcePathStyle.Full, usual.SourcePath);
        // A backup made before the folders could be moved is still known by the same id.
        Assert.Equal(Folder, usual.Id);
    }

    [Fact]
    public void The_three_folders_can_be_put_elsewhere_inside_the_destination()
    {
        var named = Parse(Folder, ("current_folder", @"www\site\"), ("history_folder", "kept/history"), ("partial_folder", "kept/incoming"));
        Assert.Equal(("www/site", "kept/history", "kept/incoming"), Layout(named));
        Assert.Empty(named.OwnFoldersInCurrent);
        Assert.Equal("www/site/a.txt", named.InCurrent("a.txt"));
        Assert.Equal("kept/history/2026-11-11_103000/a.txt", named.InHistory("2026-11-11_103000", "a.txt"));

        var inside = Parse(Folder, ("current_folder", "site"), ("history_folder", "site/.old"));
        Assert.Equal([".old"], inside.OwnFoldersInCurrent);

        // Another folder for the files is another record of what is there.
        Assert.NotEqual(Parse(Folder).Id, named.Id);
        Assert.Equal(Parse(Folder).Place, named.Place);
    }

    [Fact]
    public void Files_that_go_straight_into_the_destination_get_hbacks_folders_out_of_their_way()
    {
        var straight = Parse(Folder, ("current_folder", "."), ("source_path", "none"));

        Assert.Equal(("", ".hback/history", ".hback/partial"), Layout(straight));
        Assert.Equal([".hback/history", ".hback/partial"], straight.OwnFoldersInCurrent);
        Assert.Equal("a.txt", straight.InCurrent("a.txt"));
        Assert.Equal(SourcePathStyle.None, straight.SourcePath);

        // Unless they are named.
        Assert.Equal(("", "old", ".hback/partial"), Layout(Parse(Folder, ("current_folder", "."), ("history_folder", "old"))));
    }

    [Theory]
    [InlineData("current_folder", "/srv/files")]
    [InlineData("current_folder", "../elsewhere")]
    [InlineData("current_folder", "files//deep")]
    [InlineData("history_folder", ".")]
    [InlineData("history_folder", "kept/../..")]
    [InlineData("partial_folder", "..")]
    public void A_folder_that_is_not_a_folder_inside_the_destination_is_refused(string tag, string value)
        => Assert.Contains($"<{tag}>", Assert.Throws<ConfigException>(() => Parse(Folder, (tag, value))).Message);

    [Theory]
    [InlineData("files", "kept", "kept")]
    [InlineData("files", "kept", "kept/incoming")]
    [InlineData("kept", "kept", "incoming")]
    [InlineData("kept/files", "kept", "incoming")]
    [InlineData("files", "old", "Old/incoming")]
    public void Folders_that_would_get_in_each_others_way_are_refused(string current, string history, string partial)
        => Assert.Throws<ConfigException>(() => Parse(Folder, ("current_folder", current), ("history_folder", history), ("partial_folder", partial)));

    [Fact]
    public void How_much_of_the_source_path_is_kept_has_three_answers()
    {
        Assert.Equal(SourcePathStyle.Name, Parse(Folder, ("source_path", "Name")).SourcePath);
        Assert.Contains("full, name or none", Assert.Throws<ConfigException>(() => Parse(Folder, ("source_path", "flat"))).Message);
    }

    [Fact]
    public void Two_destinations_in_one_place_get_in_each_others_way_only_when_their_folders_do()
    {
        var usual = Parse(Folder);
        var a = Parse(Folder, ("current_folder", "a"), ("history_folder", "kept/a"), ("partial_folder", "incoming/a"));
        var b = Parse(Folder, ("current_folder", "b"), ("history_folder", "kept/b"), ("partial_folder", "incoming/b"));
        var sharedHistory = Parse(Folder, ("current_folder", "c"), ("history_folder", "kept/a"), ("partial_folder", "incoming/c"));
        var straight = Parse(Folder, ("current_folder", "."));

        Assert.True(usual.SharesFoldersWith(Parse(Folder)));
        Assert.False(a.SharesFoldersWith(b));
        Assert.False(a.SharesFoldersWith(usual));
        Assert.True(a.SharesFoldersWith(sharedHistory));
        // Files that go straight into the folder take all of it.
        Assert.True(straight.SharesFoldersWith(a));
        Assert.False(usual.SharesFoldersWith(Parse(Folder + "2")));
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
        Assert.Equal(destination, Assert.Single(projects.Destinations).Root);
        Assert.Equal(7, projects.Destinations[0].KeepHistoryDays);
        Assert.True(projects.Scheduled);
        Assert.Equal("every 30 min", projects.ScheduleText);

        var byHand = entries[1].Job!;
        Assert.False(byHand.Scheduled);
        Assert.Equal(30, byHand.Destinations[0].KeepHistoryDays);
        Assert.Equal("b", byHand.Destinations[0].Root);
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
        var destination = Assert.Single(job.Destinations);
        Assert.True(destination.IsSftp);
        Assert.Equal("/backups/laptop", destination.Root);
        Assert.NotNull(destination.PrivateKey);
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
    public void A_backup_can_go_to_several_destinations()
    {
        var source = Path.Combine(_folder, "code");
        string At(string name) => Path.Combine(_folder, name);
        var entries = Load($"""
            <hback>
              <backup>
                <name>repeated</name>
                <sources>{source}</sources>
                <destination>{At("a")}</destination>
                <destination>{At("b")}</destination>
              </backup>
              <backup>
                <name>listed</name>
                <sources>{source}</sources>
                <password>shared</password>
                <keep_history_days>7</keep_history_days>
                <destinations>
                  {At("c")}
                  # a comment
                  sftp://me@one.example.com/srv/files
                  sftp://me@two.example.com/srv/files
                </destinations>
              </backup>
              <backup>
                <name>both ways</name>
                <sources>{source}</sources>
                <destination>{At("d")}</destination>
                <destinations>{At("e")}</destinations>
              </backup>
            </hback>
            """);

        Assert.All(entries, e => Assert.Null(e.Error));
        Assert.Equal([At("a"), At("b")], entries[0].Job!.Destinations.Select(d => d.Root));
        Assert.Equal([At("d"), At("e")], entries[2].Job!.Destinations.Select(d => d.Root));

        // What the backup says about its destination holds for every one of them.
        var listed = entries[1].Job!.Destinations;
        Assert.Equal([null, "one.example.com", "two.example.com"], listed.Select(d => d.Host));
        Assert.All(listed.Skip(1), d => Assert.Equal("shared", d.Password));
        Assert.All(listed, d => Assert.Equal(7, d.KeepHistoryDays));
    }

    [Fact]
    public void A_destination_can_have_settings_of_its_own_and_takes_the_rest_from_the_backup()
    {
        var source = Path.Combine(_folder, "code");
        var entry = Assert.Single(Load($"""
            <hback>
              <backup>
                <name>site</name>
                <sources>{source}</sources>
                <source_path>none</source_path>
                <keep_history_days>7</keep_history_days>
                <password>shared</password>
                <destinations>
                  <destination>sftp://deploy@web1/var/www/site</destination>
                  <destination>
                    <name>web2</name>
                    <path>sftp://deploy@web2/var/www</path>
                    <password>its own</password>
                    <current_folder>site</current_folder>
                    <keep_history_days>0</keep_history_days>
                    <parallel>2</parallel>
                  </destination>
                  <destination>
                    <name>nas</name>
                    <path>{Path.Combine(_folder, "nas")}</path>
                    <source_path>full</source_path>
                  </destination>
                </destinations>
              </backup>
            </hback>
            """));

        Assert.Null(entry.Error);
        var job = entry.Job!;
        Assert.Equal("site", job.Name);
        Assert.Equal(3, job.Destinations.Count);
        var (web1, web2, nas) = (job.Destinations[0], job.Destinations[1], job.Destinations[2]);

        Assert.Equal((null, "web1", "shared", SourcePathStyle.None, "current", 7, 4),
            (web1.Name, web1.Host, web1.Password, web1.SourcePath, web1.CurrentFolder, web1.KeepHistoryDays, web1.Parallel));
        Assert.Equal(("web2", "web2", "its own", SourcePathStyle.None, "site", 0, 2),
            (web2.Name, web2.Host, web2.Password, web2.SourcePath, web2.CurrentFolder, web2.KeepHistoryDays, web2.Parallel));
        Assert.Equal(("nas", false, SourcePathStyle.Full, 7), (nas.Name, nas.IsSftp, nas.SourcePath, nas.KeepHistoryDays));

        // In the log a run is told apart by the destination's name, or its address when it has none.
        Assert.Equal("site (web2)", job.LabelFor(web2));
        Assert.Equal("site (sftp://deploy@web1:22/var/www/site)", job.LabelFor(web1));
    }

    [Fact]
    public void A_backup_with_one_destination_is_called_by_its_name_alone()
    {
        var entry = Assert.Single(Load($"""
            <hback><backup><name>projects</name><sources>{Path.Combine(_folder, "code")}</sources><destination>{Path.Combine(_folder, "d")}</destination></backup></hback>
            """));

        Assert.Equal("projects", entry.Job!.LabelFor(entry.Job.Destinations[0]));
    }

    [Fact]
    public void Mistakes_in_the_destinations_are_said_in_plain_words()
    {
        var source = Path.Combine(_folder, "code");
        var d = Path.Combine(_folder, "d");
        string Backup(string name, string body) => $"<backup><name>{name}</name><sources>{source}</sources>{body}</backup>";
        var entries = Load($"""
            <hback>
              {Backup("none", "")}
              {Backup("no path", "<destination><name>web</name><password>pw</password></destination>")}
              {Backup("twice", $"<destinations>{d}\n{d}{Path.DirectorySeparatorChar}</destinations>")}
              {Backup("same name", $"<destination><name>web</name><path>{d}1</path></destination><destination><name>WEB</name><path>{d}2</path></destination>")}
              {Backup("stranger", $"<destinations><server>{d}</server></destinations>")}
              {Backup("beside", $"<destination>{d}9 <keep_history_days>7</keep_history_days></destination>")}
              {Backup("bad folder", $"<destination>{d}</destination><history_folder>/var/history</history_folder>")}
              {Backup("fine", $"<destination>{d}</destination>")}
            </hback>
            """);

        Assert.Contains("'none' has no <destination>", entries[0].Error);
        Assert.Contains("needs a <path>", entries[1].Error);
        Assert.Contains("twice", entries[2].Error);
        Assert.Contains("two destinations named 'web'", entries[3].Error);
        Assert.Contains("<destinations> holds a <server>", entries[4].Error);
        // An address beside the settings, where it would be passed over, is pointed out.
        Assert.Contains($"gives its address in a <path>. '{d}9' is not in one", entries[5].Error);
        Assert.Contains("Backup 'bad folder': <history_folder>", entries[6].Error);
        Assert.Null(entries[7].Error);
    }

    [Fact]
    public void A_list_of_destinations_can_have_lines_and_blocks_side_by_side()
    {
        string At(string name) => Path.Combine(_folder, name);
        var entry = Assert.Single(Load($"""
            <hback>
              <backup>
                <name>site</name>
                <sources>{At("code")}</sources>
                <destinations>
                  {At("first")}
                  <destination>
                    <name>second</name>
                    <path>{At("second")}</path>
                    <keep_history_days>90</keep_history_days>
                  </destination>
                  {At("third")}
                  <destination>{At("fourth")}</destination>
                  # not a destination
                  {At("fifth")}
                </destinations>
              </backup>
            </hback>
            """));

        Assert.Null(entry.Error);
        // In the order they are written, which is the order they are served in.
        Assert.Equal([At("first"), At("second"), At("third"), At("fourth"), At("fifth")], entry.Job!.Destinations.Select(d => d.Root));
        Assert.Equal([30, 90, 30, 30, 30], entry.Job.Destinations.Select(d => d.KeepHistoryDays));
    }

    [Fact]
    public void Two_sources_of_the_same_name_cannot_both_be_kept_by_name()
    {
        var first = Path.Combine(_folder, "one", "app");
        var second = Path.Combine(_folder, "two", "App");
        string Backup(string name, string style) => $"""
            <backup><name>{name}</name><sources>
              {first}
              {second}
            </sources><destination>{Path.Combine(_folder, name)}</destination><source_path>{style}</source_path></backup>
            """;
        var entries = Load($"<hback>{Backup("a", "name")}{Backup("b", "full")}</hback>");

        Assert.Contains("would both go into the folder 'app'", entries[0].Error);
        Assert.Null(entries[1].Error);
    }

    [Fact]
    public void Two_backups_may_share_a_place_when_each_keeps_to_folders_of_its_own()
    {
        var source = Path.Combine(_folder, "code");
        var place = Path.Combine(_folder, "www");
        string Backup(string name, string folders) => $"<backup><name>{name}</name><sources>{source}</sources><destination>{place}</destination>{folders}</backup>";
        string Own(string name) => $"<current_folder>{name}</current_folder><history_folder>.hback/{name}/history</history_folder><partial_folder>.hback/{name}/partial</partial_folder>";
        var jobs = Load($"""
            <hback>
              {Backup("a", Own("site-a"))}
              {Backup("b", Own("site-b"))}
              {Backup("c", "<current_folder>site-c</current_folder><history_folder>.hback/site-a/history</history_folder>")}
            </hback>
            """).Select(e => e.Job!).ToList();

        Assert.Null(BackupService.FindConflict(jobs[0], jobs.Take(2)));
        Assert.Null(BackupService.FindConflict(jobs[1], jobs.Take(2)));
        // The third would put its history where the first keeps its own.
        Assert.Contains("same destination", BackupService.FindConflict(jobs[2], jobs));
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
