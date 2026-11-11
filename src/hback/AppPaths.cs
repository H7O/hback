namespace HBack;

/// <summary>
/// Where hback reads its configuration and keeps its own files (the state database, logs and locks).
/// </summary>
public sealed record AppPaths(string ConfigPath, string DataDir)
{
    public const string ConfigFileName = "hback.xml";
    public const string SampleFileName = "hback.sample.xml";

    public string DbPath => Path.Combine(DataDir, "hback.db");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string LockDir => Path.Combine(DataDir, "locks");

    /// <summary>
    /// A command-line value wins, then the environment variable, then a default beside the binary.
    /// </summary>
    public static AppPaths Resolve(string? config, string? data)
    {
        config ??= Environment.GetEnvironmentVariable("HBACK_CONFIG");
        data ??= Environment.GetEnvironmentVariable("HBACK_DATA");
        return new AppPaths(
            Path.GetFullPath(string.IsNullOrWhiteSpace(config) ? Path.Combine(AppContext.BaseDirectory, ConfigFileName) : config),
            Path.GetFullPath(string.IsNullOrWhiteSpace(data) ? Path.Combine(AppContext.BaseDirectory, "data") : data));
    }

    public bool ConfigExists => File.Exists(ConfigPath) || Directory.Exists(ConfigPath);
}
