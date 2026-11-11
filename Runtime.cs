using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VkWorkspaceSwitcher
{
    // Check the paths in a fresh process: Known Folders can be cached per process.
    internal static class ProfileEnvironment
    {
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        private static string KnownFolder(string id)
        {
            Guid guid = new Guid(id);
            IntPtr result;
            int hr = SHGetKnownFolderPath(ref guid, 0x4000, IntPtr.Zero, out result);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            try { return Marshal.PtrToStringUni(result); }
            finally { Marshal.FreeCoTaskMem(result); }
        }

        private static bool SamePath(string a, string b)
        {
            return String.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        internal static int Check(string home)
        {
            try
            {
                bool valid = SamePath(KnownFolder("3EB685DB-65F9-4CF6-A03A-E3EF65729F3D"), Path.Combine(home, "AppData", "Roaming"))
                    && SamePath(KnownFolder("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"), Path.Combine(home, "AppData", "Local"))
                    && SamePath(Environment.GetEnvironmentVariable("USERPROFILE"), home);
                return valid ? 0 : 3;
            }
            catch { return 4; }
        }

        internal static void Verify(ProcessStartInfo app, string home)
        {
            ProcessStartInfo probe = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName);
            probe.Arguments = "--check-profile \"" + home + "\"";
            probe.UseShellExecute = false;
            probe.CreateNoWindow = true;
            probe.WindowStyle = ProcessWindowStyle.Hidden;
            probe.EnvironmentVariables.Clear();
            foreach (string key in app.EnvironmentVariables.Keys)
                probe.EnvironmentVariables[key] = app.EnvironmentVariables[key];
            using (Process process = Process.Start(probe))
            {
                if (!process.WaitForExit(10000))
                {
                    try { process.Kill(); } catch { }
                    throw new InvalidOperationException("Не удалось проверить отдельную папку профиля. VK WorkSpace не запущен.");
                }
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows не перенаправила папки AppData в выбранный профиль. Запуск отменён, чтобы не открыть основной аккаунт. Этот способ несовместим с текущими настройками папок Windows.");
            }
        }
    }

    [DataContract]
    internal sealed class RunningProfile
    {
        [DataMember] public string ProfileId;
        [DataMember] public int ProcessId;
        [DataMember] public long StartedUtcTicks;
        [DataMember] public string Executable;
    }

    public sealed class ProfileRuntime
    {
        private readonly ProfileLauncher launcher;
        private readonly string runtimeDirectory;
        private readonly string mutexName;

        public ProfileRuntime(ProfileLauncher launcher)
        {
            this.launcher = launcher;
            runtimeDirectory = Path.Combine(launcher.Root, "runtime");
            using (SHA256 hash = SHA256.Create())
                mutexName = "Local\\VKWorkspaceSwitcher.Run." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(launcher.Root.ToUpperInvariant()))).Replace("-", "");
        }

        private string RecordPath(Profile profile)
        {
            if (profile.Id != "default")
            {
                Guid id;
                if (!Guid.TryParseExact(profile.Id, "N", out id)) throw new InvalidOperationException("Некорректный профиль.");
            }
            return Path.Combine(runtimeDirectory, profile.Id + ".json");
        }

        private Process ReadProcess(Profile profile)
        {
            Process process = null;
            try
            {
                RunningProfile record;
                using (FileStream file = File.OpenRead(RecordPath(profile)))
                    record = (RunningProfile)new DataContractJsonSerializer(typeof(RunningProfile)).ReadObject(file);
                if (record.ProfileId != profile.Id) return null;
                process = Process.GetProcessById(record.ProcessId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != record.StartedUtcTicks
                    || !String.Equals(process.MainModule.FileName, record.Executable, StringComparison.OrdinalIgnoreCase))
                { return null; }
                Process found = process;
                process = null;
                return found;
            }
            catch { return null; }
            finally { if (process != null) process.Dispose(); }
        }

        private void SaveProcess(Profile profile, Process process)
        {
            Directory.CreateDirectory(runtimeDirectory);
            if ((File.GetAttributes(runtimeDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Папка состояния процесса не должна быть ссылкой.");
            RunningProfile record = new RunningProfile { ProfileId = profile.Id, ProcessId = process.Id,
                StartedUtcTicks = process.StartTime.ToUniversalTime().Ticks, Executable = process.MainModule.FileName };
            string path = RecordPath(profile);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                new DataContractJsonSerializer(typeof(RunningProfile)).WriteObject(file, record);
                file.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        // Adopt an already open ordinary instance only when it is unambiguous.
        private Process FindDefault()
        {
            string exe = launcher.State.ExecutablePath;
            var known = launcher.State.Profiles.Where(p => p.Id != "default").Select(ReadProcess).Where(p => p != null).ToList();
            try
            {
                var candidates = new List<Process>();
                foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    bool matches = false;
                    try { matches = !known.Any(k => k.Id == p.Id) && String.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase); }
                    catch { }
                    if (matches) candidates.Add(p);
                    else p.Dispose();
                }
                if (candidates.Count == 1) return candidates[0];
                foreach (Process p in candidates) p.Dispose();
                return null;
            }
            finally { foreach (Process process in known) process.Dispose(); }
        }

        public bool IsRunning(Profile profile)
        {
            using (Process process = ReadProcess(profile) ?? (profile.Id == "default" ? FindDefault() : null))
                return process != null;
        }

        public int Open(Profile profile)
        {
            profile = RegisteredProfile(profile);
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) throw new InvalidOperationException("Другой запуск ещё выполняется. Попробуйте через несколько секунд.");
                    using (Process existing = ReadProcess(profile) ?? (profile.Id == "default" ? FindDefault() : null))
                    {
                        if (existing != null)
                        {
                            SaveProcess(profile, existing);
                            ApplyTitle(existing, profile);
                            ApplyHeader(existing, profile);
                            Focus(existing, profile);
                            return existing.Id;
                        }
                    }
                    ProcessStartInfo info = launcher.BuildStartInfo(profile);
                    if (profile.Id != "default") ProfileEnvironment.Verify(info, launcher.HomePath(profile));
                    using (Process process = Process.Start(info))
                    {
                        if (process.WaitForExit(1200))
                            throw new InvalidOperationException("VK WorkSpace завершился сразу после запуска. Возможно, этот профиль уже открыт вне лаунчера или клиент обновляется. Проверьте его окно и повторите запуск.");
                        SaveProcess(profile, process);
                        ApplyTitle(process, profile);
                        ApplyHeader(process, profile);
                        Focus(process, profile);
                        return process.Id;
                    }
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        private Profile RegisteredProfile(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            Profile registered = launcher.State.Profiles.FirstOrDefault(p => p.Id == profile.Id);
            if (registered == null) throw new ArgumentException("Профиль не найден.");
            return registered;
        }

        public bool UpdateWindowTitle(Profile profile)
        {
            profile = RegisteredProfile(profile);
            using (Process process = ReadProcess(profile) ?? (profile.Id == "default" ? FindDefault() : null))
            {
                if (process == null) return false;
                try
                {
                    bool titled = ApplyTitle(process, profile);
                    ApplyHeader(process, profile);
                    return titled;
                }
                catch (InvalidOperationException) { return false; }
                catch (Win32Exception) { return false; }
            }
        }

        public bool UpdateHeader(Profile profile)
        {
            profile = RegisteredProfile(profile);
            using (Process process = ReadProcess(profile) ?? (profile.Id == "default" ? FindDefault() : null))
            {
                if (process == null) return false;
                try { ApplyTitle(process, profile); return ApplyHeader(process, profile); }
                catch (InvalidOperationException) { return false; }
                catch (Win32Exception) { return false; }
            }
        }

        private static bool ApplyHeader(Process process, Profile profile)
        {
            try { return NativeHeader.Apply(process, FindMainWindow(process, profile), profile); }
            catch (Exception error)
            {
                Debug.WriteLine("Header styling failed: " + error.Message);
                return false;
            }
        }

        public bool Close(Profile profile)
        {
            profile = RegisteredProfile(profile);
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool locked = false;
                try
                {
                    try { locked = mutex.WaitOne(15000); }
                    catch (AbandonedMutexException) { locked = true; }
                    if (!locked) throw new InvalidOperationException("Другой запуск ещё выполняется. Попробуйте через несколько секунд.");
                    using (Process process = ReadProcess(profile) ?? (profile.Id == "default" ? FindDefault() : null))
                    {
                        if (process == null || process.HasExited) return false;
                        // Keep a handle open and validate the window's owner immediately before
                        // messaging. A stale PID record must never close an unrelated process.
                        IntPtr retainedHandle = process.Handle;
                        int pid = process.Id;
                        long started = process.StartTime.ToUniversalTime().Ticks;
                        IntPtr window = FindMainWindow(process, profile);
                        if (window == IntPtr.Zero)
                        {
                            if (process.HasExited) return false;
                            throw new InvalidOperationException("Окно этого профиля пока недоступно. Дождитесь запуска VK WorkSpace и повторите закрытие.");
                        }
                        uint owner;
                        uint thread = GetWindowThreadProcessId(window, out owner);
                        if (owner != pid || thread == 0 || process.HasExited) return false;
                        // Qt handles WM_QUIT by calling QCoreApplication::quit(), which
                        // allows its normal shutdown path to run. WM_CLOSE just hides to tray.
                        if (!PostThreadMessage(thread, 0x0012, IntPtr.Zero, IntPtr.Zero))
                        {
                            if (process.HasExited) return false;
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось передать VK WorkSpace запрос на закрытие.");
                        }
                        if (!process.WaitForExit(10000))
                            throw new InvalidOperationException("VK WorkSpace пока не завершился. Закройте открытые диалоги в этом профиле и повторите. Принудительное завершение не выполнялось.");
                        ForgetProcess(profile, pid, started);
                        return true;
                    }
                }
                finally { if (locked) mutex.ReleaseMutex(); }
            }
        }

        private void ForgetProcess(Profile profile, int pid, long started)
        {
            // Remove only this instance's metadata, never profile/session data.
            try
            {
                RunningProfile record;
                string path = RecordPath(profile);
                using (FileStream file = File.OpenRead(path))
                    record = (RunningProfile)new DataContractJsonSerializer(typeof(RunningProfile)).ReadObject(file);
                if (record != null && record.ProfileId == profile.Id && record.ProcessId == pid && record.StartedUtcTicks == started)
                    File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SerializationException) { }
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int max);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
        private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string text,
            uint flags, uint timeout, out UIntPtr result);

        private const string MainWindowMarker = "VKWorkspaceSwitcher.MainWindow";

        private static string WindowTitle(IntPtr window)
        {
            StringBuilder text = new StringBuilder(1024);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private static IntPtr FindMainWindow(Process process, Profile profile)
        {
            if (process.HasExited) return IntPtr.Zero;
            var candidates = new List<IntPtr>();
            IntPtr marked = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                if (pid != process.Id) return true;
                StringBuilder name = new StringBuilder(256);
                GetClassName(window, name, name.Capacity);
                if (!name.ToString().StartsWith("Qt", StringComparison.Ordinal) || !name.ToString().EndsWith("QWindowIcon", StringComparison.Ordinal)) return true;
                if (GetWindow(window, 4) != IntPtr.Zero || (GetWindowLongPtr(window, -20).ToInt64() & 0x80) != 0) return true;
                if (GetProp(window, MainWindowMarker) != IntPtr.Zero) { marked = window; return false; }
                candidates.Add(window);
                return true;
            }, IntPtr.Zero);
            if (marked != IntPtr.Zero) return marked;
            var matching = candidates.Where(h => WindowTitle(h) == "VK WorkSpace" || WindowTitle(h) == profile.Name).ToList();
            IntPtr found = matching.Count == 1 ? matching[0] : candidates.Count == 1 ? candidates[0] : IntPtr.Zero;
            if (found != IntPtr.Zero) SetProp(found, MainWindowMarker, new IntPtr(1));
            return found;
        }

        private static bool ApplyTitle(Process process, Profile profile)
        {
            IntPtr retainedHandle = process.Handle;
            IntPtr window = FindMainWindow(process, profile);
            if (window == IntPtr.Zero) return false;
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != process.Id || process.HasExited) return false;
            if (WindowTitle(window) == profile.Name) return true;
            UIntPtr result;
            return SendMessageTimeout(window, 0x000C, IntPtr.Zero, profile.Name, 0x0001 | 0x0002 | 0x0020, 100, out result) != IntPtr.Zero
                && result != UIntPtr.Zero;
        }

        private static void Focus(Process process, Profile profile)
        {
            IntPtr window = FindMainWindow(process, profile);
            if (window == IntPtr.Zero) return;
            ShowWindowAsync(window, 9);
            SetForegroundWindow(window);
        }
    }
}
