using NUnit.Framework;

namespace AntiCheat.Adapter.Tests;

[SetUpFixture]
public sealed class IsolatedRuntimeSetup
{
    [OneTimeSetUp]
    public void ConfigureRealGameStaticPaths()
    {
        // Audited Terraria.Program.SavePath is read by Main's static WorldPath/PlayerPath initialization.
        // Do not call Program.LaunchGame, start a game UI, or point the runtime at user worlds.
        var directory = Path.Combine(Path.GetTempPath(), "AntiCheat.Adapter.Tests", "runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Terraria.Program.SavePath = directory;
    }
}
