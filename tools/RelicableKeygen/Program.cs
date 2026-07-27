using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Relicable.Licensing;

namespace Relicable.Keygen;

// Offline Early Alpha code generator. MAINTAINER ONLY -- this program holds the
// private key that signs access codes. Never ship it, and never commit keys/.
//
//   init                              create the signing keypair (once)
//   mint --owner "Name#1234" --days N issue a code
//   verify <code>                     check a code the way the plugin will
//   inspect <code>                    read a code's contents WITHOUT verifying it
//
// See tools/RelicableKeygen/README.md.
internal static class Program
{
    private const string DefaultKeyPath = "keys/relicable-alpha.key";
    private const string PluginSourcePath = "Relicable/Licensing/AlphaCode.cs";

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "init" => Init(args),
                "mint" => Mint(args),
                "verify" => Verify(args),
                "inspect" => Inspect(args),
                "-h" or "--help" or "help" => Usage(),
                _ => Fail($"Unknown command \"{args[0]}\"."),
            };
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // ---------------------------------------------------------------------------
    // init -- generate the signing keypair
    // ---------------------------------------------------------------------------

    private static int Init(string[] args)
    {
        var keyPath = Arg(args, "--key") ?? DefaultKeyPath;
        var force = Flag(args, "--force");

        if (File.Exists(keyPath) && !force)
        {
            Console.Error.WriteLine($"A signing key already exists at {keyPath}.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Refusing to overwrite it. Generating a new key INVALIDATES EVERY CODE");
            Console.Error.WriteLine("you have ever issued -- every alpha tester would be locked out at once.");
            Console.Error.WriteLine("If that is genuinely what you want (e.g. the key leaked), re-run with --force.");
            return 1;
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var dir = Path.GetDirectoryName(Path.GetFullPath(keyPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var privatePem = ecdsa.ExportPkcs8PrivateKeyPem();
        File.WriteAllText(keyPath, privatePem);

        // Best effort on Windows: strip inherited ACLs so the key is not world-readable
        // on a shared machine. Not a substitute for keeping it off the repo.
        TryRestrictToCurrentUser(keyPath);

        var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        Console.WriteLine($"Private signing key written to  {keyPath}");
        Console.WriteLine();
        Console.WriteLine("  *** BACK THIS FILE UP SOMEWHERE OFF THIS MACHINE. ***");
        Console.WriteLine("  Lose it and you cannot issue another code without shipping a plugin");
        Console.WriteLine("  update that invalidates all existing ones. It is gitignored on purpose.");
        Console.WriteLine();
        Console.WriteLine("Public key (X.509 SubjectPublicKeyInfo, base64):");
        Console.WriteLine();
        Console.WriteLine("  " + publicKeyBase64);
        Console.WriteLine();

        if (TryPatchPublicKey(publicKeyBase64, out var patchedPath))
        {
            Console.WriteLine($"Patched AlphaCode.PublicKeyBase64 in {patchedPath}.");
            Console.WriteLine("Rebuild the plugin for it to take effect.");
        }
        else
        {
            Console.WriteLine($"Could not find {PluginSourcePath} from the current directory.");
            Console.WriteLine("Paste the key above into AlphaCode.PublicKeyBase64 by hand, then rebuild.");
        }

        return 0;
    }

    // ---------------------------------------------------------------------------
    // mint -- issue a code
    // ---------------------------------------------------------------------------

    private static int Mint(string[] args)
    {
        var owner = Arg(args, "--owner");
        if (string.IsNullOrWhiteSpace(owner))
            return Fail("mint requires --owner \"Name#1234\" (shown in the tester's plugin window).");
        if (owner!.Contains('|'))
            return Fail("--owner may not contain the '|' character.");

        var keyPath = Arg(args, "--key") ?? DefaultKeyPath;
        if (!File.Exists(keyPath))
            return Fail($"No signing key at {keyPath}. Run `init` first, or pass --key <path>.");

        // Expiry: --days N (from today) or --until YYYY-MM-DD. Default 90 days, which
        // is long enough for a tester to be useful and short enough that a leaked code
        // dies on its own.
        DateTime expires;
        var until = Arg(args, "--until");
        if (!string.IsNullOrEmpty(until))
        {
            if (!DateTime.TryParseExact(until, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expires))
                return Fail("--until must be a date in the form YYYY-MM-DD.");
        }
        else
        {
            var days = 90;
            var raw = Arg(args, "--days");
            if (!string.IsNullOrEmpty(raw) && !int.TryParse(raw, out days))
                return Fail("--days must be a whole number.");
            if (days <= 0)
                return Fail("--days must be greater than zero.");
            expires = DateTime.UtcNow.Date.AddDays(days);
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(keyPath));

        var serial = Arg(args, "--serial") ?? NewSerial();
        var code = AlphaCode.Mint(ecdsa, owner!, expires, serial);

        // Round-trip against the key we just signed with, so a broken build can never
        // hand out a code that does not actually work.
        var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
        if (!AlphaCode.TryValidate(code, DateTime.UtcNow, publicKeyBase64, out _, out var error))
            return Fail($"Minted a code that does not validate ({error}). This is a bug -- do not send it out.");

        var compiledIn = AlphaCode.PublicKeyBase64;
        var matchesShippedKey = string.Equals(compiledIn, publicKeyBase64, StringComparison.Ordinal);

        Console.WriteLine();
        Console.WriteLine($"  Owner    {owner}");
        Console.WriteLine($"  Expires  {expires:yyyy-MM-dd}  ({(expires.Date - DateTime.UtcNow.Date).TotalDays:0} days)");
        Console.WriteLine($"  Serial   {serial}   (add to AlphaCode.RevokedSerials to revoke just this code)");
        Console.WriteLine();
        Console.WriteLine(code);
        Console.WriteLine();

        if (!matchesShippedKey)
        {
            Console.Error.WriteLine("WARNING: this key does NOT match the public key compiled into AlphaCode.cs.");
            Console.Error.WriteLine("The code above will be rejected by the current plugin build. Either you are");
            Console.Error.WriteLine("signing with the wrong --key, or AlphaCode.PublicKeyBase64 is out of date.");
            return 1;
        }

        return 0;
    }

    // ---------------------------------------------------------------------------
    // verify / inspect
    // ---------------------------------------------------------------------------

    private static int Verify(string[] args)
    {
        var code = Positional(args);
        if (code is null)
            return Fail("verify requires a code.");

        // Verifies against the key COMPILED INTO the plugin source, which is the thing
        // that actually matters -- this answers "will a tester's plugin accept it".
        if (!AlphaCode.TryValidate(code, DateTime.UtcNow, AlphaCode.PublicKeyBase64, out var license, out var error))
        {
            Console.Error.WriteLine($"REJECTED: {error}");
            if (license.IsPresent)
                Console.Error.WriteLine($"          (code is authentic; issued to {license.Owner}, expired {license.Expires:yyyy-MM-dd})");
            return 1;
        }

        Console.WriteLine("ACCEPTED");
        Console.WriteLine($"  Owner    {license.Owner}");
        Console.WriteLine($"  Expires  {license.Expires:yyyy-MM-dd}  ({license.DaysRemaining(DateTime.UtcNow)} days left)");
        Console.WriteLine($"  Serial   {license.Serial}");
        return 0;
    }

    private static int Inspect(string[] args)
    {
        var code = Positional(args);
        if (code is null)
            return Fail("inspect requires a code.");

        if (!AlphaCode.TryInspect(code, out var license, out var error))
            return Fail(error);

        Console.WriteLine("Payload (NOT signature-checked -- use `verify` to decide access):");
        Console.WriteLine($"  Owner    {license.Owner}");
        Console.WriteLine($"  Expires  {license.Expires:yyyy-MM-dd}");
        Console.WriteLine($"  Serial   {license.Serial}");
        return 0;
    }

    // ---------------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------------

    // 8 hex characters from a cryptographic RNG: enough that two codes never collide
    // in a hand-run alpha, short enough to read out loud.
    private static string NewSerial()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    // Rewrites the PublicKeyBase64 constant in the plugin source after `init`, so the
    // key can never be mistyped in transit. Walks up from the working directory to
    // find the repo root, so it works from tools/RelicableKeygen as well as the root.
    private static bool TryPatchPublicKey(string publicKeyBase64, out string patchedPath)
    {
        patchedPath = string.Empty;

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, PluginSourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                var source = File.ReadAllText(candidate);
                var patched = Regex.Replace(
                    source,
                    "(public const string PublicKeyBase64\\s*=\\s*)\"[^\"]*\"",
                    "$1\"" + publicKeyBase64 + "\"");

                if (ReferenceEquals(patched, source) || patched == source)
                    return false;

                File.WriteAllText(candidate, patched);
                patchedPath = candidate;
                return true;
            }
            dir = dir.Parent;
        }

        return false;
    }

    // Windows only: drop inherited permissions so the private key is readable by the
    // current user alone. Silently skipped elsewhere -- it is defence in depth, not a
    // guarantee, and the real protection is that keys/ is gitignored.
    private static void TryRestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user is not null)
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            info.SetAccessControl(security);
        }
        catch
        {
            // Non-fatal: an unusual filesystem or a locked-down profile can refuse this.
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool Flag(string[] args, string name)
        => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    // First non-flag argument after the command word.
    private static string? Positional(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
            if (!args[i].StartsWith('-'))
                return args[i];
        return null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            RelicableKeygen -- Early Alpha access code generator (maintainer only)

            Run from the repository root.

              init [--key <path>] [--force]
                  Generate the P-256 signing keypair. Writes the private key to
                  keys/relicable-alpha.key and patches the public key into
                  Relicable/Licensing/AlphaCode.cs. Run this ONCE.

              mint --owner "<name>" [--days <n> | --until YYYY-MM-DD] [--serial <s>] [--key <path>]
                  Issue a code. Default expiry is 90 days. The owner name is shown
                  permanently in that tester's plugin window.

              verify <code>
                  Check a code exactly as the plugin will.

              inspect <code>
                  Show a code's contents without checking its signature.

            Examples:
              dotnet run --project tools/RelicableKeygen -- init
              dotnet run --project tools/RelicableKeygen -- mint --owner "Someone#1234" --days 60
              dotnet run --project tools/RelicableKeygen -- verify RLC1.xxxx.yyyy
            """);
        return 0;
    }
}
