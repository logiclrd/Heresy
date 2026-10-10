using System.Diagnostics;
using System.Reflection;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: ReleaseVersionCheck PUBLISH_DIR A.B.C");
    return 2;
}

string publishDirectory = args[0];
string tagVersion = args[1];
if (!Version.TryParse(tagVersion + ".0", out Version? expectedVersion))
{
    Console.Error.WriteLine("Invalid release version: " + tagVersion);
    return 2;
}

string[] assemblies =
[
    "Heresy", "Heresy.Core", "Heresy.Scripting", "Heresy.Playback",
    "Heresy.Render", "Heresy.Render.SDL", "Heresy.Render.File",
    "Heresy.UserInterface",
];

foreach (string assembly in assemblies)
{
    string file = Path.Combine(publishDirectory, assembly + ".dll");
    if (!File.Exists(file))
        throw new FileNotFoundException("Published Heresy assembly is missing.", file);

    Version? actual = AssemblyName.GetAssemblyName(file).Version;
    FileVersionInfo fileInfo = FileVersionInfo.GetVersionInfo(file);
    string expectedFileVersion = tagVersion + ".0";

    if (actual != expectedVersion
        || fileInfo.FileVersion != expectedFileVersion
        || fileInfo.ProductVersion != tagVersion)
    {
        throw new InvalidOperationException(
            $"{assembly} has inconsistent version metadata: " +
            $"assembly={actual}, file={fileInfo.FileVersion}, " +
            $"informational={fileInfo.ProductVersion}; expected " +
            $"{expectedVersion}, {expectedFileVersion}, {tagVersion}.");
    }

    Console.WriteLine($"{assembly}: {tagVersion} (assembly {actual})");
}
return 0;
