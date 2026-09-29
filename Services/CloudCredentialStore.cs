using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamLuaManager.Services;

// 按上游 companion 与 DLL 的约定存取凭证文件：JSON 明文经 DPAPI（当前用户、无附加熵）
// 加密后原子落盘；读到遗留明文（首字节 '{'）直接沿用。未复用 SecureTokenStorage，
// 其强制附加 entropy，加解密结果与上游不互通，且改动会波及 Steam 登录链路。
internal static class CloudCredentialStore
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string? ReadJson(string path)
    {
        byte[] raw;
        try
        {
            if (!File.Exists(path)) return null;
            raw = File.ReadAllBytes(path);
        }
        catch { return null; }
        if (raw.Length == 0) return null;
        if (raw[0] == (byte)'{')
            return Encoding.UTF8.GetString(raw);
        try
        {
            var plain = Unprotect(raw);
            return plain == null ? null : Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    public static bool WriteJson(string path, string json)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var blob = Protect(Encoding.UTF8.GetBytes(json));
            if (blob == null) return false;
            var tmp = path + ".new";
            File.WriteAllBytes(tmp, blob);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private static byte[]? Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows() || data.Length == 0) return null;
        IntPtr pIn = IntPtr.Zero;
        DataBlob outBlob = default;
        try
        {
            pIn = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pIn, data.Length);
            var inBlob = new DataBlob { cbData = data.Length, pbData = pIn };
            if (!CryptProtectData(ref inBlob, null, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        catch { return null; }
        finally
        {
            if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    private static byte[]? Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows() || data.Length == 0) return null;
        IntPtr pIn = IntPtr.Zero;
        DataBlob outBlob = default;
        try
        {
            pIn = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, pIn, data.Length);
            var inBlob = new DataBlob { cbData = data.Length, pbData = pIn };
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
                return null;
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        catch { return null; }
        finally
        {
            if (pIn != IntPtr.Zero) Marshal.FreeHGlobal(pIn);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
