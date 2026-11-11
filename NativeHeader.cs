using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace VkWorkspaceSwitcher
{
    internal static class NativeHeader
    {
        private static readonly object Gate = new object();
        private static IntPtr module;
        private static IntPtr callback;
        private static int requestNumber = Environment.TickCount;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
        private struct Packet
        {
            public uint Version, Request, Color;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 81)] public string Title;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct CopyData
        {
            public UIntPtr Tag;
            public uint Length;
            public IntPtr Data;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int kind, IntPtr callback, IntPtr library, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, ref CopyData data, uint flags, uint timeout, out UIntPtr result);

        private static bool LoadHelper()
        {
            if (callback != IntPtr.Zero) return true;
            byte[] bytes;
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("VkWorkspaceSwitcher.HeaderHook"))
            {
                if (resource == null) return false;
                using (MemoryStream buffer = new MemoryStream()) { resource.CopyTo(buffer); bytes = buffer.ToArray(); }
            }
            string hash;
            using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKWorkspaceSwitcher", "native", hash);
            // Hash-named copies allow updating the launcher while an older helper
            // may still be mapped in an existing VK instance.
            Directory.CreateDirectory(directory);
            for (DirectoryInfo item = new DirectoryInfo(directory); item != null; item = item.Parent)
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка вспомогательного модуля не должна быть ссылкой.");
            string path = Path.Combine(directory, "HeaderHook.dll");
            if (!File.Exists(path))
            {
                string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllBytes(temporary, bytes);
                try { File.Move(temporary, path); }
                catch (IOException) { if (!File.Exists(path)) throw; }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            using (SHA256 sha = SHA256.Create())
            using (FileStream file = File.OpenRead(path))
                if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "") != hash) throw new IOException("Вспомогательный модуль шапки повреждён.");
            module = LoadLibraryEx(path, IntPtr.Zero, 0x00000100 | 0x00000800);
            if (module == IntPtr.Zero) return false;
            callback = GetProcAddress(module, "VkWorkspaceHeaderHook");
            return callback != IntPtr.Zero;
        }

        internal static bool Apply(Process process, IntPtr window, Profile profile)
        {
            if (window == IntPtr.Zero || process.HasExited) return false;
            lock (Gate)
            {
                if (!LoadHelper()) return false;
                uint owner;
                uint thread = GetWindowThreadProcessId(window, out owner);
                if (thread == 0 || owner != process.Id || process.HasExited) return false;
                IntPtr hook = SetWindowsHookEx(4, callback, module, thread);
                if (hook == IntPtr.Zero) return false;
                IntPtr memory = IntPtr.Zero;
                try
                {
                    Packet packet = new Packet { Version = 1, Request = unchecked((uint)Interlocked.Increment(ref requestNumber)),
                        Color = uint.Parse(ProfileLauncher.EffectiveHeaderColor(profile).Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture), Title = profile.Name };
                    int size = Marshal.SizeOf(typeof(Packet));
                    memory = Marshal.AllocHGlobal(size);
                    Marshal.StructureToPtr(packet, memory, false);
                    CopyData data = new CopyData { Tag = new UIntPtr(0x564B4852), Length = (uint)size, Data = memory };
                    UIntPtr result;
                    if (SendMessageTimeout(window, 0x004A, IntPtr.Zero, ref data, 0x0001 | 0x0002 | 0x0020, 750, out result) == IntPtr.Zero) return false;
                    ulong acknowledgement = unchecked((ulong)GetProp(window, "VKWorkspaceSwitcher.HeaderResult").ToInt64());
                    return (acknowledgement >> 8) == packet.Request && (acknowledgement & 255) == 1;
                }
                finally
                {
                    UnhookWindowsHookEx(hook);
                    if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
                }
            }
        }
    }
}
