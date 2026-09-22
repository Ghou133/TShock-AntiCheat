using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed record M17NativeCombatWitness(string Label, byte[] Frame, short Damage, bool Critical,
    string CorpusFile, string CorpusHash, JsonElement NativeEvidence);

// Test runner input only. Captured client-side source evidence is never supplied to a product rule.
internal static class M17NativeCombatWitnessCorpus
{
    public static IReadOnlyList<M17NativeCombatWitness> Load(M4ObservedReplayHarness host, string directory)
    {
        string pluginHash = Hash(Path.Combine(host.RunDirectory, "app", "ServerPlugins", "AntiCheat.Plugin.TShock.dll"));
        string runtimeHash = Hash(Path.Combine(host.RunDirectory, "app", "bin", "OTAPI.dll"));
        var result = new List<M17NativeCombatWitness>(4);
        (string File, string Hash, int Count)[] inputs =
        [
            ("native-melee-witnesses.json", "21C28611EF29ABEE0D8D37089CAF8EF9E6D87520C130DA952D1E25B2D6ACDD4A", 3),
            ("native-child-witnesses.json", "35D807177AF1E1F48E95631E1761CB15581B59DA57FF1972642B92359F9E6169", 1)
        ];
        foreach (var input in inputs)
        {
            string path = Path.Combine(Path.GetFullPath(directory), input.File);
            if (new FileInfo(path).Length is < 1 or > 262144) throw new InvalidDataException("Native witness size outside bounded input contract.");
            byte[] bytes = File.ReadAllBytes(path);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != input.Hash) throw new InvalidDataException("Native witness corpus hash mismatch.");
            using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("pluginHash").GetString() != pluginHash ||
                root.GetProperty("runtimeHash").GetString() != runtimeHash || root.GetProperty("target").GetString() != "Terraria 1.4.5.8")
                throw new InvalidDataException("Native witness and running candidate/runtime identity differ; regenerate native evidence explicitly.");
            var witnesses = root.GetProperty("witnesses");
            if (witnesses.GetArrayLength() != input.Count) throw new InvalidDataException("Unexpected native witness count.");
            foreach (var item in witnesses.EnumerateArray())
            {
                byte[] frame = Convert.FromHexString(item.GetProperty("hex").GetString()!);
                if (frame.Length != 13 || BinaryPrimitives.ReadUInt16LittleEndian(frame) != 13 || frame[2] != 28)
                    throw new InvalidDataException("Expected exact captured ordinary strike frame.");
                short damage = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(5)); bool critical = frame[12] != 0;
                if (damage != item.GetProperty("damage").GetInt16() || critical != item.GetProperty("critical").GetBoolean())
                    throw new InvalidDataException("Native witness result fields disagree with original captured frame.");
                result.Add(new(item.GetProperty("label").GetString()!, frame, damage, critical, input.File, input.Hash, item.Clone()));
            }
            string evidenceDirectory = Path.Combine(host.ReportDirectory, "m16-combat", "native-producer-corpus");
            Directory.CreateDirectory(evidenceDirectory); File.WriteAllBytes(Path.Combine(evidenceDirectory, input.File), bytes);
        }
        return result;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
