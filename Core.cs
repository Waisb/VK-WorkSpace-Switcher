using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;

namespace VkWorkspaceSwitcher
{
    [DataContract]
    public sealed class Profile
    {
        [DataMember(Name = "id", Order = 1)] public string Id;
        [DataMember(Name = "name", Order = 2)] public string Name;
        [DataMember(Name = "headerColor", Order = 3, EmitDefaultValue = false)] public string HeaderColor;
    }

    [DataContract]
    public sealed class LauncherState
    {
        [DataMember(Name = "version", Order = 1)] public int Version;
        [DataMember(Name = "executablePath", Order = 2)] public string ExecutablePath;
        [DataMember(Name = "profiles", Order = 3)] public List<Profile> Profiles;
    }

    /// <summary>
    /// Stores profile labels and prepares isolated child environments.
    /// Explicit removal affects only an additional profile's isolated directory.
    /// </summary>
    public sealed class ProfileLauncher
    {
        public const string DefaultHeaderColor = "#0077FF";

        private readonly string root;
        private readonly string defaultExecutable;
        private readonly string originalHome;
        private readonly string originalData;
        private LauncherState state;

        public string Root { get { return root; } }
        public LauncherState State
        {
            get
            {
                RequireInitialized();
                return Clone(state);
            }
        }

