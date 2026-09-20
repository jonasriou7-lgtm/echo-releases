using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace EchoUniversalLauncher
{
    internal static class Program
    {
        internal const string LauncherVersion = "1.0.0";
        internal const string ManifestUrl = "https://github.com/jonasriou7-lgtm/echo-releases/releases/latest/download/echo-update.json";
        internal static readonly string EchoRoot = @"C:\echo";
        internal static readonly string ProgramDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Echo"
        );

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex mutex = new Mutex(true, "EchoUniversalLauncherMutex", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Écho est déjà en cours de lancement.", "Écho",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (args != null && args.Length >= 2 && args[0] == "--self-test")
                {
                    SelfTest(args[1]);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LauncherForm(args ?? new string[0]));
            }
        }

        private static void SelfTest(string output)
        {
            try
            {
                string test = "echo-launcher-self-test";
                string temp = Path.GetTempFileName();
                File.WriteAllText(temp, test, new UTF8Encoding(false));
                string hash = LauncherForm.Sha256(temp);
                File.Delete(temp);

                if (ManifestUrl.IndexOf("github.com/jonasriou7-lgtm/echo-releases/", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("Manifest URL invalide.");
                if (hash.Length != 64)
                    throw new InvalidOperationException("SHA-256 invalide.");

                File.WriteAllText(output, "SELFTEST_OK|" + LauncherVersion + "|" + hash, new UTF8Encoding(false));
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(output, "SELFTEST_FAIL|" + ex.Message, new UTF8Encoding(false)); } catch { }
                Environment.Exit(20);
            }
        }
    }

    internal sealed class LauncherForm : Form
    {
        private readonly string[] launchArgs;
        private readonly Label title = new Label();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly TextBox logBox = new TextBox();
        private readonly Button closeButton = new Button();

        private string logFile;

        private static readonly string[] ProtectedTopDirs = new string[]
        {
            "data", "memory", "generations", "documents_inbox", "documents_output",
            "_ECHO_BACKUPS", "_ECHO_APPLIED_DROPINS", "_ECHO_DROPINS"
        };

        private static readonly string[] ProtectedFiles = new string[]
        {
            "localisation_token.txt", "echo_mobile_token.txt",
            "memoire_echo.json", "memoire_jarvis.json",
            "CONFIGURATION_PRIVEE_A_CONSERVER.txt"
        };

        internal LauncherForm(string[] args)
        {
            launchArgs = args ?? new string[0];

            Text = "Écho";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 820;
            Height = 560;
            MinimumSize = new Size(700, 480);
            BackColor = Color.FromArgb(8, 12, 18);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10f);

            title.Text = "ÉCHO";
            title.Font = new Font("Segoe UI Semilight", 28f);
            title.ForeColor = Color.FromArgb(220, 238, 255);
            title.AutoSize = true;
            title.Left = 28;
            title.Top = 20;
            Controls.Add(title);

            status.Text = "Initialisation…";
            status.Font = new Font("Segoe UI", 11f);
            status.ForeColor = Color.FromArgb(130, 206, 255);
            status.AutoSize = true;
            status.Left = 31;
            status.Top = 72;
            Controls.Add(status);

            progress.Left = 30;
            progress.Top = 108;
            progress.Width = 742;
            progress.Height = 16;
            progress.Minimum = 0;
            progress.Maximum = 100;
            progress.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            Controls.Add(progress);

            logBox.Left = 30;
            logBox.Top = 146;
            logBox.Width = 742;
            logBox.Height = 316;
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BackColor = Color.FromArgb(13, 18, 26);
            logBox.ForeColor = Color.FromArgb(216, 225, 236);
            logBox.BorderStyle = BorderStyle.FixedSingle;
            logBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(logBox);

            closeButton.Text = "Fermer";
            closeButton.Left = 662;
            closeButton.Top = 478;
            closeButton.Width = 110;
            closeButton.Height = 34;
            closeButton.Enabled = false;
            closeButton.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            Directory.CreateDirectory(Program.ProgramDataRoot);
            string logs = Path.Combine(Program.ProgramDataRoot, "logs");
            Directory.CreateDirectory(logs);
            logFile = Path.Combine(logs, "launcher-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");

            Shown += delegate
            {
                Task.Factory.StartNew((Action)RunMainFlow);
            };
        }

        private void RunMainFlow()
        {
            try
            {
                UiStatus("Vérification d'Écho…", 5);
                Log("Echo.exe " + Program.LauncherVersion);
                Log("Canal : " + Program.ManifestUrl);

                ManifestInfo latest = null;
                Exception onlineError = null;

                try
                {
                    latest = FetchManifest();
                    Log("[OK] Version en ligne : " + latest.Version);
                }
                catch (Exception ex)
                {
                    onlineError = ex;
                    Log("[INFO] Canal GitHub indisponible : " + ex.Message);
                }

                string installed = ReadInstalledVersion();
                bool hasLocal = File.Exists(Path.Combine(Program.EchoRoot, "interface.py"));

                if (latest == null)
                {
                    if (hasLocal)
                    {
                        UiStatus("Hors ligne — lancement de la version locale", 85);
                        Log("[OK] Version locale : " + (String.IsNullOrWhiteSpace(installed) ? "inconnue" : installed));
                        LaunchEcho();
                        FinishAndClose();
                        return;
                    }

                    throw new InvalidOperationException(
                        "Premier lancement impossible sans connexion Internet. " +
                        (onlineError != null ? onlineError.Message : "")
                    );
                }

                bool repair = HasArg("--repair");
                bool updateNeeded = repair || !hasLocal || IsNewer(latest.Version, installed);

                if (updateNeeded)
                {
                    if (!IsAdministrator())
                    {
                        UiStatus("Autorisation Windows nécessaire…", 10);
                        Log("[INFO] Installation/mise à jour : demande des droits administrateur.");
                        RelaunchElevated();
                        return;
                    }

                    UiStatus(hasLocal ? "Mise à jour d'Écho…" : "Premier lancement — installation d'Écho…", 12);
                    InstallOrUpdate(latest);
                    installed = latest.Version;
                }
                else
                {
                    Log("[OK] Écho est déjà à jour : " + installed);
                    UiStatus("Écho est à jour", 82);
                }

                UiStatus("Lancement d'Écho…", 92);
                LaunchEcho();
                FinishAndClose();
            }
            catch (Exception ex)
            {
                Log("[ERREUR] " + ex.ToString());
                UiError(ex.Message);
            }
        }

        private bool HasArg(string wanted)
        {
            foreach (string a in launchArgs)
                if (String.Equals(a, wanted, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private void InstallOrUpdate(ManifestInfo latest)
        {
            UiStatus("Vérification des prérequis…", 15);

            string python = EnsurePython();
            Log("[OK] Python : " + python);

            NodeInfo node = EnsureNode();
            Log("[OK] Node : " + node.NodeExe);
            Log("[OK] npm-cli.js : " + node.NpmCliJs);

            string ollama = EnsureOllama();
            Log("[OK] Ollama : " + ollama);

            string temp = Path.Combine(Path.GetTempPath(), "echo-universal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);

            BackupPlan plan = null;
            string oldNodeModules = null;

            try
            {
                string zip = Path.Combine(temp, "EchoApp.zip");
                UiStatus("Téléchargement d'Écho " + latest.Version + "…", 22);
                DownloadFile(latest.PackageUrl, zip, latest.PackageSize, 22, 42);

                string actual = Sha256(zip);
                if (!String.Equals(actual, latest.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "SHA-256 incorrect. Le package ne sera pas installé."
                    );
                Log("[OK] SHA-256 validé.");

                string extracted = Path.Combine(temp, "extracted");
                Directory.CreateDirectory(extracted);
                UiStatus("Validation du package…", 45);
                SafeExtract(zip, extracted);

                string payload = Path.Combine(extracted, "payload");
                ValidatePayload(payload);
                Log("[OK] Package complet et sûr.");

                StopEchoProcesses();

                UiStatus("Préparation de la mise à jour…", 50);
                oldNodeModules = QuarantineNodeModules();

                Directory.CreateDirectory(Program.EchoRoot);
                string backups = Path.Combine(Program.ProgramDataRoot, "backups",
                    "update-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(backups);

                plan = PrepareBackup(payload, backups);

                UiStatus("Installation des fichiers…", 56);
                CopyPayload(payload);
                Log("[OK] Fichiers Écho installés.");

                UiStatus("Installation des dépendances…", 64);
                InstallNodeDependencies(node);

                UiStatus("Vérification de l'interface…", 70);
                RunProcess(
                    node.NodeExe,
                    Quote(node.NpmCliJs) + " run build",
                    Path.Combine(Program.EchoRoot, "echo_desktop_ui"),
                    3600,
                    true,
                    BuildNpmEnvironment(node.NodeExe)
                );
                Log("[OK] Build interface validé.");

                UiStatus("Dépendances Python…", 74);
                InstallPythonDependencies(python);
                ValidatePython(python);

                File.WriteAllText(
                    Path.Combine(Program.EchoRoot, "VERSION"),
                    latest.Version + Environment.NewLine,
                    new UTF8Encoding(false)
                );

                UiStatus("Vérification du modèle IA…", 80);
                EnsureOllamaModel(ollama);

                if (!String.IsNullOrWhiteSpace(oldNodeModules) && Directory.Exists(oldNodeModules))
                {
                    try { Directory.Delete(oldNodeModules, true); }
                    catch { Log("[INFO] Ancien node_modules conservé en sauvegarde : " + oldNodeModules); }
                }

                WriteState(latest.Version);
                Log("[OK] Écho " + latest.Version + " installé.");
            }
            catch
            {
                Log("[ROLLBACK] Restauration de la version précédente…");
                try { StopEchoProcesses(); } catch { }

                try
                {
                    string currentNm = Path.Combine(Program.EchoRoot, "echo_desktop_ui", "node_modules");
                    if (Directory.Exists(currentNm))
                        DeleteDirectoryRobust(currentNm);
                }
                catch { }

                if (plan != null)
                {
                    try { RollbackFiles(plan); }
                    catch (Exception rex) { Log("[ROLLBACK][ERREUR] " + rex.Message); }
                }

                if (!String.IsNullOrWhiteSpace(oldNodeModules) && Directory.Exists(oldNodeModules))
                {
                    try
                    {
                        string nm = Path.Combine(Program.EchoRoot, "echo_desktop_ui", "node_modules");
                        Directory.CreateDirectory(Path.GetDirectoryName(nm));
                        if (!Directory.Exists(nm))
                            Directory.Move(oldNodeModules, nm);
                        Log("[ROLLBACK] Ancien node_modules restauré.");
                    }
                    catch (Exception rex)
                    {
                        Log("[ROLLBACK][ERREUR node_modules] " + rex.Message);
                    }
                }

                throw;
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }
        }

        private ManifestInfo FetchManifest()
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "EchoUniversalLauncher/" + Program.LauncherVersion;
                wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                string url = Program.ManifestUrl + "?launcher=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string json = wc.DownloadString(url);

                JavaScriptSerializer js = new JavaScriptSerializer();
                Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(json);
                if (root == null)
                    throw new InvalidOperationException("Manifest JSON vide.");

                string product = GetString(root, "product");
                if (!String.Equals(product, "echo-desktop", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Produit manifest inattendu.");

                string version = GetString(root, "version");
                Dictionary<string, object> package = GetDict(root, "package");

                string packageUrl = GetString(package, "url");
                if (!packageUrl.StartsWith(
                    "https://github.com/jonasriou7-lgtm/echo-releases/releases/download/",
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("URL package non officielle.");

                string sha = GetString(package, "sha256").ToLowerInvariant();
                if (sha.Length != 64)
                    throw new InvalidOperationException("SHA-256 manifest invalide.");

                long size = Convert.ToInt64(package["size"]);
                if (size <= 0)
                    throw new InvalidOperationException("Taille package invalide.");

                return new ManifestInfo(version, packageUrl, sha, size);
            }
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
                throw new InvalidOperationException("Champ manifest absent : " + key);
            return Convert.ToString(v);
        }

        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null)
                throw new InvalidOperationException("Objet manifest absent : " + key);
            Dictionary<string, object> result = v as Dictionary<string, object>;
            if (result == null)
                throw new InvalidOperationException("Objet manifest invalide : " + key);
            return result;
        }

        private void DownloadFile(string url, string dst, long expectedSize, int startProgress, int endProgress)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "EchoUniversalLauncher/" + Program.LauncherVersion;
            request.AllowAutoRedirect = true;
            request.Timeout = 60000;
            request.ReadWriteTimeout = 60000;

            using (WebResponse response = request.GetResponse())
            using (Stream input = response.GetResponseStream())
            using (FileStream output = File.Create(dst))
            {
                long total = 0;
                byte[] buffer = new byte[1024 * 1024];

                while (true)
                {
                    int read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    output.Write(buffer, 0, read);
                    total += read;

                    if (expectedSize > 0)
                    {
                        double ratio = Math.Min(1.0, (double)total / (double)expectedSize);
                        int p = startProgress + (int)((endProgress - startProgress) * ratio);
                        UiProgress(p);
                    }
                }
            }

            long actual = new FileInfo(dst).Length;
            if (expectedSize > 0 && actual != expectedSize)
                throw new InvalidOperationException(
                    "Taille téléchargée incorrecte : " + actual + " / " + expectedSize
                );
        }

        internal static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] bytes = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder();
                foreach (byte b in bytes)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private void SafeExtract(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
                root += Path.DirectorySeparatorChar;

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string combined = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    string full = Path.GetFullPath(combined);

                    if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Archive ZIP dangereuse : " + entry.FullName);

                    if (String.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(full);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(full));
                        entry.ExtractToFile(full, true);
                    }
                }
            }
        }

        private void ValidatePayload(string payload)
        {
            string[] required = new string[]
            {
                "interface.py",
                Path.Combine("echo_desktop_ui", "package.json"),
                Path.Combine("echo_desktop_ui", "backend", "echo_desktop_server.py"),
                Path.Combine("echo_desktop_ui", "backend", "core_adapter.py")
            };

            foreach (string rel in required)
            {
                if (!File.Exists(Path.Combine(payload, rel)))
                    throw new InvalidOperationException("Package incomplet : " + rel);
            }

            string nodeModules = Path.Combine(payload, "echo_desktop_ui", "node_modules");
            if (Directory.Exists(nodeModules))
                throw new InvalidOperationException("Le package public ne doit pas contenir node_modules.");

            foreach (string exe in Directory.GetFiles(payload, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(exe).ToLowerInvariant();
                if (name.StartsWith("echoinstaller") || name.StartsWith("echoupdater"))
                    throw new InvalidOperationException("Installateur/updater interdit dans le package : " + name);
            }
        }

        private BackupPlan PrepareBackup(string payload, string backupRoot)
        {
            BackupPlan plan = new BackupPlan(backupRoot);

            foreach (string src in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            {
                string rel = RelativePath(payload, src);
                if (IsProtected(rel))
                    continue;

                string dst = Path.Combine(Program.EchoRoot, rel);
                if (File.Exists(dst))
                {
                    string backup = Path.Combine(backupRoot, "files", rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(dst, backup, true);
                    plan.Overwritten.Add(rel);
                }
                else
                {
                    plan.Created.Add(rel);
                }
            }

            return plan;
        }

        private void CopyPayload(string payload)
        {
            foreach (string src in Directory.GetFiles(payload, "*", SearchOption.AllDirectories))
            {
                string rel = RelativePath(payload, src);
                if (IsProtected(rel))
                {
                    Log("[INFO] Donnée protégée conservée : " + rel);
                    continue;
                }

                string dst = Path.Combine(Program.EchoRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));

                string temp = dst + ".echo-installing";
                File.Copy(src, temp, true);
                if (File.Exists(dst))
                    File.Delete(dst);
                File.Move(temp, dst);
            }
        }

        private void RollbackFiles(BackupPlan plan)
        {
            for (int i = plan.Created.Count - 1; i >= 0; i--)
            {
                string path = Path.Combine(Program.EchoRoot, plan.Created[i]);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            foreach (string rel in plan.Overwritten)
            {
                string backup = Path.Combine(plan.Root, "files", rel);
                string dst = Path.Combine(Program.EchoRoot, rel);
                if (!File.Exists(backup))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(backup, dst, true);
            }
        }

        private static bool IsProtected(string relative)
        {
            string normalized = relative.Replace('/', '\\').TrimStart('\\');
            string first = normalized;
            int idx = normalized.IndexOf('\\');
            if (idx >= 0)
                first = normalized.Substring(0, idx);

            foreach (string d in ProtectedTopDirs)
                if (String.Equals(first, d, StringComparison.OrdinalIgnoreCase))
                    return true;

            string name = Path.GetFileName(normalized);
            foreach (string f in ProtectedFiles)
                if (String.Equals(name, f, StringComparison.OrdinalIgnoreCase))
                    return true;

            if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        private string QuarantineNodeModules()
        {
            string nm = Path.Combine(Program.EchoRoot, "echo_desktop_ui", "node_modules");
            if (!Directory.Exists(nm))
                return null;

            StopEchoProcesses();

            string root = Path.Combine(Program.ProgramDataRoot, "backups",
                "node-modules-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "node_modules");

            Exception last = null;
            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    Directory.Move(nm, target);
                    Log("[OK] Ancien node_modules sauvegardé : " + target);
                    return target;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Log("[INFO] node_modules verrouillé, tentative " + i + "/10 : " + ex.Message);
                    if (i == 3 || i == 6 || i == 9)
                        StopEchoProcesses();
                    Thread.Sleep(1000);
                }
            }

            throw new InvalidOperationException(
                "Impossible de libérer node_modules. Redémarre Windows puis relance Echo.exe. " +
                (last != null ? last.Message : "")
            );
        }

        private void InstallNodeDependencies(NodeInfo node)
        {
            string ui = Path.Combine(Program.EchoRoot, "echo_desktop_ui");
            string nm = Path.Combine(ui, "node_modules");

            if (Directory.Exists(nm))
                DeleteDirectoryRobust(nm);

            Dictionary<string, string> env = BuildNpmEnvironment(node.NodeExe);

            UiStatus("npm install…", 65);
            RunProcess(
                node.NodeExe,
                Quote(node.NpmCliJs) + " install --legacy-peer-deps --no-audit --no-fund",
                ui,
                3600,
                true,
                env
            );

            string vite = Path.Combine(ui, "node_modules", ".bin", "vite.cmd");
            if (!File.Exists(vite))
                throw new InvalidOperationException("npm install incomplet : vite.cmd absent.");

            Log("[OK] Dépendances Node installées.");
        }

        private Dictionary<string, string> BuildNpmEnvironment(string nodeExe)
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            List<string> parts = new List<string>();

            foreach (string raw in path.Split(';'))
            {
                string item = raw.Trim().Trim('"');
                if (String.IsNullOrWhiteSpace(item))
                    continue;

                string low = item.ToLowerInvariant().Replace('/', '\\');
                if (low.IndexOf("node_modules\\.bin") >= 0)
                    continue;
                if (low == Program.EchoRoot.ToLowerInvariant() ||
                    low.StartsWith(Program.EchoRoot.ToLowerInvariant() + "\\"))
                    continue;

                bool duplicate = false;
                foreach (string p in parts)
                    if (String.Equals(p, item, StringComparison.OrdinalIgnoreCase))
                        duplicate = true;
                if (!duplicate)
                    parts.Add(item);
            }

            string nodeDir = Path.GetDirectoryName(nodeExe);
            parts.RemoveAll(delegate(string s) {
                return String.Equals(s, nodeDir, StringComparison.OrdinalIgnoreCase);
            });
            parts.Insert(0, nodeDir);

            env["PATH"] = String.Join(";", parts.ToArray());
            env["npm_config_prefix"] = "";
            env["NPM_CONFIG_PREFIX"] = "";
            return env;
        }

        private void InstallPythonDependencies(string python)
        {
            string[] candidates = new string[]
            {
                Path.Combine(Program.EchoRoot, "requirements.txt"),
                Path.Combine(Program.EchoRoot, "requirements_echo.txt"),
                Path.Combine(Program.EchoRoot, "requirements_echo_auto.txt")
            };

            string requirements = null;
            foreach (string p in candidates)
                if (File.Exists(p)) { requirements = p; break; }

            if (requirements == null)
            {
                Log("[INFO] Aucun fichier requirements détecté.");
                return;
            }

            RunProcess(
                python,
                "-m pip install --disable-pip-version-check -r " + Quote(requirements),
                Program.EchoRoot,
                3600,
                true,
                null
            );
            Log("[OK] Dépendances Python installées.");
        }

        private void ValidatePython(string python)
        {
            string[] files = new string[]
            {
                Path.Combine(Program.EchoRoot, "interface.py"),
                Path.Combine(Program.EchoRoot, "echo_desktop_ui", "backend", "echo_desktop_server.py"),
                Path.Combine(Program.EchoRoot, "echo_desktop_ui", "backend", "core_adapter.py")
            };

            foreach (string file in files)
            {
                RunProcess(
                    python,
                    "-m py_compile " + Quote(file),
                    Program.EchoRoot,
                    120,
                    true,
                    null
                );
            }
            Log("[OK] Tests Python critiques validés.");
        }

        private string EnsurePython()
        {
            string found = FindPython();
            if (found != null)
                return found;

            InstallWinget("Python.Python.3.13");
            found = FindPython();
            if (found == null)
                throw new InvalidOperationException("Python installé mais introuvable.");
            return found;
        }

        private string FindPython()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates = new string[]
            {
                Path.Combine(local, "Programs", "Python", "Python314", "python.exe"),
                Path.Combine(local, "Programs", "Python", "Python313", "python.exe"),
                Path.Combine(pf, "Python314", "python.exe"),
                Path.Combine(pf, "Python313", "python.exe")
            };

            foreach (string c in candidates)
                if (File.Exists(c))
                    return c;

            foreach (string c in Where("python.exe"))
            {
                if (c.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (File.Exists(c))
                    return c;
            }

            return null;
        }

        private NodeInfo EnsureNode()
        {
            NodeInfo n = FindNode();
            if (n != null)
                return n;

            InstallWinget("OpenJS.NodeJS.LTS");
            n = FindNode();
            if (n == null)
                throw new InvalidOperationException("Node.js installé mais introuvable.");
            return n;
        }

        private NodeInfo FindNode()
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            List<string> nodes = new List<string>();
            nodes.Add(Path.Combine(pf, "nodejs", "node.exe"));
            nodes.Add(Path.Combine(pf86, "nodejs", "node.exe"));
            nodes.AddRange(Where("node.exe"));

            foreach (string node in nodes)
            {
                if (!File.Exists(node))
                    continue;

                string cli = Path.Combine(
                    Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npm-cli.js"
                );
                if (File.Exists(cli))
                    return new NodeInfo(node, cli);
            }

            return null;
        }

        private string EnsureOllama()
        {
            string o = FindOllama();
            if (o != null)
                return o;

            InstallWinget("Ollama.Ollama");
            o = FindOllama();
            if (o == null)
                throw new InvalidOperationException("Ollama installé mais introuvable.");
            return o;
        }

        private string FindOllama()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string[] candidates = new string[]
            {
                Path.Combine(local, "Programs", "Ollama", "ollama.exe"),
                Path.Combine(pf, "Ollama", "ollama.exe")
            };

            foreach (string c in candidates)
                if (File.Exists(c))
                    return c;

            foreach (string c in Where("ollama.exe"))
                if (File.Exists(c))
                    return c;

            return null;
        }

        private void InstallWinget(string packageId)
        {
            string winget = FindWinget();
            if (winget == null)
                throw new InvalidOperationException(
                    "winget est introuvable. Installe 'App Installer' depuis Microsoft."
                );

            Log("[INFO] Installation automatique : " + packageId);
            RunProcess(
                winget,
                "install --id " + packageId +
                " -e --silent --accept-package-agreements --accept-source-agreements",
                null,
                1800,
                true,
                null
            );
        }

        private string FindWinget()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string direct = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(direct))
                return direct;

            foreach (string c in Where("winget.exe"))
                if (File.Exists(c))
                    return c;

            return null;
        }

        private List<string> Where(string exe)
        {
            List<string> result = new List<string>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "where.exe";
                psi.Arguments = exe;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process p = Process.Start(psi))
                {
                    string text = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15000);

                    foreach (string raw in text.Split(new char[] { '\r', '\n' },
                        StringSplitOptions.RemoveEmptyEntries))
                    {
                        string line = raw.Trim();
                        if (!String.IsNullOrWhiteSpace(line))
                            result.Add(line);
                    }
                }
            }
            catch { }
            return result;
        }

        private void EnsureOllamaModel(string ollama)
        {
            ProcessResult list = RunProcess(
                ollama, "list", null, 60, false, null
            );

            if (list.ExitCode != 0)
            {
                try
                {
                    ProcessStartInfo serve = new ProcessStartInfo();
                    serve.FileName = ollama;
                    serve.Arguments = "serve";
                    serve.UseShellExecute = false;
                    serve.CreateNoWindow = true;
                    Process.Start(serve);
                    Thread.Sleep(3000);
                }
                catch { }

                list = RunProcess(ollama, "list", null, 60, false, null);
            }

            string model = "qwen3.5:9b-q4_K_M";
            if ((list.Output ?? "").IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Log("[OK] Modèle Ollama déjà présent : " + model);
                return;
            }

            UiStatus("Premier lancement — téléchargement du modèle IA…", 84);
            Log("[INFO] Téléchargement du modèle " + model + ". Cela peut prendre du temps.");
            RunProcess(ollama, "pull " + model, null, 7200, true, null);
            Log("[OK] Modèle Ollama installé.");
        }

        private ProcessResult RunProcess(
            string file,
            string arguments,
            string workingDirectory,
            int timeoutSeconds,
            bool failOnError,
            Dictionary<string, string> environment
        )
        {
            Log("[CMD] " + file + " " + arguments);

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = file;
            psi.Arguments = arguments;
            psi.WorkingDirectory = String.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory : workingDirectory;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            if (environment != null)
            {
                foreach (KeyValuePair<string, string> kv in environment)
                {
                    if (String.IsNullOrEmpty(kv.Value))
                        psi.EnvironmentVariables.Remove(kv.Key);
                    else
                        psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            StringBuilder output = new StringBuilder();

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (output) output.AppendLine(e.Data);
                        Log("    " + e.Data);
                    }
                };
                p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null)
                    {
                        lock (output) output.AppendLine(e.Data);
                        Log("    " + e.Data);
                    }
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                if (!p.WaitForExit(timeoutSeconds * 1000))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(Path.GetFileName(file) + " a dépassé le délai maximum.");
                }

                p.WaitForExit();

                ProcessResult result = new ProcessResult(p.ExitCode, output.ToString());
                if (failOnError && result.ExitCode != 0)
                    throw new InvalidOperationException(
                        Path.GetFileName(file) + " a échoué (code " + result.ExitCode + ")."
                    );
                return result;
            }
        }

        private void StopEchoProcesses()
        {
            try
            {
                string script =
                    "$self=" + Process.GetCurrentProcess().Id + ";" +
                    "$p=Get-CimInstance Win32_Process|?{" +
                    "$_.ProcessId-ne$self-and $_.ProcessId-ne0-and $_.ProcessId-ne4-and(" +
                    "($_.CommandLine-and($_.CommandLine-like'*C:\\echo\\*'-or$_.CommandLine-like'*echo_desktop_ui*'-or$_.CommandLine-like'*interface.py*'))" +
                    "-or($_.ExecutablePath-and$_.ExecutablePath-like'C:\\echo\\*'))};" +
                    "$p|%{try{Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop}catch{}}";

                string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                RunProcess(
                    "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                    null,
                    60,
                    false,
                    null
                );
                Thread.Sleep(1200);
            }
            catch (Exception ex)
            {
                Log("[INFO] Arrêt processus Écho : " + ex.Message);
            }
        }

        private void DeleteDirectoryRobust(string path)
        {
            if (!Directory.Exists(path))
                return;

            Exception last = null;
            for (int attempt = 1; attempt <= 8; attempt++)
            {
                try
                {
                    foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    }

                    Directory.Delete(path, true);
                    if (!Directory.Exists(path))
                        return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 3 || attempt == 6)
                        StopEchoProcesses();
                    Thread.Sleep(600);
                }
            }

            throw new IOException("Impossible de supprimer " + path + ". " +
                (last != null ? last.Message : ""));
        }

        private void LaunchEcho()
        {
            string python = FindPython();
            if (python == null)
                throw new InvalidOperationException("Python est introuvable pour lancer Écho.");

            string entry = Path.Combine(Program.EchoRoot, "interface.py");
            if (!File.Exists(entry))
                throw new InvalidOperationException("interface.py est absent.");

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = python;
            psi.Arguments = Quote(entry);
            psi.WorkingDirectory = Program.EchoRoot;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["PYTHONUTF8"] = "1";
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            Process.Start(psi);

            Log("[OK] Écho lancé.");
        }

        private void FinishAndClose()
        {
            UiStatus("Écho est lancé", 100);
            Thread.Sleep(900);
            if (IsHandleCreated)
                BeginInvoke((Action)delegate { Close(); });
        }

        private string ReadInstalledVersion()
        {
            try
            {
                string version = Path.Combine(Program.EchoRoot, "VERSION");
                if (File.Exists(version))
                    return File.ReadAllText(version).Trim();
            }
            catch { }
            return null;
        }

        private void WriteState(string version)
        {
            try
            {
                Directory.CreateDirectory(Program.ProgramDataRoot);
                File.WriteAllText(
                    Path.Combine(Program.ProgramDataRoot, "state.txt"),
                    "version=" + version + Environment.NewLine +
                    "launcher=" + Program.LauncherVersion + Environment.NewLine +
                    "updated=" + DateTime.UtcNow.ToString("o") + Environment.NewLine,
                    new UTF8Encoding(false)
                );
            }
            catch { }
        }

        private bool IsNewer(string online, string installed)
        {
            if (String.IsNullOrWhiteSpace(installed))
                return true;

            Version a;
            Version b;
            if (Version.TryParse(online, out a) && Version.TryParse(installed, out b))
                return a > b;

            return !String.Equals(online, installed, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAdministrator()
        {
            try
            {
                System.Security.Principal.WindowsIdentity identity =
                    System.Security.Principal.WindowsIdentity.GetCurrent();
                System.Security.Principal.WindowsPrincipal principal =
                    new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator
                );
            }
            catch { return false; }
        }

        private void RelaunchElevated()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = "--elevated";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                Process.Start(psi);

                if (IsHandleCreated)
                    BeginInvoke((Action)delegate { Close(); });
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "L'autorisation administrateur a été refusée. " + ex.Message
                );
            }
        }

        private static string RelativePath(string root, string full)
        {
            string r = Path.GetFullPath(root);
            if (!r.EndsWith(Path.DirectorySeparatorChar.ToString()))
                r += Path.DirectorySeparatorChar;
            string f = Path.GetFullPath(full);

            Uri rootUri = new Uri(r);
            Uri fileUri = new Uri(f);
            return Uri.UnescapeDataString(
                rootUri.MakeRelativeUri(fileUri).ToString()
            ).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string Quote(string s)
        {
            return "\"" + s.Replace("\"", "\\\"") + "\"";
        }

        private void Log(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;

            try
            {
                File.AppendAllText(logFile, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }

            if (IsHandleCreated)
            {
                BeginInvoke((Action)delegate
                {
                    logBox.AppendText(line + Environment.NewLine);
                    logBox.SelectionStart = logBox.TextLength;
                    logBox.ScrollToCaret();
                });
            }
        }

        private void UiStatus(string text, int value)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                status.Text = text;
                progress.Value = Math.Max(progress.Minimum, Math.Min(progress.Maximum, value));
            });
        }

        private void UiProgress(int value)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                progress.Value = Math.Max(progress.Minimum, Math.Min(progress.Maximum, value));
            });
        }

        private void UiError(string message)
        {
            if (!IsHandleCreated) return;
            BeginInvoke((Action)delegate
            {
                status.Text = "Erreur";
                status.ForeColor = Color.FromArgb(255, 130, 130);
                closeButton.Enabled = true;
                MessageBox.Show(
                    message + Environment.NewLine + Environment.NewLine +
                    "Journal : " + logFile,
                    "Écho — erreur",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            });
        }

        private sealed class ManifestInfo
        {
            internal readonly string Version;
            internal readonly string PackageUrl;
            internal readonly string PackageSha256;
            internal readonly long PackageSize;

            internal ManifestInfo(string version, string url, string sha, long size)
            {
                Version = version;
                PackageUrl = url;
                PackageSha256 = sha;
                PackageSize = size;
            }
        }

        private sealed class NodeInfo
        {
            internal readonly string NodeExe;
            internal readonly string NpmCliJs;

            internal NodeInfo(string nodeExe, string npmCliJs)
            {
                NodeExe = nodeExe;
                NpmCliJs = npmCliJs;
            }
        }

        private sealed class BackupPlan
        {
            internal readonly string Root;
            internal readonly List<string> Overwritten = new List<string>();
            internal readonly List<string> Created = new List<string>();

            internal BackupPlan(string root)
            {
                Root = root;
            }
        }

        private sealed class ProcessResult
        {
            internal readonly int ExitCode;
            internal readonly string Output;

            internal ProcessResult(int code, string output)
            {
                ExitCode = code;
                Output = output;
            }
        }
    }
}
