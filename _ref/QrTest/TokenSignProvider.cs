using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lagrange.Core.Common;

namespace QqReborn.QrTest;

/// <summary>
/// Token-authenticated sign provider for LagrangeV2 (Linux protocol).
///
/// Modelled on Lagrange.Milky/Utility/Signer.cs (the up-to-date impl) rather than the
/// outdated DefaultBotSignProvider/LinuxSignProvider, which POST the legacy
/// /api/sign/{version} shape with no auth -- that endpoint now returns 404.
///
/// Real shape: POST {url}/api/sign/sec-sign, header "Authorization: Bearer {token}",
/// JSON body { uin, command, seq, body(hex lower), guid(hex lower), qua }
/// -> { code, message, value: { sec_sign, sec_token, sec_extra } } (all hex).
/// </summary>
public sealed class TokenSignProvider : BotSignProvider, IDisposable
{
    // Commands that require a signature. Copied verbatim from the Milky PC whitelist.
    private static readonly HashSet<string> PcWhiteListCommand =
    [
        "trpc.o3.ecdh_access.EcdhAccess.SsoEstablishShareKey", "trpc.o3.ecdh_access.EcdhAccess.SsoSecureAccess",
        "trpc.o3.report.Report.SsoReport", "MessageSvc.PbSendMsg", "wtlogin.trans_emp", "wtlogin.login",
        "wtlogin.exchange_emp", "trpc.login.ecdh.EcdhService.SsoKeyExchange",
        "trpc.login.ecdh.EcdhService.SsoNTLoginPasswordLogin", "trpc.login.ecdh.EcdhService.SsoNTLoginEasyLogin",
        "trpc.login.ecdh.EcdhService.SsoNTLoginPasswordLoginNewDevice",
        "trpc.login.ecdh.EcdhService.SsoNTLoginEasyLoginUnusualDevice",
        "trpc.login.ecdh.EcdhService.SsoNTLoginPasswordLoginUnusualDevice",
        "trpc.login.ecdh.EcdhService.SsoNTLoginRefreshTicket", "trpc.login.ecdh.EcdhService.SsoNTLoginRefreshA2",
        "OidbSvcTrpcTcp.0x11ec_1", "OidbSvcTrpcTcp.0x758_1", "OidbSvcTrpcTcp.0x7c1_1", "OidbSvcTrpcTcp.0x7c2_5",
        "OidbSvcTrpcTcp.0x10db_1", "OidbSvcTrpcTcp.0x8a1_7", "OidbSvcTrpcTcp.0x89a_0", "OidbSvcTrpcTcp.0x89a_15",
        "OidbSvcTrpcTcp.0x88d_0", "OidbSvcTrpcTcp.0x88d_14", "OidbSvcTrpcTcp.0x112a_1", "OidbSvcTrpcTcp.0x587_74",
        "OidbSvcTrpcTcp.0x1100_1", "OidbSvcTrpcTcp.0x1102_1", "OidbSvcTrpcTcp.0x1103_1", "OidbSvcTrpcTcp.0x1107_1",
        "OidbSvcTrpcTcp.0x1105_1", "OidbSvcTrpcTcp.0xf88_1", "OidbSvcTrpcTcp.0xf89_1", "OidbSvcTrpcTcp.0xf57_1",
        "OidbSvcTrpcTcp.0xf57_106", "OidbSvcTrpcTcp.0xf57_9", "OidbSvcTrpcTcp.0xf55_1", "OidbSvcTrpcTcp.0xf67_1",
        "OidbSvcTrpcTcp.0xf67_5", "OidbSvcTrpcTcp.0x6d9_4"
    ];

    private readonly string _url;
    private readonly string? _token;
    private readonly long _uin;
    private readonly HttpClient _client = new();

    /// <param name="url">Sign server base, e.g. https://sign.lagrangecore.org</param>
    /// <param name="token">Bearer token from #signer registration (null/empty = unsigned, login will fail).</param>
    /// <param name="uin">Optional uin override; 0 = use the uin Tencent passes per request.</param>
    public TokenSignProvider(string url, string? token, long uin = 0)
    {
        _url = url.TrimEnd('/');
        _token = token;
        _uin = uin;
    }

    public override bool IsWhiteListCommand(string cmd) => PcWhiteListCommand.Contains(cmd);

    public override async Task<SsoSecureInfo?> GetSecSign(long uin, string cmd, int seq, ReadOnlyMemory<byte> body)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_url}/api/sign/sec-sign");
            if (!string.IsNullOrEmpty(_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            var payload = JsonSerializer.Serialize(new SecSignRequest
            {
                Uin = uin == 0 ? _uin : uin,
                Command = cmd,
                Sequence = seq,
                Body = Convert.ToHexString(body.Span).ToLowerInvariant(),
                Guid = Convert.ToHexString(Context.Keystore.Guid).ToLowerInvariant(),
                Qua = Context.AppInfo.Qua,
            });
            request.Content = new StringContent(payload, Encoding.UTF8, MediaTypeNames.Application.Json);

            using var response = await _client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Context.LogWarning(nameof(TokenSignProvider),
                    $"sign [{cmd}] HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                return null;
            }

            var result = JsonSerializer.Deserialize<SignerResponse>(await response.Content.ReadAsStringAsync());
            if (result is null)
            {
                Context.LogWarning(nameof(TokenSignProvider), $"sign [{cmd}] empty/invalid response");
                return null;
            }
            if (result.Code != 0)
            {
                Context.LogWarning(nameof(TokenSignProvider), $"sign [{cmd}] server code {result.Code}: {result.Message}");
                return null;
            }

            return new SsoSecureInfo
            {
                SecSign = Convert.FromHexString(result.Value.SecSign),
                SecToken = Convert.FromHexString(result.Value.SecToken),
                SecExtra = Convert.FromHexString(result.Value.SecExtra),
            };
        }
        catch (Exception e)
        {
            Context.LogWarning(nameof(TokenSignProvider), $"sign [{cmd}] failed: {e.Message}");
            return null;
        }
    }

    public void Dispose() => _client.Dispose();

    private sealed class SecSignRequest
    {
        [JsonPropertyName("uin")] public required long Uin { get; init; }
        [JsonPropertyName("command")] public required string Command { get; init; }
        [JsonPropertyName("seq")] public required int Sequence { get; init; }
        [JsonPropertyName("body")] public required string Body { get; init; }
        [JsonPropertyName("guid")] public required string Guid { get; init; }
        [JsonPropertyName("qua")] public required string Qua { get; init; }
    }

    private sealed class SignerResponse
    {
        [JsonPropertyName("code")] public int Code { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("value")] public SecSignValue Value { get; init; } = new();
    }

    private sealed class SecSignValue
    {
        [JsonPropertyName("sec_sign")] public string SecSign { get; init; } = "";
        [JsonPropertyName("sec_token")] public string SecToken { get; init; } = "";
        [JsonPropertyName("sec_extra")] public string SecExtra { get; init; } = "";
    }
}