        public ProfileLauncher(string root, string defaultExecutable)
        {
            this.root = NormalizeAbsolute(root, "Папка профилей");
            this.defaultExecutable = NormalizeExecutable(defaultExecutable, false);
            originalHome = NormalizeAbsolute(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Папка пользователя");
            originalData = NormalizeAbsolute(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VK WorkSpace"), "Папка VK WorkSpace");
            if (SamePath(this.root, Path.GetPathRoot(this.root)) ||
                SamePath(this.root, originalHome) || IsInside(originalHome, this.root) ||
                SamePath(this.root, originalData) || IsInside(this.root, originalData) || IsInside(originalData, this.root))
                throw new ArgumentException("Хранилище профилей должно находиться в отдельной папке вне папки данных VK WorkSpace.");
            AssertNoReparsePoints(this.root);
        }

        public void Initialize()
        {
            using (AcquireLock())
            {
                if (File.Exists(ConfigPath))
                    state = ReadState();
                else
                {
                    state = new LauncherState
                    {
                        Version = 1,
                        ExecutablePath = defaultExecutable,
                        Profiles = new List<Profile> { new Profile { Id = "default", Name = "Основной" } }
                    };
                    WriteState(state);
                }
            }
        }

        public Profile AddProfile(string name)
        {
            RequireInitialized();
            name = ValidateName(name);
            using (AcquireLock())
            {
                LauncherState updated = ReadState();
                if (updated.Profiles.Count >= 256)
                    throw new InvalidOperationException("Достигнут предел в 256 профилей.");
                EnsureUniqueName(updated, name, null);
                Profile profile = new Profile { Id = Guid.NewGuid().ToString("N"), Name = name };
                updated.Profiles.Add(profile);
                WriteState(updated);
                state = updated;
                return new Profile { Id = profile.Id, Name = profile.Name, HeaderColor = profile.HeaderColor };
            }
        }

        public void RenameProfile(string id, string name)
        {
            RequireInitialized();
            ValidateId(id);
            name = ValidateName(name);
            using (AcquireLock())
            {
                LauncherState updated = ReadState();
                Profile profile = FindProfile(updated, id);
                EnsureUniqueName(updated, name, id);
                profile.Name = name;
                WriteState(updated);
                state = updated;
            }
        }

        internal string RemoveProfile(string id)
        {
            RequireInitialized();
            ValidateId(id);
            if (id == "default")
                throw new InvalidOperationException("Основной профиль нельзя удалить из лаунчера.");
            using (AcquireLock())
            {
                LauncherState updated = ReadState();
                Profile profile = FindProfile(updated, id);
                string home = IsolatedHome(id);
                string staged = null;
                if (File.Exists(home))
                    throw new IOException("Вместо папки профиля обнаружен файл. Удаление отменено.");
                if (Directory.Exists(home))
                {
                    ValidateRemovalTree(home);
                    staged = Path.Combine(Path.GetDirectoryName(home), ".deleted-" + id + "-" + Guid.NewGuid().ToString("N"));
                    AssertNoReparsePoints(staged);
                    Directory.Move(home, staged);
                }
                updated.Profiles.Remove(profile);
                try { WriteState(updated); }
                catch (Exception writeError)
                {
                    if (staged != null)
                    {
                        try { Directory.Move(staged, home); }
                        catch (Exception restoreError)
                        {
                            throw new IOException("Не удалось сохранить удаление профиля. Его данные сохранены в папке: " + staged +
                                ". Не удалось вернуть папку на место: " + restoreError.Message, writeError);
                        }
                    }
                    throw;
                }
                state = updated;
                if (staged != null)
                {
                    try
                    {
                        // Recheck the entire moved tree before deleting any entries.
                        ValidateRemovalTree(staged);
                        DeleteRemovalTree(staged);
                    }
                    catch (Exception error)
                    {
                        return "Аккаунт удалён из лаунчера, но часть локальных файлов осталась в папке:\n" + staged +
                            "\n\nПричина: " + error.Message;
                    }
                }
                return null;
            }
        }

        private static void ValidateRemovalTree(string directory)
        {
            AssertNoReparsePoints(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Профиль содержит ссылку. Удаление отменено: " + entry);
                if ((attributes & FileAttributes.Directory) != 0)
                    ValidateRemovalTree(entry);
            }
        }

        private static void DeleteRemovalTree(string directory)
        {
            // Delete one verified entry at a time; never follow directory links.
            AssertNoReparsePoints(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Профиль содержит ссылку: " + entry);
                if ((attributes & FileAttributes.Directory) != 0)
                    DeleteRemovalTree(entry);
                else
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    File.Delete(entry);
                }
            }
            Directory.Delete(directory, false);
        }

        public void SetExecutable(string path)
        {
            RequireInitialized();
            path = NormalizeExecutable(path, true);
            using (AcquireLock())
            {
                LauncherState updated = ReadState();
                updated.ExecutablePath = path;
                WriteState(updated);
                state = updated;
            }
        }

        public void SetHeaderColor(string id, string color)
        {
            RequireInitialized();
            ValidateId(id);
            color = ValidateHeaderColor(color);
            using (AcquireLock())
            {
                LauncherState updated = ReadState();
                Profile profile = FindProfile(updated, id);
                profile.HeaderColor = color;
                WriteState(updated);
                state = updated;
            }
        }

        public static string EffectiveHeaderColor(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            return ValidateHeaderColor(profile.HeaderColor) ?? DefaultHeaderColor;
        }

        public string HomePath(Profile profile)
        {
            Profile registered = GetRegisteredProfile(profile);
            return registered.Id == "default" ? originalHome : IsolatedHome(registered.Id);
        }

        public string DataPath(Profile profile)
        {
            Profile registered = GetRegisteredProfile(profile);
            return registered.Id == "default" ? originalData : Path.Combine(IsolatedHome(registered.Id), "AppData", "Roaming", "VK WorkSpace");
        }

        public string ProfileEnvironmentTag(Profile profile)
        {
            Profile registered = GetRegisteredProfile(profile);
            return registered.Id == "default" ? Environment.GetEnvironmentVariable("USERNAME") : "vkws_" + registered.Id;
        }

        public ProcessStartInfo BuildStartInfo(Profile profile)
        {
            RequireInitialized();
            if (profile == null)
                throw new ArgumentNullException("profile");
            ValidateId(profile.Id);
            using (AcquireLock())
            {
                state = ReadState();
                Profile registered = FindProfile(state, profile.Id);
                string executable = NormalizeExecutable(state.ExecutablePath, true);
                ProcessStartInfo start = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    WindowStyle = ProcessWindowStyle.Normal
                };

                // Default profile uses the real Windows environment unchanged.
                if (registered.Id == "default")
                    return start;

                string home = IsolatedHome(registered.Id);
                string roaming = Path.Combine(home, "AppData", "Roaming");
                string local = Path.Combine(home, "AppData", "Local");
                string temporary = Path.Combine(local, "Temp");
                string[] folders = { home, roaming, local, temporary,
                    Path.Combine(home, "Downloads"), Path.Combine(home, "Documents"), Path.Combine(home, "Desktop"),
                    Path.Combine(roaming, "VK WorkSpace") };
                foreach (string folder in folders)
                {
                    AssertNoReparsePoints(folder);
                    Directory.CreateDirectory(folder);
                }
                start.EnvironmentVariables["USERPROFILE"] = home;
                start.EnvironmentVariables["USERNAME"] = "vkws_" + registered.Id;
                start.EnvironmentVariables["APPDATA"] = roaming;
                start.EnvironmentVariables["LOCALAPPDATA"] = local;
                start.EnvironmentVariables["TEMP"] = temporary;
                start.EnvironmentVariables["TMP"] = temporary;
                string drive = Path.GetPathRoot(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                start.EnvironmentVariables["HOMEDRIVE"] = drive;
                start.EnvironmentVariables["HOMEPATH"] = home.Substring(drive.Length);
                return start;
            }
        }

        public Process Launch(Profile profile)
        {
            return Process.Start(BuildStartInfo(profile));
        }

        private string ConfigPath { get { return Path.Combine(root, "config.json"); } }

        private string IsolatedHome(string id)
        {
            ValidateId(id);
            if (id == "default")
                throw new ArgumentException("Основной профиль не является изолированным.");
            string home = NormalizeAbsolute(Path.Combine(root, "profiles", id), "Папка профиля");
            if (!IsInside(home, root))
                throw new InvalidOperationException("Папка профиля выходит за границы хранилища.");
            AssertNoReparsePoints(home);
            return home;
        }

        private Profile GetRegisteredProfile(Profile profile)
        {
            RequireInitialized();
            if (profile == null)
                throw new ArgumentNullException("profile");
            ValidateId(profile.Id);
            return FindProfile(state, profile.Id);
        }

        private static Profile FindProfile(LauncherState value, string id)
        {
            Profile profile = value.Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
                throw new ArgumentException("Профиль не зарегистрирован в этом хранилище.");
            return profile;
        }

        private FileStream AcquireLock()
        {
            AssertNoReparsePoints(root);
            Directory.CreateDirectory(root);
            string lockPath = Path.Combine(root, ".config.lock");
            AssertNoReparsePoints(lockPath);
            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    if (timer.ElapsedMilliseconds >= 5000)
                        throw new IOException("Хранилище профилей занято другим экземпляром программы. Повторите попытку.");
                    Thread.Sleep(50);
                }
            }
        }

