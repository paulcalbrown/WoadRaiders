// Verifies the release manifest the client's startup update check consumes
// (see .claude/skills/verify). A .NET 10 file-based app:
//
//   dotnet run tools/UpdateProbe.cs                  probe the LIVE GitHub manifest
//   dotnet run tools/UpdateProbe.cs build/latest.json   validate a local manifest file
//   dotnet run tools/UpdateProbe.cs http://...       probe another manifest URL
//
// Against the live manifest it asserts: it fetches, it parses, and it does not
// claim to be newer than this working copy (the repo is always >= the release).
// Run it after every `tools/release.ps1 -Publish`. Exit code 0 = all pass.

#:project ../WoadRaiders.Shared/WoadRaiders.Shared.csproj

using WoadRaiders.Shared;

var source = args.Length > 0 ? args[0] : UpdateManifest.Url;
var failures = 0;

UpdateManifest? manifest;
if (File.Exists(source))
{
    Console.WriteLine($"[probe] parsing local file {source}");
    manifest = UpdateManifest.TryParse(File.ReadAllText(source), out var parsed) ? parsed : null;
}
else
{
    Console.WriteLine($"[probe] fetching {source}");
    manifest = await UpdateManifest.FetchAsync(source);
}

Check(manifest != null, "the manifest fetches and parses");
if (manifest == null)
{
    Console.WriteLine("[probe] (no release published yet? a 404 also lands here)");
    return 1;
}

Console.WriteLine($"[probe] key={manifest.Key} page={manifest.Page}");
Console.WriteLine($"[probe] windows:      {manifest.Windows?.DownloadUrl ?? "MISSING"} sha256={manifest.Windows?.Sha256 ?? "-"}");
Console.WriteLine($"[probe] macos:        {manifest.MacOS?.DownloadUrl ?? "MISSING"} sha256={manifest.MacOS?.Sha256 ?? "-"}");
Console.WriteLine($"[probe] server win:   {manifest.ServerWindows?.DownloadUrl ?? "MISSING"} sha256={manifest.ServerWindows?.Sha256 ?? "-"}");
Console.WriteLine($"[probe] server linux: {manifest.ServerLinux?.DownloadUrl ?? "MISSING"} sha256={manifest.ServerLinux?.Sha256 ?? "-"}");

Check(NetConfig.TryParseVersion(manifest.Key, out var released), "the manifest key parses as WoadRaiders.vN");
Check(manifest.Page.StartsWith("https://", StringComparison.Ordinal), "the releases page is https");
Check(manifest.Windows is { DownloadUrl.Length: > 0, Sha256.Length: 64 }, "the Windows artifact has a url and a sha256");
Check(manifest.MacOS is { DownloadUrl.Length: > 0, Sha256.Length: 64 }, "the macOS artifact has a url and a sha256");
Check(manifest.ServerWindows is { DownloadUrl.Length: > 0, Sha256.Length: 64 }, "the Windows server artifact has a url and a sha256");
Check(manifest.ServerLinux is { DownloadUrl.Length: > 0, Sha256.Length: 64 }, "the Linux server artifact has a url and a sha256");

NetConfig.TryParseVersion(NetConfig.ConnectionKey, out var local);
Check(released <= local,
    $"the release (v{released}) is not ahead of this working copy (v{local}) — publish from an up-to-date clone");
Console.WriteLine(released < local
    ? $"[probe] note: this working copy (v{local}) is ahead of the release (v{released}); clients will be told to update once v{local} ships"
    : "[probe] the release matches this working copy");

Console.WriteLine(failures == 0 ? "[probe] ALL CHECKS PASSED" : $"[probe] {failures} CHECK(S) FAILED");
return failures == 0 ? 0 : 1;

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
    if (!ok) failures++;
}
