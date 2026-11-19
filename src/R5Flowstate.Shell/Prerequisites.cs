using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;

namespace R5Flowstate.Shell;

/// <summary>
/// Windows runtimes the client needs that a fresh install lacks: XInput 1.3 and
/// D3DCompiler_43 (DirectX June 2010) and the VC++ 2015-2022 x64 runtime. The
/// installers ship in the package under redist\ (tools/fetch_redist.py).
/// </summary>
static class Prerequisites
{
    // Keep in step with NeedsVcRuntime in setup/R5Flowstate.iss.
    const int VcMinMinor = 40;
    const string VcRuntimeKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64";

    public static string RedistDir => Path.Combine(AppContext.BaseDirectory, "redist");
    static string DxSetup => Path.Combine(RedistDir, "dx", "DXSETUP.exe");
    static string VcRedist => Path.Combine(RedistDir, "vc_redist.x64.exe");

    public static bool DirectXMissing()
    {
        var sys = Environment.SystemDirectory;
        return !File.Exists(Path.Combine(sys, "xinput1_3.dll"))
            || !File.Exists(Path.Combine(sys, "d3dcompiler_43.dll"));
    }

    public static bool VcRuntimeMissing()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(VcRuntimeKey);
            if (key?.GetValue("Installed") is int installed && installed == 1
                && key.GetValue("Minor") is int minor && minor >= VcMinMinor)
                return false;
        }
        catch (Exception)
        {
        }
        return true;
    }

    /// <summary>Display names of missing runtimes; empty when nothing is missing.</summary>
    public static IReadOnlyList<string> Missing()
    {
        var list = new List<string>();
        if (DirectXMissing())
            list.Add("DirectX June 2010 runtime (XInput 1.3, D3DCompiler 43)");
        if (VcRuntimeMissing())
            list.Add("Microsoft Visual C++ 2015-2022 runtime (x64)");
        return list;
    }

    /// <summary>
    /// Runs the needed installers behind one UAC prompt and waits. Returns null on
    /// success, else the reason. Every installer must carry a valid Microsoft
    /// signature: redist\ sits in a user-writable folder and this elevates it.
    /// </summary>
    public static string? InstallMissing(TimeSpan timeout)
    {
        var steps = new List<(string exe, string args)>();
        if (DirectXMissing())
            steps.Add((DxSetup, "/silent"));
        if (VcRuntimeMissing())
            steps.Add((VcRedist, "/install /quiet /norestart"));
        if (steps.Count == 0)
            return null;

        foreach (var (exe, _) in steps)
        {
            if (!File.Exists(exe))
                return $"installer missing: {exe}";
            if (exe.Contains('%') || exe.Contains('"'))
                return $"installer path not usable: {exe}";
        }

        // DXSETUP loads DSETUP/dsetup32 from its own folder once elevated.
        var signed = steps.Select(s => s.exe).ToList();
        if (steps.Any(s => s.exe == DxSetup))
        {
            signed.Add(Path.Combine(RedistDir, "dx", "DSETUP.dll"));
            signed.Add(Path.Combine(RedistDir, "dx", "dsetup32.dll"));
        }
        foreach (var file in signed)
        {
            if (!IsMicrosoftSigned(file))
                return $"installer signature invalid: {file}";
        }

        var chain = string.Join(" & ", steps.Select(s => $"\"{s.exe}\" {s.args}"));
        Process? p;
        try
        {
            p = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /s /c \"{chain}\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "administrator permission was declined";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (p is null)
            return "installer did not start";
        using (p)
        {
            if (!p.WaitForExit(timeout))
                return "installer timed out";
        }

        var still = Missing();
        return still.Count == 0 ? null : "still missing: " + string.Join(", ", still);
    }

    static bool IsMicrosoftSigned(string path)
    {
        if (!WinTrust.Verify(path))
            return false;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static class WinTrust
    {
        static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        const uint WTD_UI_NONE = 2;
        const uint WTD_REVOKE_NONE = 0;
        const uint WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1;
        const uint WTD_STATEACTION_CLOSE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct FileInfo
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Data
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref Data data);

        public static bool Verify(string path)
        {
            var file = new FileInfo { cbStruct = (uint)Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new Data
                {
                    cbStruct = (uint)Marshal.SizeOf<Data>(),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                };
                var action = GenericVerifyV2;
                var rc = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                return rc == 0;
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(pFile);
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