        private LauncherState ReadState()
        {
            AssertNoReparsePoints(ConfigPath);
            LauncherState value;
            using (FileStream input = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > 1024 * 1024)
                    throw new InvalidDataException("Файл конфигурации слишком большой.");
                try
                {
                    value = (LauncherState)new DataContractJsonSerializer(typeof(LauncherState)).ReadObject(input);
                }
                catch (SerializationException ex)
                {
                    throw new InvalidDataException("Не удалось прочитать конфигурацию профилей. Файл не был изменён.", ex);
                }
            }
            ValidateState(value);
            return value;
        }

        private void WriteState(LauncherState value)
        {
            ValidateState(value);
            AssertNoReparsePoints(ConfigPath);
            string temporary = Path.Combine(root, "config." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    new DataContractJsonSerializer(typeof(LauncherState)).WriteObject(output, value);
                    output.Flush(true);
                }
                if (File.Exists(ConfigPath))
                    File.Replace(temporary, ConfigPath, null);
                else
                    File.Move(temporary, ConfigPath);
            }
            finally
            {
                // Only a freshly generated metadata file can be removed here.
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        private static void ValidateState(LauncherState value)
        {
            if (value == null || value.Version != 1 || value.Profiles == null || value.Profiles.Count == 0 || value.Profiles.Count > 256)
                throw new InvalidDataException("Некорректная или неподдерживаемая конфигурация профилей.");
            value.ExecutablePath = NormalizeExecutable(value.ExecutablePath, false);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Profile profile in value.Profiles)
            {
                if (profile == null)
                    throw new InvalidDataException("Конфигурация содержит пустой профиль.");
                ValidateId(profile.Id);
                profile.Name = ValidateName(profile.Name);
                profile.HeaderColor = ValidateHeaderColor(profile.HeaderColor);
                if (!ids.Add(profile.Id) || !names.Add(profile.Name))
                    throw new InvalidDataException("Конфигурация содержит повторяющиеся профили.");
            }
            if (!ids.Contains("default"))
                throw new InvalidDataException("В конфигурации отсутствует основной профиль.");
        }

        private static void ValidateId(string id)
        {
            if (id == "default")
                return;
            Guid parsed;
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out parsed) || parsed.ToString("N") != id)
                throw new ArgumentException("Некорректный идентификатор профиля.");
        }

        private static string ValidateName(string name)
        {
            if (name == null)
                throw new ArgumentException("Укажите название профиля.");
            name = name.Trim();
            if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
                throw new ArgumentException("Название должно содержать от 1 до 80 символов без переводов строк.");
            return name;
        }

        private static string ValidateHeaderColor(string color)
        {
            if (color == null) return null;
            if (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(c =>
                !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))))
                throw new ArgumentException("Цвет шапки должен быть указан в формате #RRGGBB.");
            return color.ToUpperInvariant();
        }

        private static void EnsureUniqueName(LauncherState value, string name, string exceptId)
        {
            if (value.Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Профиль с таким названием уже существует.");
        }

        private static string NormalizeExecutable(string path, bool mustExist)
        {
            string full = NormalizeAbsolute(path, "Путь к приложению");
            if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Выберите исполняемый файл VK WorkSpace (.exe).");
            if (mustExist && !File.Exists(full))
                throw new FileNotFoundException("Не найден VK WorkSpace. Укажите путь к vkworkspace.exe.", full);
            return full;
        }

        private static string NormalizeAbsolute(string path, string label)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 ||
                !(path.StartsWith(@"\\", StringComparison.Ordinal) ||
                  (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))))
                throw new ArgumentException(label + ": требуется абсолютный путь.");
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                throw new ArgumentException(label + ": специальные пути устройств не поддерживаются.");
            string full = Path.GetFullPath(path);
            string volume = Path.GetPathRoot(full);
            return full.Length > volume.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        private static bool SamePath(string first, string second)
        {
            return string.Equals(first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsInside(string candidate, string parent)
        {
            return candidate.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void AssertNoReparsePoints(string path)
        {
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Папка профилей или её родитель является ссылкой: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (SamePath(current, parent ?? ""))
                    break;
                current = parent;
            }
        }

        private void RequireInitialized()
        {
            if (state == null)
                throw new InvalidOperationException("Сначала вызовите Initialize().");
        }

        private static LauncherState Clone(LauncherState value)
        {
            return new LauncherState
            {
                Version = value.Version,
                ExecutablePath = value.ExecutablePath,
                Profiles = value.Profiles.Select(p => new Profile { Id = p.Id, Name = p.Name, HeaderColor = p.HeaderColor }).ToList()
            };
        }
    }
}
