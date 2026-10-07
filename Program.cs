using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class Program
{
    private const int WtsInfoClassUserName = 5;
    private const int WtsInfoClassDomainName = 7;
    private const int WtsInfoClassConnectState = 8;

    private static int Main()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "config.txt");
        HashSet<string> exceptions;

        try
        {
            exceptions = LoadExceptions(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Errore nella lettura di '{configPath}': {ex.Message}");
            return 1;
        }

        IntPtr server = WTSOpenServer(Environment.MachineName);
        if (server == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Impossibile aprire il server WTS: {GetLastWin32ErrorMessage()}");
            return 1;
        }

        int exitCode = 0;
        try
        {
            if (!WTSEnumerateSessions(server, 0, 1, out IntPtr sessionsPtr, out int sessionCount))
            {
                Console.Error.WriteLine($"Impossibile enumerare le sessioni: {GetLastWin32ErrorMessage()}");
                return 1;
            }

            try
            {
                int sessionSize = Marshal.SizeOf<WtsSessionInfo>();
                for (int i = 0; i < sessionCount; i++)
                {
                    IntPtr current = IntPtr.Add(sessionsPtr, i * sessionSize);
                    WtsSessionInfo session = Marshal.PtrToStructure<WtsSessionInfo>(current);

                    if (session.State != WtsConnectState.Disconnected)
                    {
                        continue;
                    }

                    string user = QueryString(server, session.SessionId, WtsInfoClassUserName);
                    string domain = QueryString(server, session.SessionId, WtsInfoClassDomainName);
                    if (string.IsNullOrWhiteSpace(user))
                    {
                        Console.Error.WriteLine($"Sessione {session.SessionId}: nome utente non disponibile, ignorata.");
                        exitCode = 1;
                        continue;
                    }

                    string displayName = string.IsNullOrWhiteSpace(domain) ? user : $@"{domain}\{user}";
                    if (IsException(exceptions, user, domain))
                    {
                        Console.WriteLine($"Mantenuta sessione {session.SessionId}: {displayName} (eccezione).");
                        continue;
                    }

                    if (WTSLogoffSession(server, session.SessionId, false))
                    {
                        Console.WriteLine($"Terminata sessione {session.SessionId}: {displayName}.");
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"Impossibile terminare la sessione {session.SessionId} ({displayName}): " +
                            GetLastWin32ErrorMessage());
                        exitCode = 1;
                    }
                }
            }
            finally
            {
                WTSFreeMemory(sessionsPtr);
            }
        }
        finally
        {
            WTSCloseServer(server);
        }

        return exitCode;
    }

    private static HashSet<string> LoadExceptions(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("il file deve trovarsi accanto all'eseguibile", path);
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            result.Add(NormalizeIdentity(line));
        }

        return result;
    }

    private static bool IsException(HashSet<string> exceptions, string user, string domain)
    {
        string normalizedUser = NormalizeIdentity(user);
        if (exceptions.Contains(normalizedUser))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        return exceptions.Contains(NormalizeIdentity($@"{domain}\{user}")) ||
               exceptions.Contains(NormalizeIdentity($"{user}@{domain}"));
    }

    private static string NormalizeIdentity(string value) =>
        value.Trim().Trim('"').Replace('/', '\\');

    private static string QueryString(IntPtr server, int sessionId, int infoClass)
    {
        if (!WTSQuerySessionInformation(server, sessionId, infoClass, out IntPtr buffer, out int byteCount))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            return byteCount > 1 ? Marshal.PtrToStringUni(buffer) ?? string.Empty : string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string GetLastWin32ErrorMessage() =>
        new Win32Exception(Marshal.GetLastWin32Error()).Message;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WTSOpenServer(string pServerName);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSCloseServer(IntPtr hServer);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, int sessionId, int wtsInfoClass, out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(IntPtr hServer, int sessionId, bool bWait);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init
    }
}
