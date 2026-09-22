using System.ComponentModel;
using AntiCheat.DevMcp;
using NUnit.Framework;

namespace AntiCheat.DevMcp.Tests;

[TestFixture, NonParallelizable]
public sealed class WindowsPathPinsNativeTests
{
    [Test]
    public void OrdinaryNewFileRemainsReadableAndExistingEvidenceCannotBeRecreated()
    {
        using var lab = new NativeTestDirectory();
        string path = lab.File("ordinary.log");
        byte[] expected = [0, 1, 2, 127, 255];
        using var pins = WindowsPathPins.ForFile(path);
        using (var stream = pins.CreateNew()) stream.Write(expected);
        using (var stream = pins.OpenRead())
        {
            var actual = new byte[expected.Length]; stream.ReadExactly(actual);
            Assert.That(actual, Is.EqualTo(expected));
        }
        Assert.Throws<Win32Exception>(() => { using var unexpected = pins.CreateNew(); });
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(expected));
        lab.Record("ordinary-file-preserved", new { path, bytes = Convert.ToHexString(expected) });
    }

    [Test]
    public void TraversalAlternateStreamsAndDevicePathsAreRejectedBeforeOpening()
    {
        using var lab = new NativeTestDirectory();
        string[] rejected = ["relative.txt", @"C:relative.txt", @"\\server\share\file", @"\\?\C:\file",
            lab.Root + @"\..\outside", lab.Root + @"\.\file", lab.Root + @"\file:stream",
            lab.Root + @"\NUL.log", lab.Root + @"\CONIN$", lab.Root + @"\trailing.", lab.Root + "\\trailing "];
        Assert.Multiple(() =>
        {
            foreach (string path in rejected)
                Assert.That(() => WindowsPathPins.Normalize(path), Throws.ArgumentException, path);
        });
        // Authorization of an ordinary absolute sibling belongs to the caller's root/index,
        // not to WindowsPathPins, which explicitly documents itself as a path primitive.
        lab.Record("rejected-paths", rejected);
    }

    [Test]
    public void PinnedDirectoryCannotBeRenamedUntilTheHandleIsReleased()
    {
        using var lab = new NativeTestDirectory();
        string original = lab.File("pinned"), renamed = lab.File("renamed");
        Directory.CreateDirectory(original);
        using (var pins = WindowsPathPins.ForDirectory(original))
        {
            Assert.That(() => Directory.Move(original, renamed), Throws.InstanceOf<IOException>());
            Assert.That(Directory.Exists(original), Is.True);
        }
        Directory.Move(original, renamed);
        Assert.That(Directory.Exists(renamed), Is.True, "Releasing the real pin must release its rename exclusion.");
        lab.Record("directory-pin", new { original, renamed, renameAfterRelease = true });
    }

    [Test]
    public void RealHardLinkIsRejectedWithoutChangingEitherName()
    {
        using var lab = new NativeTestDirectory();
        string original = lab.File("original.log"), alias = lab.File("hardlink.log");
        const string expected = "retained evidence\n";
        File.WriteAllText(original, expected);
        NativeTestLinks.CreateHardLink(alias, original);
        try
        {
            foreach (string path in new[] { original, alias })
            {
                using var pins = WindowsPathPins.ForFile(path);
                Assert.That(() => { using var stream = pins.OpenRead(); },
                    Throws.InstanceOf<IOException>().With.Message.Contains("hard link"));
                Assert.That(File.ReadAllText(path), Is.EqualTo(expected));
            }
            lab.Record("hardlink-rejected", new { original, alias, content = expected });
        }
        finally { File.Delete(alias); }
    }

    [Test]
    public void RealJunctionAncestorIsRejectedWithoutFollowingItsTarget()
    {
        using var lab = new NativeTestDirectory();
        string target = lab.File("junction-target"), junction = lab.File("junction");
        Directory.CreateDirectory(target);
        string evidence = Path.Combine(target, "sentinel.log");
        File.WriteAllText(evidence, "target must remain intact");
        NativeTestLinks.CreateJunction(junction, target);
        try
        {
            Assert.That(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint), Is.True);
            Assert.That(() => { using var pins = WindowsPathPins.ForDirectory(junction); },
                Throws.InstanceOf<IOException>().With.Message.Contains("Reparse"));
            Assert.That(() => { using var pins = WindowsPathPins.ForFile(Path.Combine(junction, "sentinel.log")); },
                Throws.InstanceOf<IOException>().With.Message.Contains("Reparse"));
            Assert.That(File.ReadAllText(evidence), Is.EqualTo("target must remain intact"));
            lab.Record("junction-rejected", new { junction, target, evidence });
        }
        finally { Directory.Delete(junction); } // Removes this exact link, never recursively follows it.
    }
}
