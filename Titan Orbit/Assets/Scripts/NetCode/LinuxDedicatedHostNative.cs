using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// Linux dedicated-server helpers that the IL2CPP <c>Process.Start</c> wrapper cannot do.
    /// Spawn uses <c>posix_spawn</c> (vfork-style, no 2 GB address-space copy). The UDP
    /// reader watches <c>/proc/self/net/udp</c> for a receive buffer the transport stopped draining.
    /// </summary>
    static class LinuxDedicatedHostNative
    {
        /// <summary>Kernel rx queue at or above this for several samples means the socket is not being read.</summary>
        public const int WedgedRxBytes = 64 * 1024;

        /// <summary>True on the Linux player / dedicated server. False in the Windows editor.</summary>
        public static bool IsLinuxHost => File.Exists("/proc/self/exe");

        /// <summary>
        /// Starts <paramref name="executable"/> without <see cref="System.Diagnostics.Process.Start"/>.
        /// IL2CPP's managed spawn throws <c>Win32Exception</c> "Native error= Success" and never
        /// creates the child, so age rotation cannot hand off.
        /// </summary>
        public static bool TryPosixSpawn(
            string executable,
            string workingDirectory,
            string arguments,
            out int childPid,
            out string error)
        {
            childPid = 0;
            error = null;
            IntPtr actions = IntPtr.Zero;
            bool actionsReady = false;
            var heap = new List<IntPtr>(64);
            try
            {
                if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                {
                    error = "executable missing";
                    return false;
                }

                // --- argv / env ---
                // [TITAN-ORBIT] Inherit /proc/self/environ so SDL_VIDEODRIVER and the Unity
                // library path reach the child. posix_spawn does not accept a null envp.
                string[] argvTokens = BuildArgv(executable, arguments);
                string[] envTokens = ReadEnviron();
                IntPtr argv = AllocPointerArray(argvTokens, heap);
                IntPtr envp = AllocPointerArray(envTokens, heap);

                actions = Marshal.AllocHGlobal(256);
                for (int i = 0; i < 256; i++)
                    Marshal.WriteByte(actions, i, 0);

                int initRc = posix_spawn_file_actions_init(actions);
                if (initRc != 0)
                {
                    error = "file_actions_init errno=" + initRc.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                actionsReady = true;
                if (!string.IsNullOrEmpty(workingDirectory))
                {
                    int chdirRc = posix_spawn_file_actions_addchdir_np(actions, workingDirectory);
                    if (chdirRc != 0)
                    {
                        error = "addchdir errno=" + chdirRc.ToString(CultureInfo.InvariantCulture);
                        return false;
                    }
                }

                int rc = posix_spawn(out childPid, executable, actions, IntPtr.Zero, argv, envp);
                if (rc != 0)
                {
                    error = "posix_spawn errno=" + rc.ToString(CultureInfo.InvariantCulture);
                    childPid = 0;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                childPid = 0;
                return false;
            }
            finally
            {
                if (actionsReady)
                    posix_spawn_file_actions_destroy(actions);
                if (actions != IntPtr.Zero)
                    Marshal.FreeHGlobal(actions);
                for (int i = 0; i < heap.Count; i++)
                {
                    if (heap[i] != IntPtr.Zero)
                        Marshal.FreeHGlobal(heap[i]);
                }
            }
        }

        /// <summary>
        /// Largest UDP receive queue for this process, in bytes.
        /// <c>/proc</c> files report size 0, so this reads the stream instead of <c>File.ReadAllText</c>.
        /// </summary>
        public static bool TryReadMaxUdpRxQueue(out int maxRxBytes)
        {
            maxRxBytes = 0;
            if (!IsLinuxHost)
                return false;

            try
            {
                string text = ReadProcFile("/proc/self/net/udp");
                if (string.IsNullOrEmpty(text))
                    return false;

                int max = 0;
                int lineStart = 0;
                for (int i = 0; i <= text.Length; i++)
                {
                    if (i != text.Length && text[i] != '\n')
                        continue;

                    int len = i - lineStart;
                    if (len > 0 && text[lineStart] != 's')
                    {
                        if (TryParseUdpRx(text, lineStart, len, out int rx) && rx > max)
                            max = rx;
                    }

                    lineStart = i + 1;
                }

                maxRxBytes = max;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static string[] BuildArgv(string executable, string arguments)
        {
            var tokens = new List<string> { executable };
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                string[] parts = arguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                    tokens.Add(parts[i]);
            }

            return tokens.ToArray();
        }

        static string[] ReadEnviron()
        {
            try
            {
                byte[] raw;
                using (var fs = new FileStream(
                    "/proc/self/environ", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    using (var ms = new MemoryStream())
                    {
                        fs.CopyTo(ms);
                        raw = ms.ToArray();
                    }
                }

                var list = new List<string>();
                int start = 0;
                for (int i = 0; i <= raw.Length; i++)
                {
                    if (i != raw.Length && raw[i] != 0)
                        continue;
                    if (i > start)
                        list.Add(Encoding.UTF8.GetString(raw, start, i - start));
                    start = i + 1;
                }

                if (list.Count > 0)
                    return list.ToArray();
            }
            catch (Exception)
            {
                // Fall through to a minimal environment.
            }

            return new[] { "PATH=/usr/bin:/bin", "SDL_VIDEODRIVER=dummy" };
        }

        static string ReadProcFile(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                return reader.ReadToEnd();
        }

        /// <summary>
        /// The tx:rx column is 8 hex digits, a colon, 8 hex digits. Address columns are 8:4.
        /// </summary>
        static bool TryParseUdpRx(string text, int start, int length, out int rxBytes)
        {
            rxBytes = 0;
            int i = start;
            int end = start + length;
            while (i < end)
            {
                while (i < end && text[i] == ' ')
                    i++;
                int field = i;
                while (i < end && text[i] != ' ' && text[i] != '\r')
                    i++;
                if (i - field == 17 && text[field + 8] == ':')
                {
                    return int.TryParse(
                        text.Substring(field + 9, 8),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out rxBytes);
                }
            }

            return false;
        }

        static IntPtr AllocPointerArray(string[] items, List<IntPtr> heap)
        {
            IntPtr block = Marshal.AllocHGlobal(IntPtr.Size * (items.Length + 1));
            heap.Add(block);
            for (int i = 0; i < items.Length; i++)
            {
                IntPtr str = Marshal.StringToHGlobalAnsi(items[i] ?? string.Empty);
                heap.Add(str);
                Marshal.WriteIntPtr(block, i * IntPtr.Size, str);
            }

            Marshal.WriteIntPtr(block, items.Length * IntPtr.Size, IntPtr.Zero);
            return block;
        }

        [DllImport("libc", SetLastError = false)]
        static extern int posix_spawn(
            out int pid,
            string path,
            IntPtr fileActions,
            IntPtr attrp,
            IntPtr argv,
            IntPtr envp);

        [DllImport("libc", SetLastError = false)]
        static extern int posix_spawn_file_actions_init(IntPtr fileActions);

        [DllImport("libc", SetLastError = false)]
        static extern int posix_spawn_file_actions_destroy(IntPtr fileActions);

        [DllImport("libc", SetLastError = false)]
        static extern int posix_spawn_file_actions_addchdir_np(IntPtr fileActions, string path);
    }
}
