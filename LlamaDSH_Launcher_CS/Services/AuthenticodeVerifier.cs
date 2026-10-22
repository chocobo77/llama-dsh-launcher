using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LlamaDSH_Launcher.Services
{
    /// <summary>
    /// Authenticode 验签（WinVerifyTrust）。
    /// 与 <see cref="X509Certificate.CreateFromSignedFile"/> 的区别：后者只把证书从 PE 里抠出来，
    /// 既不校验签名与文件内容是否匹配，也不校验证书链——攻击者把合法证书贴到改过的安装包上照样通过。
    /// WinVerifyTrust 才会真正做摘要运算与信任链校验，因此这是安装前唯一可信的一道门。
    /// 全程使用 WTD_UI_NONE + 仅缓存吊销信息，不会弹窗、不需要联网。
    /// </summary>
    public static class AuthenticodeVerifier
    {
        private const int WtdUiNone = 2;
        private const int WtdRevokeNone = 0;
        private const int WtdChoiceFile = 1;
        private const int WtdStateActionVerify = 1;
        private const int WtdStateActionClose = 2;
        private const int WtdCacheOnlyUrlRetrieval = 0x1000;

        private const int TrustENoSignature = unchecked((int)0x800B0100);
        private const int TrustEBadDigest = unchecked((int)0x80096010);
        private const int TrustEExplicitDistrust = unchecked((int)0x800B0111);
        private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
        private const int CertEExpired = unchecked((int)0x800B0101);
        private const int CertEChaining = unchecked((int)0x800B010A);

        private static readonly Guid ActionGenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        /// <summary>验签结果：是否通过、原始 HRESULT、可读原因、签名主体。</summary>
        public readonly struct Result
        {
            public Result(bool isValid, int hresult, string message, string subject)
            {
                IsValid = isValid;
                HResult = hresult;
                Message = message;
                Subject = subject;
            }

            /// <summary>签名有效且证书链可信。</summary>
            public bool IsValid { get; }

            /// <summary>WinVerifyTrust 返回的 HRESULT（0 表示通过）。</summary>
            public int HResult { get; }

            /// <summary>失败原因（中文，可直接展示给用户）。</summary>
            public string Message { get; }

            /// <summary>签名证书 Subject；取不到时为空串。</summary>
            public string Subject { get; }
        }

        /// <summary>
        /// 对文件做 Authenticode 验签。任何异常都归为"不可信"，绝不抛出。
        /// </summary>
        public static Result Verify(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return new Result(false, unchecked((int)0x80070002), "文件不存在，无法验签。", string.Empty);
            }

            int hr;
            try
            {
                hr = WinVerifyTrustFile(filePath);
            }
            catch (Exception ex)
            {
                return new Result(false, unchecked((int)0x80004005), "验签调用失败：" + ex.Message, string.Empty);
            }

            string subject = TryGetSubject(filePath);
            if (hr == 0)
            {
                return new Result(true, 0, "签名有效。", subject);
            }
            return new Result(false, hr, DescribeHResult(hr), subject);
        }

        private static string DescribeHResult(int hr)
        {
            switch (hr)
            {
                case TrustENoSignature:
                    return "文件没有数字签名。";
                case TrustEBadDigest:
                    return "数字签名与文件内容不匹配（文件可能被篡改）。";
                case TrustEExplicitDistrust:
                    return "该证书已被系统明确列为不信任。";
                case CertEUntrustedRoot:
                    return "证书链的根证书不受信任。";
                case CertEExpired:
                    return "签名证书已过期。";
                case CertEChaining:
                    return "无法构建证书链。";
                default:
                    return "验签失败（HRESULT 0x" + hr.ToString("X8") + "）。";
            }
        }

        private static string TryGetSubject(string filePath)
        {
            try
            {
                using var cert = X509Certificate.CreateFromSignedFile(filePath);
                using var cert2 = new X509Certificate2(cert);
                return cert2.Subject ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int WinVerifyTrustFile(string filePath)
        {
            IntPtr pPath = Marshal.StringToHGlobalUni(filePath);
            IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            try
            {
                WinTrustFileInfo fileInfo = new WinTrustFileInfo
                {
                    cbStruct = Marshal.SizeOf<WinTrustFileInfo>(),
                    pFilePath = pPath,
                    hFile = IntPtr.Zero,
                    pgKnownSubject = IntPtr.Zero,
                };
                Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                WinTrustData data = new WinTrustData
                {
                    cbStruct = Marshal.SizeOf<WinTrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = pFileInfo,
                    dwStateAction = WtdStateActionVerify,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval,
                };
                Marshal.StructureToPtr(data, pData, false);

                // 静态只读字段不能按 ref 传递，这里复制到局部变量再取地址。
                Guid action = ActionGenericVerifyV2;
                int result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

                // 无论成败都要 CLOSE，否则 WinVerifyTrust 内部持有的状态数据会泄漏。
                data.dwStateAction = WtdStateActionClose;
                Marshal.StructureToPtr(data, pData, false);
                WinVerifyTrust(IntPtr.Zero, ref action, pData);

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
                Marshal.FreeHGlobal(pFileInfo);
                Marshal.FreeHGlobal(pPath);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr pWvtData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public int cbStruct;
            public IntPtr pFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSipClientData;
            public int dwUIChoice;
            public int fdwRevocationChecks;
            public int dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWvtStateData;
            public IntPtr pwszUrlReference;
            public int dwProvFlags;
            public int dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
