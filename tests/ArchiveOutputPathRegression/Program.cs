using Zipora.Services;

int checks = 0;
void Expect(string actual, string expected, string label)
{
    if (actual != expected) throw new Exception($"{label}: expected '{expected}', got '{actual}'");
    checks++;
    Console.WriteLine("PASS " + label);
}

string directory = @"D:\我的备份.zip\子目录";
string renamed = directory + @"\手动改名.exe";
string[] extensions = { ".7z", ".zip", ".exe", ".tar", ".tar.gz", ".tar.bz2", ".tar.xz", ".wim" };
foreach (string before in extensions)
    foreach (string after in extensions)
        Expect(ArchiveOutputPath.WithExtension(directory + @"\资料.v2" + before, after),
            directory + @"\资料.v2" + after, $"switch {before} to {after}");
string switched = ArchiveOutputPath.WithExtension(renamed, ".tar.gz");
Expect(switched, directory + @"\手动改名.tar.gz", "reported rename EXE to TAR.GZ");
Expect(ArchiveOutputPath.WithExtension(switched, ".zip"), directory + @"\手动改名.zip", "repeat switch removes whole compound suffix");
Expect(ArchiveOutputPath.WithExtension(directory + @"\资料.v2", ".zip"), directory + @"\资料.v2.zip", "preserve dotted custom name");
Expect(ArchiveOutputPath.WithExtension(directory + @"\资料", ".7z"), directory + @"\资料.7z", "append missing suffix");
Expect(ArchiveOutputPath.WithExtension(directory + @"\资料.TAR.GZ", ".zip"), directory + @"\资料.zip", "case insensitive compound suffix");
Expect(ArchiveOutputPath.WithExtension("", ".zip"), "", "empty input stays empty");
Expect(ArchiveOutputPath.WithExtension(directory + @"\", ".zip"), directory + @"\", "unfinished directory stays unchanged");
Expect(ArchiveOutputPath.WithExtension("relative.name.7z", ".tar.bz2"), "relative.name.tar.bz2", "relative filename");

string fixtureRoot = Path.GetFullPath(args[0]);
if (Directory.Exists(fixtureRoot)) throw new Exception("Use a fresh output directory for regression fixtures.");
Directory.CreateDirectory(fixtureRoot);
string occupied = Path.Combine(fixtureRoot, "资料.v2.tar.gz");
File.WriteAllText(occupied, "existing archive placeholder");
File.WriteAllText(Path.Combine(fixtureRoot, "资料.v2 (1).tar.gz"), "existing numbered archive");
Expect(ArchiveOutputPath.FindAvailablePath(occupied, ".tar.gz"),
    Path.Combine(fixtureRoot, "资料.v2 (2).tar.gz"), "number before complete compound suffix");
Expect(File.ReadAllText(occupied), "existing archive placeholder", "existing output untouched");
Expect(ArchiveOutputPath.FindAvailablePath(Path.Combine(fixtureRoot, "新名字.exe"), ".tar.xz"),
    Path.Combine(fixtureRoot, "新名字.tar.xz"), "normalize final creation path");
Console.WriteLine($"PASS: {checks} checks");
