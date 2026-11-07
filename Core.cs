// GZ Mod Manager - core logic (no UI). Portable, MO2-style staging + deploy.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GZModManager
{
    public static class GameInfo
    {
        public const string Name = "Godzilla: Destroy All Monsters Melee Remastered";
        public const string FolderName = "Godzilla Destroy All Monsters Melee Remastered";
        public const string SteamAppId = "4251460";
        public const string Win64 = "GZPro/Binaries/Win64";
        public const string Exe = "GZPro/Binaries/Win64/GZProSteam-Win64-Shipping.exe";
        public const string Ue4ssDir = "GZPro/Binaries/Win64/ue4ss";
        public const string Ue4ssMods = "GZPro/Binaries/Win64/ue4ss/Mods";
        public const string Ue4ssProxy = "GZPro/Binaries/Win64/dwmapi.dll";
        public const string PakMods = "GZPro/Content/Paks/~mods";
        public const string LogicMods = "GZPro/Content/Paks/LogicMods";

        // Mods that ship with UE4SS itself; never treated as user mods.
        public static readonly HashSet<string> BuiltInUe4ssMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "ActorDumperMod","BPML_GenericFunctions","BPModLoaderMod","CheatManagerEnablerMod","ConsoleCommandsMod",
            "ConsoleEnablerMod","EventViewerMod","Keybinds","KismetDebuggerMod","LineTraceMod","SplitScreenMod",
            "jsbLuaProfilerMod","shared"
        };
    }

    public static class P
    {
        // Internal relative paths always use '/'.
        public static string Norm(string rel) { return rel.Replace('\\', '/').Trim('/'); }
        public static string Os(string root, string rel) { return Path.Combine(root, Norm(rel).Replace('/', Path.DirectorySeparatorChar)); }
        public static string Rel(string root, string full)
        {
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);
            return Norm(f.Substring(r.Length));
        }
        public static string[] Segs(string rel) { return Norm(rel).Split('/'); }
        public static bool StartsWith(string rel, string prefix)
        {
            rel = Norm(rel); prefix = Norm(prefix);
            return rel.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                   rel.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }
        public static string SafeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim().Trim('.');
            return s.Length == 0 ? "Mod" : s;
        }
    }

    public class ModEntry
    {
        public string Name;
        public bool Enabled;
        public string Type = "";
        public string Version = "";
        public string Source = "";
        public string Installed = "";
        public List<string> Files = new List<string>(); // game-root-relative
    }

    public class InstallPlan
    {
        public Dictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // srcRel -> gameRel
        public List<string> Docs = new List<string>();
        public List<string> Unknown = new List<string>();
        public HashSet<string> Kinds = new HashSet<string>();
        public string TypeLabel { get { return Kinds.Count == 0 ? "Unknown" : string.Join(" + ", Kinds.OrderBy(k => k)); } }
    }

    public class Conflict
    {
        public string Path;
        public string Winner;
        public List<string> Losers = new List<string>();
    }

    public class ModManager
    {
        public readonly string Root;        // manager folder (portable)
        public string GamePath = "";
        public string Profile = "Default";
        public List<ModEntry> Mods = new List<ModEntry>(); // order = priority, last wins
        public bool DeployedStateDirty = true;

        public string ModsDir { get { return Path.Combine(Root, "mods"); } }
        public string ProfilesDir { get { return Path.Combine(Root, "profiles"); } }
        public string BackupDir { get { return Path.Combine(Root, "backup"); } }
        public string ManifestPath { get { return Path.Combine(Root, "deployed.manifest"); } }
        public string SettingsPath { get { return Path.Combine(Root, "GZModManager.ini"); } }
        public string LastDeployPath { get { return Path.Combine(Root, "lastdeploy.txt"); } }

        public static readonly string[] DocExts = { ".txt", ".md", ".png", ".jpg", ".jpeg", ".gif", ".url", ".pdf", ".html", ".htm", ".rtf", ".webp", ".bmp", ".nfo" };
        public static readonly string[] PakExts = { ".pak", ".utoc", ".ucas", ".sig" };

        public ModManager(string root)
        {
            Root = root;
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(ProfilesDir);
            LoadSettings();
            if (!Directory.Exists(Path.Combine(ProfilesDir, Profile))) Directory.CreateDirectory(Path.Combine(ProfilesDir, Profile));
            Reload();
        }

        // ---------------------------------------------------------------- settings
        void LoadSettings()
        {
            if (!File.Exists(SettingsPath)) return;
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                if (k == "GamePath") GamePath = v;
                else if (k == "Profile" && v.Length > 0) Profile = v;
            }
        }
        public void SaveSettings()
        {
            File.WriteAllLines(SettingsPath, new[] { "GamePath=" + GamePath, "Profile=" + Profile });
        }
        public bool GamePathValid { get { return GamePath.Length > 0 && File.Exists(P.Os(GamePath, GameInfo.Exe)); } }

        // ---------------------------------------------------------------- profiles
        public List<string> Profiles()
        {
            var l = Directory.GetDirectories(ProfilesDir).Select(Path.GetFileName).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            if (l.Count == 0) { Directory.CreateDirectory(Path.Combine(ProfilesDir, "Default")); l.Add("Default"); }
            return l;
        }
        string ModListPath { get { return Path.Combine(ProfilesDir, Profile, "modlist.txt"); } }

        public void SwitchProfile(string name)
        {
            SaveModList();
            Profile = name;
            Directory.CreateDirectory(Path.Combine(ProfilesDir, name));
            SaveSettings();
            Reload();
            DeployedStateDirty = true;
        }
        public void CreateProfile(string name, bool copyCurrent)
        {
            name = P.SafeName(name);
            string dir = Path.Combine(ProfilesDir, name);
            if (Directory.Exists(dir)) throw new Exception("A profile named '" + name + "' already exists.");
            SaveModList();
            Directory.CreateDirectory(dir);
            if (copyCurrent && File.Exists(ModListPath)) File.Copy(ModListPath, Path.Combine(dir, "modlist.txt"));
            SwitchProfile(name);
        }
        public void DeleteProfile(string name)
        {
            var all = Profiles();
            if (all.Count <= 1) throw new Exception("You can't delete the only profile.");
            Directory.Delete(Path.Combine(ProfilesDir, name), true);
            if (name.Equals(Profile, StringComparison.OrdinalIgnoreCase))
            {
                Profile = Profiles()[0];
                SaveSettings();
                Reload();
            }
        }

        // ---------------------------------------------------------------- mod list
        public void Reload()
        {
            var installed = Directory.GetDirectories(ModsDir).Select(Path.GetFileName).ToList();
            var order = new List<KeyValuePair<string, bool>>();
            if (File.Exists(ModListPath))
                foreach (var line in File.ReadAllLines(ModListPath))
                {
                    if (line.Length < 2 || (line[0] != '+' && line[0] != '-')) continue;
                    order.Add(new KeyValuePair<string, bool>(line.Substring(1), line[0] == '+'));
                }
            var result = new List<ModEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in order)
            {
                string actual = installed.FirstOrDefault(n => n.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                if (actual == null || seen.Contains(actual)) continue;
                seen.Add(actual);
                result.Add(LoadMod(actual, kv.Value));
            }
            foreach (var n in installed.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                if (!seen.Contains(n)) result.Add(LoadMod(n, false)); // new mods go last (highest priority), disabled
            Mods = result;
            SaveModList();
        }

        ModEntry LoadMod(string name, bool enabled)
        {
            var m = new ModEntry { Name = name, Enabled = enabled };
            string meta = Path.Combine(ModsDir, name, "meta.ini");
            if (File.Exists(meta))
                foreach (var line in File.ReadAllLines(meta))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    if (k == "type") m.Type = v;
                    else if (k == "version") m.Version = v;
                    else if (k == "source") m.Source = v;
                    else if (k == "installed") m.Installed = v;
                }
            string root = Path.Combine(ModsDir, name, "root");
            if (Directory.Exists(root))
                m.Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => P.Rel(root, f)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            return m;
        }

        public void SaveModList()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ModListPath));
            File.WriteAllLines(ModListPath, Mods.Select(m => (m.Enabled ? "+" : "-") + m.Name));
        }

        public void Move(int index, int newIndex)
        {
            if (index < 0 || index >= Mods.Count) return;
            newIndex = Math.Max(0, Math.Min(Mods.Count - 1, newIndex));
            var m = Mods[index];
            Mods.RemoveAt(index);
            Mods.Insert(newIndex, m);
            SaveModList();
            DeployedStateDirty = true;
        }
        public void SetEnabled(ModEntry m, bool on) { m.Enabled = on; SaveModList(); DeployedStateDirty = true; }

        public void RemoveMod(ModEntry m)
        {
            Directory.Delete(Path.Combine(ModsDir, m.Name), true);
            Mods.Remove(m);
            SaveModList();
            DeployedStateDirty = true;
        }
        public void RenameMod(ModEntry m, string newName)
        {
            newName = P.SafeName(newName);
            if (newName.Equals(m.Name, StringComparison.Ordinal)) return;
            if (Mods.Any(x => x != m && x.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("A mod named '" + newName + "' already exists.");
            string src = Path.Combine(ModsDir, m.Name), dst = Path.Combine(ModsDir, newName);
            if (src.Equals(dst, StringComparison.OrdinalIgnoreCase)) { Directory.Move(src, src + ".tmp_rename"); src += ".tmp_rename"; }
            Directory.Move(src, dst);
            m.Name = newName;
            SaveModList();
            DeployedStateDirty = true;
        }

        // ---------------------------------------------------------------- installing
        public static string GuessVersion(string fileName)
        {
            var mt = Regex.Match(fileName, @"[vV](\d+(?:\.\d+){0,3})");
            if (mt.Success) return mt.Groups[1].Value;
            mt = Regex.Match(fileName, @"(\d+\.\d+(?:\.\d+){0,2})");
            return mt.Success ? mt.Groups[1].Value : "";
        }
        public static string GuessName(string fileName)
        {
            string n = Path.GetFileNameWithoutExtension(fileName);
            n = Regex.Replace(n, @"[-_ ]?[vV]?\d+(?:\.\d+){1,3}.*$", "");   // strip version
            n = Regex.Replace(n, @"-\d+-\d+(-\d+)*$", "");                   // strip nexus ids
            return P.SafeName(n.Length > 0 ? n : Path.GetFileNameWithoutExtension(fileName));
        }

        // Work out where each file of an extracted mod belongs in the game folder.
        public static InstallPlan Plan(string srcRoot, string modName)
        {
            var plan = new InstallPlan();
            var files = Directory.GetFiles(srcRoot, "*", SearchOption.AllDirectories).Select(f => P.Rel(srcRoot, f)).ToList();

            // 1) Archive already laid out like the game folder (contains GZPro/ or Engine/).
            string gameRootPrefix = null;
            foreach (var f in files)
            {
                var s = P.Segs(f);
                for (int i = 0; i < s.Length - 1; i++)
                    if (s[i].Equals("GZPro", StringComparison.OrdinalIgnoreCase))
                    { gameRootPrefix = string.Join("/", s.Take(i)); break; }
                if (gameRootPrefix != null) break;
            }
            if (gameRootPrefix != null)
            {
                foreach (var f in files)
                {
                    if (gameRootPrefix.Length > 0 && !P.StartsWith(f, gameRootPrefix)) { Classify(plan, f, null); continue; }
                    string rel = gameRootPrefix.Length > 0 ? f.Substring(gameRootPrefix.Length + 1) : f;
                    var s0 = P.Segs(rel)[0];
                    if (s0.Equals("GZPro", StringComparison.OrdinalIgnoreCase) || s0.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                    { plan.Map[f] = rel; plan.Kinds.Add("Game files"); }
                    else Classify(plan, f, null);
                }
                return plan;
            }

            // 2) Archive containing a ue4ss/ folder (e.g. Win64 layout).
            // 3) UE4SS mod folders (Scripts/main.lua or dlls/main.dll).
            var modDirs = new List<string>();
            foreach (var f in files)
            {
                var s = P.Segs(f);
                if (s.Length >= 2)
                {
                    string last = s[s.Length - 1], parent = s[s.Length - 2];
                    if ((parent.Equals("Scripts", StringComparison.OrdinalIgnoreCase) && last.Equals("main.lua", StringComparison.OrdinalIgnoreCase)) ||
                        (parent.Equals("dlls", StringComparison.OrdinalIgnoreCase) && last.Equals("main.dll", StringComparison.OrdinalIgnoreCase)))
                    {
                        string dir = string.Join("/", s.Take(s.Length - 2));
                        if (!modDirs.Contains(dir)) modDirs.Add(dir);
                    }
                }
            }
            foreach (var f in files)
            {
                var s = P.Segs(f);
                int ue = Array.FindIndex(s, x => x.Equals("ue4ss", StringComparison.OrdinalIgnoreCase));
                if (ue >= 0 && ue < s.Length - 1)
                {
                    plan.Map[f] = GameInfo.Win64 + "/" + string.Join("/", s.Skip(ue));
                    plan.Kinds.Add("UE4SS files");
                    continue;
                }
                string modDir = modDirs.Where(d => d.Length == 0 || P.StartsWith(f, d)).OrderByDescending(d => d.Length).FirstOrDefault();
                if (modDir != null)
                {
                    string folder = modDir.Length == 0 ? modName : P.Segs(modDir).Last();
                    string inner = modDir.Length == 0 ? f : f.Substring(modDir.Length + 1);
                    // Docs at the very top of a root-level mod stay docs.
                    if (modDir.Length == 0 && !inner.Contains("/") && IsDoc(inner) && !inner.Equals("enabled.txt", StringComparison.OrdinalIgnoreCase))
                    { plan.Docs.Add(f); continue; }
                    plan.Map[f] = GameInfo.Ue4ssMods + "/" + folder + "/" + inner;
                    if (inner.StartsWith("dlls/", StringComparison.OrdinalIgnoreCase)) plan.Kinds.Add("UE4SS C++");
                    else if (inner.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase)) plan.Kinds.Add("UE4SS Lua");
                    continue;
                }
                Classify(plan, f, null);
            }
            return plan;
        }

        static bool IsDoc(string f) { return DocExts.Contains(Path.GetExtension(f).ToLowerInvariant()); }

        static void Classify(InstallPlan plan, string f, string unused)
        {
            string ext = Path.GetExtension(f).ToLowerInvariant();
            var s = P.Segs(f);
            if (PakExts.Contains(ext))
            {
                bool logic = s.Any(x => x.Equals("LogicMods", StringComparison.OrdinalIgnoreCase));
                plan.Map[f] = (logic ? GameInfo.LogicMods : GameInfo.PakMods) + "/" + s.Last();
                plan.Kinds.Add(logic ? "Blueprint (LogicMods)" : "Pak");
            }
            else if (IsDoc(f)) plan.Docs.Add(f);
            else plan.Unknown.Add(f);
        }

        // Install from a .zip file or a folder. Returns the new mod entry.
        public ModEntry Install(string path, string modName, bool allowUnknownAsGameRoot)
        {
            modName = P.SafeName(modName);
            if (Mods.Any(m => m.Name.Equals(modName, StringComparison.OrdinalIgnoreCase)))
                throw new Exception("A mod named '" + modName + "' is already installed. Remove it first or pick another name.");

            string temp = Path.Combine(Root, "temp", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                string src;
                if (Directory.Exists(path)) src = path;
                else if (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipSafe(path, temp);
                    src = temp;
                }
                else if (PakExts.Contains(Path.GetExtension(path).ToLowerInvariant()))
                {
                    // Single pak file: bring its .utoc/.ucas/.sig siblings along.
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    foreach (var e in PakExts)
                    {
                        string sib = Path.Combine(Path.GetDirectoryName(path), baseName + e);
                        if (File.Exists(sib)) File.Copy(sib, Path.Combine(temp, baseName + e));
                    }
                    src = temp;
                }
                else throw new Exception("Unsupported file type. Use a .zip, a folder, or .pak/.utoc/.ucas files.\n(.7z and .rar: extract them first and install the folder.)");

                var plan = Plan(src, modName);
                if (plan.Map.Count == 0 && plan.Unknown.Count == 0)
                    throw new Exception("No installable files were found.");
                if (plan.Map.Count == 0 && !allowUnknownAsGameRoot)
                    throw new UnknownLayoutException(plan);
                if (allowUnknownAsGameRoot)
                    foreach (var u in plan.Unknown) { plan.Map[u] = u; plan.Kinds.Add("Game files"); }

                string modDir = Path.Combine(ModsDir, modName);
                string root = Path.Combine(modDir, "root");
                Directory.CreateDirectory(root);
                foreach (var kv in plan.Map)
                {
                    string dst = P.Os(root, kv.Value);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(P.Os(src, kv.Key), dst, true);
                }
                if (plan.Docs.Count > 0)
                {
                    string docs = Path.Combine(modDir, "docs");
                    foreach (var d in plan.Docs)
                    {
                        string dst = P.Os(docs, d);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        File.Copy(P.Os(src, d), dst, true);
                    }
                }
                File.WriteAllLines(Path.Combine(modDir, "meta.ini"), new[] {
                    "type=" + plan.TypeLabel,
                    "version=" + GuessVersion(Path.GetFileName(path.TrimEnd('\\','/'))),
                    "source=" + Path.GetFileName(path.TrimEnd('\\','/')),
                    "installed=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                });
                var entry = LoadMod(modName, true);
                Mods.Add(entry);
                SaveModList();
                DeployedStateDirty = true;
                return entry;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        static void ExtractZipSafe(string zip, string dest)
        {
            string full = Path.GetFullPath(dest).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var za = ZipFile.OpenRead(zip))
                foreach (var e in za.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue; // directory entry
                    string target = Path.GetFullPath(Path.Combine(dest, P.Norm(e.FullName).Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
        }

        // Move UE4SS mod folders that were installed by hand into the manager.
        public List<string> FindUnmanagedUe4ssMods()
        {
            var list = new List<string>();
            if (!GamePathValid) return list;
            string modsDir = P.Os(GamePath, GameInfo.Ue4ssMods);
            if (!Directory.Exists(modsDir)) return list;
            var deployed = new HashSet<string>(ReadManifest().Where(l => l.StartsWith("F|")).Select(l => l.Substring(2)), StringComparer.OrdinalIgnoreCase);
            foreach (var d in Directory.GetDirectories(modsDir))
            {
                string n = Path.GetFileName(d);
                if (GameInfo.BuiltInUe4ssMods.Contains(n)) continue;
                bool managed = deployed.Any(p => P.StartsWith(p, GameInfo.Ue4ssMods + "/" + n));
                if (!managed) list.Add(n);
            }
            return list;
        }
        public ModEntry ImportUnmanaged(string folderName)
        {
            string src = P.Os(GamePath, GameInfo.Ue4ssMods + "/" + folderName);
            var entry = Install(src, folderName, false);
            Directory.Delete(src, true); // it now lives in the manager and gets deployed from there
            return entry;
        }

        // ---------------------------------------------------------------- conflicts
        public Dictionary<string, Conflict> Conflicts()
        {
            var owners = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in Mods.Where(m => m.Enabled))
                foreach (var f in m.Files)
                {
                    if (IsPakRel(f)) continue; // paks get unique names on deploy
                    List<string> l;
                    if (!owners.TryGetValue(f, out l)) owners[f] = l = new List<string>();
                    l.Add(m.Name);
                }
            var res = new Dictionary<string, Conflict>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in owners.Where(k => k.Value.Count > 1))
                res[kv.Key] = new Conflict { Path = kv.Key, Winner = kv.Value.Last(), Losers = kv.Value.Take(kv.Value.Count - 1).ToList() };
            return res;
        }
        static bool IsPakRel(string rel) { return P.StartsWith(rel, GameInfo.PakMods) || P.StartsWith(rel, GameInfo.LogicMods); }

        // ---------------------------------------------------------------- deploy
        List<string> ReadManifest() { return File.Exists(ManifestPath) ? File.ReadAllLines(ManifestPath).ToList() : new List<string>(); }
        public bool IsDeployed { get { return File.Exists(ManifestPath); } }

        // Returns list of messages.
        public List<string> Deploy()
        {
            if (!GamePathValid) throw new Exception("Set the game folder first.");
            var log = new List<string>();
            log.AddRange(Purge());

            // Build target map: gameRel -> source full path ("" = create empty file).
            var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int prio = 0;
            foreach (var m in Mods.Where(m => m.Enabled))
            {
                prio++;
                string root = Path.Combine(ModsDir, m.Name, "root");
                foreach (var f in m.Files)
                {
                    string rel = f;
                    if (IsPakRel(f))
                    {
                        string dir = f.Substring(0, f.LastIndexOf('/'));
                        rel = dir + "/" + prio.ToString("000") + "_" + f.Substring(f.LastIndexOf('/') + 1);
                    }
                    target[rel] = P.Os(root, f);
                }
                // UE4SS mods need enabled.txt to load.
                foreach (var folder in m.Files.Where(f => P.StartsWith(f, GameInfo.Ue4ssMods))
                                              .Select(f => P.Segs(f.Substring(GameInfo.Ue4ssMods.Length + 1))[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string en = GameInfo.Ue4ssMods + "/" + folder + "/enabled.txt";
                    if (!target.ContainsKey(en)) target[en] = "";
                }
            }

            var manifest = new List<string>();
            try
            {
                foreach (var kv in target.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string dst = P.Os(GamePath, kv.Key);
                    // Record directories we create so purge can remove them.
                    var created = new List<string>();
                    string d = Path.GetDirectoryName(dst);
                    while (!Directory.Exists(d)) { created.Insert(0, d); d = Path.GetDirectoryName(d); }
                    foreach (var c in created) { Directory.CreateDirectory(c); manifest.Add("D|" + P.Rel(GamePath, c)); }

                    if (File.Exists(dst))
                    {
                        string bak = P.Os(BackupDir, kv.Key);
                        Directory.CreateDirectory(Path.GetDirectoryName(bak));
                        File.Copy(dst, bak, true);
                        manifest.Add("B|" + kv.Key);
                        log.Add("Backed up original: " + kv.Key);
                    }
                    if (kv.Value.Length == 0) File.WriteAllText(dst, "");
                    else File.Copy(kv.Value, dst, true);
                    manifest.Add("F|" + kv.Key);
                }
            }
            finally
            {
                File.WriteAllLines(ManifestPath, manifest); // always written, so a partial deploy can still be purged
            }
            File.WriteAllText(LastDeployPath, Profile + "|" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            DeployedStateDirty = false;
            log.Add(string.Format("Deployed {0} file(s) from {1} enabled mod(s).", target.Count, Mods.Count(m => m.Enabled)));
            return log;
        }

        public List<string> Purge()
        {
            var log = new List<string>();
            var lines = ReadManifest();
            if (lines.Count == 0 && !File.Exists(ManifestPath)) return log;
            if (!GamePathValid) throw new Exception("Game folder not found, can't purge.");
            int removed = 0;
            foreach (var l in lines.Where(l => l.StartsWith("F|")))
            {
                string p = P.Os(GamePath, l.Substring(2));
                if (File.Exists(p)) { File.Delete(p); removed++; }
            }
            foreach (var l in lines.Where(l => l.StartsWith("B|")))
            {
                string rel = l.Substring(2), bak = P.Os(BackupDir, rel), dst = P.Os(GamePath, rel);
                if (File.Exists(bak))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(bak, dst, true);
                    File.Delete(bak);
                    log.Add("Restored original: " + rel);
                }
            }
            foreach (var l in lines.Where(l => l.StartsWith("D|")).Reverse())
            {
                string p = P.Os(GamePath, l.Substring(2));
                try { if (Directory.Exists(p) && !Directory.EnumerateFileSystemEntries(p).Any()) Directory.Delete(p); } catch { }
            }
            File.Delete(ManifestPath);
            if (File.Exists(LastDeployPath)) File.Delete(LastDeployPath);
            DeployedStateDirty = true;
            log.Add("Purged " + removed + " deployed file(s).");
            return log;
        }

        // ---------------------------------------------------------------- UE4SS
        public string Ue4ssState()
        {
            if (!GamePathValid) return "unknown";
            bool dll = File.Exists(P.Os(GamePath, GameInfo.Ue4ssProxy));
            bool off = File.Exists(P.Os(GamePath, GameInfo.Ue4ssProxy + ".disabled"));
            bool dir = Directory.Exists(P.Os(GamePath, GameInfo.Ue4ssDir));
            if (dll && dir) return "enabled";
            if (off && dir) return "disabled";
            return "missing";
        }
        public void SetUe4ss(bool on)
        {
            string a = P.Os(GamePath, GameInfo.Ue4ssProxy), b = a + ".disabled";
            if (on && File.Exists(b) && !File.Exists(a)) File.Move(b, a);
            if (!on && File.Exists(a)) { if (File.Exists(b)) File.Delete(b); File.Move(a, b); }
        }

        // ---------------------------------------------------------------- game detection
        public static string DetectGamePath(IEnumerable<string> steamRoots)
        {
            foreach (var steam in steamRoots.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var libs = new List<string> { steam };
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match mt in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(mt.Groups[1].Value.Replace("\\\\", "\\"));
                foreach (var lib in libs)
                {
                    string g = Path.Combine(lib, "steamapps", "common", GameInfo.FolderName);
                    if (File.Exists(P.Os(g, GameInfo.Exe))) return g;
                }
            }
            return "";
        }
    }

    public class UnknownLayoutException : Exception
    {
        public InstallPlan Plan;
        public UnknownLayoutException(InstallPlan p) : base("Couldn't tell where this mod's files go.") { Plan = p; }
    }
}
